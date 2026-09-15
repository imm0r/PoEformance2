using System.Text.RegularExpressions;

namespace PoEformance.Core.Tests;

/// <summary>
/// The rule about <c>data/</c>: it holds what the tool LOADS, and nothing else.
/// </summary>
/// <remarks>
/// WHY A RULE RATHER THAN A LIST. The App .csproj globs <c>data\*.json</c> and <c>data\*.tsv</c>
/// into the release on purpose, and its comment records what a narrower glob cost: a build in
/// which every unique was nameless, because the one table that was not JSON fell outside it. So
/// the shipping side must stay broad, and the only place left to state the rule is the directory.
///
/// IT HELD IN NEITHER DIRECTION. Six generated name maps - base items, mods, monsters, skills and
/// two of the unique tables - sat in data/ read by nothing whatever, written there by
/// tools/poe_tools.py as a by-product of building the files that ARE read, and shipped in every
/// download for it. Nothing was wrong on screen, which is exactly why it lasted: a file nobody
/// reads cannot fail, it can only be carried.
///
/// BOTH DIRECTIONS ARE FAULTS AND THEY ARE DIFFERENT ONES. A file the app asks for and data/ does
/// not have is a feature that silently degrades to "nothing known" - the failure the csproj
/// comment remembers. A file data/ has and nothing asks for is weight in the release and a
/// question for whoever finds it later. This asks both.
///
/// THE APP'S OWN SOURCE IS THE AUTHORITY, not a list kept here, because a list kept here is a
/// list that drifts from the thing it describes - the same failure as the narrow glob, moved one
/// file along. FindDataFile is the single way the app names a data file, so scraping its calls
/// asks the code what it loads rather than asking somebody to remember.
/// </remarks>
public class DataFilesTests
{
    /// <summary>The one call the app makes to name a data file.</summary>
    private static readonly Regex Asked = new(
        @"FindDataFile\(""(?<name>[^""]+)""\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// What the release actually carries, which is the csproj's two globs and nothing else.
    /// </summary>
    /// <remarks>
    /// THE RULE IS ABOUT WHAT SHIPS, not about what sits in the folder, and the difference is a
    /// real file rather than a nicety: data/minimap-icons.urls.txt is a RECIPE, read by
    /// scripts/fetch-minimap-icons.ps1 to rebuild 2.6 MB of art that .gitignore deliberately
    /// keeps out of the repository. It is not loaded by the tool, it is not matched by either
    /// glob, and it is exactly where it belongs. Asking "is every file here loaded" would have
    /// moved it for no reason; asking "is every file we SHIP loaded" leaves it alone.
    ///
    /// The Oodle decoder falls out of the same rule for free - it is a .dll, so neither glob
    /// takes it, and it reaches the release by a line of its own because it is loaded as a
    /// native library from beside the executable rather than read as data.
    /// </remarks>
    private static readonly string[] Globbed = ["*.json", "*.tsv"];

    private static DirectoryInfo Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!;
        }
    }

    private static HashSet<string> Wanted()
    {
        string source = Path.Combine(Root.FullName, "src", "PoEformance.App");
        Assert.True(Directory.Exists(source), $"no app source at {source}");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match hit in Asked.Matches(File.ReadAllText(file)))
            {
                names.Add(hit.Groups["name"].Value);
            }
        }

        // A scrape that finds nothing would pass both tests below by saying nothing at all, which
        // is the shape of check this project treats as worse than no check.
        Assert.True(names.Count > 8, $"only {names.Count} data files scraped - FindDataFile has moved");
        return names;
    }

    private static IEnumerable<string> Shipped()
        => Globbed
            .SelectMany(glob => Directory.EnumerateFiles(Path.Combine(Root.FullName, "data"), glob))
            .Select(Path.GetFileName)
            .Where(name => name is { Length: > 0 })
            .Select(name => name!);

    [Fact]
    public void EVERYFileTheAppAsksForIsInData()
    {
        // The csproj's own memory: "every download so far has run without them - silently,
        // because each one degrades to nothing known by design".
        var missing = Wanted()
            .Where(name => !File.Exists(Path.Combine(Root.FullName, "data", name)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, $"the app loads files data/ does not have: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ANDNothingInDataIsCarriedForNobody()
    {
        HashSet<string> wanted = Wanted();

        var unread = Shipped()
            .Where(name => !wanted.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unread.Count == 0,
            $"data/ is shipped whole and these are read by nothing: {string.Join(", ", unread)}."
                + " An extraction the tool does not load belongs in tools/extracted/.");
    }
}
