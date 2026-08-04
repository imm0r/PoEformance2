using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// The drift-alarm tests. Each one recreates a REAL offset drift from the AHK tool's
/// history and asserts the validator catches it - these are the regressions the whole
/// schema design exists to prevent.
/// </summary>
public class ValidatorTests
{
    private static StructDef SingleField(string structName, string fieldName, int offset, FieldType type, Invariant invariant)
        => new(structName, null, [new FieldDef(fieldName, offset, type, null, invariant)], new Dictionary<string, long>());

    [Fact]
    public void W2SMatrixDrift_TheMinus8ByteShift_IsCaught()
    {
        // History (issue #158): the camera matrix moved -8 bytes. Reading at the old
        // offset put a huge translation value into the w-row and every projection
        // collapsed onto screen centre. The unitVector3 invariant on row 3 detects it.
        const ulong baseAddr = 0x10000;
        var good = new FakeMemoryReader();
        // Correct matrix at 0x1A0: w-row direction = the real in-game unit vector.
        good.Place<float>(baseAddr + 0x1A0 + 0x30, 0.467f);
        good.Place<float>(baseAddr + 0x1A0 + 0x34, 0.467f);
        good.Place<float>(baseAddr + 0x1A0 + 0x38, 0.751f);

        StructDef worldData = SingleField("WorldData", "W2SMatrix", 0x1A0, FieldType.Mat4x4, new Invariant.UnitVector3(0x30));

        List<FieldCheck> ok = new SchemaValidator(good).ValidateStruct(worldData, baseAddr);
        Assert.Equal(CheckOutcome.Pass, ok[0].Outcome);

        // Drifted world: same memory, but the schema still points at the OLD +8 offset,
        // so the translation value -24951 lands in the w-row z-slot (the documented bug).
        var drifted = new FakeMemoryReader();
        drifted.Place<float>(baseAddr + 0x1A8 + 0x30, 0.467f);
        drifted.Place<float>(baseAddr + 0x1A8 + 0x34, 0.751f);
        drifted.Place<float>(baseAddr + 0x1A8 + 0x38, -24951f);

        StructDef stale = SingleField("WorldData", "W2SMatrix", 0x1A8, FieldType.Mat4x4, new Invariant.UnitVector3(0x30));
        List<FieldCheck> bad = new SchemaValidator(drifted).ValidateStruct(stale, baseAddr);
        Assert.Equal(CheckOutcome.Fail, bad[0].Outcome);
        Assert.Contains("expected ~1.0", bad[0].Detail);
    }

    [Fact]
    public void AnimationIdDrift_TheFrozenField_IsCaughtBySecondSample()
    {
        // History: AnimationId shifted +0x10; the old offset FROZE at 0 while the
        // character acted. The 'changes' invariant needs two samples and flags exactly that.
        const ulong baseAddr = 0x20000;
        var reader = new FakeMemoryReader().Place(baseAddr + 0x8A0, 0);

        StructDef actor = SingleField("Actor", "AnimationId", 0x8A0, FieldType.I32, new Invariant.ChangesOverTime());
        var validator = new SchemaValidator(reader);

        var sample = new Dictionary<string, ulong>();
        List<FieldCheck> first = validator.ValidateStruct(actor, baseAddr, previousSample: null, sampleOut: sample);
        Assert.Equal(CheckOutcome.Skipped, first[0].Outcome); // one sample proves nothing

        // Second run, value unchanged -> the frozen-field alarm.
        List<FieldCheck> second = validator.ValidateStruct(actor, baseAddr, previousSample: sample);
        Assert.Equal(CheckOutcome.Fail, second[0].Outcome);
        Assert.Contains("frozen", second[0].Detail);

        // A live field (value changed) passes.
        reader.Place(baseAddr + 0x8A0, 195); // FixedRun
        List<FieldCheck> third = validator.ValidateStruct(actor, baseAddr, previousSample: sample);
        Assert.Equal(CheckOutcome.Pass, third[0].Outcome);
    }

    [Fact]
    public void VectorDrift_GarbageCount_IsCaught()
    {
        // History: probing ActiveSkills at a shifted offset decoded count=155587 and
        // garbage pointers. vectorSane rejects both bad ordering and absurd counts.
        const ulong baseAddr = 0x30000;
        var reader = new FakeMemoryReader();
        reader.Place(baseAddr + 0xB08, 0x50000UL);            // first
        reader.Place(baseAddr + 0xB08 + 8, 0x50000UL + 16 * 42); // last: 42 elements
        StructDef good = SingleField("Actor", "ActiveSkills", 0xB08, FieldType.StdVector, new Invariant.VectorSane(16, 512));
        Assert.Equal(CheckOutcome.Pass, new SchemaValidator(reader).ValidateStruct(good, baseAddr)[0].Outcome);
        Assert.Contains("42 elements", new SchemaValidator(reader).ValidateStruct(good, baseAddr)[0].Detail);

        var drifted = new FakeMemoryReader();
        drifted.Place(baseAddr + 0xB18, 0x50000UL);
        drifted.Place(baseAddr + 0xB18 + 8, 0x50000UL + 16 * 155587); // absurd count
        StructDef bad = SingleField("Actor", "ActiveSkills", 0xB18, FieldType.StdVector, new Invariant.VectorSane(16, 512));
        Assert.Equal(CheckOutcome.Fail, new SchemaValidator(drifted).ValidateStruct(bad, baseAddr)[0].Outcome);
    }

