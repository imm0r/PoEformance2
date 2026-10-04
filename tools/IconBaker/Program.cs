using System.Runtime.InteropServices;
using PoEformance.Features;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Tools.IconBaker;

/// <summary>
/// Bakes exported boss pictures into the icon sheet: the paste that used to be done by hand.
/// </summary>
/// <remarks>
/// WHAT IT REPLACES: open assets/icons.png in an image editor, find where the art ends, paste
/// a 64-pixel Active and Inactive side by side, work out which cell numbers that made, and
/// type both names into a table - for each of a hundred bosses. Every step of that is
/// mechanical, and the one that goes wrong quietly is the cell number.
///
/// THE SAME RULE AS THE OVERLAY. Placement comes from <see cref="IconSheetGrowth"/>, which the
/// overlay also uses to draw exports live, so baking does not move anything somebody already
/// saw on the map. A family already ours is painted over in place (a re-posed boss keeps its
/// number); a family the game's own art carries is reported and left alone.
///
/// AND THE ENTRIES THAT SAY WHERE THEY GO. The export also writes area, tile and name into
/// data/boss-icons.json - the copy beside the executable that made it, not the repository's.
/// That copy is found the way the tool found it (FindDataFile: data/ in the executable's folder
/// or the nearest one above it) and merged into the repository's with
/// <see cref="BossIcons.Merge"/>, so pictures and entries arrive in the same commit.
///
/// WRITES NOTHING UNTIL IT KNOWS EVERYTHING FITS, and nothing at all when nothing changed -
/// re-running it over the same folder is a no-op, not a re-encode of a five-megabyte file.
/// </remarks>
internal static class Program
{
    private const string Usage =
        "usage: dotnet run --project tools/IconBaker -- <exports folder> [options]\n"
        + "  <exports folder>   where the model pane wrote <Family>Active.png / <Family>Inactive.png\n"
        + "  --dry-run          say what would change, write nothing\n"
        + "  --repo <path>      the repository root, when not run from inside it\n"
        + "  --entries <path>   the boss-icons.json the tool wrote, when it is not in data/ beside\n"
        + "                     the exports folder (found the way the tool finds it)\n"
        + "  --no-entries       bake the pictures only, leave data/boss-icons.json alone";

    /// <summary>The entries file, under data/ - the name the tool loads it by.</summary>
    private const string EntriesFile = "boss-icons.json";

    /// <summary>The PNG the sheet is written back as: the format it shipped in, compressed hard.</summary>
    /// <remarks>
    /// RGBA at eight bits, as the original is. Transparent pixels keep whatever colour they
    /// carried, so the game's rows come back byte for byte rather than "the same picture".
    /// </remarks>
    private static readonly PngEncoder Encoder = new()
    {
        ColorType = PngColorType.RgbWithAlpha,
        BitDepth = PngBitDepth.Bit8,
        CompressionLevel = PngCompressionLevel.BestCompression,
        TransparentColorMode = PngTransparentColorMode.Preserve,
    };

    /// <summary>Contiguous, so the sheet's pixels are one span. See IconCache.Contiguous.</summary>
    private static readonly Configuration Contiguous = Contiguously();

    private static Configuration Contiguously()
    {
        Configuration configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        return configuration;
    }

    private static int Main(string[] args)
    {
        string exports = string.Empty;
        string repository = string.Empty;
        string entries = string.Empty;
        bool dry = false;
        bool noEntries = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dry = true; break;
                case "--no-entries": noEntries = true; break;
                case "--repo" when i + 1 < args.Length: repository = args[++i]; break;
                case "--entries" when i + 1 < args.Length: entries = args[++i]; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || exports.Length > 0)
                    {
                        Console.Error.WriteLine($"not understood: {args[i]}\n{Usage}");
                        return 1;
                    }

