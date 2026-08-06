using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// The way to somewhere in memory: where it started, and every offset followed since.
/// </summary>
/// <remarks>
/// THE ROUTE IS THE FINDING. The address it lands on today is not - it will be somewhere
/// else when the area reloads and somewhere else again after a restart, whereas
/// "AreaInstance -> +0x598 -> +0x20" is true tomorrow and is what gets written into the
/// schema.
///
/// Its own type, away from the window that draws it, because it is logic and logic that
/// lives in a UI class cannot be tested - which is not a hypothetical here. The first
/// version of this lived in the dissector window and was wrong three ways at once: it
/// crashed when stepping back off the last hop, it skipped the first offset, and it never
/// showed the name it started from, so every route read as a bare address. None of that was
/// visible from the outside until it took the tool down.
/// </remarks>
public sealed class PointerTrail
{
    private readonly List<(ulong From, int Offset)> _hops = [];
    private string _origin = string.Empty;

    /// <summary>How many hops have been taken.</summary>
    public int Count => _hops.Count;

    /// <summary>True when nothing has been followed - there is no route to show.</summary>
    public bool IsEmpty => _hops.Count == 0;

    /// <summary>
    /// Begins again somewhere, named however it should be written down.
    /// </summary>
    /// <param name="origin">
    /// "AreaInstance", or an entity's path, or a plain address. The name is half the value of
    /// the route, so it is stated when the route starts rather than guessed at afterwards -
    /// by the second hop there is nothing left to guess it from.
    /// </param>
    public void Restart(string origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        _hops.Clear();
        _origin = origin;
    }

    /// <summary>Records following the pointer at <paramref name="offset"/> of <paramref name="from"/>.</summary>
    public void Follow(ulong from, int offset)
    {
        if (_hops.Count == 0 && _origin.Length == 0)
        {
            _origin = $"0x{from:X}";
        }

        _hops.Add((from, offset));
    }

    /// <summary>Steps back one hop, giving the address it came from.</summary>
    /// <returns>False when there is nothing to step back to.</returns>
    public bool TryStepBack(out ulong address)
    {
        if (_hops.Count == 0)
        {
            address = 0;
            return false;
        }

        address = _hops[^1].From;
        _hops.RemoveAt(_hops.Count - 1);
        return true;
    }

    /// <summary>The route, written the way it would be written down.</summary>
    public string Describe()
    {
        if (_hops.Count == 0)
        {
            return string.Empty;
        }

        string route = _origin.Length > 0
            ? _origin
            : $"0x{_hops[0].From.ToString("X", CultureInfo.InvariantCulture)}";

        // Every hop, in the order taken. Each holds the offset followed FROM that address,
        // so the last one lands on wherever we are now - which is why none of them is
        // skipped and none is the current position.
        foreach ((ulong _, int offset) in _hops)
        {
            if (offset >= 0)
            {
                route += $" -> +0x{offset:X}";
            }
        }

        return route;
    }
}
