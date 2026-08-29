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
/// MONSTERS ARE SETTLED, and they were the whole point. Two sessions had measured all of this
/// on the player only; <c>tests/fixtures/session-2026-08-monsters.rec</c> put it to 54 monsters
/// in a real fight and the game answered twice over:
///
///  - 210 completed monster moves ended on the destination this reads, 185 of them EXACTLY -
///    median miss 0.00 world units, worst 10.87, which is one cell. Across 39 distinct monsters
///    of eleven kinds, none of which anybody had aimed a probe at.
///  - Over 1649 monster SKILL actions the direction from origin to target agrees with
///    Render.RotationCurrent to a median of 1.6 degrees, 94% inside thirty. That is a field
///    found a month earlier by a different method on a different recording, so it is
///    corroboration and not consistency.
///
/// MONSTERS DO NOT FACE WHERE THEY WALK, which is the same lesson the player taught and worth
/// stating because the obvious check gets it backwards. Over 7597 monster MOVE actions the
/// bearing to the destination sits 25.9 degrees off the facing, and measuring it from where the
/// monster currently stands makes it WORSE (32.5) rather than better. A monster faces its
/// quarry and walks around obstacles, so the facing says what it is aimed at and this says where
/// it is going - two different questions, and an evasion warning wants both.
/// </remarks>
public sealed class ActionReader
{
    /// <summary>
    /// The bit that means a SKILL is running. ActionId is a flags word, not an enum.
    /// </summary>
    /// <remarks>
    /// FOUND BY WATCHING MONSTERS (session-2026-08-monsters.rec, 4659 acting sightings across
    /// 54 monsters), where the first two sessions had shown only the player's two values. The
    /// ids that turned up are 0x0002, 0x1080, 0x0200, 0x1000, 0x1480, 0x1200 and 0x1002, and
    /// they decompose cleanly: every id with this bit had the SKILL slot filled, every id with
    /// <see cref="MoveBit"/> had the MOVE slot filled, and 0x1002 - which has both - had both.
    /// Read as whole numbers instead, five of those seven are unrecognised values; read as
    /// flags, they are two facts and some detail nobody needs yet.
    /// </remarks>
    private const short SkillBit = 0x0002;

    /// <summary>The bit that means a MOVE is running, on the same evidence.</summary>
    /// <remarks>
    /// 0x1080 is the ordinary one (3807 sightings, animation Run every time). 0x1000 WITHOUT
    /// the 0x0080 beside it reads animation Idle every time (11 sightings) - the same
    /// "committed but not yet moving" moment the player session found, showing up here as a
    /// distinct id. Both are moves and both carry a destination, which is why the bit and not
    /// the number is what this tests.
    /// </remarks>
    private const short MoveBit = 0x1000;

    private readonly IMemoryReader _reader;
    private readonly int _actionId;
    private readonly int _skillActionPtr;
    private readonly int _moveActionPtr;
    private readonly int _currentSkillPtr;
    private readonly int _animationId;
    private readonly int _targetGrid;
    private readonly int _originGrid;
    private readonly int _animationRow;

    /// <summary>Animation ids already named, so each costs one resolution per session.</summary>
    private readonly HashSet<int> _resolved = [];

    /// <summary>
    /// How many times an id has been TRIED and failed.
    /// </summary>
    /// <remarks>
    /// A FAILED RESOLUTION IS NOT CACHED AS A SUCCESS - the lesson this project already paid for
    /// once, where a single bad frame cached against a stable pointer silenced a whole session
    /// (see ActionHunt.SampleFrame). An id whose first sighting happened to be unreadable would
    /// otherwise never be named again, which is exactly what happened here: four of six skills
    /// learned, and the two that lost the race stayed anonymous with nothing to say so.
    ///
    /// Bounded rather than infinite, because the other failure is a row that is never readable -
    /// two pointer reads per frame forever, for an answer that is not coming.
    /// </remarks>
    private readonly Dictionary<int, int> _attempts = [];

