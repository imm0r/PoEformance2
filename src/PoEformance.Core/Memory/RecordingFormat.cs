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
///   tag          u8       1 = frame, 2 = read
///   frame:       frameIndex u32, elapsedMs u32
///   read:        address u64, length u32, bytes[length]
/// </code>
/// </remarks>
public static class RecordingFormat
{
    /// <summary>File signature. The trailing digit is the format version.</summary>
    public static ReadOnlySpan<byte> Magic => "POEFREC1"u8;

    /// <summary>Current format version. Bumped whenever the entry layout changes.</summary>
    public const uint Version = 1;

    /// <summary>Entry tag: a frame boundary.</summary>
    public const byte TagFrame = 1;

    /// <summary>Entry tag: a single memory read.</summary>
    public const byte TagRead = 2;

    /// <summary>
    /// Largest single read we will store. A read bigger than this is almost certainly a
    /// bug (a garbage length from a bad pointer) and would otherwise blow up the file.
    /// </summary>
    public const int MaxReadLength = 8 * 1024 * 1024;
}
