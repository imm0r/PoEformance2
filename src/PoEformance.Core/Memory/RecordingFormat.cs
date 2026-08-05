namespace PoEformance.Core.Memory;

/// <summary>
/// On-disk layout of a captured memory session.
/// </summary>
/// <remarks>
/// Deliberately trivial: a small header followed by a flat stream of tagged entries.
/// A recording is append-only, so a crash mid-session still leaves a readable file up
/// to the last complete entry.
///
/// <code>
/// Header
///   magic        8 bytes  "POEFREC1"
///   version      u32
///   processId    u32
///   moduleBase   u64
///   moduleSize   u32
///   createdUtc   i64      DateTime.UtcNow.Ticks
///
/// Entries (repeating, until end of file)
///   tag          u8       1 = frame, 2 = read, 3 = note
///   frame:       frameIndex u32, elapsedMs u32
///   read:        address u64, length u32, bytes[length]
///   note:        keyLen u16, key(utf8), valueLen u16, value(utf8)
/// </code>
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
    public const uint Version = 2;

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
