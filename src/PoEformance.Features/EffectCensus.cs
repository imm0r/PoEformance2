using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>What one sort of effect is, and how many of it are out there.</summary>
/// <param name="Path">The metadata path, which is the only name these things have.</param>
public readonly record struct EffectSort(string Path, int Count, bool Friendly);

/// <summary>
/// Which entities are effects, and what sorts of them are about.
/// </summary>
/// <remarks>
/// HERE RATHER THAN IN THE LAYER because it is a decision rather than a drawing. "What counts
/// as an effect" is the whole of what the debug overlay says, it is got wrong silently - an
/// ordinary monster ringed as an effect looks exactly like a correct answer - and the drawing
/// project cannot be tested at all. It is also the one definition the marks and the list have
/// to agree on, and two copies of a rule is one copy that drifts.
///
/// THREE SOURCES, genuinely different: a hostile ground effect the reader kept on purpose (its
/// own kind), a friendly one that always travels on with its flag set, and an engine asset node
/// that only exists at all when the noise filter has been told to let it through - which
/// arrives unclassified, because nothing about it says what it is.
/// </remarks>
public static class EffectCensus
{
    /// <summary>Whether this entity is one of the things the effects overlay is about.</summary>
    public static bool Is(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return entity.Kind == EntityKind.Effect
               || entity.IsEffect
               || entity.Kind == EntityKind.Unknown;
    }

    /// <summary>What is out there right now, grouped by path and commonest first.</summary>
    /// <remarks>
    /// The half that makes the overlay a debugging tool rather than confetti. A mark says
    /// something is there; a list of paths says WHAT, which is the question an unexplained
    /// death or an unnamed mechanic actually raises.
    /// </remarks>
    public static IReadOnlyList<EffectSort> Sorts(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var counts = new Dictionary<string, (int Count, bool Friendly)>();
        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (!Is(entity))
            {
                continue;
            }

            counts.TryGetValue(entity.Path, out (int Count, bool Friendly) was);
            counts[entity.Path] = (was.Count + 1, was.Friendly || entity.IsFriendly);
        }

        var sorts = new List<EffectSort>(counts.Count);
        foreach ((string path, (int count, bool friendly)) in counts)
        {
            sorts.Add(new EffectSort(path, count, friendly));
        }

        sorts.Sort((left, right) => right.Count.CompareTo(left.Count));
        return sorts;
    }
}
