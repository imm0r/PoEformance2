using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PoEformance.Overlay;

/// <summary>The game's drawable area in screen pixels.</summary>
public readonly record struct ClientRect(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// Follows the game window, so the overlay covers exactly the area the game draws into.
/// </summary>
/// <remarks>
/// Without this the overlay keeps its default size and the projection is computed against
/// the WRONG viewport: NDC is resolution-independent, so the maths still "works" and the
/// player still reads as centred, but every dot is placed inside a small window that does
/// not line up with the game. That makes a viewport mismatch easy to miss - hence tracking
/// it explicitly rather than assuming a full-screen game.
///
/// It is the CLIENT rect, not the window rect: in windowed mode the border and title bar
/// are not part of what the game renders, and using the window rect would offset every dot
/// by the title bar height. The client origin is converted to screen coordinates, which
/// makes borderless, windowed and full-screen all fall out of the same code path.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class GameWindowTracker
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hWnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hWnd, ref Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>True when the game is the window the user is actually looking at.</summary>
    /// <remarks>
    /// This is the KEYSTROKE test, and it is deliberately strict: synthesised input lands
    /// wherever focus is, not where it was aimed, so anything but the game itself in front
    /// means the keystroke would be typed into something else. Drawing asks a wider
    /// question - see <see cref="IsOwnProcessForeground"/>.
    /// </remarks>
    public static bool IsForeground(IntPtr windowHandle)
        => windowHandle != IntPtr.Zero && GetForegroundWindow() == windowHandle;

    /// <summary>True when a window of THIS process is in front.</summary>
    /// <remarks>
    /// The drawing test needs this alongside the game's own window, and leaving it out
    /// makes the overlay's own controls impossible to use: clicking one gives the overlay
    /// window focus, at which point the game is no longer foreground, so the next frame
    /// draws nothing, so there is nothing left to click. The window vanishes under the
    /// cursor and neither dragging nor a button ever registers.
    ///
    /// It also keeps the overlay up while the config window has focus, which is what makes
    /// a setting visibly take effect as it is changed.
    /// </remarks>
    public static bool IsOwnProcessForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Where the mouse is inside the game's client area, or null when it is elsewhere.
    /// </summary>
    /// <remarks>
    /// In the same pixels <see cref="PoEformance.Game.World.WorldToScreen"/> projects into, so
    /// a monster's projected point and the cursor can be compared directly - which is what
    /// "how many monsters am I aiming at" needs and what the AHK tool's own cursor radius
    /// does. Un-projecting the cursor into the world would be the other way round, and it
    /// means intersecting a ray with a ground plane that is not flat.
    ///
    /// GetCursorPos rather than a window message, for the reason the overlay's click-through
    /// icon relies on: a message-driven position freezes the moment the pointer leaves, and
    /// the answer here has to stay right while somebody is aiming.
    ///
    /// Null when the pointer is OUTSIDE the client area. Clamping instead would report a
    /// cursor parked over the inventory as being at the edge of the world, and a rule aimed
    /// at it would fire on whatever monster stood nearest that edge.
    /// </remarks>
    public static (float X, float Y)? CursorInClient(IntPtr windowHandle)
    {
        ClientRect client = TryGet(windowHandle);
        if (!client.IsValid || !GetCursorPos(out Point cursor))
        {
            return null;
        }

        float x = cursor.X - client.X;
        float y = cursor.Y - client.Y;

        return x >= 0 && y >= 0 && x < client.Width && y < client.Height ? (x, y) : null;
    }

    /// <summary>
    /// Reads the game window's client area in screen coordinates, or an invalid rect when
    /// the window is gone or minimised.
    /// </summary>
    public static ClientRect TryGet(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle) || !GetClientRect(windowHandle, out Rect rect))
        {
            return default;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return default; // minimised
        }

        var origin = new Point { X = 0, Y = 0 };
        return ClientToScreen(windowHandle, ref origin)
            ? new ClientRect(origin.X, origin.Y, width, height)
            : default;
    }
}
