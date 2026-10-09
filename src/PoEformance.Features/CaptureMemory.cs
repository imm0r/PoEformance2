using System.Globalization;
using System.Text;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Items;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>What the capture key asks of the memory half: where to write, and what the pass inside the recording reads.</summary>
/// <param name="Folder">The capture's folder.</param>
/// <param name="Reads">What the ticked parts need read once more - see <see cref="CaptureParts.Reads"/>.</param>
public sealed record CaptureMemoryAsk(string Folder, CaptureReads Reads);

/// <summary>
/// The memory half of a capture: what the ticked parts need, read once more from nothing inside the recording - and an index of it.
/// </summary>
/// <remarks>
/// WHY ONCE MORE, AND FROM NOTHING. The recording holds what is read while it runs, and the
/// overlay's own reader reads most things once and keeps them: the terrain once per area, the
/// loaded files once per area, an entity's path and components once per entity. Three seconds of
/// it are three seconds of the per-frame part only. So the capture reads again with a reader that
/// has kept nothing - a fresh <see cref="WorldReader"/>, a fresh <see cref="PreloadReader"/> - and
/// the recording waits for it.
///
/// EVERY SWITCH ON, where the world is read. The overlay's reader leaves out what no visible
/// feature wants - the game's visual entities, the ground effects, monster buffs, aim and actions,
/// entities off screen, the ones the noise filter refuses on their path, the room-level probe. A
/// capture is for questions nobody has asked yet, so this one reads all of them, and as many
/// entities as the game lists.
///
/// ONLY WHAT WAS ASKED - see <see cref="CaptureReads"/>. One pass used to read all of it on every
/// press: the world, the files, the raw sweep. A question about a room's doodads needs the entity
/// maps and nothing of the sweep's megabytes; the parts that are ticked say what they need and
/// the pass reads the union. The entity maps are the one read the overlay never makes whole
/// (SleepingDoodads, every named entity), and the raw bytes - <see cref="CaptureSweep"/>: the
/// chain's roots, one hop further along every pointer in them, every component of the entities
/// around the player - are the part for the questions nobody has asked.
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

    /// <summary>Whether the pass reads the world for these asks - asked for itself, or by the sweep, which follows the entities the world read lists.</summary>
    public static bool ReadsWorld(CaptureReads reads) => (reads & (CaptureReads.World | CaptureReads.Sweep)) != 0;

    /// <summary>What the pass reads, in a few words - for the line that says what the recording holds.</summary>
    public static string Said(CaptureReads reads)
    {
        if (reads == CaptureReads.None)
        {
            return "nothing more";
        }

        var parts = new List<string>(4);
        if (ReadsWorld(reads))
        {
            parts.Add("the world with every switch on");
        }

        if ((reads & CaptureReads.Loaded) != 0)
        {
            parts.Add("the loaded files");
        }

        if ((reads & CaptureReads.Doodads) != 0)
        {
            parts.Add("both entity maps with every named entity's model");
        }

        if ((reads & CaptureReads.Sweep) != 0)
        {
            parts.Add("the raw bytes of the roots, one hop beyond them and the components around you");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The pass: whatever <paramref name="reads"/> asks for, in the order the stages need each other. Returns the index.
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
    /// <param name="reads">What the ticked parts need - see <see cref="CaptureParts.Reads"/>.</param>
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
        ulong areaCounter,
        CaptureReads reads)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        var said = new StringBuilder();
        long started = Environment.TickCount64;
        said.Append("the pass reads ").AppendLine(Said(reads));
        if (reads == CaptureReads.None)
        {
            said.AppendLine("the recording holds the overlay's own reads alone - no ticked part asked for more");
            return said.ToString();
        }

        WorldSnapshot? snapshot = null;
        if (ReadsWorld(reads))
        {
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

            snapshot = world.Read(gameStatesStatic, MostEntities, scale);
            said.AppendLine().Append("=== a fresh world read, every switch on: ").AppendLine(Ms(started));
            said.Append("state ").Append(snapshot.State).Append(", in game ").Append(snapshot.InGame ? "yes" : "no")
                .Append(", area ").Append(snapshot.Area.Id).Append(" hash 0x").AppendLine(snapshot.AreaHash.ToString("X8", CultureInfo.InvariantCulture));
            said.Append("environment ").AppendLine(snapshot.Area.Environment.Length > 0 ? snapshot.Area.Environment : "(did not resolve)");
            said.Append(Say(snapshot.Entities.Count)).AppendLine(" entities read");
            foreach (IGrouping<EntityKind, WorldEntity> kind in snapshot.Entities.GroupBy(one => one.Kind).OrderByDescending(group => group.Count()))
            {
                said.Append("  ").Append(kind.Key).Append(' ').AppendLine(Say(kind.Count()));
            }

            said.AppendLine(snapshot.Terrain is TerrainGrid grid
                ? $"terrain {grid.Width} x {grid.Height} cells, {grid.TilesX} x {grid.TilesY} tiles, {grid.Rooms.Count} tile rooms, heights {(grid.HasHeights ? "read" : "not read")}, tiles {(grid.Tiles is null ? "not read" : "read")}"
                : "terrain not read");
        }

        if ((reads & CaptureReads.Loaded) != 0)
        {
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
        }

        if ((reads & CaptureReads.Doodads) != 0)
        {
            // THE AREA FROM THE CHAIN, resolved here rather than taken from the overlay's last frame:
            // the pass is the recording's, and a replay must find every read it made inside it.
            long doodadsFrom = Environment.TickCount64;
            GameChainAddresses chain = GameChain.Resolve(reader, schema, gameStatesStatic);
            DoodadSurvey survey = SleepingDoodads.Read(reader, schema, chain.AreaInstance, paths: null);
            said.AppendLine().Append("=== both entity maps, every named entity's path, position and model: ").AppendLine(Ms(doodadsFrom));
            said.AppendLine(survey.Why.Length > 0
                ? survey.Why
                : string.Create(CultureInfo.InvariantCulture,
                    $"sleeping map {survey.SleepingNodes} of {survey.SleepingSize} walked, awake {survey.AwakeNodes}, {survey.Named} with a path, {survey.Found.Count} with a position"));
        }

        if ((reads & CaptureReads.Sweep) != 0 && snapshot is not null)
        {
            long sweptFrom = Environment.TickCount64;
            var sweep = new CaptureSweep(reader, schema);
            List<string> index = sweep.Run(gameStatesStatic, snapshot.Entities, snapshot.Player?.WorldX ?? 0f, snapshot.Player?.WorldY ?? 0f);
            said.AppendLine().Append("=== raw: the roots, one hop from them, the components around the player: ").AppendLine(Ms(sweptFrom));
            foreach (string line in index)
            {
                said.AppendLine(line);
            }
        }

        said.AppendLine().Append("the whole pass took ").AppendLine(Ms(started));
        return said.ToString();
    }

    private static string Ms(long from) => $"{Environment.TickCount64 - from} ms";

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);
}
