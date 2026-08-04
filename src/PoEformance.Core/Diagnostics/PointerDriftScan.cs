using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>A neighbouring pointer slot that leads somewhere the probe accepted.</summary>
/// <param name="FieldOffset">Offset within the parent struct where this pointer was read.</param>
/// <param name="Target">The pointer value found there.</param>
/// <param name="Delta">Signed distance from the offset the schema currently uses.</param>
public sealed record PointerCandidate(int FieldOffset, ulong Target, int Delta);

/// <summary>
/// Finds a drifted STRUCT POINTER by sweeping the pointer slots around the one the schema
/// currently uses and asking a probe which target looks right.
/// </summary>
/// <remarks>
/// This exists because of a failure mode that is easy to misdiagnose: when a game patch
/// inserts a field into a parent struct, every pointer after it shifts. Reading the struct
/// through the stale pointer then yields a base that is 8 bytes off, and EVERY field inside
/// reads misaligned - which looks like "all these offsets drifted" when in truth only the
/// one parent pointer moved.
///
/// The 2026-08 wave was exactly this: AreaInstance's tail moved +0x08 and
/// InGameState.AreaInstanceData moved 0x288 -> 0x290. So when a struct's contents look
/// wrong, the first question is not "which fields moved?" but "did the pointer that got me
/// here move?" - and this answers it mechanically.
/// </remarks>
public static class PointerDriftScan
{
    /// <summary>
    /// Reads the pointer at <paramref name="parentAddress"/> + each 8-byte slot within
    /// +/-<paramref name="radius"/> of <paramref name="currentOffset"/>, and returns those
    /// whose target <paramref name="probe"/> accepts, nearest-first.
    /// </summary>
    public static List<PointerCandidate> Scan(
        IMemoryReader reader,
        ulong parentAddress,
        int currentOffset,
        int radius,
        Func<ulong, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(probe);

        var found = new List<PointerCandidate>();
        if (parentAddress == 0)
        {
            return found;
        }

        // Pointers are 8-byte aligned, so only those slots can hold one.
        int firstSlot = Math.Max(0, currentOffset - radius);
        int lastSlot = currentOffset + radius;
        firstSlot -= firstSlot % 8;

        for (int offset = firstSlot; offset <= lastSlot; offset += 8)
        {
            ulong target = reader.ReadPointer(parentAddress + (ulong)offset);
            if (target == 0)
            {
                continue;
            }

            bool accepted;
            try
            {
                accepted = probe(target);
            }
            catch (Exception)
            {
                // A probe that throws on garbage is a rejection, not a crash: this runs over
                // arbitrary memory by design.
                accepted = false;
            }

            if (accepted)
            {
                found.Add(new PointerCandidate(offset, target, offset - currentOffset));
            }
        }

        found.Sort((a, b) => Math.Abs(a.Delta).CompareTo(Math.Abs(b.Delta)));
        return found;
    }
}
