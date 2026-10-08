using System.IO.Compression;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where an area's light comes from - its WorldAreas row names an Environments row, which names the .env - and the zip a capture is sent in.
/// </summary>
public class AreaEnvironmentTests
{
    private const ulong WorldData = 0x10_0000;
    private const ulong Details = 0x20_0000;
    private const ulong Row = 0x30_0000;
    private const ulong EnvironmentRow = 0x40_00B3; // rows of a 0x75-byte table sit at odd addresses, as the live one did
    private const ulong IdText = 0x50_0000;
    private const ulong FileText = 0x60_0000;
    private const ulong OtherText = 0x70_0000;

    /// <summary>A string in a page of its own, as a table's string block is - the reader asks for a whole window and halves it only where memory ends.</summary>
    private static byte[] Text(string text)
    {
        var bytes = new byte[0x1000];
        System.Text.Encoding.Unicode.GetBytes(text, bytes);
        return bytes;
    }

    private static (FakeMemoryReader Fake, OffsetSchema Schema) Area()
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        StructDef row = schema.Structs["WorldAreaDat"];
        var fake = new FakeMemoryReader()
            .Place(WorldData + (ulong)schema.Structs["WorldData"].OffsetOf("WorldAreaDetailsPtr"), Details)
            .Place(Details + (ulong)schema.Structs["WorldAreaDetails"].OffsetOf("RowPtr"), Row)
            .Place(Row + (ulong)row.OffsetOf("IdPtr"), IdText)
            .Place(IdText, Text("MapSeepage"))
            .Place(Row + (ulong)row.OffsetOf("NamePtr"), 0UL)
            .Place(Row + (ulong)row.OffsetOf("Act"), 10)
            .Place(Row + (ulong)row.OffsetOf("IsTown"), (byte)0)
            .Place(Row + (ulong)row.OffsetOf("EnvironmentRef"), EnvironmentRow)
            .Place(EnvironmentRow + (ulong)schema.Structs["EnvironmentsDat"].OffsetOf("BaseEnvFilePtr"), FileText)
            .Place(FileText, Text("Metadata/EnvironmentSettings/Maps/Seepage.env"))
            .Place(OtherText, Text("Metadata/EnvironmentSettings/other.env"));
        return (fake, schema);
    }

    /// <summary>The area's row leads to its .env, and the row's environment is read once - a second frame on the same row asks nothing.</summary>
    [Fact]
    public void ANAREASRowNamesItsEnvironmentReadOncePerRow()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Area();
        var areas = new WorldAreaReader(fake, schema);

        AreaInfo first = areas.Read(WorldData);
        Assert.Equal("MapSeepage", first.Id);
        Assert.Equal("Metadata/EnvironmentSettings/Maps/Seepage.env", first.Environment);

        fake.Place(EnvironmentRow + (ulong)schema.Structs["EnvironmentsDat"].OffsetOf("BaseEnvFilePtr"), OtherText);
        Assert.Equal("Metadata/EnvironmentSettings/Maps/Seepage.env", areas.Read(WorldData).Environment);
    }

    /// <summary>A row whose environment does not resolve still reads as the area, with no environment.</summary>
    [Fact]
    public void ANAREAWithoutAnEnvironmentIsStillTheArea()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Area();
        fake.Place(Row + (ulong)schema.Structs["WorldAreaDat"].OffsetOf("EnvironmentRef"), 0UL);

        AreaInfo area = new WorldAreaReader(fake, schema).Read(WorldData);
        Assert.Equal("MapSeepage", area.Id);
        Assert.Equal(string.Empty, area.Environment);
    }

    /// <summary>The player's level is one byte: what sits in the next three no longer turns level 96 into 16777312.</summary>
    [Fact]
    public void THEPLAYERSLevelIsOneByte()
    {
        StructDef player = RealSessionTests.LiveSchema().Structs["Player"];
        Assert.Equal(FieldType.U8, player.Field("Level")!.Type);
        Assert.Equal(0x204, player.OffsetOf("Level"));
    }

    /// <summary>A capture's folder is packed beside it under its own name, the pictures and the recording stored as they are, the text compressed.</summary>
    [Fact]
    public void ACAPTUREIsPackedBesideItsFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"capture-pack-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "2026-10-08 15-58-09 Seepage");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "game.png"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(folder, "memory.rec"), new byte[4096]);
            File.WriteAllText(Path.Combine(folder, "loaded-files.txt"), string.Concat(Enumerable.Repeat("Metadata/Terrain/Maps/Seepage/a.tdt\n", 200)));

            (string zip, List<string> leftOut) = CaptureReport.Pack(folder);

            Assert.Equal(folder + ".zip", zip);
            Assert.Empty(leftOut);
            using ZipArchive archive = ZipFile.OpenRead(zip);
            Assert.Equal(["game.png", "loaded-files.txt", "memory.rec"], archive.Entries.Select(one => one.FullName));
            Assert.Equal(4096, archive.GetEntry("game.png")!.CompressedLength);
            Assert.Equal(4096, archive.GetEntry("memory.rec")!.CompressedLength);
            ZipArchiveEntry text = archive.GetEntry("loaded-files.txt")!;
            Assert.True(text.CompressedLength * 10 < text.Length, $"{text.CompressedLength} of {text.Length}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