    /// <summary>How many times to try naming one animation before letting it go.</summary>
    private const int MostAttempts = 8;

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
        _animationRow = wrapper.OffsetOf("AnimationRow");
    }

    /// <summary>
    /// Where to record the names read out of the game, or null to read none.
    /// </summary>
    /// <remarks>
    /// OPT-IN, and a property rather than a constructor argument so the diagnostics and the tests
    /// that only want an action pay nothing. When it is set, every animation id is resolved ONCE
    /// per session - not once per frame, and not only when the shipped table lacks a name.
    ///
    /// Once per id rather than only-when-unknown is deliberate: the shipped table is
    /// hand-maintained and drifts, so an id it already "knows" is exactly the case worth
    /// checking. That is how the ElementalWeakness / InteractLeanWell disagreement turned up,
    /// and it would have stayed invisible under an only-when-missing rule.
    /// </remarks>
    public AnimationNames? Names { get; set; }

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

        // SKILL WINS WHERE BOTH BITS ARE SET, and that ordering is measured rather than
        // preferred: 812 of the 824 skill sightings in the monster session had BOTH wrapper
        // slots filled - a monster casting with a move still pending - and in every one of
        // them the skill slot held the aim point while the move slot held a stale walk. The
        // dangerous half is the skill, so it is also the one to read.
        ActionKind kind = (rawId & SkillBit) != 0 ? ActionKind.Skill
            : (rawId & MoveBit) != 0 ? ActionKind.Move
            : ActionKind.Unknown;

        // The slot that goes with the bit. An Unknown id is looked up in both, because a kind
        // nobody has named still has a wrapper somewhere and its target is the useful part.
        ulong wrapper = kind == ActionKind.Move
            ? _reader.ReadPointer(actorAddress + (ulong)_moveActionPtr)
            : _reader.ReadPointer(actorAddress + (ulong)_skillActionPtr);
        if (wrapper == 0)
        {
            wrapper = _reader.ReadPointer(actorAddress
                + (ulong)(kind == ActionKind.Move ? _skillActionPtr : _moveActionPtr));
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

        LearnAnimationName(wrapper, animation);

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
    /// Asks the GAME what this animation is called, once per id per session.
    /// </summary>
    /// <remarks>
    /// The wrapper points straight at the animation's own row in Data/Balance/Animation.dat and
    /// the row's first field is its id string - see <c>ActionWrapper.AnimationRow</c> in the
    /// schema, where the proof lives (the rows are an array indexed by animation id, stride 106).
    /// Two pointer hops and a short string, and only for an id not yet seen this session.
    ///
    /// WHY IT IS WORTH DOING AT ALL when a table ships with the tool: that table is
    /// hand-maintained, its own header calls a name "a LABEL, never a fact", and the game
    /// disagrees with it. A name feeds <see cref="AnimationNames.KindOf"/>, which is what decides
    /// whether an animation is quiet - so a wrong name is a mis-filtered threat, and a missing
    /// one is an animation the tool can only report as a number.
    ///
    /// Failures are silent by design. This is a label, the caller asked for an action, and an
    /// unreadable row must not turn a good action read into a bad one.
    /// </remarks>
    private void LearnAnimationName(ulong wrapper, int animation)
    {
        if (Names is not AnimationNames names || animation <= 0 || _resolved.Contains(animation))
        {
            return;
        }

        int tried = _attempts.GetValueOrDefault(animation);
        if (tried >= MostAttempts)
        {
            return;
        }

        _attempts[animation] = tried + 1;

        ulong row = _reader.ReadPointer(wrapper + (ulong)_animationRow);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            return;
        }

        ulong id = _reader.ReadPointer(row);
        if (PoEformance.Game.Diagnostics.SkillHunt.TextAt(_reader, id) is not string name)
        {
            return;
        }

        names.Learn(animation, name);
        _resolved.Add(animation);
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
