using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>One entity hidden where it stands: its kind, and the spot it stands on.</summary>
/// <remarks>
/// NOT THE GAME'S ID, which is the obvious key and the wrong one: ids are handed out per area
/// load, so a remembered one would come back next session pointing at a stranger - a row
/// missing from a list nobody can tell is incomplete, which is the worst shape a bug can take
/// in a tool whose whole job is showing what is there. The address is worse still; the game
/// reuses the slot within the area.
///
/// The path AND the place, because either alone is wrong: the path alone is the kind (which is
/// the other button), and a place alone would hide whatever later stands there. Together they
/// say "that doodad, the one by the door" - which is what scenery is, and scenery is what this
/// exists for. Something that MOVES stops matching once it has moved, and that is the honest
/// behaviour rather than a shortcoming: hiding a walking monster for ever is not a thing
/// anybody means.
/// </remarks>
public sealed record EntitySpot(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y)
{
    /// <summary>
    /// How far from the recorded place still counts as the same thing, in world units.
    /// </summary>
    /// <remarks>
    /// Not zero: the position is a float the reader rounds, and a doodad can be re-placed a
    /// hair off between loads. Small enough that two of a kind side by side stay separate -
    /// a character is about 10 units tall, so this is knee height.
    /// </remarks>
    public const int Tolerance = 4;

    /// <summary>Whether an entity of this path at this place is the one recorded.</summary>
    public bool Matches(string path, float x, float y)
        => string.Equals(Path, path, StringComparison.Ordinal)
           && Math.Abs(x - X) <= Tolerance
           && Math.Abs(y - Y) <= Tolerance;

    /// <summary>How the hidden list names it: the short kind, and where.</summary>
    public string Describe()
    {
        int slash = Path.LastIndexOf('/');
        string kind = slash >= 0 && slash < Path.Length - 1 ? Path[(slash + 1)..] : Path;
        return $"{kind}  at {X}, {Y}";
    }
}

/// <summary>
/// What the entity browser has been told not to list.
/// </summary>
/// <remarks>
/// The browser lists everything the reader saw, and most of an area is scenery: a hideout
/// shows page after page of DoodadNoBlocking, all of it visual, none of it ever the thing
/// somebody opened the browser to find. A text filter cannot express "not that" - it says
/// what to KEEP, so hiding one sort of clutter means naming everything else.
///
/// TWO GRAINS, because the two questions differ. A KIND is a metadata path - "I am never
/// looking for DoodadNoBlocking again" - and it is the same text on every client, unlike the
/// displayed name. ONE ENTITY is a kind on a spot; see <see cref="EntitySpot"/> for why it is
/// not the id it would obviously be.
///
/// Both last. Hiding clutter is a decision somebody makes once, and a list that emptied itself
/// on every restart would be a list nobody bothers to curate.
/// </remarks>
public sealed class EntityHiding
{
    private readonly HashSet<string> _kinds = new(StringComparer.Ordinal);
    private readonly List<EntitySpot> _spots = [];

    /// <summary>Fires when something changed, so it can be written down.</summary>
    public Action? Changed { get; set; }

    /// <summary>The hidden kinds, sorted, for the list somebody undoes them from.</summary>
    public IReadOnlyList<string> Kinds => [.. _kinds.Order(StringComparer.Ordinal)];

    /// <summary>The hidden single entities, in the order they were hidden.</summary>
    public IReadOnlyList<EntitySpot> Spots => _spots;

    /// <summary>Whether anything is hidden at all.</summary>
    public bool Any => _kinds.Count > 0 || _spots.Count > 0;

    /// <summary>How many rows the list is leaving out - the number worth showing.</summary>
    public int Count => _kinds.Count + _spots.Count;

    /// <summary>Whether this entity should be left out of the list.</summary>
    public bool Hides(string path, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_kinds.Contains(path))
        {
            return true;
        }

        foreach (EntitySpot spot in _spots)
        {
            if (spot.Matches(path, x, y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hides every entity with this metadata path, from now on.</summary>
    public void HideKind(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (_kinds.Add(path))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Lists that kind again.</summary>
    public void ShowKind(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_kinds.Remove(path))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Hides the one entity of this kind standing here.</summary>
    /// <remarks>
    /// Refuses a duplicate rather than stacking two records of the same thing, so pressing the
    /// button twice - which is what somebody does when a row does not vanish instantly - does
    /// not leave a second entry to be deleted later.
    /// </remarks>
    public void HideOne(string path, float x, float y)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (_spots.Any(spot => spot.Matches(path, x, y)))
        {
            return;
        }

        _spots.Add(new EntitySpot(path, (int)MathF.Round(x), (int)MathF.Round(y)));
        Changed?.Invoke();
    }

    /// <summary>Lists that one entity again.</summary>
    public void ShowOne(EntitySpot spot)
    {
        ArgumentNullException.ThrowIfNull(spot);

        if (_spots.Remove(spot))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Lists everything again.</summary>
    public void ShowEverything()
    {
        if (!Any)
        {
            return;
        }

        _kinds.Clear();
        _spots.Clear();
        Changed?.Invoke();
    }

    /// <summary>Takes what a settings file remembered.</summary>
    /// <remarks>
    /// Silent: applying what was saved is not a change to save back, and announcing it would
    /// have every launch write the file it just read.
    /// </remarks>
    public void Use(IEnumerable<string> kinds, IEnumerable<EntitySpot> spots)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(spots);

        _kinds.Clear();
        foreach (string path in kinds)
        {
            if (!string.IsNullOrEmpty(path))
            {
                _kinds.Add(path);
            }
        }

        _spots.Clear();
        foreach (EntitySpot spot in spots)
        {
            if (!string.IsNullOrEmpty(spot?.Path))
            {
                _spots.Add(spot);
            }
        }
    }
}
