using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.Ui;

namespace PoEformance.Game.World;

/// <summary>What an entity is, for colouring and filtering. Derived from its metadata path.</summary>
public enum EntityKind
{
    Unknown,
    Player,
    Monster,
    Chest,
    WorldItem,
    Npc,
    Effect,
    Terrain,

    /// <summary>
    /// Something in flight: an arrow, a fireball, a bolt.
    /// </summary>
    /// <remarks>
    /// LAST on purpose. The alert rules store a kind by its NUMBER, so inserting a value
    /// anywhere else renumbers everything after it and silently re-points every saved rule at
    /// a different kind of entity.
    /// </remarks>
    Projectile,

    /// <summary>
    /// Something the world is furnished with: a bench, a well, a stash, a locker, a lever.
    /// </summary>
    /// <remarks>
    /// The game's own bucket - these are what it files under MiscellaneousObjects - and the
    /// reason a hideout used to be pages of "Unknown". A kind rather than nothing because
    /// "Unknown" is a claim that nobody has looked, and here the path says plainly what it is.
    ///
    /// APPENDED, like every kind after it, for the numbering reason above.
    /// </remarks>
    Object,

    /// <summary>A way between areas: a portal, a transition.</summary>
    Portal,

    /// <summary>
    /// A pet following somebody about.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than a monster, which is the reference's reading too - GameHelper2
    /// lists <c>Metadata/Pet</c> among the paths its monster classification refuses. Two of
    /// them turn up in this project's own delirium recording, classified as nothing.
    /// </remarks>
    Pet,
}

/// <summary>
/// Where a read's time went.
/// </summary>
/// <remarks>
/// One number for a whole read says a frame was expensive and nothing about why, and the
/// lesson the AHK tool wrote down after chasing this twice is that an expensive phase is
/// usually a REDUNDANCY rather than a fundamental cost - a stats component read twice, a scan
/// running every tick that need not. Neither was findable without a breakdown, and both were
/// cheap to fix once seen.
///
/// Measured per PHASE rather than per entity: a timestamp around each stage costs nothing at
/// a handful per read, while one inside the entity loop would be measuring itself.
/// </remarks>
/// <param name="Skipped">Entities the noise filter refused before their components were read.</param>
public readonly record struct ReadCost(
    double TotalMs,
    double EntitiesMs,
    double PlayerMs,
    double TerrainMs,
    double MapsMs,
    int Entities,
    int Skipped,
    int OffScreen = 0)
{
    /// <summary>Everything not attributed to a phase: the chain, the matrix, the area.</summary>
    public double OtherMs => Math.Max(0, TotalMs - EntitiesMs - PlayerMs - TerrainMs - MapsMs);

    /// <summary>A one-line summary for a status readout.</summary>
    public override string ToString()
        => $"{TotalMs:F1} ms = ent {EntitiesMs:F1} ({Entities}, {Skipped} skipped"
           + (OffScreen > 0 ? $", {OffScreen} off screen)" : ")")
           + $"  player {PlayerMs:F1}  terrain {TerrainMs:F1}  maps {MapsMs:F1}  other {OtherMs:F1}";
}

/// <summary>
/// Where an entity is pointing and what it is doing - together, because neither is much use
/// alone.
/// </summary>
/// <remarks>
/// THE PAIR IS THE POINT. The angle says a monster is pointing at you and says nothing about
/// whether that matters, since a monster walking past points at you too. The animation says a
/// slam is starting and says nothing about where it will land. Together they are the only thing
/// in memory that answers "that slam is coming HERE", which is the question this was built for -
/// the game keeps no target pointer at all, it aims by turning (see the schema's
/// RotationCurrent).
/// </remarks>
/// <param name="Angle">
/// Which way it faces now, in radians. Zero is world -Y and it runs the same way round as
/// atan2 - use <see cref="Facing"/> rather than working with it directly.
/// </param>
/// <param name="Turning">
/// Which way it is turning to face: the aim, a step ahead of the pose. Equal to
/// <paramref name="Angle"/> whenever nothing is turning, which is most of the time.
/// </param>
/// <param name="Animation">
/// The game's own animation id, or -1 when it was not read. <see cref="AnimationNames"/> turns
/// it into a name and a kind.
/// </param>
public readonly record struct Aim(float Angle, float Turning, int Animation = -1)
{
    /// <summary>How far it still has to turn, in radians, signed. Zero when it is settled.</summary>
    public float Turn => Facing.Between(Angle, Turning);

    /// <summary>Whether it is mid-turn - which is to say, taking aim right now.</summary>
    /// <remarks>
    /// The threshold is the same one the fixtures were measured with. Below it the two floats
    /// are the same number and the entity is pointing where it means to.
    /// </remarks>
    public bool IsTurning => MathF.Abs(Turn) > 0.01f;

    /// <summary>The unit direction it faces, in world space.</summary>
    public (float X, float Y) Direction => Facing.Direction(Angle);

    /// <summary>The unit direction it is turning to face.</summary>
    public (float X, float Y) Aiming => Facing.Direction(Turning);
}

/// <summary>One entity as the overlay needs it: what it is, where it is, and its address.</summary>
/// <param name="WorldZ">
/// The entity's BASE height - its feet. This is what the world-to-screen projection wants:
/// GameHelper2's HealthBars plugin starts here and subtracts the model height to reach the
/// health bar, so this end is the bottom.
/// </param>
/// <param name="TerrainHeight">
/// Ground height in the MAP's coordinate space. Belongs to the map radar, NOT to the
/// world-to-screen projection - the game keeps two separate systems (see the type remarks).
/// </param>
/// <param name="ModelBoundsZ">Model height; WorldZ minus this is where the health bar floats.</param>
/// <param name="Rarity">
/// For a ground drop, how good it is - resolved through the wrapper entity, since the thing
/// in the entity map carries no rarity of its own. <see cref="ItemRarity.Unknown"/> for
/// everything that is not an item, and for a drop whose inner entity has not resolved yet.
/// </param>
/// <param name="Poi">
/// Whether this entity marks a PLACE worth walking to - an exit, a waypoint, an encounter.
/// Separate from <paramref name="Kind"/> because the two answer different questions: a
/// strongbox is a Chest and also a landmark, and a terrain piece is scenery unless it happens
/// to be the way out.
/// </param>
/// <param name="Life">
/// A monster's health pool. Default - and <see cref="Vital.IsValid"/> false - for everything
/// that has none, and for a monster whose Life component could not be read. Those are the
/// same answer on purpose: neither is a monster at zero health.
/// </param>
/// <param name="EnergyShield">A monster's shield pool, on the same terms.</param>
/// <param name="Opened">
/// Whether a chest has already been opened. Null for anything that is not a chest, and for a
/// chest whose component did not resolve.
/// </param>
/// <param name="Name">
/// What the game calls this - "Elder Madox" rather than "ElderMadoxMapIntro". Empty for
/// everything that carries no name, which is most of a map: scenery, effects, ground items.
/// Read from the Render component once per entity and remembered, because it never changes.
/// </param>
/// <param name="IsEffect">
/// Whether this is a passing effect - a flame wall, a patch of burning ground - rather than
/// something that can be fought. True here always means a FRIENDLY one: hostile effects never
/// reach a snapshot, the reader drops them. It is carried instead of dropped because "is this
/// an effect" is a fact and "should it be drawn" is not, and the two are answered in different
/// places.
/// </param>
/// <param name="Render">
/// The Render component's address - what actually IDENTIFIES the thing in the world.
/// </param>
/// <param name="RememberedForMs">
/// How long ago the game last listed this entity, for one it is no longer listing.
///
/// Null - the ordinary case - means the game listed it in the read this snapshot came from.
/// A value means the entity map no longer has it and this is a SIGHTING: the position and
/// everything else on it are what they were when it was last read. See
/// <see cref="EntityMemory"/> for what gets kept and why nothing that moves ever does.
///
/// Carried rather than left implicit because the one thing a remembered entity cannot be used
/// for is reading more memory: its address belonged to an object the game may since have
/// freed, so anything that follows a pointer - the dissector, the duplicate check - has to be
/// able to tell the two apart.
/// </param>
/// <param name="Buffs">
/// What is currently on this monster, when somebody asked for it.
///
/// Null is the ordinary case and means NOBODY ASKED, not "nothing on it" - reading a monster's
/// buffs is a component walk per monster that only the status icons want, so
/// <see cref="WorldReader.ReadMonsterBuffs"/> has to be switched on and only the monsters worth
/// the read are given one. An empty <see cref="ActiveBuffs"/> is the other answer: it was read
/// and there was nothing on it.
/// </param>
/// <param name="Aim">
/// Where this entity is pointing and what it is doing, when somebody asked for it.
///
/// Null means NOBODY ASKED, on the same terms as <paramref name="Buffs"/>: it costs two reads
/// per entity that nothing else in the tool wants, so <see cref="WorldReader.ReadAim"/> has to
/// be switched on. Filled for the player and for hostile monsters - a friendly minion's aim is
/// nobody's question, and scenery has no Actor component to ask.
/// </param>
/// <param name="Action">
/// What this entity has COMMITTED to and where that is aimed, when somebody asked for it.
///
/// Null means NOBODY ASKED, exactly as for <paramref name="Aim"/>, and switched on by
/// <see cref="WorldReader.ReadActions"/>. It answers the question the aim cannot: an aim is a
/// direction and this is a PLACE, so a slam's landing spot is here and nowhere else. It is also
/// the earlier signal of the two - an action is committed before any animation plays, which
/// <c>ActionFieldsTests</c> measures - and that head start is the whole value of it to a warning.
/// </param>
/// <remarks>
/// Carried because the game hands one monster several entity objects over a single set of
/// components, so an entity address is not an identity: three entities with three addresses
/// and three ids can share one Render, one Life and one Monster component, differing only in
/// Positioned. Two entities sharing this are one object with one position and one model, and
/// that is a proof rather than the coincidence a matching position would be.
/// </remarks>
/// <summary>The line a beam draws, both ends in world coordinates.</summary>
/// <param name="SourceX">Where it starts - the beam entity's own position, exactly.</param>
/// <param name="TargetX">Where it ends. This is the end a player has to be out of.</param>
/// <remarks>
/// Decoded 2026-08 from tests/fixtures/session-2026-08-sweep.rec; see the Beam struct in the
/// schema for the control that settled the far end. Both ends are set once when the beam is
/// created and never move while it lives, so a drawn line does not need re-reading per frame -
/// it needs the beam to still be in the entity list, which is what makes it expire on its own.
/// </remarks>
public readonly record struct BeamLine(
    float SourceX,
    float SourceY,
    float SourceZ,
    float TargetX,
    float TargetY,
    float TargetZ)
{
    /// <summary>How long the beam is, in world units. Measured 17 to 1116 across one session.</summary>
    public float Length => MathF.Sqrt(
        ((TargetX - SourceX) * (TargetX - SourceX))
        + ((TargetY - SourceY) * (TargetY - SourceY))
        + ((TargetZ - SourceZ) * (TargetZ - SourceZ)));
}

