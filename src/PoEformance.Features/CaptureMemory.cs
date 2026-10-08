using System.Globalization;
using System.Text;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Items;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// The memory half of a capture: everything the tool knows how to read, read once more from nothing, inside the recording - and an index of it.
/// </summary>
/// <remarks>
/// WHY ONCE MORE, AND FROM NOTHING. The recording holds what is read while it runs, and the
/// overlay's own reader reads most things once and keeps them: the terrain once per area, the
/// loaded files once per area, an entity's path and components once per entity. Three seconds of
/// it are three seconds of the per-frame part only. So the capture reads everything again with a
/// reader that has kept nothing - a fresh <see cref="WorldReader"/>, a fresh
/// <see cref="PreloadReader"/> - and the recording waits for it.
///
/// EVERY SWITCH ON. The overlay's reader leaves out what no visible feature wants - the game's
/// visual entities, the ground effects, monster buffs, aim and actions, entities off screen, the
/// ones the noise filter refuses on their path, the room-level probe. A capture is for questions
/// nobody has asked yet, so this one reads all of them, and as many entities as the game lists.
///
/// THEN THE RAW BYTES: <see cref="CaptureSweep"/> reads the chain's roots whole, one hop further
/// along every pointer in them, and every component of the entities around the player - the
/// bytes no reader understands yet, which is where a new question's answer is.
///
/// ON ITS OWN THREAD, not the feed's: the terrain alone is a second's work on a large map, and
/// the feed also drives the flasks. The recording's reader is shared and its writing is
/// serialised, so the reads land in the file whichever thread made them.
/// </remarks>
public static class CaptureMemory
{
    /// <summary>The index of what the pass read, beside the recording.</summary>
    public const string IndexFile = "memory.txt";

    /// <summary>As many entities as the game lists - the overlay's own read stops at 512.</summary>
    public const int MostEntities = 4096;

    /// <summary>The largest read the capture's recording keeps: the format's own largest, which takes the terrain's grid and tile array whole.</summary>
    public const int MaxReadBytes = RecordingFormat.MaxReadLength;

    /// <summary>The size the capture's recording stops growing at.</summary>
    public const long MaxTotalBytes = 96L * 1024 * 1024;

    /// <summary>
    /// The pass: a fresh world read with every switch on, the loaded files, then the raw sweep. Returns the index.
    /// </summary>
    /// <param name="reader">The recording's reader, so every read lands in it.</param>
    /// <param name="schema">The offsets.</param>
    /// <param name="gameStatesStatic">The GameStates static.</param>
    /// <param name="rotation">The terrain's rotation tables, as the overlay's reader has them.</param>
    /// <param name="names">The item names, as the overlay's reader has them, or null.</param>
    /// <param name="landmarks">The curated landmark names, or null.</param>
    /// <param name="animations">A FRESH copy of the animation table, so each animation the game names is asked for again - or null.</param>
    /// <param name="scale">The viewport, for the interface's elements.</param>
    /// <param name="fileRoot">The FileRoot static, or zero.</param>
    /// <param name="areaCounter">The AreaChangeCounter static, or zero.</param>
    public static string Pass(
        IMemoryReader reader,
        OffsetSchema schema,
        ulong gameStatesStatic,
        TerrainRotationTables rotation,
        ItemNames? names,
        LandmarkNames? landmarks,
        AnimationNames? animations,
        UiScale scale,
        ulong fileRoot,
        ulong areaCounter)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        var said = new StringBuilder();
        long started = Environment.TickCount64;

        var world = new WorldReader(reader, schema, rotation, names)
        {
            KeepEffects = true,
            ReadVisualEntities = true,
            ReadMonsterBuffs = true,
            MonsterBuffFloor = ItemRarity.Normal,
            ReadAim = true,
            ReadActions = true,
            ProbeRooms = true,
            SkipOffScreenReads = false,
        };
        world.Noise.Enabled = false;
        world.AnimationNames = animations;
        if (landmarks is not null)
        {
            world.LandmarkNames = landmarks;
        }

        WorldSnapshot snapshot = world.Read(gameStatesStatic, MostEntities, scale);
        said.Append("=== a fresh world read, every switch on: ").AppendLine(Ms(started));
        said.Append("state ").Append(snapshot.State).Append(", in game ").Append(snapshot.InGame ? "yes" : "no")
            .Append(", area ").Append(snapshot.Area.Id).Append(" hash 0x").AppendLine(snapshot.AreaHash.ToString("X8", CultureInfo.InvariantCulture));
        said.Append(Say(snapshot.Entities.Count)).AppendLine(" entities read");
        foreach (IGrouping<EntityKind, WorldEntity> kind in snapshot.Entities.GroupBy(one => one.Kind).OrderByDescending(group => group.Count()))
        {
            said.Append("  ").Append(kind.Key).Append(' ').AppendLine(Say(kind.Count()));
        }

        said.AppendLine(snapshot.Terrain is TerrainGrid grid
            ? $"terrain {grid.Width} x {grid.Height} cells, {grid.TilesX} x {grid.TilesY} tiles, {grid.Rooms.Count} tile rooms, heights {(grid.HasHeights ? "read" : "not read")}, tiles {(grid.Tiles is null ? "not read" : "read")}"
            : "terrain not read");

        long loadedFrom = Environment.TickCount64;
        said.AppendLine().Append("=== the area's loaded files: ");
        if (fileRoot == 0 || areaCounter == 0)
        {
            said.AppendLine("the file root or the area counter did not resolve");
        }
        else
        {
            var preload = new PreloadReader(reader, schema);
            int counter = preload.AreaChangeCount(areaCounter);
            HashSet<string> files = preload.Read(fileRoot, counter);
            said.Append(Say(files.Count)).Append(" files for area ").Append(Say(counter)).Append(", ").AppendLine(Ms(loadedFrom));
            if (preload.LastError.Length > 0)
            {
                said.AppendLine(preload.LastError);
            }
        }

        long sweptFrom = Environment.TickCount64;
        var sweep = new CaptureSweep(reader, schema);
        List<string> index = sweep.Run(gameStatesStatic, snapshot.Entities, snapshot.Player?.WorldX ?? 0f, snapshot.Player?.WorldY ?? 0f);
        said.AppendLine().Append("=== raw: the roots, one hop from them, the components around the player: ").AppendLine(Ms(sweptFrom));
        foreach (string line in index)
        {
            said.AppendLine(line);
        }

        said.AppendLine().Append("the whole pass took ").AppendLine(Ms(started));
        return said.ToString();
    }

    private static string Ms(long from) => $"{Environment.TickCount64 - from} ms";

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);
}
