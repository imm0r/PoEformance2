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
/// HOW LONG TO HOLD IS A NUMBER, and it was briefly not. The keys have to stay down until the
/// game has read them, and for a while this waited for the game to SAY so - the player's own
/// animation id turning into one the game calls a dodge roll. The premise was right and the
/// signal was not: measured in play (2026-08-29) that arrives in 49-62 ms, three frames at
/// 60 fps, on a machine where a flat 20 ms hold was already shown to work. The game reads the
/// keys long before the animation id turns over, so waiting for it held three times longer than
/// necessary and bought nothing. It was removed rather than kept as a switch nobody would use.
/// See EvasionSettings.SteerHoldMs for what the number is sized against.
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
    /// <summary>1 while a sequence owns the movement keys.</summary>
    private static int _running;

    /// <summary>Whether a roll is being performed right now.</summary>
    public static bool Busy => Volatile.Read(ref _running) != 0;

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
    /// <param name="holdMs">How long to hold them. See EvasionSettings.SteerHoldMs.</param>
    public static void Roll(
        ushort dodgeKey, IReadOnlyList<ushort> steer, IReadOnlyList<ushort> movement, int holdMs)
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

        var thread = new Thread(() => Sequence(dodgeKey, wanted, held, holdMs))
        {
            IsBackground = true,
            Name = "dodge-steer",
        };

        thread.Start();
    }

    private static void Sequence(ushort dodgeKey, ushort[] steer, ushort[] held, int holdMs)
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
            //
            //    SLEEP IS QUANTISED to the per-process system timer - 15.6 ms unless something in
            //    THIS process has raised it, and since Windows 10 2004 the game raising its own
            //    does nothing for us. So the hold is a FLOOR rather than a duration: a Sleep(20)
            //    lands somewhere between 20 and 31 ms. That is the safe direction to be wrong in,
            //    and it is worth knowing when reading the numbers in EvasionSettings.SteerHoldMs.
            if (holdMs > 0)
            {
                Thread.Sleep(holdMs);
            }
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