    [Fact]
    public void PlayerPointer_IsVerifiedByItsMetadataPath()
    {
        // The strongest anchor check: the local player field must lead to an entity
        // whose EntityDetails path starts with Metadata/Characters.
        const ulong playerInfo = 0x40000;
        const ulong entity = 0x50000;
        const ulong details = 0x60000;

        var reader = new FakeMemoryReader()
            .Place(playerInfo + 0x20, entity)   // LocalPlayerPtr
            .Place(entity + 0x08, details)      // Entity -> EntityDetails
            .PlaceStdWString(details + 0x08, "Metadata/Characters/Int/IntFour", 0x70000);

        var invariant = new Invariant.StringContains("Metadata/Characters", [0x00, 0x08], 0x08);
        StructDef def = SingleField("LocalPlayerStruct", "LocalPlayerPtr", 0x20, FieldType.Ptr, invariant);

        List<FieldCheck> ok = new SchemaValidator(reader).ValidateStruct(def, playerInfo);
        Assert.Equal(CheckOutcome.Pass, ok[0].Outcome);

        // Drift: the field now points at some other entity (a monster).
        var wrong = new FakeMemoryReader()
            .Place(playerInfo + 0x20, entity)
            .Place(entity + 0x08, details)
            .PlaceStdWString(details + 0x08, "Metadata/Monsters/Skeletons/SkeletonMelee", 0x70000);

        List<FieldCheck> bad = new SchemaValidator(wrong).ValidateStruct(def, playerInfo);
        Assert.Equal(CheckOutcome.Fail, bad[0].Outcome);

        // Broken chain (null entity pointer) is a fail, not a crash.
        var broken = new FakeMemoryReader().Place(playerInfo + 0x20, 0UL);
        Assert.Equal(CheckOutcome.Fail, new SchemaValidator(broken).ValidateStruct(def, playerInfo)[0].Outcome);
    }

    [Fact]
    public void UnitVector_WithExpectedDirection_RejectsTheWrongUnitVector()
    {
        // The lesson from the 2026-08 matrix hunt: length alone cannot verify an offset,
        // because memory is full of unit vectors - a wrong pick would go GREEN. PoE2's camera
        // is fixed, so pinning the direction turns the check into a real verification.
        const ulong baseAddr = 0x90000;
        double[] cameraForward = [0.466531, 0.466531, 0.751464];
        var invariant = new Invariant.UnitVector3(0x30, cameraForward);
        StructDef def = SingleField("WorldData", "W2SMatrix", 0x11C, FieldType.Mat4x4, invariant);

        // The real camera: unit length AND the expected direction.
        var right = new FakeMemoryReader();
        right.Place<float>(baseAddr + 0x11C + 0x30, 0.466531f);
        right.Place<float>(baseAddr + 0x11C + 0x34, 0.466531f);
        right.Place<float>(baseAddr + 0x11C + 0x38, 0.751464f);
        Assert.Equal(CheckOutcome.Pass, new SchemaValidator(right).ValidateStruct(def, baseAddr)[0].Outcome);

        // A different perfectly-unit vector (the 0x270 candidate from the live dump) must NOT
        // pass just because it is unit length.
        var decoy = new FakeMemoryReader();
        decoy.Place<float>(baseAddr + 0x11C + 0x30, 0.707107f);
        decoy.Place<float>(baseAddr + 0x11C + 0x34, 0.531365f);
        decoy.Place<float>(baseAddr + 0x11C + 0x38, 0.466531f);
        List<FieldCheck> decoyResult = new SchemaValidator(decoy).ValidateStruct(def, baseAddr);
        Assert.Equal(CheckOutcome.Fail, decoyResult[0].Outcome);
        Assert.Contains("expected", decoyResult[0].Detail);

        // The NEGATED forward (the paired inverse transform one row later) is also rejected -
        // same axis, opposite direction, different matrix.
        var negated = new FakeMemoryReader();
        negated.Place<float>(baseAddr + 0x11C + 0x30, -0.466531f);
        negated.Place<float>(baseAddr + 0x11C + 0x34, -0.466531f);
        negated.Place<float>(baseAddr + 0x11C + 0x38, -0.751464f);
        Assert.Equal(CheckOutcome.Fail, new SchemaValidator(negated).ValidateStruct(def, baseAddr)[0].Outcome);
    }

    [Fact]
    public void RangeInvariant_CatchesOutOfBandValues()
    {
        const ulong baseAddr = 0x80000;
        var reader = new FakeMemoryReader().Place(baseAddr + 0xC4, 68);
        StructDef def = SingleField("AreaInstance", "CurrentAreaLevel", 0xC4, FieldType.I32, new Invariant.Range(0, 100));
        Assert.Equal(CheckOutcome.Pass, new SchemaValidator(reader).ValidateStruct(def, baseAddr)[0].Outcome);

        reader.Place(baseAddr + 0xC4, -123456);
        Assert.Equal(CheckOutcome.Fail, new SchemaValidator(reader).ValidateStruct(def, baseAddr)[0].Outcome);
    }

    [Fact]
    public void UnreadableField_IsAFailNotACrash()
    {
        StructDef def = SingleField("S", "F", 0x10, FieldType.U32, new Invariant.Range(0, 10));
        List<FieldCheck> result = new SchemaValidator(new FakeMemoryReader()).ValidateStruct(def, 0x999000);
        Assert.Equal(CheckOutcome.Fail, result[0].Outcome);
        Assert.Equal("unreadable", result[0].Detail);
    }
}