public sealed record WorldEntity(
    uint Id,
    ulong Address,
    string Path,
    EntityKind Kind,
    float WorldX,
    float WorldY,
    float WorldZ,
    float TerrainHeight = 0f,
    float ModelBoundsZ = 0f,
    ItemRarity Rarity = ItemRarity.Unknown,
    PoiKind Poi = PoiKind.None,
    string MapIcon = "",
    Vital Life = default,
    Vital EnergyShield = default,
    bool? Opened = null,
    bool IsFriendly = false,
    bool IsEffect = false,
    string Name = "",
    ulong Render = 0,
    bool? Present = null,
    int? RememberedForMs = null,
    ActiveBuffs? Buffs = null,
    Aim? Aim = null,
    ActorAction? Action = null,

    // How long this patch of ground still burns, or null when the entity is not one. Straight
    // from the game's own countdown - see GroundEffect.SecondsRemaining in the schema, and note
    // that it reaches zero a consistent 0.38 s before the entity is delisted, so a display of
    // it will sit at 0.0 for a beat before the thing disappears.
    // Whether this entity carries a GroundEffect component at all - the game's own answer to
    // "is this dangerous ground". SEPARATE FROM THE COUNTDOWN ON PURPOSE: a third of the effects
    // in the sweep capture never carry a readable timer, and gating the ring on the timer would
    // silently leave those unmarked. Presence is the hazard; the countdown is extra.
    bool IsGroundEffect = false,

    // How long this patch still burns, or null when it has no timer. Null is COMMON and is not
    // a failed read: 33 of the 72 effects in the sweep capture never carry a number here and 39
    // always do, with not one entity crossing between the two. See GroundEffect.SecondsRemaining.
    float? GroundSeconds = null,

    // How wide the patch is, in WORLD units - or null when the entity is not a ground effect.
    // A candidate rather than a measurement: see GroundEffect.RadiusCandidate in the schema.
    // It is carried so the overlay can draw a ring of exactly this size on the ground, which is
    // what turns the question into one screenshot.
    float? GroundRadius = null,

    // The line this beam draws, or null when the entity is not one. See BeamLine.
    BeamLine? Beam = null,

    // WHICH KIND of ground this is - a row of the game's own GroundEffectTypes table, or null
    // when the entity is not a ground effect. The entity path cannot answer this: every ground
    // effect on file carries the same generic VisibleServerGroundEffect. Resolving the row to a
    // name (and to the buffs that decide whether it damages anybody) needs the game's data
    // files, so it happens a layer up - see GroundEffectTypeTable.
    int? GroundType = null)
{
    /// <summary>Whether this comes from memory rather than from the game's current list.</summary>
    public bool IsRemembered => RememberedForMs is not null;

    /// <summary>Whether this is a place somebody has already been through.</summary>
    /// <remarks>
    /// Two ways of being told the same thing. A chest says it itself, in the byte Opened
    /// carries. A league mechanic says it in the icon the GAME chose: an abyss trail's
    /// markers read AbyssPitActive until the chest at the end has been taken and
    /// AbyssPitInactive afterwards, and the same Active/Inactive pairing runs through
    /// AbyssCrack, AbyssChest and the G4 encounters.
    ///
    /// Matched on "Inactive" exactly, and deliberately NOT on the idea of "not active":
    /// the checkpoint icon is CheckpointNotActive and means the opposite - one you have not
    /// used yet is precisely the one worth walking to. Two spellings are all that separates
    /// those, which is thin enough that this may only ever thin out a LIST. What is drawn on
    /// the map does not depend on it.
    /// </remarks>
    public bool IsSpent => Opened == true
        || MapIcon.EndsWith("Inactive", StringComparison.Ordinal);

    /// <summary>Whether this is a place worth marking on the map.</summary>
    /// <remarks>
    /// A fact plus a let-out. The kind says the thing is a place; <see cref="Present"/> says
    /// whether the game agrees it is there, and only an explicit NO takes it off the map.
    /// Null - no Targetable component, or a read that failed - still draws, because a place
    /// nobody could judge is worth more on screen than off it, and because this rule reaches
    /// every checkpoint and exit in the game.
    /// </remarks>
    public bool IsPlace => Poi != PoiKind.None && Present != false;
    /// <summary>
    /// A readable label for a point of interest.
    /// </summary>
    /// <remarks>
    /// The game's own icon name when it has one, because that is what the game calls the
    /// thing rather than what its file is called - and for a quest objective the difference
    /// is "Quest Object" against "Brazier Lever 03".
    /// </remarks>
    public string PoiName => Name.Length > 0
        ? Name
        : MapIcon.Length > 0
            ? PointsOfInterest.Readable(MapIcon)
            : PointsOfInterest.Name(Path, Poi);

    /// <summary>Where the game floats this entity's health bar: the top of its model.</summary>
    public float HealthBarZ => WorldZ - ModelBoundsZ;

    /// <summary>
    /// What to call this on screen: the game's own name, else the last part of its path.
    /// </summary>
    /// <remarks>
    /// The name first, because it is the one somebody recognises - "Zar Wali, the Bone
    /// Tyrant" is what the game says and "MonsterBossZarWali01" is what the file is called.
    /// The path stays as the fallback rather than as a second choice nobody sees: most
    /// entities have no name at all, and a label that vanished for them would be worse than
    /// an ugly one.
    /// </remarks>
    public string ShortName => Name.Length > 0 ? Name : FileName;

    /// <summary>The last segment of the metadata path.</summary>
    public string FileName
    {
        get
        {
            int slash = Path.LastIndexOf('/');
            return slash >= 0 && slash < Path.Length - 1 ? Path[(slash + 1)..] : Path;
        }
    }
}

/// <summary>Everything a frame needs: the player, the entities around it, and the camera matrix.</summary>
/// <remarks>
/// THE GAME HAS TWO SCREEN-SPACE SYSTEMS, and they are not interchangeable:
///
/// 1. THE 3D WORLD - the world-to-screen matrix, used to draw over what is actually
///    rendered. GameHelper2's WorldToScreen takes (x, y, height) and its HealthBars plugin
///    feeds it Render.WorldPosition with the model height subtracted. That is this snapshot.
///
/// 2. THE IN-GAME MAP - a fixed 38.7-degree isometric projection with NO matrix at all,
///    driven by the map UI element's own zoom and shift:
///        deltaZ /= 10.86957
///        screen = mapCentre + ((dx - dy) * cos, (deltaZ - (dx + dy)) * sin)
///    where dx/dy are GRID deltas from the player, deltaZ comes from TerrainHeight, and
///    cos/sin fold in the map's diagonal and zoom (large map x0.187812, minimap x0.748).
///
/// Markers projected through (1) therefore will NOT line up with the markers the game draws
/// on its own map, because the map is zoomable and (2) accounts for that while (1) cannot.
/// Comparing the two is what makes a correct projection look broken. The map radar is a
/// separate feature and needs the UI element tree; it does not exist yet.
/// </remarks>
/// <param name="Entities">
/// What is in the area: everything read this frame, followed by the standing things the game
/// has stopped listing because the player walked out of range of them. The tail carry
/// <see cref="WorldEntity.RememberedForMs"/> and are counted by <paramref name="Remembered"/>;
/// anything that must only ever touch entities the game is currently listing - anything that
/// follows their addresses back into memory - filters on <see cref="WorldEntity.IsRemembered"/>.
/// </param>
/// <param name="Remembered">
/// How many of <paramref name="Entities"/> came from memory rather than from this read. They
/// sit at the END of the list, so the live ones are <c>Entities.Count - Remembered</c>.
/// </param>
public sealed record WorldSnapshot(
    bool InGame,
    WorldEntity? Player,
    IReadOnlyList<WorldEntity> Entities,
    float[] Matrix,
    MapView? LargeMap = null,
    MapView? MiniMap = null,
    Vitals? PlayerVitals = null,
    ActiveBuffs? PlayerBuffs = null,
    FlaskBelt? FlaskBelt = null,
    AreaInfo Area = default,
    TerrainGrid? Terrain = null,
    uint AreaHash = 0,
    GameStateKind State = GameStateKind.NotLoaded,
    ReadCost Cost = default,
    int Collapsed = 0,
    CorpseSigns Corpses = default,
    GamePanel Panels = GamePanel.None,
    int Remembered = 0,
    ulong ServerData = 0,
    IReadOnlyList<PanelArea>? PanelAreas = null,
    int AreaLevel = 0,
    int PlayerLevel = 0,

    // Where the game's own interface is this frame, part by part. Measured rather than
    // configured - the HUD is an ordinary UiElement and its parts are its children - which is
    // what lets the map overlay stay off it at any resolution or interface scale. See InterfaceReader.
    IReadOnlyList<InterfacePart>? Hud = null,

    // The camera's own view volume, read beside the matrix. Null when the block was not
    // readable, which is an ordinary answer - a recording made before anything read it has
    // nothing there. See CameraFrustum.
    CameraFrustum? Frustum = null,

    // The entity under the cursor, as the GAME has it, or 0 for none. An address rather than a
    // WorldEntity: join it against Entities on Address when a caller wants the rest. Zero is
    // the normal answer on most frames - the cursor is usually over floor. See MouseOverReader.
    ulong Hovered = 0)
{
    /// <summary>The parts of the game's interface on screen, empty when none were read.</summary>
    /// <remarks>
    /// Empty is an ordinary answer and does NOT mean the HUD is gone: a diagnostic run with no
    /// viewport reads no rectangles at all, and a loading screen has no interface to measure.
    /// What it means to a caller is "nothing known to keep off", which leaves the overlay
    /// exactly where it was rather than blanking it.
    /// </remarks>
    public IReadOnlyList<InterfacePart> InterfaceParts => Hud ?? [];

    /// <summary>
    /// Whether the player is looking at a panel rather than at the game.
    /// </summary>
    /// <remarks>
    /// Anything drawn in world space is drawn UNDERNEATH such a panel, so it is right
    /// information in the way - which is worse than none. See <see cref="PanelReader"/>.
    /// </remarks>
    public bool InAPanel => Panels != GamePanel.None;

    /// <summary>
    /// Which parts of the screen those panels took, for anything that sits in one place.
    /// </summary>
    /// <remarks>
    /// Empty is the ordinary answer and means "nothing is in the way", but it does NOT follow
    /// from <see cref="InAPanel"/> being false: a panel can be open and still contribute no
    /// rectangle, because the read was given no viewport or the element could not say how big
    /// it is. See <see cref="PanelReader"/> - the bit is the sturdy half of this.
    /// </remarks>
    public IReadOnlyList<PanelArea> Covering => PanelAreas ?? [];

    /// <summary>
    /// Only what the game listed in this read: the entities whose addresses are still live.
    /// </summary>
    /// <remarks>
    /// Where anything that goes back to MEMORY has to start - the component survey, the
    /// duplicate check, the matrix hunt. A remembered entity's address belonged to an object
    /// the game has since been free to release, so following it reads whatever is there now,
    /// and the answer looks like data rather than like a mistake.
    ///
    /// Drawing is the other case and wants <see cref="Entities"/>: a marker needs a position,
    /// which a sighting has, and not a pointer.
    /// </remarks>
    public IEnumerable<WorldEntity> Listed => Entities.Where(entity => !entity.IsRemembered);

    /// <summary>An empty snapshot - not in an area, or the chain did not resolve.</summary>
    public static WorldSnapshot Empty { get; } = new(false, null, [], new float[16]);
}

