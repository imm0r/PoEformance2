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
/// WRITES NOTHING UNTIL IT KNOWS EVERYTHING FITS, and nothing at all when no pixel changed -
/// re-running it over the same folder is a no-op, not a re-encode of a five-megabyte file.
/// </remarks>
internal static class Program
{
    private const string Usage =
        "usage: dotnet run --project tools/IconBaker -- <exports folder> [--dry-run] [--repo <path>]\n"
        + "  <exports folder>  where the model pane wrote <Family>Active.png / <Family>Inactive.png\n"
        + "  --dry-run         say what would change, write nothing\n"
        + "  --repo <path>     the repository root, when not run from inside it";

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
        bool dry = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dry = true; break;
                case "--repo" when i + 1 < args.Length: repository = args[++i]; break;
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

        try
        {
            return Bake(exports, repository, dry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or UnknownImageFormatException or InvalidImageContentException or ArgumentException)
        {
            Console.Error.WriteLine($"stopped, nothing written: {exception.Message}");
            return 2;
        }
    }

    private static int Bake(string exports, string repository, bool dry)
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
