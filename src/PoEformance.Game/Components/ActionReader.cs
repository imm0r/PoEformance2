using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Game.Components;

/// <summary>What sort of thing an actor has committed to.</summary>
/// <remarks>
/// From the Actor's own ActionId, NOT from the animation - which is the point of having it.
/// An action is committed before, and sometimes without, any animation to show it.
/// </remarks>
public enum ActionKind
{
    /// <summary>Nothing running, or nothing readable.</summary>
    None,

    /// <summary>A skill: the target is where it is aimed.</summary>
    Skill,

    /// <summary>A move: the target is where the actor is going.</summary>
    Move,

    /// <summary>
    /// An action whose id this build has no name for.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="None"/>, and for the same reason
    /// <see cref="AnimationNames.IsQuiet"/> asks its question the safe way round: the session
    /// that found these values contained a walk and five casts, so the id is certainly a flags
    /// word with bits nobody here has seen. Folding an unknown id into "nothing is happening"
    /// would make every unseen action silently harmless, which is the one failure a danger
    /// warning must not have.
    /// </remarks>
    Unknown,
}

/// <summary>
/// What an actor is doing right now, and where it is aimed - in world coordinates.
/// </summary>
/// <param name="RawId">The ActionId as read, so an unrecognised value can still be shown.</param>
/// <param name="AnimationId">
/// The animation alongside it, or -1 when unread. Carried together because the interesting
/// cases are where the two disagree.
/// </param>
/// <param name="SkillAddress">
/// The skill object being cast, or 0. Identifies WHICH skill without naming it: it is
/// one-to-one with the animation id across everything measured so far.
/// </param>
public readonly record struct ActorAction(
    ActionKind Kind,
    int RawId,
    float TargetX,
    float TargetY,
    float OriginX,
    float OriginY,
    ulong SkillAddress,
    int AnimationId)
{
    /// <summary>Nothing is running.</summary>
    public static ActorAction None { get; } = new(ActionKind.None, 0, 0, 0, 0, 0, 0, -1);

    /// <summary>Whether this action carries a place worth drawing.</summary>
    public bool HasTarget => Kind is not ActionKind.None;

    /// <summary>How far the action reaches, in world units.</summary>
    public float Reach => MathF.Sqrt(((TargetX - OriginX) * (TargetX - OriginX))
                                     + ((TargetY - OriginY) * (TargetY - OriginY)));
}

/// <summary>
/// Reads the Actor component's action: its kind, its target and where it started.
/// </summary>
/// <remarks>
/// THE FIELDS THIS READS WERE FOUND BY <c>--actionhunt</c>, and the evidence is in
/// <c>ActionFieldsTests</c> against <c>tests/fixtures/session-2026-08-actions.rec</c> - a real
/// session in which a destination read a second and a half early landed within one grid cell
/// of where the character then stopped. Every offset comes from the schema; the arithmetic
/// here is only the grid-to-world conversion.
///
/// TWO SLOTS, ONE MECHANISM. A skill action and a move action live in adjacent pointer slots
/// and are never both set, so this reads whichever is there and reports which it was.
///
/// COORDINATES COME OUT IN WORLD UNITS, converted here rather than at each call site. The game
/// stores this target in integer GRID cells, unlike everything else an overlay handles - and a
/// grid pair mistaken for a world position lands a marker a hundred times too close to the
/// map's origin, which looks like a projection bug rather than like a unit mix-up.
///
/// WHAT IS NOT ESTABLISHED, and matters most for the feature this serves: every measurement
/// behind these offsets was taken from the PLAYER's actor. Monsters carry the same component,
/// so the same offsets should hold - "should" being exactly the word AnimationNames uses about
/// the animation table for the same reason. Until a recording settles it, a monster reading
/// nonsense here is a possibility to check for rather than a surprise.
/// </remarks>
public sealed class ActionReader
{
    /// <summary>ActionId while a skill is running.</summary>
    private const short SkillAction = 2;

    /// <summary>ActionId while a move is running.</summary>
    private const short MoveAction = 4224;

    private readonly IMemoryReader _reader;
    private readonly int _actionId;
    private readonly int _skillActionPtr;
    private readonly int _moveActionPtr;
    private readonly int _currentSkillPtr;
    private readonly int _animationId;
    private readonly int _targetGrid;
    private readonly int _originGrid;