/// <summary>
/// Builds a <see cref="WorldSnapshot"/>: walk the entity map, read each entity's path and
/// position, and grab the camera matrix.
/// </summary>
/// <remarks>
/// This is the seam between "reading memory" and "drawing things". Everything above it
/// (the overlay) consumes a finished snapshot and never touches the game process, which is
/// what keeps the renderer testable and the read logic in one place.
///
/// Only entities with a Render component get a position, so decorations and logic-only
/// entities drop out naturally rather than needing a filter.
/// </remarks>
public sealed class WorldReader
{
    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityMapReader _map;
    private readonly MapRadarReader _mapRadar;
    private readonly EntityReader _entities;
    private readonly RenderReader _render;
    private readonly LifeReader _life;
    private readonly BuffsReader _buffs;
    private readonly ActionReader _actions;
    private readonly FlaskBeltReader _flasks;
    private readonly CorpseFilter _corpses = new();

    /// <summary>
    /// Which entities are not worth reading. On by default; turn it off to see everything.
    /// </summary>
    /// <remarks>
    /// Applied at the READ rather than at the drawing, which is the whole point - the entities
    /// nobody wants to see are the ones nobody should pay to read. The cost of that is real
    /// and worth stating: a filtered entity is absent from the snapshot entirely, so the
    /// reverse-engineering tools cannot see it either. That is what the switch is for.
    /// </remarks>
    public NoiseFilter Noise { get; } = new();

    /// <summary>
    /// The standing things the game has stopped listing, kept rather than lost.
    /// </summary>
    /// <remarks>
    /// The entity list is a bubble around the player, so everything already worked out about a
    /// place goes away the moment it is far enough behind - which is exactly when it becomes
    /// worth marking. See <see cref="EntityMemory"/> for the rule that decides when a thing is
    /// genuinely gone rather than merely out of range.
    /// </remarks>
    public EntityMemory Memory { get; } = new();

    /// <summary>
    /// Keep the ground effects instead of dropping them. FOR LOOKING AT, not for playing.
    /// </summary>
    /// <remarks>
    /// A hostile thing that expires on its own and cannot be targeted is a ground effect
    /// wearing a monster's components - a flame wall, damaging ground, an ice crystal - and
    /// dropping it is what stopped a Firewall build covering its own screen in enemy markers.
    /// That is the right default and this does not change it.
    ///
    /// What this is for is the other question: WHERE are they. A build dying to something it
    /// cannot see, a mechanic whose danger zone is not obvious, an offset that needs a visible
    /// thing to check against - none of those can be answered from an entity the read threw
    /// away. So it is kept, marked as its own kind, and drawn only by something that asked for
    /// that kind.
    ///
    /// OFF by default and worth leaving off while playing: these are the most numerous entities
    /// in the game and every one of them costs a component read.
    /// </remarks>
    public bool KeepEffects { get; set; }

    /// <summary>
    /// Read the game's visual entities too - which is where the projectiles are.
    /// </summary>
    /// <remarks>
    /// The game keys its entity map by id and files everything from 0x40000000 up as a visual:
    /// decorations, effects, and every projectile in flight. <see cref="EntityMapReader"/>
    /// drops those before their path is read, so with this off no projectile can reach a
    /// snapshot however it is classified afterwards - which is what an overlay that drew
    /// nothing over a screen of Spark turned out to be.
    ///
    /// OFF by default and it costs what it says. A recorded Spark session ran 17 gameplay
    /// entities against 51 visuals per frame, so this roughly quadruples what the entity walk
    /// looks at. Most of the extra are engine nodes the noise filter refuses on their path
    /// alone, before the expensive component walk - so the cost is far below four times the
    /// read, and it is real. The read-cost breakdown is where to look at it rather than guess:
    /// the entities figure is the one that moves.
    /// </remarks>
    public bool ReadVisualEntities { get; set; }

    /// <summary>
    /// Read what is currently ON the monsters, which is what the status icons draw.
    /// </summary>
    /// <remarks>
    /// OFF by default because it is the only per-monster read in here that nothing else wants.
    /// A Buffs component is a vector walk plus three reads per entry, and paying it for every
    /// monster on a screen would be the most expensive thing this reader does - so it is
    /// switched on by the feature that needs it and taken with it when that is switched off.
    ///
    /// Even switched on it is not paid for every monster: see the rarity floor at the call
    /// site. Forty white monsters in a breach are not what anybody is watching a debuff timer
    /// on, and they are most of what an area contains.
    /// </remarks>
    public bool ReadMonsterBuffs { get; set; }

    /// <summary>
    /// The least rare monster whose buffs are worth reading.
    /// </summary>
    /// <remarks>
    /// Rare and above, from the reference plugin, which shows monster status effects for
    /// exactly that. It bounds the cost against the thing that makes it dangerous - a pack's
    /// worth of ordinary monsters - while keeping every monster a person actually watches.
    /// </remarks>
    public ItemRarity MonsterBuffFloor { get; set; } = ItemRarity.Rare;

    /// <summary>
    /// Read where things are POINTING, and what they are doing - the aim overlay's input.
    /// </summary>
    /// <remarks>
    /// OFF by default, like the buffs, and for the same reason: two reads per monster that
    /// nothing else in the tool wants. It is the cheaper of the two - eight bytes off the Render
    /// component that was already resolved, plus four off the Actor component - and it is still
    /// a cost nobody should pay for a layer they have switched off.
    ///
    /// NO RARITY FLOOR, unlike the buffs, and that is deliberate. A debuff timer is something
    /// you watch on a rare; a slam that is about to land is a question about whatever is next to
    /// you, and an ordinary monster's slam kills exactly as well.
    /// </remarks>
    public bool ReadAim { get; set; }

    /// <summary>
    /// Read what things have COMMITTED to doing, and where - the evasion input.
    /// </summary>
    /// <remarks>
    /// OFF by default on the same terms as <see cref="ReadAim"/>, and it is the more expensive
    /// of the two: an id off the Actor component, then a pointer, then the wrapper's two pairs -
    /// four reads where the aim costs two. Paid only for the player and hostile monsters.
    ///
    /// SEPARATE FROM <see cref="ReadAim"/> RATHER THAN FOLDED INTO IT, though the same layer
    /// wants both, because they are separate CLAIMS. The facing pair is settled on the player and
    /// on monsters alike - the aim overlay has drawn monster rays for a month. The action fields
    /// were measured only on the player's own actor, so a monster's action is the newer and
    /// weaker reading of the two, and a switch that turned both on at once would let the weaker
    /// one ride into a feature on the stronger one's evidence.
    /// </remarks>
    public bool ReadActions { get; set; }

