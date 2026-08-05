using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PoEformance.App;

/// <summary>
/// Presses flask keys, and answers whether the game currently has focus.
/// </summary>
/// <remarks>
/// The side effect lives here, at the composition root, deliberately: the Features layer
/// decides and produces plain data, and only this shell turns a decision into something the
/// outside world can observe. That is the same boundary that lets the flask rules be tested
/// without a game, and it means there is exactly ONE place in the codebase capable of
/// synthesising input.
///
/// SendInput rather than PostMessage: the game reads input through the raw input / DirectInput
/// path, where posted window messages simply do not arrive. SendInput goes through the same
/// queue a real keyboard does, which is also why the focus check matters so much - the
/// keystroke lands wherever focus IS, not where it was aimed.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class FlaskKeySender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    /// <summary>True when the given window is the one receiving keystrokes right now.</summary>
    public static bool IsForeground(IntPtr window)
        => window != IntPtr.Zero && GetForegroundWindow() == window;

    /// <summary>
    /// Sends one key press and release.
    /// </summary>
    /// <remarks>
    /// Scan codes, not virtual keys: games commonly read the scan code and ignore the
    /// virtual key, so a virtual-key-only event is accepted by Windows and then quietly
    /// does nothing in the game. Sending both is what makes the press actually register.
    /// </remarks>
    public static void Press(ushort virtualKey)
    {
        var scanCode = (ushort)MapVirtualKeyW(virtualKey, 0);

        Input[] inputs =
        [
            new Input
            {
                Type = InputKeyboard,
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = scanCode,
                    Flags = KeyEventScanCode,
                },
            },
            new Input
            {
                Type = InputKeyboard,
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = scanCode,
                    Flags = KeyEventScanCode | KeyEventKeyUp,
                },
            },
        ];

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }
}
