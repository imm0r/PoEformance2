using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PoEformance.Overlay;

/// <summary>
/// A rectangle of the screen, copied to the clipboard as a picture.
/// </summary>
/// <remarks>
/// THE SCREEN, NOT THE OVERLAY'S OWN SWAP CHAIN, and that is a choice with a reason: reading the
/// renderer back would give ImGui alone on a transparent background, and what somebody wants
/// when they press this is almost always what they SEE - the window and the game behind it. It
/// also keeps this independent of the rendering backend, which is a package this project does
/// not own.
///
/// CAPTUREBLT is what makes the overlay appear in it at all. A layered window - and this one is
/// layered, that is how it is transparent - is skipped by a plain screen BitBlt, so without the
/// flag the picture would show the game with the tool politely missing.
///
/// BOTTOM-UP 24-BIT, no alpha: a 32-bit DIB from the screen carries zero in the alpha byte, and
/// a paste target that honours alpha shows it as an empty picture. Twenty-four bits has nowhere
/// to put that mistake.
///
/// ONE CALL, ONE CLIPBOARD OPEN: the clipboard is shared with everything on the machine, so it
/// is held for as short as it can be and a busy one (another process has it) is retried a few
/// times rather than failing the first time it is looked at.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class ScreenCapture
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint ClipboardDib = 8;
    private const uint GlobalMoveable = 0x0002;
    private const uint BiRgb = 0;
    private const uint DibRgbColours = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColourUsed;
        public uint ColourImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint handle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(
        nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(
        nint dc, nint bitmap, uint start, uint lines, nint bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);

    /// <summary>Copies the rectangle, in screen pixels, to the clipboard. False when anything failed.</summary>
    public static bool CopyToClipboard(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        nint screen = GetDC(0);
        if (screen == 0)
        {
            return false;
        }

        nint memory = 0;
        nint bitmap = 0;
        nint canvas = 0;
        nint previous = 0;
        try
        {
            canvas = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (canvas == 0 || bitmap == 0)
            {
                return false;
            }

            previous = SelectObject(canvas, bitmap);
            if (!BitBlt(canvas, 0, 0, width, height, screen, x, y, SrcCopy | CaptureBlt))
            {
                return false;
            }

            // Rows are padded to four bytes, which is part of what a DIB IS and not a detail
            // to skip: a width that is not a multiple of four would shear the picture.
            int stride = ((width * 3) + 3) & ~3;
            int pixelBytes = stride * height;
            int headerBytes = Marshal.SizeOf<BitmapInfoHeader>();

            memory = GlobalAlloc(GlobalMoveable, (nuint)(headerBytes + pixelBytes));
            if (memory == 0)
            {
                return false;
            }

            nint block = GlobalLock(memory);
            if (block == 0)
            {
                return false;
            }

            var header = new BitmapInfoHeader
            {
                Size = (uint)headerBytes,
                Width = width,
                Height = height, // positive: bottom-up, which is what every paste target expects
                Planes = 1,
                BitCount = 24,
                Compression = BiRgb,
                SizeImage = (uint)pixelBytes,
            };

            // The bitmap must not be selected into a DC while it is read out.
            SelectObject(canvas, previous);
            previous = 0;

            int lines = GetDIBits(screen, bitmap, 0, (uint)height, block + headerBytes, ref header, DibRgbColours);
            Marshal.StructureToPtr(header, block, fDeleteOld: false);
            GlobalUnlock(memory);
            if (lines == 0)
            {
                return false;
            }

            if (!OpenWithRetry())
            {
                return false;
            }

            try
            {
                EmptyClipboard();
                if (SetClipboardData(ClipboardDib, memory) == 0)
                {
                    return false;
                }

                // The system owns the block now; freeing it would empty the clipboard again.
                memory = 0;
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (previous != 0)
            {
                SelectObject(canvas, previous);
            }

            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (canvas != 0)
            {
                DeleteDC(canvas);
            }

            if (memory != 0)
            {
                GlobalFree(memory);
            }

            ReleaseDC(0, screen);
        }
    }

    private static bool OpenWithRetry()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }
}
