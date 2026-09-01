using System.Runtime.Versioning;
using PoEformance.Features;

namespace PoEformance.App;

/// <summary>
/// Takes the pointer, confirms it landed on the intended monster, acts, and puts it back.
/// </summary>
/// <remarks>
/// WHY THE POINTER HAS TO BE TAKEN AT ALL. A cull is a threshold rule - "a rare within range is
/// at or below its execute share" - but the skill that performs it has to be POINTED at the
/// monster. Without this the rule fired its key at wherever the pointer happened to be, which
/// from outside looks exactly like the feature working: the key goes out, the cooldown starts,
/// and nothing dies.
///
/// THE CONFIRMATION IS THE WHOLE DESIGN, not a safety net bolted on. Placing the cursor is a
/// projection, and a projection can miss - the monster moved between the read and the move, the
/// point was off screen, the player was mid-swipe and dragged it off. So the sequence asks the
/// GAME what is under the pointer now, through the same hovered-entity slot the browser reads,
/// and presses only when that is the entity the decision named. The failure mode is therefore a
/// cull that did not happen, never a skill cast into empty floor.
///
/// HOW LONG THE POINTER IS OURS. Single-digit milliseconds: place, let the game read the
/// position, confirm, act, restore. Measured against the owner's own play, a hover lasts a
/// median 93 ms and a quarter of them do not survive one 44 ms read - so a window of tens of
/// milliseconds would be pulled off target often, and one of a few is not. That is what makes
/// the short dwell worth the extra reads rather than waiting for the next snapshot.
///
/// WHY NOT BlockInput, which was the first suggestion. <see cref="DodgeSteer"/> already wrote
/// down why it is refused there: it does not clear what is already held and it SWALLOWS the
/// player's release, which leaves a key stuck down. Over a window this short it buys nothing
/// against that risk.
///
/// ITS OWN THREAD PER SEQUENCE, for the same reason the dodge roll has one: the sequence waits
/// on the game, and the caller is the reader loop - waiting there stops the reads that the next
/// decision is made from. A sequence that arrives while one is running is DROPPED rather than
/// queued: a stale aim points at where a monster used to be.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class AimSequence
{
    /// <summary>How long to leave the pointer in place before asking what is under it.</summary>
    /// <remarks>
    /// The game samples the pointer on its own frames, so this has to cover at least one of
    /// them. Small enough that the player cannot meaningfully move in it, large enough that a
    /// 60 fps client has ticked - and if it turns out to be short in play, this is the number
    /// to raise. Nothing here can measure it without the game.
    /// </remarks>
    public const int SettleMs = 8;

    private static int _running;

    /// <summary>Whether a sequence owns the pointer right now.</summary>
    public static bool Busy => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// Runs one aim-confirm-act-restore sequence, off the caller's thread.
    /// </summary>
    /// <param name="target">Where to put the pointer, in screen pixels.</param>
    /// <param name="expected">The entity that should be hovered once it is there.</param>
    /// <param name="hovered">
    /// Re-reads the game's hovered-entity slot. Called from the sequence's own thread, so it
    /// must not touch anything the reader loop owns exclusively.
    /// </param>
    /// <param name="act">What to do once the target is confirmed.</param>
    /// <param name="report">
    /// Told how it went at EVERY step, including the ones that worked. Success is reported for
    /// the same reason the failures are: the only way to tell "the confirmation is rejecting
    /// every aim" from "the rule never held" is to see the sequence say something. Takes a
    /// wording and a measurement, which end up in their own columns.
    /// </param>
    public static void Run(
        (int X, int Y) target,
        ulong expected,
        Func<ulong> hovered,
        Action act,
        Action<string, string> report)
    {
        ArgumentNullException.ThrowIfNull(hovered);
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(report);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            report("skipped, still aiming at the last one", string.Empty);
            return;
        }

        var thread = new Thread(() => Sequence(target, expected, hovered, act, report))
        {
            IsBackground = true,
            Name = "aim",
        };

        thread.Start();
    }

    private static void Sequence(
        (int X, int Y) target,
        ulong expected,
        Func<ulong> hovered,
        Action act,
        Action<string, string> report)
    {
        (int X, int Y)? was = InputSender.CursorAt();
        try
        {
            if (was is null)
            {
                report("could not read where the pointer is", string.Empty);
                return;
            }

            if (!InputSender.MoveCursor(target.X, target.Y))
            {
                report("could not move the pointer", $"{target.X},{target.Y}");
                return;
            }

            // Its own line, and the pixel with it. Between the decision and the hover check
            // sit two things that can each be wrong on their own - the projection that turned
            // a world point into this pixel, and the pointer actually going there - and one
            // line covering both cannot say which.
            report("pointer moved", $"{target.X},{target.Y}");

            Thread.Sleep(SettleMs);

            ulong under = hovered();
            if (under != expected)
            {
                // The honest outcome, and the one this exists to produce. Named rather than
                // silent: "the cursor did not land" and "the rule never held" are different
                // problems, and only one of them is fixed by changing the rule.
                //
                // The detail carries what the game says IS hovered, which is the difference
                // between "the projection is off by a little" and "it is off by a screen".
                report(
                    under == 0 ? "hover check: nothing there" : "hover check: something else",
                    under == 0 ? $"wanted #{expected & 0xFFFFFFFF:x8}" : $"#{under & 0xFFFFFFFF:x8}");
                return;
            }

            report("hover check: confirmed", $"#{under & 0xFFFFFFFF:x8}");
            act();
        }
        finally
        {
            // Always, on every path - a pointer left somewhere the player did not put it is
            // worse than any missed cull. Restored before the flag clears, so a sequence that
            // arrives immediately after cannot read a half-restored cursor as the player's.
            if (was is (int x, int y))
            {
                InputSender.MoveCursor(x, y);
            }

            Volatile.Write(ref _running, 0);
        }
    }
}