    /// <summary>
    /// Skip the per-entity reads whose only consumers draw AT the entity, when the camera
    /// cannot see it.
    /// </summary>
    /// <remarks>
    /// The saving the frustum was worth reading for. <see cref="ReadAim"/> and
    /// <see cref="ReadMonsterBuffs"/> are the two switches this file already describes as
    /// costing a read per monster, and both feed things drawn over the monster - a ray from
    /// its feet, icons above its head. A monster the camera cannot see is drawn nowhere, so
    /// the read buys nothing.
    ///
    /// WHY THIS IS SAFE TO HAVE ON, in the order the evidence was gathered rather than in the
    /// order that flatters it:
    ///
    ///  - The gate and the overlay agree. What gets drawn is decided by projecting; what gets
    ///    read is decided here by the frustum. Over the eighteen committed recordings, at the
    ///    frame each one actually read the frustum, the two agree on ALL 1042 tested points -
    ///    every entity's feet and the top of its model - with nothing on screen yet outside
    ///    the frustum, and nothing inside the frustum yet off screen. That is not luck: the
    ///    frustum's eight corners project onto the edges of the viewport exactly, so the two
    ///    are one predicate computed twice. FrustumGateTests measures it.
    ///  - It fails OPEN. No frustum - an old recording, a drifted offset, a frame the block
    ///    was unreadable - and nothing is skipped at all. An unreadable field must never be
    ///    able to switch a feature off quietly, which is this project's most expensive
    ///    recurring bug.
    ///  - <see cref="ReadActions"/> is deliberately NOT gated. It feeds evasion, which is a
    ///    question about danger to the player rather than about what is on screen.
    ///
    /// WHAT DOES CHANGE, stated because "provably free" would be too strong: two readouts in
    /// the Tracker window list monsters rather than draw on them. Its action census is titled
    /// "what the monsters ON SCREEN are doing", so gating makes it match its own description;
    /// its buff-name list, which exists so a name can be copied into a rule, is narrowed to
    /// monsters that are visible - which is where somebody reading names is looking anyway.
    ///
    /// THE ONE THING NOT MEASURED is how often the game rewrites the frustum. That is what
    /// <see cref="OffScreenMargin"/> absorbs, and why it is sized rather than zero.
    /// </remarks>
    public bool SkipOffScreenReads { get; set; } = true;

    /// <summary>
    /// How far outside the view volume still counts as visible, in world units.
    /// </summary>
    /// <remarks>
    /// SIZED FROM A MEASUREMENT, not chosen: the fastest the player crosses the world in any
    /// committed recording is 2045 units per second, so at the reader's 30 Hz the camera moves
    /// about 68 units between two ticks. 250 is three and a half ticks of that - headroom for
    /// a frustum the game updates a frame or two behind the matrix, which is the only
    /// staleness a per-frame read can still be exposed to.
    ///
    /// Being wrong high costs a handful of reads at the screen edge; being wrong low costs a
    /// ray that flickers off as a monster reaches the boundary. The asymmetry is why it is
    /// generous.
    /// </remarks>
    public float OffScreenMargin { get; set; } = 250f;

    /// <summary>
    /// Where to record the animation names the game supplies, or null to read none.
    /// </summary>
    /// <remarks>
    /// Passed straight through to <see cref="ActionReader.Names"/>. Here rather than on the
    /// reader's constructor for the same reason <see cref="ReadActions"/> is a property: the
    /// composition root decides, per tick, what the read is for.
    /// </remarks>
    public AnimationNames? AnimationNames
    {
        get => _actions.Names;
        set => _actions.Names = value;
    }

    private readonly GroundItemReader _groundItems;
    private readonly MinimapIconReader _mapIcons;
    private LandmarkNames _landmarkNames = LandmarkNames.Empty;
    private readonly WorldAreaReader _areas;
    private readonly PanelReader _panels;

    /// <summary>Where the game's own interface is, measured part by part every tick.</summary>
    private readonly InterfaceReader _hud;

    /// <summary>
    /// The child path to the atlas panel, for finding the screen its furniture hangs on.
    /// </summary>
    /// <remarks>
    /// The path rather than an AtlasReader: all that is wanted here is the element's ADDRESS,
    /// so that InterfaceReader can walk up from it, and building the whole atlas decoder to ask
    /// for three child pointers would be a second owner of a panel this class does not read.
    /// </remarks>
    private readonly int[] _atlasPath;

    /// <summary>
    /// Reused across the readers that walk the interface, and reused for a reason.
    /// </summary>
    /// <remarks>
    /// The class holds only resolved offsets, so sharing it costs nothing - but it is also the
    /// thing that would have to be built twice a tick otherwise, once for the panels and once
    /// for the HUD.
    /// </remarks>
    private readonly UiElementReader _uiElements;

    private readonly TerrainReader _terrain;
    private readonly MouseOverReader _mouseOver;

    /// <summary>The last resolved InGameState, for a hover re-read between snapshots.</summary>
    private ulong _lastInGameState;

    /// <summary>
    /// Asks the game again what the cursor is on, without taking a whole snapshot.
    /// </summary>
    /// <remarks>
    /// Three reads, off the chain the last snapshot resolved. It exists for one caller: an
    /// aiming effect that has just placed the pointer and needs to know whether it landed
    /// before it presses anything. Reading the whole world for that would cost the reader
    /// thread its cadence, and using the LAST snapshot's answer would be reading the slot as it
    /// was before the pointer moved - which is the one reading guaranteed to be wrong.
    ///
    /// Answers 0 before any snapshot has resolved a chain, which is the same answer it gives
    /// for "nothing hovered" and means the same thing to the only caller: do not press.
    /// </remarks>
    public ulong ReadHoveredNow()
        => _lastInGameState == 0 ? 0 : _mouseOver.Read(_lastInGameState);
    private readonly int _playerInfo;
    private readonly int _serverData;
    private readonly int _awakeEntities;
    private readonly int _w2sMatrix;
    private readonly int _frustumCorners;
    private readonly int _frustumPlanes;
    private readonly int _groundSeconds;
    private readonly int _groundRadius;
    private readonly int _groundType;
    private readonly int _beamSource;
    private readonly int _beamTarget;
    private readonly int _areaHash;
    private readonly int _areaLevel;
    private readonly int _playerLevelField;
    private readonly int _lifeHealth;
    private readonly int _lifeEnergyShield;
    private readonly int _vitalCurrent;
    private readonly int _vitalMax;
    private readonly int _vitalReservedFlat;
    private readonly int _vitalReservedPercent;
    private readonly int _lifeSpan;
    private readonly int _isTargetable;
    private readonly int _monsterRarity;
    private readonly int _chestOpened;
    private readonly int _reaction;
    private readonly int _animationId;

    /// <summary>Names already read, by entity address.</summary>
    /// <remarks>
    /// A name never changes while an entity exists, and reading one is a header plus a heap
    /// read - so paying for it on every snapshot would buy the same answer sixty times a
    /// second. EMPTY answers are remembered too, and that is most of the value: scenery and
    /// effects have no name, they are the bulk of the map, and without this they would be
    /// asked again every frame forever.
    /// </remarks>
    private readonly Dictionary<ulong, string> _names = [];

    /// <summary>The area those names belong to. Addresses are handed out again in the next one.</summary>
    private uint _namesArea;

    /// <param name="rotation">
    /// Where the terrain rotation tables live, for the within-tile heights. Optional: without
    /// them the map is drawn from tile-level heights, which is a coarser correction rather
    /// than none.
    /// </param>
    public WorldReader(IMemoryReader reader, OffsetSchema schema, TerrainRotationTables rotation = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _map = new EntityMapReader(reader, schema);
        _mapRadar = new MapRadarReader(reader, schema);
        _entities = new EntityReader(reader, schema);
        _render = new RenderReader(reader, schema);
        _life = new LifeReader(reader, schema);
        _buffs = new BuffsReader(reader, schema);
        _actions = new ActionReader(reader, schema);
        _flasks = new FlaskBeltReader(reader, schema);
        _groundItems = new GroundItemReader(reader, schema);
        _mapIcons = new MinimapIconReader(reader, schema);
        _areas = new WorldAreaReader(reader, schema);
        _uiElements = new UiElementReader(reader, schema);
        _panels = new PanelReader(reader, schema, _uiElements);
        _hud = new InterfaceReader(reader, schema, _uiElements);

        StructDef atlasPanel = schema.Structs["AtlasPanel"];
        _atlasPath =
        [
            (int)atlasPanel.Constants["PathFromUiRoot0"],
            (int)atlasPanel.Constants["PathFromUiRoot1"],
            (int)atlasPanel.Constants["PathFromUiRoot2"],
        ];
        _terrain = new TerrainReader(reader, schema, rotation);
        _mouseOver = new MouseOverReader(reader, schema);
        _playerInfo = schema.Structs["AreaInstance"].OffsetOf("PlayerInfo");
        _serverData = schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr");
        _awakeEntities = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");
        _w2sMatrix = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
        _frustumCorners = schema.Structs["WorldData"].OffsetOf("FrustumCorners");
        _frustumPlanes = schema.Structs["WorldData"].OffsetOf("FrustumPlanes");

        // The two hazards decoded from the --sweep capture. Read unconditionally rather than
        // behind a switch, on the measured counts rather than on principle: across every
        // committed recording there are at most 11 ground effects and 5 beams alive at once, so
        // this is one 4-byte read and one 24-byte read on a handful of entities - against the
        // hundreds of monsters the same loop already pays for. Gating it would cost more in
        // state that can be wrong than it could ever save.
        _groundSeconds = schema.Structs["GroundEffect"].OffsetOf("SecondsRemaining");
        _groundRadius = schema.Structs["GroundEffect"].OffsetOf("RadiusCandidate");
        _groundType = schema.Structs["GroundEffect"].OffsetOf("TypeRow");
        _beamSource = schema.Structs["Beam"].OffsetOf("SourceX");
        _beamTarget = schema.Structs["Beam"].OffsetOf("TargetX");
        _areaHash = schema.Structs["AreaInstance"].OffsetOf("CurrentAreaHash");

        // Two levels the schema has carried - with invariants, so the drift report already
        // checks them - that nothing read until the rule engine wanted to ask about them.
        // One i32 each, off structs this read has already resolved.
        _areaLevel = schema.Structs["AreaInstance"].OffsetOf("CurrentAreaLevel");
        _playerLevelField = schema.Structs["Player"].OffsetOf("Level");

        // Read straight from the schema rather than through LifeReader, which would follow
        // three pools through twelve separate reads for every monster on screen. Life and
        // energy shield come out of ONE span read covering both sub-structs - the same
        // number of calls the corpse check used to make for a single number, and the reason
        // health bars cost nothing to add.
        //
        // Mana is deliberately outside the span: no monster's mana is worth drawing, and
        // including it would widen the read for nothing.
        _lifeHealth = schema.Structs["Life"].OffsetOf("Health");
        _lifeEnergyShield = schema.Structs["Life"].OffsetOf("EnergyShield");
        _vitalCurrent = schema.Structs["Vital"].OffsetOf("Current");
        _vitalMax = schema.Structs["Vital"].OffsetOf("Max");
        _vitalReservedFlat = schema.Structs["Vital"].OffsetOf("ReservedFlat");
        _vitalReservedPercent = schema.Structs["Vital"].OffsetOf("ReservedPercent");
        _lifeSpan = _lifeEnergyShield - _lifeHealth + _vitalCurrent + sizeof(int);
        _isTargetable = schema.Structs["Targetable"].OffsetOf("IsTargetable");
        _monsterRarity = schema.Structs["ObjectMagicProperties"].OffsetOf("Rarity");
        _animationId = schema.Structs["Actor"].OffsetOf("AnimationId");
        _chestOpened = schema.Structs["Chest"].OffsetOf("IsOpened");
        _reaction = schema.Structs["Positioned"].OffsetOf("Reaction");
    }