                    exports = args[i];
                    break;
            }
        }

        if (exports.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (!Directory.Exists(exports))
        {
            Console.Error.WriteLine($"no folder at {exports}");
            return 1;
        }

        repository = repository.Length > 0 ? repository : Root(Directory.GetCurrentDirectory());
        if (repository.Length == 0 || !File.Exists(Path.Combine(repository, "assets", IconSheet.Resource)))
        {
            Console.Error.WriteLine($"no repository found - run inside it or pass --repo\n{Usage}");
            return 1;
        }

        if (entries.Length > 0 && !File.Exists(entries))
        {
            Console.Error.WriteLine($"no file at {entries}");
            return 1;
        }

        try
        {
            string from = noEntries ? string.Empty : entries.Length > 0 ? entries : Beside(exports);
            return Bake(exports, repository, from, noEntries, dry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or UnknownImageFormatException or InvalidImageContentException or ArgumentException)
        {
            Console.Error.WriteLine($"stopped, nothing written: {exception.Message}");
            return 2;
        }
    }

    /// <summary>
    /// The pictures, then the entries - with both entry files read and checked first.
    /// </summary>
    /// <remarks>
    /// THE CHECK COMES BEFORE THE SHEET IS WRITTEN, so a repository file that does not parse
    /// stops the run with nothing changed rather than half of it baked. Saving the entries
    /// comes after, so a sheet that could not be written never leaves entries pointing at
    /// pictures the sheet does not have.
    /// </remarks>
    private static int Bake(string exports, string repository, string from, bool noEntries, bool dry)
    {
        string target = Path.Combine(repository, "data", EntriesFile);
        BossIcons? theirs = null;
        BossIcons? ours = null;

        if (noEntries)
        {
            // Asked for, so nothing to say.
        }
        else if (from.Length == 0)
        {
            Console.WriteLine($"entries: no data/{EntriesFile} beside {exports} - pass --entries to name it");
        }
        else if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"entries: the tool already writes the repository's own {EntriesFile}");
        }
        else
        {
            theirs = BossIcons.Load(from);
            ours = BossIcons.Load(target);
            if (theirs.Unreadable || ours.Unreadable)
            {
                Console.Error.WriteLine(
                    $"stopped, nothing written: {(theirs.Unreadable ? from : target)} is there and could not be read");
                return 2;
            }
        }

        int baked = BakeSheet(exports, repository, dry);
        if (baked != 0 || theirs is null || ours is null)
        {
            return baked;
        }

        List<BossIconChange> changes = ours.Merge(theirs);
        foreach (BossIconChange change in changes)
        {
            string verb = change.Was.Length == 0 ? "add" : change.Now.Length == 0 ? "remove" : "change";
            string value = change.Was.Length > 0 && change.Now.Length > 0
                ? $"{change.Was} -> {change.Now}"
                : change.Now.Length > 0 ? change.Now : change.Was;
            Console.WriteLine($"  {verb,-7} {change.Section}: {change.Key} = {value}");
        }

        if (changes.Count == 0)
        {
            Console.WriteLine($"entries: nothing new in {from}");
            return 0;
        }

        if (dry)
        {
            Console.WriteLine($"entries: {changes.Count} change(s) - dry run, nothing written");
            return 0;
        }

        if (!ours.Save(out string said))
        {
            Console.Error.WriteLine($"entries: {said}");
            return 2;
        }

        Console.WriteLine($"entries: {said} - commit {Path.GetRelativePath(repository, target)} too.");
        return 0;
    }

    /// <summary>
    /// The entries file the tool beside an exports folder wrote to, found the way it finds it.
    /// </summary>
    /// <remarks>
    /// THE SAME WALK AS FindDataFile IN THE APP, and it has to be: the exports folder is
    /// MonsterPortrait.Folder, AppContext.BaseDirectory/exports, and the file an export's entry
    /// went into is the first data/boss-icons.json from that base directory upwards. Guessing a
    /// fixed sibling instead would pick the wrong file on a development build, whose bin folder
    /// has a data/ of its own.
    /// </remarks>
    private static string Beside(string exports)
    {
        DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(exports)).Parent;
        for (; directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "data", EntriesFile);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static int BakeSheet(string exports, string repository, bool dry)
    {
        string assets = Path.Combine(repository, "assets");
        string sheetPath = Path.Combine(assets, IconSheet.Resource);
        string customPath = Path.Combine(assets, IconSheet.CustomNameTable);

        Dictionary<int, string> game = Table(Path.Combine(assets, IconSheet.NameTable));
        Dictionary<int, string> custom = Table(customPath);
        Dictionary<string, int> byName = Reverse(game, custom);
        int lastNamed = Math.Max(Max(game), Max(custom)) - 1;

        List<string> families = IconSheetGrowth.Families(Directory.EnumerateFiles(exports, "*.png"));
        if (families.Count == 0)
        {
            Console.WriteLine($"no <Family>{BossIcons.ActiveSuffix}.png in {exports} - nothing to bake");
            return 0;
        }

        Image<Rgba32> sheet = Image.Load<Rgba32>(new DecoderOptions { Configuration = Contiguous }, sheetPath);
        try
        {
            int columns = IconSheet.ColumnsIn(sheet.Width);
            int rows = IconSheet.RowsIn(sheet.Height);
            int last = IconSheetGrowth.LastOccupied(Pixels(sheet), sheet.Width, sheet.Height);
            int first = IconSheetGrowth.FirstFree(columns, last, lastNamed);
            IconGrowth growth = IconSheetGrowth.Place(
                families,
                name => byName.TryGetValue(name, out int cell) ? cell : 0,
                columns,
                first,
                IconSheet.MaxEdge / IconSheet.Tile);

            foreach (string family in growth.Game)
            {
                Console.WriteLine($"  skip    {family}: the game's own art already carries it");
            }

            if (growth.Full.Count > 0)
            {
                Console.Error.WriteLine(
                    $"no room for {growth.Full.Count} more under {IconSheet.MaxEdge} px: {string.Join(", ", growth.Full)}");
                return 2;
            }

            // Every picture is read and checked BEFORE the sheet is touched, so a bad file stops
            // the run with nothing half-written.
            var cells = new List<(int Index, byte[] Pixels, string Name, bool Fresh)>();
            foreach (IconPlacement placement in growth.Placed)
            {
                Add(cells, exports, BossIcons.Named(placement.Family, cleared: false), placement.Active, placement.Fresh);
                Add(cells, exports, BossIcons.Named(placement.Family, cleared: true), placement.Inactive, placement.Fresh);
            }

            int tall = growth.RowsFor(columns, rows);
            int changed = 0;
            foreach ((int index, byte[] pixels, string name, bool fresh) in cells)
            {
                bool same = !fresh && index / columns < rows && Same(sheet, index, pixels);
                string verb = fresh ? "new" : same ? "same" : "repaint";
                Console.WriteLine($"  {verb,-7} {name} -> cell {index + 1}");
                changed += same ? 0 : 1;
            }

            if (changed == 0)
            {
                Console.WriteLine("nothing changed - the sheet already holds every export as it is");
                return 0;
            }

            Console.WriteLine(
                $"{changed} cell(s) to write; sheet {sheet.Width}x{sheet.Height}"
                + (tall > rows ? $" grows to {sheet.Width}x{tall * IconSheet.Tile}" : " keeps its size"));
            if (dry)
            {
                Console.WriteLine("dry run - nothing written");
                return 0;
            }

            sheet = Taller(sheet, tall);
            Span<byte> bytes = Pixels(sheet);
            foreach ((int index, byte[] pixels, string name, bool fresh) in cells)
            {
                IconSheetGrowth.Copy(bytes, sheet.Width, pixels, index);
                if (fresh)
                {
                    custom[index + 1] = name;
                }
            }

            // Written beside and moved over, so an interrupted run never leaves half a PNG
            // where the project's art was.
            string staged = sheetPath + ".baking";
            sheet.SaveAsPng(staged, Encoder);
            File.Move(staged, sheetPath, overwrite: true);
            WriteTable(customPath, custom);

            Console.WriteLine(
                $"baked into {Path.GetRelativePath(repository, sheetPath)} and"
                + $" {Path.GetRelativePath(repository, customPath)} - commit both.");
            return 0;
        }
        finally
        {
            sheet.Dispose();
        }
    }

    /// <summary>Reads one exported cell and queues it, or says why it cannot be used.</summary>
    private static void Add(
        List<(int Index, byte[] Pixels, string Name, bool Fresh)> cells, string exports, string name, int index, bool fresh)
    {
        string path = Path.Combine(exports, name + ".png");
        if (!File.Exists(path))
        {
            // Only ever the Inactive half - an Active file is what made it a family at all. The
            // cell beside the Active one stays empty and reserved, which is what the overlay does.
            return;
        }

        using Image<Rgba32> picture = Image.Load<Rgba32>(new DecoderOptions { Configuration = Contiguous }, path);
        if (picture.Width != IconSheet.Tile || picture.Height != IconSheet.Tile)
        {
            throw new ArgumentException(
                $"{name}.png is {picture.Width}x{picture.Height}, a sheet cell is {IconSheet.Tile}x{IconSheet.Tile}");
        }

        byte[] pixels = new byte[IconSheet.Tile * IconSheet.Tile * IconSheetGrowth.Channels];
        picture.CopyPixelDataTo(pixels);
        cells.Add((index, pixels, name, fresh));
    }

    /// <summary>Whether a cell of the sheet already holds exactly these pixels.</summary>
    private static bool Same(Image<Rgba32> sheet, int index, byte[] pixels)
    {
        const int Line = IconSheet.Tile * IconSheetGrowth.Channels;
        ReadOnlySpan<byte> bytes = Pixels(sheet);
        int columns = IconSheet.ColumnsIn(sheet.Width);
        int stride = sheet.Width * IconSheetGrowth.Channels;
        int left = index % columns * Line;
        int top = index / columns * IconSheet.Tile;
        for (int y = 0; y < IconSheet.Tile; y++)
        {
            if (!bytes.Slice(((top + y) * stride) + left, Line).SequenceEqual(pixels.AsSpan(y * Line, Line)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The sheet with empty rows added under it, or the same sheet when it is tall enough.</summary>
    private static Image<Rgba32> Taller(Image<Rgba32> sheet, int rows)
    {
        int tall = rows * IconSheet.Tile;
        if (tall <= sheet.Height)
        {
            return sheet;
        }

        var grown = new Image<Rgba32>(Contiguous, sheet.Width, tall);
        Pixels(sheet).CopyTo(Pixels(grown));
        sheet.Dispose();
        return grown;
    }

    private static Span<byte> Pixels(Image<Rgba32> image)
        => image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory)
            ? MemoryMarshal.AsBytes(memory.Span)
            : throw new ArgumentException($"{image.Width}x{image.Height} did not decode into one buffer");

    /// <summary>A cell table, cell to name. A missing file is an empty table.</summary>
    private static Dictionary<int, string> Table(string path)
    {
        var names = new Dictionary<int, string>();
        if (!File.Exists(path))
        {
            return names;
        }

        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] == '#' || tab <= 0 || !int.TryParse(line.AsSpan(0, tab), out int cell))
            {
                continue;
            }

            string name = line[(tab + 1)..].Trim();
            if (name.Length > 0)
            {
                names[cell] = name;
            }
        }

        return names;
    }

    /// <summary>Both tables read backwards, keeping the lower cell for a name - as IconNames does.</summary>
    private static Dictionary<string, int> Reverse(params Dictionary<int, string>[] tables)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<int, string> table in tables)
        {
            foreach ((int cell, string name) in table)
            {
                if (!byName.TryGetValue(name, out int seen) || cell < seen)
                {
                    byName[name] = cell;
                }
            }
        }

        return byName;
    }

    private static int Max(Dictionary<int, string> table) => table.Count > 0 ? table.Keys.Max() : 0;

    /// <summary>Rewrites the custom table: its own comment lines kept, the rows sorted by cell.</summary>
    private static void WriteTable(string path, Dictionary<int, string> names)
    {
        List<string> head = File.Exists(path)
            ? [.. File.ReadLines(path).TakeWhile(line => line.StartsWith('#'))]
            : [$"# Names for the cells this project added to assets/{IconSheet.Resource}. Written by tools/IconBaker."];

        var lines = new List<string>(head.Count + names.Count);
        lines.AddRange(head);
        foreach ((int cell, string name) in names.OrderBy(pair => pair.Key))
        {
            lines.Add($"{cell}\t{name}");
        }

        // LF only, as the generated table is: a table that changes line endings with the
        // machine it was baked on is a whole-file diff for one new boss.
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    /// <summary>The repository root above a folder, found by the offsets schema, or empty.</summary>
    private static string Root(string from)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
            {
                return dir.FullName;
            }
        }

        return string.Empty;
    }
}
