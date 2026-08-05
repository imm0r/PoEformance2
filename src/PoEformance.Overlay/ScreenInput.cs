using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PoEformance.Overlay;

/// <summary>
/// The cursor and a hotkey, read from the OS rather than from the focused window.
/// </summary>
/// <remarks>
/// Both exist for one reason: "show me the interface element under the cursor" is a question
/// asked WHILE PLAYING, so the game has focus and the overlay receives no input at all.
/// ImGui's own mouse and keyboard state describe the overlay's window, which is precisely the
/// window that is not being used at that moment - it would report the cursor sitting still
/// wherever it last entered a panel.
///
/// GetAsyncKeyState and GetCursorPos ask the system instead, so they answer regardless of who
/// has focus. Nothing here sends or swallows input: the key is OBSERVED, and the game receives
/// it exactly as it would have.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class ScreenInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    /// <summary>The cursor in screen pixels.</summary>
    public static Vector2 Cursor()
        => GetCursorPos(out Point point) ? new Vector2(point.X, point.Y) : Vector2.Zero;

    /// <summary>
    /// The cursor relative to a client area - the same space the overlay draws in.
    /// </summary>
    /// <remarks>
    /// Returns null when the cursor is outside that area, because "outside" is a real answer
    /// here: hit-testing the interface at a point that is not on it would otherwise report
    /// whatever full-screen element happens to contain the clamped position.
    /// </remarks>
    public static Vector2? CursorIn(ClientRect client)
    {
        if (!client.IsValid)
        {
            return null;
        }

        Vector2 screen = Cursor();
        var local = new Vector2(screen.X - client.X, screen.Y - client.Y);
        return local.X >= 0 && local.Y >= 0 && local.X < client.Width && local.Y < client.Height
            ? local
            : null;
    }

    /// <summary>True while the key is held. The high bit is the down state.</summary>
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}

/// <summary>
/// A key held down, reported once per press.
/// </summary>
/// <remarks>
/// The polled equivalent of a key-down event: the render loop asks every frame, and without
/// the edge a single press would read as held for as long as a finger stays on it - which for
/// a "capture what is under the cursor now" action means capturing it sixty times, each with
/// the cursor a little further along.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class KeyEdge(int virtualKey)
{
    private bool _wasDown;

    /// <summary>True on the frame the key goes down.</summary>
    public bool Pressed()
    {
        bool down = ScreenInput.IsDown(virtualKey);
        bool pressed = down && !_wasDown;
        _wasDown = down;
        return pressed;
    }
}
