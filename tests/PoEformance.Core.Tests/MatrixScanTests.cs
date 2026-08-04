using PoEformance.Core.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// Proves the matrix drift scanner: given a base where the real world-to-screen matrix
/// sits at some unknown offset, it must surface that offset by its unit-length w-row -
/// exactly the hunt that recovered the matrix after each historic drift.
/// </summary>
public class MatrixScanTests
{
    [Fact]
    public void Scan_FindsTheOffsetWhereTheWRowIsAUnitVector()
    {
        const ulong baseAddr = 0x10_0000;
        var fake = new FakeMemoryReader();

        // Put a real camera matrix at +0xC0 (the "new" location after a hypothetical
        // drift). Only its w-row direction (+0x30 into the matrix) needs to be a unit
        // vector for the scanner to find it - the in-game camera forward.
        const int matrixAt = 0xC0;
        fake.Place<float>(baseAddr + (ulong)(matrixAt + 0x30), 0.467f);
        fake.Place<float>(baseAddr + (ulong)(matrixAt + 0x34), 0.467f);
        fake.Place<float>(baseAddr + (ulong)(matrixAt + 0x38), 0.751f);

        // And a decoy: a huge translation value where a MISaligned read would land, so
        // the scanner must reject non-unit vectors rather than grab the first floats.
        fake.Place<float>(baseAddr + (ulong)(0x40 + 0x30), 24951f);

        List<MatrixCandidate> candidates = MatrixScan.Scan(fake, baseAddr, 0, 0x180);

        Assert.NotEmpty(candidates);
        Assert.Equal(matrixAt, candidates[0].Offset); // best (closest to length 1.0) wins
        Assert.True(Math.Abs(candidates[0].Length - 1.0) < 0.02);
        Assert.DoesNotContain(candidates, c => c.Offset == 0x40); // decoy rejected
    }

    [Fact]
    public void Scan_ReturnsEmptyWhenNoMatrixIsPresent()
    {
        // All-garbage region (no unit vectors) -> no false candidates.
        var fake = new FakeMemoryReader();
        fake.Place(0x20_0000, new byte[0x200]); // zeros: every w-row length is 0, never ~1
        Assert.Empty(MatrixScan.Scan(fake, 0x20_0000, 0, 0x180));
    }
}
