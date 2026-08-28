using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// The shipped base-name table, loaded once for tests that build items.
/// </summary>
/// <remarks>
/// THE SAME TABLE THE GAME READER USES, rather than a name invented per test. A price line is
/// found by the item's picture AND the name resolved from its metadata path - see
/// <see cref="PoEformance.Features.StashWorth"/> - so an item called "base", or called after the
/// last segment of its own path, matches nothing any real book contains. Fixtures like that pass
/// only while the picture is allowed to answer on its own, and they went red the day it stopped
/// being allowed to: not because the tool broke, but because they had never been describing it.
/// </remarks>
internal static class TestNames
{
    /// <summary>The table as shipped in data/, or the empty one if it cannot be found.</summary>
    internal static ItemNames Shipped { get; } = Load();

    private static ItemNames Load()
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var at = new DirectoryInfo(root);
            while (at is not null)
            {
                string names = Path.Combine(at.FullName, "data", "item-names.json");
                if (File.Exists(names))
                {
                    return ItemNames.Load(null, names);
                }

                at = at.Parent;
            }
        }

        return ItemNames.Empty;
    }
}