    public ActionReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef actor = schema.Structs["Actor"];
        _actionId = actor.OffsetOf("ActionId");
        _skillActionPtr = actor.OffsetOf("SkillActionPtr");
        _moveActionPtr = actor.OffsetOf("MoveActionPtr");
        _currentSkillPtr = actor.OffsetOf("CurrentSkillPtr");
        _animationId = actor.OffsetOf("AnimationId");

        StructDef wrapper = schema.Structs["ActionWrapper"];
        _targetGrid = wrapper.OffsetOf("TargetGrid");
        _originGrid = wrapper.OffsetOf("OriginGrid");
    }

    /// <summary>
    /// Reads the action off an Actor component address. Returns
    /// <see cref="ActorAction.None"/> when nothing is running or nothing could be read.
    /// </summary>
    /// <remarks>
    /// A missing wrapper does NOT fall back to None: an action whose id says something is
    /// running keeps its kind and reports a zero reach, because "it is doing something and I
    /// could not read where" is a different claim from "it is doing nothing", and only one of
    /// them is safe to draw nothing for.
    /// </remarks>
    public ActorAction Read(ulong actorAddress)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(actorAddress))
        {
            return ActorAction.None;
        }

        if (!_reader.TryRead(actorAddress + (ulong)_actionId, out short rawId) || rawId == 0)
        {
            return ActorAction.None;
        }

        ActionKind kind = rawId switch
        {
            SkillAction => ActionKind.Skill,
            MoveAction => ActionKind.Move,
            _ => ActionKind.Unknown,
        };

        // Whichever slot is set. An Unknown id is looked up in both, because a kind nobody
        // has named still has a wrapper somewhere and its target is the useful part.
        ulong wrapper = _reader.ReadPointer(actorAddress + (ulong)_skillActionPtr);
        if (wrapper == 0)
        {
            wrapper = _reader.ReadPointer(actorAddress + (ulong)_moveActionPtr);
        }

        int animation = _reader.TryRead(actorAddress + (ulong)_animationId, out int read) ? read : -1;
        ulong skill = kind == ActionKind.Move
            ? 0
            : _reader.ReadPointer(actorAddress + (ulong)_currentSkillPtr);

        if (wrapper == 0)
        {
            return new ActorAction(kind, rawId, 0, 0, 0, 0, skill, animation);
        }

        // One read covering both pairs: the target sits below the origin and the gap is
        // small, so the whole span costs the same call as either pair alone.
        int span = _originGrid - _targetGrid + (2 * sizeof(int));
        Span<byte> block = stackalloc byte[span];
        if (!_reader.TryRead(wrapper + (ulong)_targetGrid, block))
        {
            return new ActorAction(kind, rawId, 0, 0, 0, 0, skill, animation);
        }

        int originAt = _originGrid - _targetGrid;
        return new ActorAction(
            kind,
            rawId,
            ToWorld(BitConverter.ToInt32(block)),
            ToWorld(BitConverter.ToInt32(block[sizeof(int)..])),
            ToWorld(BitConverter.ToInt32(block[originAt..])),
            ToWorld(BitConverter.ToInt32(block[(originAt + sizeof(int))..])),
            skill,
            animation);
    }

    /// <summary>
    /// Grid cells to world units, landing on the cell's CENTRE.
    /// </summary>
    /// <remarks>
    /// The half cell is not a fudge, it is the measurement. Across the four completed move
    /// actions in <c>tests/fixtures/session-2026-08-actions.rec</c> the player settled at
    /// exactly <c>grid + 0.500</c> in both axes every time - the spread over those eight
    /// numbers is 0.4999..0.5000 - so the stored integer names a cell and the actor comes to
    /// rest in the middle of it. With the half cell the destination predicts the arrival to
    /// 0.00 world units; without it every prediction is 7.69 units short, in the same
    /// direction, every time. A residual that never varies is quantisation, and quantisation
    /// is worth correcting rather than tolerating.
    ///
    /// The cell size itself comes from <see cref="MapView.WorldToGrid"/> rather than from a
    /// second copy of 250/23.
    /// </remarks>
    private static float ToWorld(int cells) => (cells + 0.5f) * MapView.WorldToGrid;
}
