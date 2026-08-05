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
    ItemRarity Rarity = ItemRarity.Unknown)
{
    /// <summary>Where the game floats this entity's health bar: the top of its model.</summary>
    public float HealthBarZ => WorldZ - ModelBoundsZ;

    /// <summary>Short display name: the last segment of the metadata path.</summary>
    public string ShortName
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
    uint AreaHash = 0)
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
    private readonly GroundItemReader _groundItems;
    private readonly WorldAreaReader _areas;
    private readonly TerrainReader _terrain;
    private readonly int _playerInfo;
    private readonly int _serverData;
    private readonly int _awakeEntities;
    private readonly int _w2sMatrix;
    private readonly int _areaHash;
    private readonly int _lifeHealth;
    private readonly int _vitalCurrent;
    private readonly int _isTargetable;
    private readonly int _monsterRarity;

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
        _areas = new WorldAreaReader(reader, schema);
        _terrain = new TerrainReader(reader, schema, rotation);
        _playerInfo = schema.Structs["AreaInstance"].OffsetOf("PlayerInfo");
        _serverData = schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr");
        _awakeEntities = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");
        _w2sMatrix = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
        _areaHash = schema.Structs["AreaInstance"].OffsetOf("CurrentAreaHash");

        // Read straight from the schema rather than through LifeReader: a corpse check
        // needs one int per monster, and building the full three-pool Vitals for every
        // monster on screen every frame would be most of a read budget spent on two
        // numbers that are thrown away.
        _lifeHealth = schema.Structs["Life"].OffsetOf("Health");
        _vitalCurrent = schema.Structs["Vital"].OffsetOf("Current");
        _isTargetable = schema.Structs["Targetable"].OffsetOf("IsTargetable");
        _monsterRarity = schema.Structs["ObjectMagicProperties"].OffsetOf("Rarity");
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
        ulong life = entity.Component("Life");
        if (life != 0 && _reader.TryRead(life + (ulong)_lifeHealth + (ulong)_vitalCurrent, out int current))
        {
            health = current;
        }

        bool? targetable = null;
        ulong targetableComponent = entity.Component("Targetable");
        if (targetableComponent != 0
            && _reader.TryRead(targetableComponent + (ulong)_isTargetable, out byte flag))
        {
            targetable = flag != 0;
        }

        // Rarity 3 and above is Unique/Boss. This is only needed to spare bosses the
        // targetable rule, so an unreadable rarity simply means "not a boss" - the worst
        // case is a boss dot blinking during a phase, never a live monster hidden.
        bool isBoss = false;
        ulong properties = entity.Component("ObjectMagicProperties");
        if (properties != 0 && _reader.TryRead(properties + (ulong)_monsterRarity, out int rarity))
        {
            isBoss = rarity >= 3;
        }

        return new MonsterSigns(health, targetable, isBoss);
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
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            return WorldSnapshot.Empty;
        }

        var matrix = new float[16];
        _reader.TryRead(
            chain.WorldData + (ulong)_w2sMatrix,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(matrix.AsSpan()));

        // The map struct sits INLINE in AreaInstance: pass its address, not a pointer read
        // from it (its first field is the head, which is why reading it as a pointer works
        // for the drift report but is not what the traversal needs).
        ulong mapStruct = chain.AreaInstance + (ulong)_awakeEntities;
        Dictionary<uint, ulong> pointers = _map.ReadEntityPointers(mapStruct, maxEntities);

        var entities = new List<WorldEntity>(pointers.Count);
        WorldEntity? player = null;
        long nowMs = Environment.TickCount64;

        foreach ((uint id, ulong address) in pointers)
        {
            Entity? entity = _entities.Read(address);
            if (entity is null || entity.Path.Length == 0)
            {
                continue;
            }

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
            if (kind == EntityKind.Monster && _corpses.IsCorpse(address, ReadMonsterSigns(entity), nowMs))
            {
                continue;
            }

            // Resolved here rather than filtered here: how good a drop is, is a fact about
            // the world, while "is it worth drawing" is a preference. The snapshot carries
            // the fact so the overlay - and a future loot tracker, which wants everything -
            // can each decide for themselves.
            ItemRarity rarity = kind == EntityKind.WorldItem
                ? _groundItems.RarityOf(entity, nowMs)
                : ItemRarity.Unknown;

            var world = new WorldEntity(
                id, address, entity.Path, kind,
                position.Value.X, position.Value.Y, position.Value.Z,
                position.Value.TerrainHeight, position.Value.ModelBoundsZ, rarity);

            entities.Add(world);
            if (address == chain.PlayerEntity)
            {
                player = world;
            }
        }

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

        MapView? largeMap = null;
        MapView? miniMap = null;
        if (scale is UiScale viewport && chain.UiRoot != 0)
        {
            // Order matters: reading the minimap first is what leaves its diagonal cached
            // for the large map, which cannot supply its own.
            miniMap = _mapRadar.Read(chain.UiRoot, viewport, largeMap: false);
            largeMap = _mapRadar.Read(chain.UiRoot, viewport, largeMap: true);
        }

        return new WorldSnapshot(
            true, player, entities, matrix, largeMap, miniMap, playerVitals, playerBuffs, flaskBelt,
            _areas.Read(chain.WorldData),
            _terrain.Read(chain.AreaInstance, nowMs),
            _reader.Read<uint>(chain.AreaInstance + (ulong)_areaHash));
    }

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
