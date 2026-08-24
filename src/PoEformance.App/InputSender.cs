using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PoEformance.App;

/// <summary>
/// Synthesises keyboard and mouse input, and answers whether the game currently has focus.
/// </summary>
/// <remarks>
/// The side effect lives here, at the composition root, deliberately: the Features layer
/// decides and produces plain data, and only this shell turns a decision into something the
/// outside world can observe. That is the same boundary that lets the flask rules and the rule
/// engine be tested without a game, and it means there is exactly ONE place in the codebase
/// capable of synthesising input.
///
/// It was FlaskKeySender until the rule engine needed to hold a key down, click and scroll. A
/// second sender beside it would have been the easier change and would have cost that
/// invariant - and the invariant is the thing that makes "can this tool press something
/// unexpected" a question with one place to look.
///
/// SendInput rather than PostMessage: the game reads input through the raw input / DirectInput
/// path, where posted window messages simply do not arrive. SendInput goes through the same
/// queue a real keyboard does, which is also why the focus check matters so much - the
/// keystroke lands wherever focus IS, not where it was aimed.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class InputSender
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseWheel = 0x0800;

    /// <summary>One notch of the wheel, as Windows counts them.</summary>
    private const int WheelStep = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public int Data;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
        [FieldOffset(8)] public MouseInput Mouse;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    /// <summary>True when the given window is the one receiving keystrokes right now.</summary>
    /// <remarks>
    /// Deferred to the window tracker rather than asking Windows again here. The overlay
    /// needs the same answer to decide whether to draw at all, and two copies of "is the
    /// game in front" is exactly the sort of thing that drifts apart.
    /// </remarks>
    public static bool IsForeground(IntPtr window)
        => PoEformance.Overlay.GameWindowTracker.IsForeground(window);

    /// <summary>Sends one key press and release.</summary>
    /// <remarks>
    /// Scan codes, not virtual keys: games commonly read the scan code and ignore the
    /// virtual key, so a virtual-key-only event is accepted by Windows and then quietly
    /// does nothing in the game. Sending both is what makes the press actually register.
    /// </remarks>
    public static void Press(ushort virtualKey)
    {
        if (virtualKey == 0)
        {
            return;
        }

        Send([Key(virtualKey, up: false), Key(virtualKey, up: true)]);
    }

    /// <summary>Holds a key down, without releasing it.</summary>
    /// <remarks>
    /// The half of a pair. Nothing here tracks what is held, and nothing releases it on
    /// shutdown - a held key whose release never came is a rule somebody has to fix rather
    /// than a state this layer should paper over, and guessing at a release is how a tool ends
    /// up interrupting a key the PLAYER is holding.
    /// </remarks>
    public static void Hold(ushort virtualKey)
    {
        if (virtualKey != 0)
        {
            Send([Key(virtualKey, up: false)]);
        }
    }

    /// <summary>Releases a held key.</summary>
    public static void Release(ushort virtualKey)
    {
        if (virtualKey != 0)
        {
            Send([Key(virtualKey, up: true)]);
        }
    }

    /// <summary>Presses several keys in order, each a full press and release.</summary>
    public static void Press(IReadOnlyList<ushort> virtualKeys)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        foreach (ushort key in virtualKeys)
        {
            Press(key);
        }
    }

    /// <summary>Clicks a mouse button where the cursor already is.</summary>
    /// <remarks>
    /// Where the cursor already is, and this layer never moves it. A tool that moves somebody's
    /// pointer mid-fight is a tool they cannot aim with, and the rule engine has no notion of
    /// a target to aim at.
    /// </remarks>
    public static void Click(bool left)
        => Send([Mouse(left ? MouseLeftDown : MouseRightDown), Mouse(left ? MouseLeftUp : MouseRightUp)]);

    /// <summary>Holds a mouse button down.</summary>
    public static void MouseDown(bool left) => Send([Mouse(left ? MouseLeftDown : MouseRightDown)]);

    /// <summary>Releases a held mouse button.</summary>
    public static void MouseUp(bool left) => Send([Mouse(left ? MouseLeftUp : MouseRightUp)]);

    /// <summary>Turns the wheel one notch.</summary>
    public static void Scroll(bool up) => Send([Mouse(MouseWheel, up ? WheelStep : -WheelStep)]);

    private static Input Key(ushort virtualKey, bool up)
    {
        var scanCode = (ushort)MapVirtualKeyW(virtualKey, 0);
        return new Input
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                ScanCode = scanCode,
                Flags = up ? KeyEventScanCode | KeyEventKeyUp : KeyEventScanCode,
            },
        };
    }

    private static Input Mouse(uint flags, int data = 0)
        => new()
        {
            Type = InputMouse,
            Mouse = new MouseInput { Flags = flags, Data = data },
        };

    private static void Send(Input[] inputs)
        => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
}
