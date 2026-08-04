using PoEformance.Core.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The matrix drift scanner. These tests encode what the first two live runs taught:
/// a unit vector alone proves nothing (memory is full of identity matrices), and when a
/// struct contains no camera at all the real culprit is usually the pointer that led there.
/// </summary>
public class MatrixScanTests
{
    /// <summary>The in-game camera forward axis - diagonal, every component non-zero.</summary>
    private static readonly (float X, float Y, float Z) CameraForward = (0.467f, 0.467f, 0.751f);

    /// <summary>Places a direction row at <paramref name="matrixAt"/> + 0x30.</summary>
    private static void PlaceWRow(FakeMemoryReader fake, ulong baseAddr, int matrixAt, float x, float y, float z)
    {
        fake.Place<float>(baseAddr + (ulong)(matrixAt + 0x30), x);
        fake.Place<float>(baseAddr + (ulong)(matrixAt + 0x34), y);
        fake.Place<float>(baseAddr + (ulong)(matrixAt + 0x38), z);
    }

    [Fact]
    public void Scan_FindsACameraMatrixByItsDiagonalDirectionRow()
    {
        const ulong baseAddr = 0x10_0000;
        var fake = new FakeMemoryReader();
        const int matrixAt = 0xC0;
        PlaceWRow(fake, baseAddr, matrixAt, CameraForward.X, CameraForward.Y, CameraForward.Z);

        // A huge translation value where a misaligned read would land - must be rejected.
        PlaceWRow(fake, baseAddr, 0x40, 24951f, 0f, 0f);

        List<MatrixCandidate> candidates = MatrixScan.Scan(fake, baseAddr, 0, 0x180);

        Assert.True(MatrixScan.HasCameraLike(candidates));
        MatrixCandidate best = candidates[0];
        Assert.Equal(matrixAt, best.Offset);
        Assert.False(best.IsAxisAligned);
        Assert.DoesNotContain(candidates, c => c.Offset == 0x40);
    }

    [Fact]
    public void IdentityMatrices_AreRankedLastAndFlagged_NotOfferedAsAnswers()
    {
        // Exactly the first live run's result: several perfect unit vectors, all axis-aligned
        // rows of identity matrices. The scanner must report "no camera here", not four
        // confident-looking wrong answers.
        const ulong baseAddr = 0x20_0000;
        var fake = new FakeMemoryReader();
        PlaceWRow(fake, baseAddr, 0x240, 0f, 1f, 0f);
        PlaceWRow(fake, baseAddr, 0x254, 0f, 1f, 0f);
        PlaceWRow(fake, baseAddr, 0x258, 1f, 0f, 0f);
        PlaceWRow(fake, baseAddr, 0x264, 0f, 0f, 1f);

        List<MatrixCandidate> candidates = MatrixScan.Scan(fake, baseAddr, 0, 0x400);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.True(c.IsAxisAligned));
        Assert.False(MatrixScan.HasCameraLike(candidates)); // the decisive assertion
    }

    [Fact]
    public void CameraOutranksIdentity_WhenBothArePresent()
    {
        const ulong baseAddr = 0x30_0000;
        var fake = new FakeMemoryReader();
        PlaceWRow(fake, baseAddr, 0x100, 0f, 1f, 0f); // identity row, earlier in memory
        PlaceWRow(fake, baseAddr, 0x200, CameraForward.X, CameraForward.Y, CameraForward.Z);

        List<MatrixCandidate> candidates = MatrixScan.Scan(fake, baseAddr, 0, 0x400);

        Assert.Equal(0x200, candidates[0].Offset); // diagonal wins regardless of position
        Assert.False(candidates[0].IsAxisAligned);
    }

    [Fact]
    public void PointerDriftScan_FindsTheShiftedParentPointer()
    {
        // The +0x08-wave scenario: the schema reads the struct pointer at 0x368, but the
        // patch moved it to 0x370. Reading through the stale slot yields a base that is off,
        // so the matrix inside reads garbage - while the neighbouring slot is correct.
        const ulong parent = 0x40_0000;
        const ulong wrongBase = 0x50_0000;
        const ulong rightBase = 0x60_0000;
        const int matrixAt = 0x1A0;

        var fake = new FakeMemoryReader();
        fake.Place(parent + 0x368, wrongBase); // stale slot -> a struct with no camera
        fake.Place(parent + 0x370, rightBase); // drifted slot -> the real one
        PlaceWRow(fake, wrongBase, matrixAt, 0f, 1f, 0f); // identity noise only
        PlaceWRow(fake, rightBase, matrixAt, CameraForward.X, CameraForward.Y, CameraForward.Z);

        List<PointerCandidate> found = PointerDriftScan.Scan(
            fake, parent, currentOffset: 0x368, radius: 0x40,
            probe: candidateBase => MatrixScan.HasCameraLike(
                MatrixScan.Scan(fake, candidateBase, matrixAt, matrixAt + 4)));

        Assert.Single(found);
        Assert.Equal(0x370, found[0].FieldOffset);
        Assert.Equal(8, found[0].Delta); // "+0x08" - the wave signature
        Assert.Equal(rightBase, found[0].Target);
    }

    [Fact]
    public void PointerDriftScan_ReturnsNothingWhenNoNeighbourFits()
    {
        var fake = new FakeMemoryReader();
        fake.Place(0x70_0000 + 0x368, 0x80_0000UL); // points somewhere with no camera
        List<PointerCandidate> found = PointerDriftScan.Scan(
            fake, 0x70_0000, 0x368, 0x40, _ => false);
        Assert.Empty(found);
    }

    [Fact]
    public void Scan_ReturnsEmptyWhenNoMatrixIsPresent()
    {
        var fake = new FakeMemoryReader();
        fake.Place(0x90_0000, new byte[0x200]); // zeros: every row length is 0, never ~1
        Assert.Empty(MatrixScan.Scan(fake, 0x90_0000, 0, 0x180));
    }
}
