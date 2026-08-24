using System.Runtime.Versioning;

namespace PoEformance.App;

/// <summary>
/// Plays the audible cue a rule asked for, without holding up the read that asked.
/// </summary>
/// <remarks>
/// <c>Console.Beep</c> BLOCKS for the whole duration it is given. The rule engine runs on the
/// reader thread, which produces a snapshot roughly every 33 ms, so a 120 ms cue played inline
/// would stall four reads - the overlay's markers would visibly stutter every time a rule made
/// a noise, and the damage meter would file a fifth of a second of the fight as one sample.
/// The reference plugin calls it straight from its render loop, where the same cost lands on
/// the frame rate instead.
///
/// So it goes to the thread pool, and AT MOST ONE is in flight. Without that cap a rule with a
/// short cooldown queues cues faster than they can play, and the queue outlives the thing it
/// was warning about - a beep about a monster that died ten seconds ago, then another, and
/// another. Dropping the overlapping ones is the honest behaviour: a cue nobody could
/// distinguish from the one already sounding carries no information.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SoundCue
{
    private static int _sounding;

    /// <summary>Plays a cue, or does nothing if one is already sounding.</summary>
    public static void Play(int pitch, int milliseconds)
    {
        if (Interlocked.CompareExchange(ref _sounding, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                Console.Beep(state.Pitch, state.Ms);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or IOException)
            {
                // No sound device, or a machine that refuses the call. A rule making no noise
                // is not worth ending a session over, and the settings already clamp the range
                // - so anything reaching here is the machine's answer rather than the user's
                // mistake.
            }
            finally
            {
                Volatile.Write(ref _sounding, 0);
            }
        },
        (Pitch: pitch, Ms: milliseconds),
        preferLocal: false);
    }
}
