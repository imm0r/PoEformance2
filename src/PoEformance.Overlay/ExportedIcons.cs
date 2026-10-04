using System.Runtime.InteropServices;
using PoEformance.Features;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// Lays the model pane's exported boss pictures into the overlay's copy of the icon sheet.
/// </summary>
/// <remarks>
/// THE STEP THAT WAS A PASTE. An export used to do nothing until somebody opened
/// assets/icons.png in an image editor, found the end of it, pasted two cells in, named them
/// in a table and rebuilt. Now the export is on the map the moment it is written, and the
/// paste is tools/IconBaker - which places by the same rule (<see cref="IconSheetGrowth"/>),
/// so a cell number seen this session is the one the baked sheet will have.
///
/// ONLY THE 64-PIXEL PAIR IS READ. The export also writes a 1024-pixel version of each for
/// whoever wants to look at it; the sheet takes the cell the export already downscaled
/// properly, rather than doing it again here with a different filter.
/// </remarks>
internal static class ExportedIcons
{
    /// <summary>
    /// The sheet with every export in the folder laid into it, possibly a new, taller image.
    /// </summary>
    /// <param name="sheet">The decoded embedded sheet. Disposed here when a taller one replaces it.</param>
    /// <param name="folder">The exports folder.</param>
    /// <param name="live">Gets the names of the cells laid in that no shipped table names yet.</param>
    /// <param name="problems">Gets a line for every export that could not be used.</param>
    public static Image<Rgba32> LayInto(
        Image<Rgba32> sheet, string folder, List<(string Name, int Cell)> live, List<string> problems)
    {
        if (!Directory.Exists(folder))
        {
            return sheet;
        }

        // One listing, not a probe per family: this runs at every rebuild, and the folder grows
        // with every boss somebody poses.
        List<string> families = IconSheetGrowth.Families(Directory.EnumerateFiles(folder, "*.png"));
        if (families.Count == 0)
        {
            return sheet;
        }

        if (!sheet.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> pixels))
        {
            problems.Add($"{IconSheet.Resource} is not in one buffer, so no exports were laid into it.");
            return sheet;
        }

        int columns = IconSheet.ColumnsIn(sheet.Width);
        int rows = IconSheet.RowsIn(sheet.Height);
        int last = IconSheetGrowth.LastOccupied(MemoryMarshal.AsBytes(pixels.Span), sheet.Width, sheet.Height);
        int first = IconSheetGrowth.FirstFree(columns, last, IconNames.LastBaked - 1);
        IconGrowth growth = IconSheetGrowth.Place(
            families, IconNames.BakedCellFor, columns, first, IconSheet.MaxEdge / IconSheet.Tile);

        foreach (string family in growth.Full)
        {
            problems.Add($"exports: no room left on the sheet for {family}.");
        }

        if (growth.Placed.Count == 0)
        {
            return sheet;
        }

        sheet = Taller(sheet, growth.RowsFor(columns, rows));
        if (!sheet.DangerousTryGetSinglePixelMemory(out pixels))
        {
            problems.Add($"{IconSheet.Resource} grown is not in one buffer, so no exports were laid into it.");
            return sheet;
        }

        Span<byte> bytes = MemoryMarshal.AsBytes(pixels.Span);
        byte[] cell = new byte[IconSheet.Tile * IconSheet.Tile * IconSheetGrowth.Channels];
        foreach (IconPlacement placement in growth.Placed)
        {
            string active = BossIcons.Named(placement.Family, cleared: false);
            string inactive = BossIcons.Named(placement.Family, cleared: true);

            if (Read(Path.Combine(folder, active + ".png"), cell, problems))
            {
                IconSheetGrowth.Copy(bytes, sheet.Width, cell, placement.Active);
                if (placement.Fresh)
                {
                    live.Add((active, placement.Active + 1));
                }
            }

            if (Read(Path.Combine(folder, inactive + ".png"), cell, problems))
            {
                IconSheetGrowth.Copy(bytes, sheet.Width, cell, placement.Inactive);
                if (placement.Fresh)
                {
                    live.Add((inactive, placement.Inactive + 1));
                }
            }
        }

        return sheet;
    }

    /// <summary>The sheet with empty rows added under it, or the same sheet when it is tall enough.</summary>
    private static Image<Rgba32> Taller(Image<Rgba32> sheet, int rows)
    {
        int tall = rows * IconSheet.Tile;
        if (tall <= sheet.Height)
        {
            return sheet;
        }

        // A fresh buffer is zeroed, which is transparent - the new rows need nothing but the
        // old pixels copied over the top of them, and the two spans are one block each.
        var grown = new Image<Rgba32>(IconCache.Contiguous, sheet.Width, tall);
        if (sheet.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> from)
            && grown.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> to))
        {
            from.Span.CopyTo(to.Span);
        }

        sheet.Dispose();
        return grown;
    }

    /// <summary>Reads one exported cell into the buffer. False when it is missing or unusable.</summary>
    private static bool Read(string path, byte[] cell, List<string> problems)
    {
        if (!File.Exists(path))
        {
            // An Active picture without its Inactive one is allowed - the cell beside it stays
            // empty and reserved, and the marker uses the one picture for both states.
            return false;
        }

        try
        {
            using Image<Rgba32> picture =
                Image.Load<Rgba32>(new DecoderOptions { Configuration = IconCache.Contiguous }, path);
            if (picture.Width != IconSheet.Tile || picture.Height != IconSheet.Tile)
            {
                problems.Add(
                    $"exports: {Path.GetFileName(path)} is {picture.Width}x{picture.Height},"
                    + $" a sheet cell is {IconSheet.Tile}x{IconSheet.Tile}.");
                return false;
            }

            picture.CopyPixelDataTo(cell);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or UnknownImageFormatException
                or InvalidImageContentException or NotSupportedException)
        {
            problems.Add($"exports: {Path.GetFileName(path)} could not be read: {exception.Message}");
            return false;
        }
    }
}