    /// <summary>
    /// Reads the three signals that say whether a monster is still alive.
    /// </summary>
    /// <remarks>
    /// Both "the component is not there" and "the read failed" yield null, never a default,
    /// and that carries the whole safety of the filter. Read&lt;T&gt; returns 0 on failure,
    /// so reading health through it would turn every unreadable monster into a zero-health
    /// corpse and empty the overlay - which is exactly what the replay tests caught the
    /// moment this was written the convenient way. TryRead is the only correct call here.
    /// </remarks>
    /// <summary>Seconds until this patch of ground stops burning, or null if it is not one.</summary>
    /// <remarks>
    /// The component's presence IS the answer to "is this dangerous ground" - which is what
    /// makes this different from the path rules the tracker has had until now. Those were the
    /// only way to name a hazard when nothing could read one, and they carry the usual cost of
    /// asking a person to describe something the game already knows: a rule matches what
    /// somebody thought to type.
    /// </remarks>
    private float? ReadGroundSeconds(Entity entity)
    {
        ulong at = entity.Component("GroundEffect");
        if (at == 0 || !_reader.TryRead(at + (ulong)_groundSeconds, out float seconds))
        {
            return null;
        }

        // NOT FINITE IS THE ORDINARY ANSWER, not a failure: a third of ground effects carry no
        // timer at all and hold NaN here for their whole life. Everything else out of range says
        // the offset moved or the object was freed mid-read, and null is the honest answer to
        // all three - the ring is still drawn, just without a number in it.
        return float.IsFinite(seconds) && seconds is >= 0 and <= 600 ? seconds : null;
    }

    /// <summary>The candidate radius of this patch in world units, or null if it is not one.</summary>
    /// <remarks>
    /// Read beside the countdown from the same component, so it costs one more 4-byte read on
    /// an entity the loop is already holding. The range guard is wider than the observed 18.7
    /// on purpose - the point of drawing it is to find out what it is, and a guard tight around
    /// today's only sighting would hide the answer on the first effect that differs.
    /// </remarks>
    private float? ReadGroundRadius(Entity entity)
    {
        ulong at = entity.Component("GroundEffect");
        if (at == 0 || !_reader.TryRead(at + (ulong)_groundRadius, out float radius))
        {
            return null;
        }

        return float.IsFinite(radius) && radius is > 0 and <= 500 ? radius : null;
    }

    /// <summary>Which kind of ground this is, as a row of the game's GroundEffectTypes table.</summary>
    /// <remarks>
    /// The only field on this component that says anything about WHAT a patch is. Every ground
    /// effect in every recording here carries the same generic entity path, so the path cannot
    /// tell a burning patch from the glow under a league object - and damage is a property of
    /// the type, written in the game's data files as the buffs that type applies, not as a flag
    /// anywhere on the instance. See GroundEffect.TypeRow in the schema.
    ///
    /// The bound is a PLAUSIBILITY check and nothing more: a row index is small, so a value in
    /// the millions says the read landed on something that is not one. Resolving it against the
    /// real table - which is what says whether row 17 exists at all - happens in the Features
    /// layer, where the game's files can be opened; this layer only carries the number.
    /// </remarks>
    private int? ReadGroundType(Entity entity)
    {
        ulong at = entity.Component("GroundEffect");
        if (at == 0 || !_reader.TryRead(at + (ulong)_groundType, out uint row))
        {
            return null;
        }

        return row <= 65535 ? (int)row : null;
    }

    /// <summary>Both ends of this beam, or null if it is not one.</summary>
    private BeamLine? ReadBeam(Entity entity)
    {
        ulong at = entity.Component("Beam");
        if (at == 0)
        {
            return null;
        }

        Span<float> ends = stackalloc float[6];
        if (!_reader.TryRead(at + (ulong)_beamSource, System.Runtime.InteropServices.MemoryMarshal.AsBytes(ends[..3]))
            || !_reader.TryRead(at + (ulong)_beamTarget, System.Runtime.InteropServices.MemoryMarshal.AsBytes(ends[3..])))
        {
            return null;
        }

        foreach (float f in ends)
        {
            if (!float.IsFinite(f))
            {
                return null;
            }
        }

        var line = new BeamLine(ends[0], ends[1], ends[2], ends[3], ends[4], ends[5]);

        // A beam of no length is a beam that has not been aimed yet, and drawing it puts a dot
        // on its own source for a frame. Measured lengths start at 17 world units.
        return line.Length > 1f ? line : null;
    }

    private MonsterSigns ReadMonsterSigns(Entity entity)
    {
        int? health = null;
        Vital pool = default;
        Vital shield = default;

        ulong life = entity.Component("Life");
        if (life != 0)
        {
            // ONE read covering both sub-structs rather than one per number. The corpse check
            // needs current health; the maximum sits four bytes from it and the shield a
            // little further on, so taking the span costs the same single call and yields a
            // health bar as well.
            Span<byte> span = stackalloc byte[_lifeSpan];
            if (_reader.TryRead(life + (ulong)_lifeHealth, span))
            {
                pool = VitalIn(span, 0);
                shield = VitalIn(span, _lifeEnergyShield - _lifeHealth);
                health = pool.Current;
            }
        }

        bool? targetable = null;
        ulong targetableComponent = entity.Component("Targetable");
        if (targetableComponent != 0
            && _reader.TryRead(targetableComponent + (ulong)_isTargetable, out byte flag))
        {
            targetable = flag != 0;
        }

        // Rarity 3 and above is Unique/Boss, which spares bosses the targetable rule - an
        // unreadable rarity simply means "not a boss" there, and the worst case is a boss dot
        // blinking during a phase rather than a live monster hidden.
        //
        // The VALUE is carried out as well, and it is the most useful thing a monster radar
        // can say. Which of forty dots is the rare pack leader decides whether you walk in;
        // the read was already happening and the answer was being thrown away.
        ItemRarity monsterRarity = ItemRarity.Unknown;
        ulong properties = entity.Component("ObjectMagicProperties");
        if (properties != 0 && _reader.TryRead(properties + (ulong)_monsterRarity, out int rarity))
        {
            monsterRarity = Rarities.FromRaw(rarity);
        }

        // Whose side it is on. One byte, and the thing that decides whether a dot is a
        // threat or your own summon - which nothing here was asking, so every minion, totem
        // and cast effect has been drawn as an enemy.
        bool friendly = ReadFriendly(entity);

        // Whether it expires on its own. Presence of the component is the whole answer, so
        // this costs a lookup and no read at all.
        bool temporary = entity.Component("DiesAfterTime") != 0;

        // Whether it has a Life component at all - the reference's test for whether an entity
        // is a monster in the first place, and the one that separates a projectile or a patch
        // of burning ground from something you can fight. An empty component list means the
        // walk told us nothing, which is a different answer from "it has none".
        bool? hasLife = entity.Components.Count > 0 ? life != 0 : null;

        // And whether anything can target it at all, which is not the same question as what
        // the component says: a boss mid-phase reads untargetable and still has one.
        bool? hasTargetable = entity.Components.Count > 0 ? targetableComponent != 0 : null;

        return new MonsterSigns(
            health, targetable, monsterRarity >= ItemRarity.Unique, monsterRarity, pool, shield,
            friendly, temporary, hasLife, hasTargetable);
    }

    /// <summary>
    /// Where an entity is pointing and what it is doing, or null when neither could be read.
    /// </summary>
    /// <remarks>
    /// The ANGLE is what makes this worth having, so an entity whose facing will not read gets
    /// nothing at all - an animation on its own says a slam is starting and cannot say where it
    /// is going, which is the half that was already available and never enough.
    ///
    /// The animation is the other way round: optional. Not everything that faces somewhere has
    /// an Actor component, and -1 travels on to say "not read" rather than being mistaken for
    /// animation zero, which is Idle.
    /// </remarks>
    private Aim? ReadAimOf(Entity entity, ulong renderAddress)
    {
        if (_render.ReadFacing(renderAddress) is not (float angle, float turning))
        {
            return null;
        }

        int animation = -1;
        ulong actor = entity.Component("Actor");
        if (actor != 0 && _reader.TryRead(actor + (ulong)_animationId, out int read))
        {
            animation = read;
        }

        return new Aim(angle, turning, animation);
    }

    /// <summary>Whether the game says this entity is on the player's side.</summary>
    /// <remarks>
    /// ONE byte and ONE copy of the rule, because two things ask it now: the monster signs,
    /// where it separates a threat from your own summon, and a projectile, where it is the
    /// only thing on the entity that answers "is this one mine".
    ///
    /// A missing Positioned component - or a read that fails - answers false, which is a claim
    /// this cannot actually support. It is kept because the alternative costs more than it
    /// buys: a tri-state would have to travel on every entity to serve the handful that cannot
    /// be judged, and the consequence of being wrong here is a marker in the enemy colour
    /// rather than a monster hidden. Whoever draws projectiles shows both sides by default for
    /// exactly this reason.
    /// </remarks>
    private bool ReadFriendly(Entity entity)
    {
        ulong positioned = entity.Component("Positioned");
        return positioned != 0
               && _reader.TryRead(positioned + (ulong)_reaction, out byte reaction)
               && (reaction & 0x7F) == 0x01;
    }

