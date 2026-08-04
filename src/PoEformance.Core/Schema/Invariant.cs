namespace PoEformance.Core.Schema;

/// <summary>
/// A declared property of a correct field value. The validator evaluates these at
/// attach time (and on demand) to catch offset drift the moment it happens.
/// </summary>
/// <remarks>
/// Deliberately a small closed set of named checks instead of an expression language:
/// each kind maps to one real drift symptom this project has already lived through.
/// The canonical example is the world-to-screen matrix - a game patch shifted it by
/// 8 bytes, the misaligned read put a translation value into the w-row, and every
/// projection collapsed onto the screen centre for weeks before the cause was found.
/// A <see cref="UnitVector3"/> invariant on that field turns the same drift into a
/// red row in the attach report.
/// </remarks>
public abstract class Invariant
{
    /// <summary>Human-readable description used by reports, e.g. "value in 0..8192".</summary>
    public abstract string Describe();

    /// <summary>Numeric value must lie in a closed range.</summary>
    public sealed class Range(double min, double max) : Invariant
    {
        public double Min { get; } = min;

        public double Max { get; } = max;

        public override string Describe() => $"value in {Min}..{Max}";
    }

    /// <summary>Value must be zero or a plausible user-space pointer.</summary>
    public sealed class PlausiblePtr : Invariant
    {
        public override string Describe() => "null or user-space pointer";
    }

    /// <summary>Value must be a plausible user-space pointer (not null).</summary>
    public sealed class NonNullPtr : Invariant
    {
        public override string Describe() => "non-null user-space pointer";
    }

    /// <summary>
    /// Three consecutive floats starting <see cref="At"/> bytes into the field must form a
    /// unit-length vector, and - when <see cref="Expect"/> is set - must point that way.
    /// </summary>
    /// <remarks>
    /// The direction check is what gives this invariant real proving power. Length alone is
    /// a weak signal: memory is full of unit vectors, so a drift hunt can "confirm" a wrong
    /// offset simply because something unit-length lives there. But PoE2's camera angle is
    /// FIXED - it never rotates - so the camera forward is a constant across every session
    /// and machine. Pinning the expected direction turns "some unit vector is here" into
    /// "the camera is here", which is the difference between a guess and a verification.
    /// </remarks>
    public sealed class UnitVector3(int at, double[]? expect = null) : Invariant
    {
        /// <summary>Byte offset of the vector inside the field.</summary>
        public int At { get; } = at;

        /// <summary>Expected direction (need not be normalised), or null to check length only.</summary>
        public double[]? Expect { get; } = expect;

        public override string Describe() => Expect is null
            ? $"unit vector at +0x{At:X}"
            : $"unit vector at +0x{At:X} pointing ({Expect[0]:F3}, {Expect[1]:F3}, {Expect[2]:F3})";
    }

    /// <summary>
    /// A std::vector header whose pointers are ordered and whose element count lies in
    /// 0..<see cref="MaxCount"/>. Catches "the vector moved and we are reading noise".
    /// </summary>
    public sealed class VectorSane(int elementSize, long maxCount) : Invariant
    {
        public int ElementSize { get; } = elementSize;

        public long MaxCount { get; } = maxCount;

        public override string Describe() => $"std::vector of {ElementSize}-byte elements, count 0..{MaxCount}";
    }

    /// <summary>
    /// A pointer chain starting at the field must end at an MSVC std::wstring containing
    /// <see cref="Needle"/>. Catches "this no longer points at an entity/path".
    /// </summary>
    /// <remarks>
    /// Evaluation: <c>address = fieldAddress</c>; for each hop,
    /// <c>address = *(address + hop)</c> (so the first hop is usually 0 - dereference the
    /// field itself); finally the std::wstring is read at <c>address + StringAt</c>.
    /// Example - "the local player field really points at a player entity":
    /// hops [0x00, 0x08] walks field -> Entity -> EntityDetails, stringAt 0x08 reads the
    /// Path wstring, needle "Metadata/Characters".
    /// </remarks>
    public sealed class StringContains(string needle, int[] hops, int stringAt) : Invariant
    {
        public string Needle { get; } = needle;

        public int[] Hops { get; } = hops;

        /// <summary>Offset of the std::wstring relative to the last dereferenced pointer.</summary>
        public int StringAt { get; } = stringAt;

        public override string Describe() => $"std::wstring containing \"{Needle}\"";
    }

    /// <summary>
    /// Value must change across samples taken over time. Catches "the field froze" -
    /// exactly how the AnimationId drift (+0x10) presented.
    /// The validator can only evaluate this when given two samples; single-sample runs
    /// report it as skipped rather than passed.
    /// </summary>
    public sealed class ChangesOverTime : Invariant
    {
        public override string Describe() => "changes while the game runs";
    }
}
