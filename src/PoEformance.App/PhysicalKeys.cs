using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PoEformance.Overlay;

namespace PoEformance.App;

/// <summary>
/// Which keys the PLAYER is holding down, as opposed to which keys Windows thinks are down.
/// </summary>
/// <remarks>
/// THE TWO ARE NOT THE SAME AS SOON AS THIS TOOL SENDS ANYTHING, and that difference is the whole
/// reason this exists. <c>GetAsyncKeyState</c> answers about the input queue's state, and
/// <c>SendInput</c> writes to that queue: send a key-up for W while the owner is physically
/// holding W and every API in Windows will tell you W is up. There is no second question to ask.
///
/// WHY THAT MATTERS HERE. Steering a dodge roll means releasing the movement key the player is
/// holding, holding the one the escape needs, rolling, and then PUTTING BACK what they were
/// holding - the owner asked for exactly this, in as many words: they must not have to physically
/// let go of W and press it again before the game responds to it. Restoring blind is the obvious
/// implementation and it has a failure that costs a character: if they let go of W during the few
/// tens of milliseconds the roll takes, their release is spent on a key that was already up, and
/// the restore then presses W down with nothing left to release it. The character runs forwards
/// until they happen to tap W again.
///
/// A LOW-LEVEL KEYBOARD HOOK IS THE ONLY THING THAT CAN TELL THEM APART, because it sees each
/// event before the queue does and Windows marks the synthesised ones with LLKHF_INJECTED. This
/// is also how the AHK tool answers the same question - its <c>GetKeyState(key, "P")</c> is
/// backed by precisely this hook - so it is the reference implementation and not an invention.
///
/// WHAT IT IS NOT: it observes, and it never swallows. Every event is passed straight on with
/// <c>CallNextHookEx</c> and the array write in between is a single store. That matters more than
/// tidiness: Windows silently removes a low-level hook whose callback overruns
/// <c>LowLevelHooksTimeout</c>, so a slow one does not fail loudly, it just stops working.
///
/// INJECTED MEANS "NOT A FINGER", including input injected by something other than this tool.
/// That is the behaviour wanted rather than a limitation - the question being asked is what the
/// PLAYER is holding, and another tool's synthetic W is not it.
/// </remarks>
[SupportedOSPlatform("windows")]
public static unsafe partial class PhysicalKeys
{
    private const int LowLevelKeyboard = 13;

    /// <summary>LLKHF_INJECTED - the event came from SendInput, not from a keyboard.</summary>
    private const uint Injected = 0x00000010;

    private const nint KeyDown = 0x0100;
    private const nint SysKeyDown = 0x0104;
    private const nint KeyUp = 0x0101;
    private const nint SysKeyUp = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(
        int type, delegate* unmanaged<int, nint, nint, nint> callback, nint module, uint thread);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetModuleHandleW(nint name);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Message message, nint window, uint first, uint last);

    /// <summary>
    /// One byte per virtual key, written only by the hook and read by everything else.
    /// </summary>
    /// <remarks>
    /// A byte array rather than bools with a lock: the hook thread writes one element and the
    /// reader thread reads one element, which on every architecture .NET supports is atomic
    /// without help. A lock here would put the reader thread's scheduling inside a callback that
    /// Windows times out.
    /// </remarks>
    private static readonly byte[] Down = new byte[256];

    private static nint _hook;

    /// <summary>Whether the hook is installed and the answers are real.</summary>
    /// <remarks>
    /// Public because the caller has to DEGRADE rather than fail when it is false, and it can
    /// only do that if it knows. See <see cref="DodgeSteer"/>, which falls back to restoring
    /// whatever was held when it started.
    /// </remarks>
    public static bool Watching => Volatile.Read(ref _hook) != 0;

    /// <summary>True while a finger is on the key.</summary>
    public static bool IsDown(ushort virtualKey)
        => virtualKey != 0 && Volatile.Read(ref _hook) != 0 && Down[virtualKey] != 0;

    /// <summary>
    /// Starts watching, on a thread of its own. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// ITS OWN THREAD BECAUSE A LOW-LEVEL HOOK NEEDS A MESSAGE LOOP: the system delivers the
    /// callback to the thread that installed the hook, and only while that thread is pumping
    /// messages. The reader thread is in a read loop and the render thread belongs to the
    /// overlay, so neither can host it - and a hook installed on a thread that never pumps is
    /// the quiet kind of broken, since it installs successfully and then never fires.
    ///
    /// Nothing removes it. The thread lives as long as the process, the hook goes with it, and
    /// an unhook path would exist only to be got wrong on the way out.
    /// </remarks>
    public static void Watch()
    {
        if (Volatile.Read(ref _hook) != 0)
        {
            return;
        }

        var thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "physical-keys",

            // Above normal: this thread's only job is to answer a callback Windows will
            // abandon the hook over if it is late.
            Priority = ThreadPriority.AboveNormal,
        };

        thread.Start();
    }

    private static void Pump()
    {
        nint hook = SetWindowsHookExW(LowLevelKeyboard, &Observe, GetModuleHandleW(0), 0);
        if (hook == 0)
        {
            // Nothing to report and nothing to retry: a machine that refuses the hook refuses it
            // for the session. The caller reads Watching and does the lesser thing.
            return;
        }

        // SEEDED FROM THE OS BEFORE THE HOOK IS ANNOUNCED, because a hook only ever learns about
        // keys that MOVE: a key already held when it was installed has no event to have been
        // seen, and would read as up until the player let go of it. That is not academic - this
        // is installed the first tick steering is switched on, which can be mid-fight with a
        // finger already on W, and the cost would be exactly the failure the class exists to
        // prevent, on the first roll of the session.
        //
        // Safe to seed from GetAsyncKeyState here, and only here: nothing this tool sends is
        // outstanding at this moment. Every key it has sent so far was a complete press and
        // release, so the queue's state and the player's fingers still agree.
        for (int key = 1; key < Down.Length; key++)
        {
            Down[key] = ScreenInput.IsDown(key) ? (byte)1 : (byte)0;
        }

        Volatile.Write(ref _hook, hook);

        while (GetMessageW(out Message message, 0, 0, 0) > 0)
        {
            // Nothing to dispatch - this thread owns no window. The loop exists so the system
            // has somewhere to deliver the hook callback.
            _ = message;
        }
    }

    /// <summary>The hook itself. Runs on the pump thread, for every key event on the machine.</summary>
    [UnmanagedCallersOnly]
    private static nint Observe(int code, nint wParam, nint lParam)
    {
        // A negative code means "not for you, pass it on" and is not optional.
        if (code >= 0 && lParam != 0)
        {
            KeyboardEvent key = *(KeyboardEvent*)lParam;
            if ((key.Flags & Injected) == 0 && key.VirtualKey < 256)
            {
                if (wParam == KeyDown || wParam == SysKeyDown)
                {
                    Down[key.VirtualKey] = 1;
                }
                else if (wParam == KeyUp || wParam == SysKeyUp)
                {
                    Down[key.VirtualKey] = 0;
                }
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }
}