    /// <summary>Pulls one vital sub-struct out of a span read from the Life component.</summary>
    /// <remarks>
    /// A bad read leaves the span zeroed, which reads back as a pool with no maximum -
    /// <see cref="Vital.IsValid"/> is false for that, so nothing downstream mistakes it for a
    /// monster at zero health. The distinction matters here for the same reason it does in
    /// the corpse check: "no answer" and "dead" are not the same claim.
    /// </remarks>
    private Vital VitalIn(ReadOnlySpan<byte> span, int at) => new(
        BitConverter.ToInt32(span[(at + _vitalCurrent)..]),
        BitConverter.ToInt32(span[(at + _vitalMax)..]),
        BitConverter.ToInt32(span[(at + _vitalReservedFlat)..]),
        BitConverter.ToInt32(span[(at + _vitalReservedPercent)..]));

    /// <summary>
    /// Names for particular terrain tiles, used when an area is one somebody described.
    /// </summary>
    /// <remarks>
    /// Optional, and the boss arenas are found without it - this only puts a real name on
    /// them where one is known. Set once at startup.
    /// </remarks>
    public LandmarkNames LandmarkNames
    {
        get => _landmarkNames;
        set => _landmarkNames = value ?? LandmarkNames.Empty;
    }

    /// <summary>Reads one frame's worth of world state.</summary>
    /// <param name="maxEntities">Cap on entities read, to bound a frame's cost.</param>
    /// <param name="scale">
    /// The viewport being drawn into. Supplied by the renderer because only it knows where
    /// the pixels are going; passing it lets the game's own maps be resolved in the same
    /// pass, so the renderer still never touches memory itself.
    /// </param>
    public WorldSnapshot Read(ulong gameStatesStatic, int maxEntities = 512, UiScale? scale = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);

        // ONE gate, at the source. The game runs a stack of states and only one of them is
        // the world; outside it the pointers below are stale or being rebuilt, so reading
        // them produces a plausible picture of the area that was just left. Refusing here
        // means every consumer - the overlay, the route, the flasks - is off without any of
        // them having to know the rule, and it saves the read as well.
        //
        // The state travels on the empty snapshot so the status line can say WHICH state,
        // rather than reporting "not in an area" over a loading screen.
        if (!chain.InGame)
        {
            return WorldSnapshot.Empty with { State = chain.State };
        }

        var matrix = new float[16];
        _reader.TryRead(
            chain.WorldData + (ulong)_w2sMatrix,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(matrix.AsSpan()));

        // One more read beside the matrix, and it buys two things. The camera's frustum is
        // the game's own "is that on screen", which nothing here could ask before; and being
        // read EVERY frame is what puts it into a --record session frame by frame, which is
        // the only way to settle how often the game rewrites it. Every committed recording
        // swept this region once, so a replay of them cannot tell a constant from a
        // photograph - see docs/architecture.md.
        CameraFrustum? frustum = CameraFrustum.Read(_reader, chain.WorldData, _frustumCorners, _frustumPlanes);

        // Three reads for what the cursor is on. Kept unconditional for the same reason as the
        // frustum above: a slot that is read every frame lands in a --record session frame by
        // frame, and this one was only settled because a capture finally contained it.
        ulong hovered = _mouseOver.Read(chain.InGameState);

        // Kept so the slot can be asked again BETWEEN reads. An aiming effect places the
        // pointer and has to know within a few milliseconds whether it landed, and waiting for
        // the next snapshot would make the window tens of milliseconds - long enough for the
        // player's own hand to pull it off target. See WorldReader.ReadHoveredNow.
        _lastInGameState = chain.InGameState;

        // The map struct sits INLINE in AreaInstance: pass its address, not a pointer read
        // from it (its first field is the head, which is why reading it as a pointer works
        // for the drift report but is not what the traversal needs).
        // Read before the entities rather than with the rest of the snapshot: the names
        // remembered below belong to one area, and an address freed in one is handed out to
        // something else in the next.
        uint areaHash = _reader.Read<uint>(chain.AreaInstance + (ulong)_areaHash);
        if (areaHash != _namesArea)
        {
            _namesArea = areaHash;
            _names.Clear();
        }

        ulong mapStruct = chain.AreaInstance + (ulong)_awakeEntities;
        long entitiesFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        Dictionary<uint, ulong> pointers =
            _map.ReadEntityPointers(mapStruct, maxEntities, ReadVisualEntities);
        int skipped = 0;

        var entities = new List<WorldEntity>(pointers.Count);
        WorldEntity? player = null;
        long nowMs = Environment.TickCount64;

        // Which rendered objects a monster has already been taken for this read, and how
        // many repeat entities were dropped because of it. See where they are used, below.
        var rendered = new HashSet<ulong>();
        int collapsed = 0;

        // How many entities the frustum gate spared a drawing read. Counted rather than felt,
        // for the same reason every other number in ReadCost is: a saving nobody can see is
        // indistinguishable from a feature that quietly stopped working.
        int offScreen = 0;

        // What the corpse check saw, so a screen full of dots on cleared ground can be
        // explained instead of guessed at. See CorpseSigns for the three shapes.
        int targetable = 0, untargetable = 0, unreadableTargetable = 0;

