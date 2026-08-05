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
