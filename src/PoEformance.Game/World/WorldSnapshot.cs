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
    int Skipped)
{
    /// <summary>Everything not attributed to a phase: the chain, the matrix, the area.</summary>
    public double OtherMs => Math.Max(0, TotalMs - EntitiesMs - PlayerMs - TerrainMs - MapsMs);

    /// <summary>A one-line summary for a status readout.</summary>
    public override string ToString()
        => $"{TotalMs:F1} ms = ent {EntitiesMs:F1} ({Entities}, {Skipped} skipped)"
           + $"  player {PlayerMs:F1}  terrain {TerrainMs:F1}  maps {MapsMs:F1}  other {OtherMs:F1}";
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
    string Name = "")
{
    /// <summary>Whether this is a chest somebody has already been through.</summary>
    public bool IsSpent => Opened == true;
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
    ReadCost Cost = default)
{
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
    private readonly GroundItemReader _groundItems;
    private readonly MinimapIconReader _mapIcons;
    private LandmarkNames _landmarkNames = LandmarkNames.Empty;
    private readonly WorldAreaReader _areas;
    private readonly TerrainReader _terrain;
    private readonly int _playerInfo;
    private readonly int _serverData;
    private readonly int _awakeEntities;
    private readonly int _w2sMatrix;
    private readonly int _areaHash;
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
        _flasks = new FlaskBeltReader(reader, schema);
        _groundItems = new GroundItemReader(reader, schema);
        _mapIcons = new MinimapIconReader(reader, schema);
        _areas = new WorldAreaReader(reader, schema);
        _terrain = new TerrainReader(reader, schema, rotation);
        _playerInfo = schema.Structs["AreaInstance"].OffsetOf("PlayerInfo");
        _serverData = schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr");
        _awakeEntities = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");
        _w2sMatrix = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
        _areaHash = schema.Structs["AreaInstance"].OffsetOf("CurrentAreaHash");

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
        bool friendly = false;
        ulong positioned = entity.Component("Positioned");
        if (positioned != 0 && _reader.TryRead(positioned + (ulong)_reaction, out byte reaction))
        {
            friendly = (reaction & 0x7F) == 0x01;
        }

        // Whether it expires on its own. Presence of the component is the whole answer, so
        // this costs a lookup and no read at all.
        bool temporary = entity.Component("DiesAfterTime") != 0;

        return new MonsterSigns(
            health, targetable, monsterRarity >= ItemRarity.Unique, monsterRarity, pool, shield,
            friendly, temporary);
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
        Dictionary<uint, ulong> pointers = _map.ReadEntityPointers(mapStruct, maxEntities);
        int skipped = 0;

        var entities = new List<WorldEntity>(pointers.Count);
        WorldEntity? player = null;
        long nowMs = Environment.TickCount64;

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

            RenderComponent? position = _render.Read(renderAddress);
            if (position is null)
            {
                continue;
            }

            EntityKind kind = ClassifyPath(entity.Path);

            // Corpses stay in the entity map long after the fight, so without this the
            // overlay marks a cleared screen full of dead monsters. Only monsters are
            // checked: the same targetable byte also goes to 0 on an OPENED chest, which
            // is a different question and not this one.
            MonsterSigns signs = kind == EntityKind.Monster ? ReadMonsterSigns(entity) : default;
            if (kind == EntityKind.Monster && _corpses.IsCorpse(address, signs, nowMs))
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
                continue;
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

            var world = new WorldEntity(
                id, address, entity.Path, kind,
                position.Value.X, position.Value.Y, position.Value.Z,
                position.Value.TerrainHeight, position.Value.ModelBoundsZ, rarity,
                PointsOfInterest.Classify(entity.Path, mapIcon), mapIcon,
                signs.Life, signs.EnergyShield, opened, signs.Friendly, signs.IsEffect,
                NameOf(address, renderAddress));

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
        if (scale is UiScale viewport && chain.UiRoot != 0)
        {
            // Order matters: reading the minimap first is what leaves its diagonal cached
            // for the large map, which cannot supply its own.
            miniMap = _mapRadar.Read(chain.UiRoot, viewport, largeMap: false);
            largeMap = _mapRadar.Read(chain.UiRoot, viewport, largeMap: true);
        }

        double mapsMs = Since(mapsFrom);

        // Read BEFORE the terrain: which area this is decides which names apply to its tiles,
        // and the terrain read is where the tiles are looked at.
        AreaInfo area = _areas.Read(chain.WorldData);
        _terrain.CuratedLandmarks = _landmarkNames.For(area.Id);

        long terrainFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        TerrainGrid? terrain = _terrain.Read(chain.AreaInstance, nowMs);
        double terrainMs = Since(terrainFrom);

        return new WorldSnapshot(
            true, player, entities, matrix, largeMap, miniMap, playerVitals, playerBuffs, flaskBelt,
            area,
            terrain,
            areaHash,
            chain.State,
            new ReadCost(Since(started), entitiesMs, playerMs, terrainMs, mapsMs, entities.Count, skipped));
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

        return EntityKind.Unknown;
    }
}
