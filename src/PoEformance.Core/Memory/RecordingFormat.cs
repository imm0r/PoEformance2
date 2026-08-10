namespace PoEformance.Core.Memory;

/// <summary>
/// On-disk layout of a captured memory session.
/// </summary>
/// <remarks>
/// Deliberately trivial: a small header followed by a flat stream of tagged entries.
///
/// <code>
/// Header (never compressed)
///   magic        8 bytes  "POEFREC1"
///   version      u32
///   processId    u32
///   moduleBase   u64
///   moduleSize   u32
///   createdUtc   i64      DateTime.UtcNow.Ticks
///
/// Entries (repeating, until end of stream; Brotli-compressed since version 3)
///   tag          u8       1 = frame, 2 = read, 3 = note
///   frame:       frameIndex u32, elapsedMs u32
///   read:        address u64, length u32, bytes[length]
///   note:        keyLen u16, key(utf8), valueLen u16, value(utf8)
/// </code>
///
/// The header stays uncompressed so a recording is still identifiable by its first eight
/// bytes, and so the version that decides how to read the rest can be read without
/// guessing. Everything after it is compressed, because the entry stream is enormously
/// repetitive - the same addresses, the same tags, the same lengths - and a real session
/// packed down to a hundred-and-fiftieth of its size.
///
/// Notes carry facts that were DERIVED rather than read - above all the resolved static
/// addresses. That matters for size: finding those statics requires copying the game's
/// whole 76 MB module image, and recording that made a session file 77 MB, far too large
/// to share. Storing the six resulting addresses instead keeps a recording in the
/// kilobytes, which is what makes "send me your session" practical.
/// </remarks>
public static class RecordingFormat
{
    /// <summary>File signature. The trailing digit is the format version.</summary>
    public static ReadOnlySpan<byte> Magic => "POEFREC1"u8;

    /// <summary>Current format version. Bumped whenever the entry layout changes.</summary>
    /// <remarks>
    /// 4 stores the entries as a chain of finished Brotli streams rather than one long one,
    /// which is what makes a half-written recording readable - see BrotliWriter. The bump
    /// exists because an older build reading a version-4 file would decode the first segment
    /// and stop, and a recording that silently ends early is the exact failure this whole
    /// change was chasing.
    /// </remarks>
    public const uint Version = 4;

    /// <summary>
    /// The version whose entries were one long Brotli stream. Read the same way, since a
    /// single stream is a chain of one.
    /// </summary>
    public const uint SingleStreamVersion = 3;

    /// <summary>
    /// The last version whose entries were stored uncompressed. Still readable, because
    /// the recordings under <c>tests/fixtures/</c> are in it and they are the regression
    /// tests against real memory - a format change that threw those away would cost more
    /// than it saved.
    /// </summary>
    public const uint UncompressedVersion = 2;

    /// <summary>Entry tag: a frame boundary.</summary>
    public const byte TagFrame = 1;

    /// <summary>Entry tag: a single memory read.</summary>
    public const byte TagRead = 2;

    /// <summary>Entry tag: a derived key/value fact (e.g. a resolved static address).</summary>
    public const byte TagNote = 3;

    /// <summary>Note key prefix for resolved static addresses: <c>static:GameStates</c>.</summary>
    public const string StaticNotePrefix = "static:";

    /// <summary>
    /// Reads larger than this are passed through but NOT written to the recording. The only
    /// read that hits this cap is the module-image copy used for pattern scanning, which is
    /// both enormous and redundant once the resolved statics are stored as notes.
    /// </summary>
    public const int DefaultMaxRecordedReadBytes = 64 * 1024;

    /// <summary>
    /// Largest single read we will store. A read bigger than this is almost certainly a
    /// bug (a garbage length from a bad pointer) and would otherwise blow up the file.
    /// </summary>
    public const int MaxReadLength = 8 * 1024 * 1024;
}