        foreach ((uint id, ulong address) in pointers)
        {
            // The PATH first, and the components only if the path earns them: walking an
            // effect node's component table costs the same as walking a monster's, and there
            // is no reason to pay for an entity that is about to be thrown away.
            EntityIdentity? found = _entities.ReadIdentity(address);
            if (found is not { } identity || identity.Path.Length == 0)
            {
                continue;
            }

            if (Noise.IsNoise(identity.Path))
            {
                skipped++;
                continue;
            }

            Entity entity = _entities.Read(identity);

            ulong renderAddress = entity.Component("Render");
            if (renderAddress == 0)
            {
                continue; // no position - nothing to draw
            }

            EntityKind kind = ClassifyPath(entity.Path);

            // ONE MONSTER PER RENDERED OBJECT. Measured, not assumed: an area listed twelve
            // monster entities of one kind - twelve addresses, twelve ids, from twelve
            // separate nodes - while the game's own counter and the four map dots both said
            // four. Taking three of them apart in the dissector settled what they were:
            // ids 229, 230 and 231 shared EVERY component address, Life and Render included,
            // and differed only in Positioned. Id 269 next to them shared none of it.
            //
            // So the game gives one monster several entity objects over one set of
            // components, and every consumer counted each. The damage meter is where that
            // was expensive: it credits a monster's remaining pool when its entity goes
            // away, so a monster represented three times was paid for three times - into
            // the least certain bucket of the figure, where it was hardest to notice.
            //
            // KEYED ON THE COMPONENT, NOT ON THE POSITION, and the difference is between a
            // proof and a coincidence. Two entities sharing one Render component ARE one
            // object - there is a single position and a single model behind them. Two
            // entities merely STANDING on identical coordinates might be a pack still
            // stacked on its spawn point, and collapsing those would take a live monster off
            // the overlay. The first test cannot be wrong in that way.
            //
            // Render rather than Life because every entity that gets this far has one - it
            // is the component just read above - so the key costs nothing and needs no
            // fallback for the entities that carry no health.
            //
            // Monsters only. Ground effects legitimately repeat, and nothing here has been
            // measured about how the game represents those.
            if (kind == EntityKind.Monster && !rendered.Add(renderAddress))
            {
                collapsed++;
                continue;
            }

            RenderComponent? position = _render.Read(renderAddress);
            if (position is null)
            {
                continue;
            }

            // Corpses stay in the entity map long after the fight, so without this the
            // overlay marks a cleared screen full of dead monsters. Only monsters are
            // checked: the same targetable byte also goes to 0 on an OPENED chest, which
            // is a different question and not this one.
            // Timed against the RENDER component rather than the entity address, because the
            // entity address is not the monster. One monster wears several entities, only one
            // of them survives the collapsing above, and WHICH one depends on the order the
            // entity map is walked in - which shifts as the tree rebalances around things
            // dying and spawning. Keyed on the entity, a flip in that choice restarts the
            // untargetable clock from zero, it never reaches its threshold, and corpses stop
            // being recognised: a cleared screen keeps its dots. Keyed on the object, the
            // clock belongs to the monster and does not care which of its entities was seen.
            MonsterSigns signs = kind == EntityKind.Monster ? ReadMonsterSigns(entity) : default;

            // Whose projectile this is. The monster signs are not read for this kind - a
            // projectile has no health and nothing targets it, so all twelve of those reads
            // would answer nothing - but the one byte that says whose side it is on is the
            // whole question a projectile overlay is asked, so it is read on its own.
            bool friendly = kind == EntityKind.Projectile ? ReadFriendly(entity) : signs.Friendly;

            // Only over the monsters this is actually a question about: hostile ones that are
            // not effects. Counting everything made the readout useless in precisely the
            // situation it was built for - a Firewall build puts twenty of its own walls on
            // the ground, none of which carries a Targetable component because none of them
            // is something anybody targets, and the counter reported that the component was
            // "mostly NOT BEING FOUND" every time the player cast. 6,669 sightings of Firewall
            // against 3,618 of every real monster in a whole map, so the alarm was mostly
            // measuring the build.
            if (kind == EntityKind.Monster && !signs.Friendly && !signs.IsEffect)
            {
                switch (signs.Targetable)
                {
                    case true: targetable++; break;
                    case false: untargetable++; break;
                    default: unreadableTargetable++; break;
                }
            }

            if (kind == EntityKind.Monster && _corpses.IsCorpse(renderAddress, signs, nowMs))
            {
                continue;
            }

            // A hostile thing that expires on its own and cannot be targeted is not a
            // monster - it is a ground effect wearing a monster's components. Flame walls,
            // ice crystals, damaging ground: they carry Life, so they were drawn as enemies
            // and given health bars, which is what a screen full of unexplained dots was.
            //
            // Straight from the reference, including the targetable let-out: some real
            // summoned monsters expire too, and those ARE worth drawing. Friendly ones are
            // never dropped here, because whether to show your own minions is a preference
            // and this is a question of fact - they travel on with IsEffect set instead.
            if (kind == EntityKind.Monster && signs.IsHostileEffect)
            {
                // RECLASSIFIED, not un-dropped, when somebody asks to see them. Letting them
                // travel on as monsters would put them back in every count and every health
                // bar this rule exists to keep them out of; as their own kind they are
                // invisible to all of it, because everything else here asks for Monster by
                // name. The one place that draws them has to ask for Effect on purpose.
                if (!KeepEffects)
                {
                    continue;
                }

                kind = EntityKind.Effect;
            }

            // Resolved here rather than filtered here: how good a drop is, is a fact about
            // the world, while "is it worth drawing" is a preference. The snapshot carries
            // the fact so the overlay - and a future loot tracker, which wants everything -
            // can each decide for themselves.
            // One field for one question - how rare is this thing - answered from whichever
            // component knows: the wrapper's inner item for a drop, ObjectMagicProperties for
            // a monster. The scale is the same 0-3 in both cases because the game uses one.
            ItemRarity rarity = kind switch
            {
                EntityKind.WorldItem => _groundItems.RarityOf(entity, nowMs),
                EntityKind.Monster => signs.Rarity,
                _ => ItemRarity.Unknown,
            };

            // The game's own map marking, when it has one. Only read for entities that carry
            // the component, so it costs nothing on the monsters and drops that never do.
            ulong iconComponent = entity.Component("MinimapIcon");
            string mapIcon = iconComponent != 0 ? _mapIcons.Read(iconComponent) : string.Empty;

            // Whether a chest has already been looted. One byte, and only on entities that
            // carry the component - so it costs nothing on the monsters and drops that do
            // not, and it answers the thing a marked chest is most often lying about.
            bool? opened = null;
            ulong chest = entity.Component("Chest");
            if (chest != 0 && _reader.TryRead(chest + (ulong)_chestOpened, out byte openFlag))
            {
                opened = openFlag != 0;
            }

            PoiKind poi = PointsOfInterest.Classify(entity.Path, mapIcon);

            // Whether the game itself thinks this place is really there. An NPC that belongs
            // to a quest nobody has taken sits in the entity list all the same - awake, valid,
            // positioned, carrying a MinimapIcon whose row says "NPC" - and the overlay marked
            // a person standing in an empty map ("Lurking Creature", 2026-08). Nothing about
            // the entity says it is absent; the game's own answer is the targetable byte.
            //
            // Only for places, and never for monsters or chests, both of which already ask
            // this byte a different question: on a monster it is the corpse test above, and
            // on a chest a 0 means "already opened", which Opened carries instead.
            bool? present = null;
            if (poi != PoiKind.None && poi != PoiKind.Chest && kind != EntityKind.Monster)
            {
                ulong targetableComponent = entity.Component("Targetable");
                if (targetableComponent != 0
                    && _reader.TryRead(targetableComponent + (ulong)_isTargetable, out byte reachable))
                {
                    present = reachable != 0;
                }
            }

            // Would anything be DRAWN at this entity? That is the whole licence for skipping
            // the two reads below and no licence at all for skipping the third - see
            // SkipOffScreenReads. Both the entity's feet and the top of its model, because a
            // marker floats at the second and the two are not the same point.
            bool drawn = frustum is null || !SkipOffScreenReads
                || frustum.Margin(position.Value.X, position.Value.Y, position.Value.Z) >= -OffScreenMargin
                || frustum.Margin(
                    position.Value.X, position.Value.Y, position.Value.Z - position.Value.ModelBoundsZ)
                    >= -OffScreenMargin;

            // What is currently on it, and ONLY when somebody is drawing that. The floor keeps
            // the cost on the monsters a debuff timer is ever watched on; the friendly check
            // keeps it off your own minions, which are numerous and whose buffs nothing here
            // asks about.
            ActiveBuffs? buffs = null;
            if (ReadMonsterBuffs && drawn && kind == EntityKind.Monster && !friendly && rarity >= MonsterBuffFloor)
            {
                ulong monsterBuffs = entity.Component("Buffs");
                if (monsterBuffs != 0)
                {
                    buffs = _buffs.Read(monsterBuffs);
                }
            }

            // Where it is pointing and what it is doing. Only the things that can aim at you -
            // the player, and the monsters that are not on your side - because everything else
            // here is scenery, a drop, or your own summon.
            Aim? aim = null;
            bool wanted = kind == EntityKind.Player || (kind == EntityKind.Monster && !friendly);
            if (ReadAim && drawn && wanted)
            {
                aim = ReadAimOf(entity, renderAddress);
            }

            if (!drawn && wanted && (ReadAim || ReadMonsterBuffs))
            {
                offScreen++;
            }

            // What it has committed to, and where. Same audience as the aim, and read here
            // rather than by the consumer for the reason every other per-entity read is: the
            // Actor component has already been located by the walk above, so asking later
            // would mean resolving the whole component map a second time.
            // NOT GATED ON THE FRUSTUM, and that is the point of the split rather than an
            // oversight: this one feeds the evasion planner, which is a question about danger
            // to the PLAYER and not about what is on screen. A boss commits a beam from
            // outside the view volume and the player is still standing in it.
            ActorAction? action = null;
            if (ReadActions && (kind == EntityKind.Player || (kind == EntityKind.Monster && !friendly)))
            {
                ulong actorAddress = entity.Component("Actor");
                if (actorAddress != 0)
                {
                    action = _actions.Read(actorAddress);
                }
            }

            var world = new WorldEntity(
                id, address, entity.Path, kind,
                position.Value.X, position.Value.Y, position.Value.Z,
                position.Value.TerrainHeight, position.Value.ModelBoundsZ, rarity,
                poi, mapIcon,
                signs.Life, signs.EnergyShield, opened, friendly, signs.IsEffect,
                NameOf(address, renderAddress), renderAddress, present,
                Buffs: buffs, Aim: aim, Action: action,
                IsGroundEffect: entity.Component("GroundEffect") != 0,
                GroundSeconds: ReadGroundSeconds(entity),
                GroundRadius: ReadGroundRadius(entity),
                Beam: ReadBeam(entity),
                GroundType: ReadGroundType(entity));

            entities.Add(world);
            if (address == chain.PlayerEntity)
            {
                player = world;
            }
        }

        double entitiesMs = Since(entitiesFrom);
        long playerFrom = System.Diagnostics.Stopwatch.GetTimestamp();

        // The player is in the map too, but resolve it directly as a fallback so a frame
        // that missed it (mid-mutation) still knows where the camera is centred.
        if (player is null)
        {
            Entity? playerEntity = _entities.Read(chain.PlayerEntity);
            ulong renderAddress = playerEntity?.Component("Render") ?? 0;
            RenderComponent? position = renderAddress != 0 ? _render.Read(renderAddress) : null;
            if (playerEntity is not null && position is not null)
            {
                player = new WorldEntity(
                    0, chain.PlayerEntity, playerEntity.Path, EntityKind.Player,
                    position.Value.X, position.Value.Y, position.Value.Z,
                    position.Value.TerrainHeight, position.Value.ModelBoundsZ);
            }
        }

        // The player's pools ride along in the snapshot, so the reader thread reads them
        // once per tick and every consumer - overlay, auto-flask, config page - works from
        // the same instant rather than each issuing its own reads.
        Vitals? playerVitals = null;
        ActiveBuffs? playerBuffs = null;
        Entity? localPlayer = _entities.Read(chain.PlayerEntity);

        ulong lifeAddress = localPlayer?.Component("Life") ?? 0;
        if (lifeAddress != 0)
        {
            playerVitals = _life.Read(lifeAddress);
        }

        ulong buffsAddress = localPlayer?.Component("Buffs") ?? 0;
        if (buffsAddress != 0)
        {
            playerBuffs = _buffs.Read(buffsAddress);
        }

        // Both stay 0 when they cannot be read, and 0 is out of range for either - the schema
        // says a character is level 1-100 and an area is 0-100 - so "unreadable" and "really
        // that value" never collide. What a rule sees is null, which is what stops a threshold
        // firing on a number nobody produced.
        ulong playerComponent = localPlayer?.Component("Player") ?? 0;
        int playerLevel = playerComponent != 0
            ? _reader.Read<int>(playerComponent + (ulong)_playerLevelField)
            : 0;
        int areaLevel = _reader.Read<int>(chain.AreaInstance + (ulong)_areaLevel);

        // The flask belt hangs off ServerData, which is the INLINE LocalPlayerStruct's
        // first field - the same struct the player pointer comes from.
        FlaskBelt? flaskBelt = null;
        ulong serverData = _reader.ReadPointer(chain.AreaInstance + (ulong)_playerInfo + (ulong)_serverData);
        if (MemoryReaderExtensions.IsPlausiblePointer(serverData))
        {
            flaskBelt = _flasks.Read(serverData);
        }

        double playerMs = Since(playerFrom);
        long mapsFrom = System.Diagnostics.Stopwatch.GetTimestamp();

        MapView? largeMap = null;
        MapView? miniMap = null;
        IReadOnlyList<InterfacePart> hud = [];
        if (scale is UiScale viewport && chain.UiRoot != 0)
        {
            // Order matters: reading the minimap first is what leaves its diagonal cached
            // for the large map, which cannot supply its own.
            miniMap = _mapRadar.Read(chain.UiRoot, viewport, largeMap: false);
            largeMap = _mapRadar.Read(chain.UiRoot, viewport, largeMap: true);

            // Where the game's own interface is, so the map overlay can stay off it - the large
            // map is drawn across the whole window with the orbs and the bars painted on top,
            // and an overlay has no way underneath.
            //
            // THE MAPS AND EVERYTHING THEY HANG UNDER ARE EXCLUDED BY ADDRESS. Whatever the tree
            // turns out to look like, an element the minimap lives inside must never come back
            // as a piece of interface: that would take the minimap out of the region it is meant
            // to be drawn ON, and the radar would stop working while every readout reported a
            // perfectly healthy HUD.
            (ulong large, ulong mini) = _mapRadar.Resolve(chain.UiRoot);
            var notThese = new HashSet<ulong>();
            _uiElements.AndAncestors(large, notThese);
            _uiElements.AndAncestors(mini, notThese);

            hud = _hud.Read(chain.UiRoot, viewport, notThese);
        }

