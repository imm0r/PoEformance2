using System.Diagnostics;
using System.Runtime.Versioning;
using PoEformance.Overlay;

namespace PoEformance.App;

/// <summary>
/// Rolls in a chosen direction: takes the movement keys over for the length of the roll, then
/// hands them back exactly as they were.
/// </summary>
/// <remarks>
/// WHY THE KEYS HAVE TO BE TAKEN OVER AT ALL. The game decides a roll's direction by looking at
/// the movement keys first and the cursor only if none is held - established by the owner testing
/// it, and written up in <c>DodgeRollDirectionTests</c>. So a player holding W while a boss
/// channels a beam at them rolls along W, whatever the tool thinks; the only way to send the roll
/// somewhere else is to be the one holding a key when it starts.
///
/// WINDOWS HAS ONE KEYBOARD AND NO NOTION OF WHO PRESSED WHAT. There is a single up/down state
/// per key: a synthesised W-up is not "the tool's W-up", it is W being up, and the player's
/// finger on the physical key does not put it back. That is what makes this a SEQUENCE rather
/// than an override - release, steer, roll, restore - and what makes the restore the delicate
/// part rather than an afterthought. <see cref="PhysicalKeys"/> is what makes it exact.
///
/// WHY NOT BlockInput, which is the AHK answer to a related question: it stops new input from
/// reaching the queue, it does not clear what is already held - so the player's W stays down
/// through it and the roll still follows W - and it SWALLOWS their release, which puts the
/// keyboard in exactly the stuck state this is trying to avoid.
///
/// ITS OWN THREAD PER ROLL. The sequence has to hold the steering key across at least one of the
/// game's frames, which means waiting, and the caller is the reader loop - waiting there stops
/// the reads that the next decision is made from. One thread per roll is affordable because a
/// roll is bounded by the dodge cooldown, and a roll that arrives while one is running is dropped
/// rather than queued: a stale roll is worse than a missed one, because its direction was chosen
/// for where the character used to be.
///
/// HOW LONG TO HOLD IS ASKED OF THE GAME, not of a setting. The keys have to stay down until the
/// game has read them, and the game says when that was: the roll starts. So the wait polls
/// <c>PoEformance.Features.RollWatch</c> - the player's own animation id turning into one the
/// game calls a dodge roll - and lets go as soon as it does, which is a frame and a bit however
/// long that frame took. <c>SteerHoldMs</c> is what is left when nothing confirms: a CEILING, not
/// a duration. See RollWatch for why this is the answer rather than reading the frame rate.
///
/// THE FOCUS GATE STAYS IN THE PLANNER and is deliberately not repeated here. It checks the game
/// has focus immediately before deciding, so the only exposure left is somebody alt-tabbing
/// inside the few tens of milliseconds a roll takes - and what would land elsewhere is a movement
/// key the player has their finger on anyway. Re-checking mid-sequence would be worse than not:
/// the honest response to losing focus half way is still to finish putting the keyboard back,
/// which is what the finally block does, so a check could only decide to skip the restore and
/// leave a key down.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DodgeSteer
{
    /// <summary>
    /// How often the wait asks whether the roll has started, in milliseconds.
    /// </summary>
    /// <remarks>
    /// One read of one integer, so the cost is a syscall of a few microseconds against a
    /// millisecond of waiting. It is also the SLOP on the measurement the status line shows -
    /// the roll is reported up to this much later than it started.
    /// </remarks>
    private const double PollMs = 1.0;

    /// <summary>How long to keep holding after the game has confirmed, in milliseconds.</summary>
    /// <remarks>
    /// NOT zero, deliberately, and the reason is that the confirmation says a frame has been and
    /// gone - it does not say the game is finished with the keyboard for that frame. Letting go
    /// in the same breath as noticing would bet on the animation id being written after every
    /// other use of the input, which nothing here has established. Four milliseconds is a
    /// fifteenth of the default ceiling and buys that whole question off.
    /// </remarks>
    private const double GraceMs = 4.0;

    /// <summary>1 while a sequence owns the movement keys.</summary>
    private static int _running;

    /// <summary>What the last steered roll actually cost, for the status line.</summary>
    private static string _lastRoll = string.Empty;

    /// <summary>Milliseconds the last steered roll held the keys, or -1 before the first.</summary>
    private static int _lastHoldMs = -1;

    /// <summary>Whether a roll is being performed right now.</summary>
    public static bool Busy => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// How long the keys were held for the last steered roll, or -1 before there has been one.
    /// </summary>
    /// <remarks>
    /// Worth having as a number rather than only as a sentence, because it is the closest thing
    /// the tool has to a measurement of the game's frame time: a confirmed hold is one frame plus
    /// <see cref="PollMs"/> plus <see cref="GraceMs"/>. Read it repeatedly and the frame rate is
    /// in there - which is what the owner was after when they asked for the FPS.
    /// </remarks>
    public static int LastHoldMs => Volatile.Read(ref _lastHoldMs);

    /// <summary>A sentence about the last steered roll, or empty before there has been one.</summary>
    public static string LastRoll => Volatile.Read(ref _lastRoll);

    /// <summary>
    /// Presses the dodge key with <paramref name="steer"/> held, then restores the player's keys.
    /// </summary>
    /// <param name="dodgeKey">What to press. Nothing happens when it is 0.</param>
    /// <param name="steer">
    /// The movement keys the roll should go along - one or two of them. Empty rolls unsteered,
    /// which is the old behaviour and a perfectly good one.
    /// </param>
    /// <param name="movement">
    /// All four movement keys, so the ones the player holds can be found and put back.
    /// </param>
    /// <param name="holdMs">
    /// The LONGEST the steering keys may be held, in milliseconds. Reached only when
    /// <paramref name="started"/> never says yes. See EvasionSettings.SteerHoldMs.
    /// </param>
    /// <param name="started">
    /// Asks the game whether the roll has begun; the keys go back the moment it says yes. Null
    /// when nothing can answer - a missing animation table, a reader that cannot be read from
    /// this thread - and then the hold is the flat <paramref name="holdMs"/> it always was.
    ///
    /// CALLED FROM THE SEQUENCE THREAD, roughly once a millisecond, so it must be cheap and it
    /// must be safe off the reader loop. One <c>ReadProcessMemory</c> of four bytes is both.
    /// </param>
    public static void Roll(
        ushort dodgeKey,
        IReadOnlyList<ushort> steer,
        IReadOnlyList<ushort> movement,
        int holdMs,
        Func<bool>? started = null)
    {
        ArgumentNullException.ThrowIfNull(steer);
        ArgumentNullException.ThrowIfNull(movement);

        if (dodgeKey == 0)
        {
            return;
        }

        // Nothing to take over: the plain press, on the calling thread, exactly as before
        // steering existed. No thread, no wait, no keys touched.
        if (steer.Count == 0)
        {
            InputSender.Press(dodgeKey);
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        // SNAPSHOT BEFORE ANYTHING IS SENT, on this thread, because the first synthesised key-up
        // destroys the answer - see PhysicalKeys. Captured into locals so the sequence thread
        // cannot read a later, tool-influenced state.
        ushort[] keys = [.. movement.Where(k => k != 0).Distinct()];
        ushort[] held = [.. keys.Where(k => ScreenInput.IsDown(k))];
        ushort[] wanted = [.. steer.Where(k => k != 0).Distinct()];

        var thread = new Thread(() => Sequence(dodgeKey, wanted, held, holdMs, started))
        {
            IsBackground = true,
            Name = "dodge-steer",
        };

        thread.Start();
    }

    private static void Sequence(
        ushort dodgeKey, ushort[] steer, ushort[] held, int holdMs, Func<bool>? started)
    {
        try
        {
            // 1. Let go of what the player is holding, except any key the roll wants anyway -
            //    releasing and re-pressing that one would be a gap the game could sample in.
            foreach (ushort key in held)
            {
                if (!steer.Contains(key))
                {
                    InputSender.Release(key);
                }
            }

            // 2. Take hold of the direction. Before the roll, so the game sees the key down when
            //    it comes to resolve which way the roll goes.
            foreach (ushort key in steer)
            {
                if (!held.Contains(key))
                {
                    InputSender.Hold(key);
                }
            }

            // 3. The roll.
            InputSender.Press(dodgeKey);

            // 4. Stay held across at least one of the game's frames. The game samples input per
            //    frame, so keys sent and released inside one of them can be missed entirely.
            Wait(holdMs, started);
        }
        finally
        {
            // 5. Put the keyboard back the way the PLAYER is holding it now - which is not
            //    necessarily how they were holding it in step 1, and that difference is the
            //    whole reason PhysicalKeys exists. In the finally block because a keyboard left
            //    with a key down is the one outcome here that is worse than not rolling.
            Restore(steer, held);
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Holds until the game has taken the keys, or until <paramref name="holdMs"/> runs out.
    /// </summary>
    /// <remarks>
    /// WHY THIS SPINS INSTEAD OF SLEEPING. Thread.Sleep is quantised to the system timer, which
    /// is 15.6 ms unless somebody in THIS process has raised it - since Windows 10 2004 the
    /// resolution is per-process, so the game raising its own does nothing for us. A Sleep(1)
    /// poll would therefore be a Sleep(16) poll and would throw away the resolution the whole
    /// idea depends on. (It also means the flat holds measured so far are floors, not durations:
    /// a Sleep(20) was somewhere between 20 and 31 ms, which is worth knowing when reading the
    /// numbers in EvasionSettings.SteerHoldMs.)
    ///
    /// The spin costs a core for as long as the hold, and the hold is tens of milliseconds once
    /// per dodge cooldown - under 2% of one core at the default. That is a fair price for the
    /// keys going back to the player a frame after the roll instead of three.
    /// </remarks>
    private static void Wait(int holdMs, Func<bool>? started)
    {
        // Nothing can answer, so there is nothing to wait FOR: the flat sleep this has always
        // been. Sleep rather than spin, because a wait with no question to ask is just a wait.
        if (started is null)
        {
            if (holdMs > 0)
            {
                Thread.Sleep(holdMs);
            }

            Report(holdMs, -1, watched: false);
            return;
        }

        var clock = Stopwatch.StartNew();
        double deadline = holdMs;
        double confirmed = -1;
        double next = 0;

        while (true)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (now >= deadline)
            {
                break;
            }

            if (confirmed < 0 && now >= next)
            {
                next = now + PollMs;
                if (started())
                {
                    // The game has the keys. Everything after this is the grace, and the
                    // deadline can only come DOWN - a confirmation must never extend the hold.
                    confirmed = now;
                    deadline = Math.Min(deadline, now + GraceMs);
                }
            }

            // Short enough that the clock is checked far more often than PollMs, so the poll
            // cadence comes from the Stopwatch rather than from how fast this machine spins.
            Thread.SpinWait(64);
        }

        Report(
            (int)Math.Round(clock.Elapsed.TotalMilliseconds),
            (int)Math.Round(confirmed),
            watched: true);
    }

    /// <summary>Records what the hold cost, for the status line.</summary>
    /// <remarks>
    /// Three outcomes and not two, because "nobody was watching" and "watched and never
    /// confirmed" are different things and only the second is worth a second look.
    /// </remarks>
    private static void Report(int heldMs, int confirmedMs, bool watched)
    {
        Volatile.Write(ref _lastHoldMs, heldMs);
        Volatile.Write(
            ref _lastRoll,
            !watched ? $"held {heldMs} ms"
                : confirmedMs >= 0 ? $"roll seen after {confirmedMs} ms"

                    // Not necessarily a failure: chain-rolling never changes the animation id,
                    // so this is also what a second roll out of a first one looks like. It IS
                    // the case where the ceiling is doing the work, which is worth seeing.
                    : $"held {heldMs} ms, roll unconfirmed");
    }

    /// <summary>Leaves exactly the keys the player is holding down, and no others.</summary>
    /// <remarks>
    /// WITHOUT THE HOOK this falls back to restoring the snapshot, because
    /// <c>GetAsyncKeyState</c> can no longer answer: the tool's own key-up is indistinguishable
    /// from a finger lifting, so every key would read as released and none would be restored -
    /// which is the failure the owner asked about, arriving by the other road. The snapshot is
    /// wrong only for a key let go of during the roll itself, and wrong for a moment.
    /// </remarks>
    private static void Restore(ushort[] steer, ushort[] held)
    {
        bool exact = PhysicalKeys.Watching;

        // Everything the tool is holding that the player is not - the steering keys, and only
        // those, since step 1 released the rest.
        foreach (ushort key in steer)
        {
            bool player = exact ? PhysicalKeys.IsDown(key) : held.Contains(key);
            if (!player)
            {
                InputSender.Release(key);
            }
        }

        // ...and everything the player is holding that the tool is not.
        foreach (ushort key in held)
        {
            if (!steer.Contains(key) && (!exact || PhysicalKeys.IsDown(key)))
            {
                InputSender.Hold(key);
            }
        }
    }
}
