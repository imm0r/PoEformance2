using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>
/// Recognises the moment the game has actually started the dodge roll it was asked for.
/// </summary>
/// <remarks>
/// WHAT IT IS FOR. Steering a roll means holding a movement key across the frame in which the
/// game resolves the roll's direction, and the only reason <c>EvasionSettings.SteerHoldMs</c>
/// exists is that nothing told the tool when that frame had been and gone - so it held for a
/// guessed length of time instead. This is the thing that tells it. The animation id in the
/// player's Actor component turns into a roll the moment the game commits to one, and a commit
/// is exactly when the direction is read; so once this says yes, the keys have done their job
/// and can go straight back to the player.
///
/// WHY NOT READ THE FRAME RATE, which is the obvious way to ask the same question and the one
/// the owner suggested. Two reasons, and the second is the real one. First, nothing supplies it:
/// GameHelper2's FPS is its own overlay's (<c>ImGui.GetIO().Framerate</c>), the AHK tool's is its
/// own profiler's, and no reference reads a frame rate out of the game - so it would be a hunt
/// with no reference to check the answer against. Second, and this is what settles it, the frame
/// rate would only ever be a PROXY for "has the game seen the keys yet", and the game answers
/// that question itself, exactly, for free. A hold derived from a measured 62 fps is still a
/// guess about what the game did with the input; the roll starting is the input having been used.
///
/// It also self-corrects for the thing a frame-rate reading would miss: a stutter, a load spike,
/// one frame that took 200 ms. A number derived from an average frame rate is wrong precisely
/// when the frames are not average, which is when the tool is most likely to be rolling.
///
/// THE WORD IS "dodgeroll" AND THAT IS DELIBERATE. The game's table has fourteen animations whose
/// names contain it - DodgeRoll, DodgeRollBack, DodgeRollSprint, FloatDodgeRoll, CannonDodgeRoll,
/// DodgeRollMoveCancel and the rest - which is why this asks
/// <see cref="AnimationNames.IdsNamed"/> instead of listing ids. Matching on "roll" alone would
/// also catch RollingMagma, a spell, and a spell being cast is not a roll having started.
///
/// COMPARING AGAINST <see cref="Before"/> is what stops a roll already under way from confirming
/// the next one instantly. Chain-rolling holds the same animation id throughout, so the id never
/// changes, this never says yes, and the caller falls back to holding for the full ceiling -
/// which is the behaviour that has always been there. Every way this can fail lands on that same
/// fallback, which is why it is safe to put in front of a system that already works.
/// </remarks>
/// <param name="Before">
/// The player's animation id read immediately before the dodge key was pressed.
/// </param>
/// <param name="RollIds">The ids the game itself calls a dodge roll.</param>
public readonly record struct RollWatch(int Before, IReadOnlySet<int> RollIds)
{
    /// <summary>The word the game spells its roll animations with.</summary>
    public const string Word = "dodgeroll";

    /// <summary>Nothing to watch for - <see cref="Started"/> is always false.</summary>
    public static RollWatch None { get; } = new(-1, new HashSet<int>());

    /// <summary>What to watch for, given the table and the animation running right now.</summary>
    public static RollWatch For(AnimationNames? names, int before)
        => names is null ? None : new RollWatch(before, names.IdsNamed(Word));

    /// <summary>
    /// Whether this is worth polling at all. False when the table has no roll in it, which
    /// means the caller should hold for the full time rather than wait for a yes that cannot come.
    /// </summary>
    public bool CanWatch => RollIds.Count > 0;

    /// <summary>Whether <paramref name="animation"/> is the roll having started.</summary>
    public bool Started(int animation)
        => animation >= 0 && animation != Before && RollIds.Contains(animation);
}