        // Which panels are in the way and where, from the same interface root that was just
        // resolved. Read every tick rather than on an interval: a panel opens and shuts between
        // two frames, and an overlay that lags a third of a second behind the stash is the
        // thing this exists to stop.
        //
        // The same viewport the maps were read with, which is what turns a panel into a
        // rectangle somebody's window can be compared against. Without one - a diagnostic run
        // with no overlay - the bits still arrive and the rectangles do not.
        PanelsOnScreen panels = _panels.Read(chain.UiRoot, scale);

        // The world screen's own furniture - the title bar and its act tabs, the search box, the
        // quest selector, the map legend - which is painted over the atlas exactly as the orbs
        // are painted over the map. Read ONLY while the atlas is actually open: it is a walk of
        // a screen's children, and there is nothing on the atlas to keep off while nobody is
        // looking at it.
        if ((panels.Panels & GamePanel.Atlas) != 0 && scale is UiScale on)
        {
            ulong atlas = chain.UiRoot;
            foreach (int step in _atlasPath)
            {
                atlas = _uiElements.Child(atlas, step);
            }

            hud = [.. hud, .. _hud.AtlasChrome(chain.UiRoot, atlas, on)];
        }

        double mapsMs = Since(mapsFrom);

        // Read BEFORE the terrain: which area this is decides which names apply to its tiles,
        // and the terrain read is where the tiles are looked at.
        AreaInfo area = _areas.Read(chain.WorldData);
        _terrain.CuratedLandmarks = _landmarkNames.For(area.Id);

        long terrainFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        TerrainGrid? terrain = _terrain.Read(chain.AreaInstance, nowMs);
        double terrainMs = Since(terrainFrom);

        // The places and drops the game has stopped listing, appended AFTER everything that
        // measures the read itself. Every count above - the cost breakdown, the corpse census,
        // the collapsed duplicates - is about what was READ this frame, and folding sightings
        // into those numbers would make a memory look like work being done.
        //
        // Last, because it wants the player: the rule that decides a thing is genuinely gone
        // rather than merely out of range measures from where the player is standing, and the
        // fallback above is what settles that on a frame the entity map did not.
        IReadOnlyList<WorldEntity> remembered = Memory.Update(areaHash, entities, player, nowMs);
        int live = entities.Count;
        entities.AddRange(remembered);

        return new WorldSnapshot(
            true, player, entities, matrix, largeMap, miniMap, playerVitals, playerBuffs, flaskBelt,
            area,
            terrain,
            areaHash,
            chain.State,
            new ReadCost(Since(started), entitiesMs, playerMs, terrainMs, mapsMs, live, skipped, offScreen),
            collapsed,
            new CorpseSigns(targetable, untargetable, unreadableTargetable, _corpses.Tracking),
            panels.Panels,
            remembered.Count,

            // Carried rather than re-resolved: the quest flags hang off this and the walk to
            // it is four dereferences that this read has already paid for.
            serverData,
            panels.Areas,
            areaLevel,
            playerLevel,
            hud,
            frustum,
            hovered);
    }

    /// <summary>How many names are worth remembering before starting over.</summary>
    /// <remarks>
    /// A bound on a dictionary filled from game memory, not a view about how many entities an
    /// area has. Monsters die and spawn all map long, each with an address of its own, so
    /// without this a long map grows it without limit.
    /// </remarks>
    private const int MostNamesRemembered = 4096;

    /// <summary>The name this entity shows, read once and remembered.</summary>
    private string NameOf(ulong entity, ulong render)
    {
        if (_names.TryGetValue(entity, out string? known))
        {
            return known;
        }

        if (_names.Count >= MostNamesRemembered)
        {
            _names.Clear();
        }

        string name = _render.ReadName(render);
        _names[entity] = name;
        return name;
    }

    /// <summary>Milliseconds since a timestamp.</summary>
    private static double Since(long from)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Classifies an entity by its metadata path. Path prefixes are how the game itself
    /// distinguishes entity types, and they are stable across patches - far more reliable
    /// than guessing from which components are present.
    /// </summary>
    public static EntityKind ClassifyPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.StartsWith("Metadata/Characters/", StringComparison.Ordinal))
        {
            return EntityKind.Player;
        }

        if (path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal))
        {
            return EntityKind.Monster;
        }

        // Straight from the AHK tool's _ClassifyEntityType, which arrived at it against this
        // game, and confirmed against the game's own file tree - Metadata/Projectiles is a real
        // folder in the bundles (e.g. Metadata/Projectiles/ShockingArrowFaridun).
        //
        // NOT the only shape a projectile comes in, and the difference is worth stating so
        // nobody looks for the missing half. A monster's skill often spawns its projectile
        // under the monster instead - Metadata/Monsters/VaalHumanoids/VaalHumanoidBow/objects/
        // LightningArrow, seen 36 times in one recorded map - and that path says Monsters, so
        // it classifies as one and is dropped by the hostile-effect rule (it carries no Life).
        // Changing that would put those back into every monster count and health bar the rule
        // exists to keep them out of; this covers the entities the game itself files as
        // projectiles, which is where a player's own skills put theirs.
        if (path.StartsWith("Metadata/Projectiles/", StringComparison.Ordinal))
        {
            return EntityKind.Projectile;
        }

        if (path.StartsWith("Metadata/Chests/", StringComparison.Ordinal))
        {
            return EntityKind.Chest;
        }

        if (path.Contains("/WorldItem", StringComparison.Ordinal))
        {
            return EntityKind.WorldItem;
        }

        if (path.StartsWith("Metadata/NPC", StringComparison.Ordinal))
        {
            return EntityKind.Npc;
        }

        if (path.StartsWith("Metadata/Effects/", StringComparison.Ordinal))
        {
            return EntityKind.Effect;
        }

        if (path.StartsWith("Metadata/Terrain/", StringComparison.Ordinal))
        {
            return EntityKind.Terrain;
        }

        // ── What used to fall through as Unknown ─────────────────────────────
        //
        // LAST, and only ever turning UNKNOWN into something. Every rule above still decides
        // first, so the noise filter, the alert rules, the health bars and the drawn-kind
        // filter see exactly what they saw before - these entities were Unknown, and Unknown
        // is drawn by nothing and styled as "anything else". Nothing on screen moves; the
        // browser stops saying it has no idea.
        //
        // A pet is its own kind rather than a monster, which is the reference's reading too:
        // GameHelper2 lists Metadata/Pet among the paths its monster classification refuses.
        // The prefix is deliberately "Pet" and not "Pets/" - that is the spelling in both the
        // reference's list and this project's own recording (Metadata/Pet/BetaKiwis/VaalKiwi).
        if (path.StartsWith("Metadata/Pet", StringComparison.Ordinal))
        {
            return EntityKind.Pet;
        }

        // The game's own furniture drawer, and the reason a hideout listed page after page of
        // nothing: benches, wells, stashes, lockers, waypoints and portals all live here.
        // Confirmed against the running game rather than supposed - Hideout/TransmutationBench,
        // Hideout/KaruiHealingWell, Hideout/ReforgingBench, Sanctum/SanctumLocker_Hideout and
        // Portals/DemonicApparitionPortal were all read out of one hideout.
        if (path.StartsWith("Metadata/MiscellaneousObjects/", StringComparison.Ordinal))
        {
            // A way out is worth telling apart from the furniture. Both words rather than the
            // folder alone, because the game files exits under either - the Portals folder,
            // and a name ending in Transition - which is the same breadth the points-of-
            // interest rules settled on after an exit built into terrain went missing.
            return path.Contains("Portal", StringComparison.OrdinalIgnoreCase)
                   || path.Contains("Transition", StringComparison.OrdinalIgnoreCase)
                ? EntityKind.Portal
                : EntityKind.Object;
        }

        return EntityKind.Unknown;
    }

    /// <summary>
    /// Which family of objects this one belongs to - "Hideout", "Sanctum" - or empty.
    /// </summary>
    /// <remarks>
    /// The folder the game files it under, one level below its bucket. That segment is the
    /// difference between two entities that are otherwise the same word: a Reforging Bench is
    /// hideout furniture and a Relic Locker is Sanctum's, and the path says so where the kind
    /// alone cannot.
    ///
    /// NOT a kind of its own, and that is the point. The families run to a dozen and gain one
    /// every league, so an enum would need a value per league and every saved alert rule would
    /// be renumbered around it - see the note on <see cref="EntityKind.Projectile"/>. This is a
    /// LABEL, read from the path each time it is drawn, and nothing stores it.
    ///
    /// MiscellaneousObjects only, because that is the bucket whose contents differ by folder.
    /// Monsters and chests have families too - Skeletons, StrongBoxes - and could be described
    /// the same way, but nobody has asked to read them that way and a rule nobody wants is a
    /// rule that only ever gets in the way.
    /// </remarks>
    public static string FamilyOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        const string bucket = "Metadata/MiscellaneousObjects/";
        if (!path.StartsWith(bucket, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> rest = path.AsSpan(bucket.Length);
        int slash = rest.IndexOf('/');

        // No sub-folder means no family: Metadata/MiscellaneousObjects/Stash is just a stash,
        // and calling it a "Stash Object" would be saying one thing twice.
        return slash <= 0 ? string.Empty : rest[..slash].ToString();
    }

    /// <summary>What to call an entity's kind in a list, family included where there is one.</summary>
    /// <remarks>
    /// "Hideout Object", "Sanctum Object" - the shape somebody reading the list asked for.
    /// A family that repeats the kind is dropped rather than printed: the game files portals
    /// under a Portals folder, and "Portals Portal" is noise where "Portal" is an answer.
    /// </remarks>
    public static string DescribeKind(EntityKind kind, string path)
    {
        string family = FamilyOf(path);
        string name = kind.ToString();

        if (family.Length == 0
            || name.Contains(family, StringComparison.OrdinalIgnoreCase)
            || family.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return $"{family} {name}";
    }
}
