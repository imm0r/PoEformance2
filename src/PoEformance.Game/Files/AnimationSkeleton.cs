using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace PoEformance.Game.Files;

/// <summary>One joint: where it sits at rest, and who stands next to it in the tree.</summary>
/// <param name="Name">The game's own name for it, such as <c>L_shoulder_jntBnd</c>.</param>
/// <param name="Sibling">The next bone at this level, or -1 for none.</param>
/// <param name="Child">This bone's first child, or -1 for none.</param>
/// <param name="Bind">Where the bone rests before any animation moves it.</param>
public readonly record struct SkeletonBone(string Name, int Sibling, int Child, Matrix4x4 Bind);

/// <summary>One animation, and where its keyframes are kept.</summary>
/// <param name="Name">What the game calls it, such as <c>walk_sword_shield_02</c>.</param>
/// <param name="Parent">What it blends from, on the few that name one. Usually empty.</param>
/// <param name="Tracks">How many bones it moves. One per bone, on every file seen.</param>
/// <param name="Rate">Frames per second - 30 or 60 on the rig measured.</param>
/// <param name="Kind">A byte the format has not been shown to explain. Only 0x6c, 0x6e and 0x6f occur.</param>
/// <param name="At">Where this animation's keyframes start, in the UNPACKED track region.</param>
/// <param name="Length">And how many bytes of it there are.</param>
public readonly record struct SkeletonAnimation(
    string Name, string Parent, int Tracks, int Rate, int Kind, int At, int Length);

/// <summary>
/// A monster's skeleton and the list of animations hung on it - the <c>.ast</c> file.
/// </summary>
/// <remarks>
/// WHERE ONE COMES FROM: a monster's own <c>.ao</c> names it, inside the client block -
/// <c>ClientAnimationController { skeleton = "Art/Models/MONSTERS/BasicSkeleton/rig.ast" }</c> -
/// and the same file's AnimationController names an <c>.amd</c> beside it. One rig serves a whole
/// folder of meshes: BasicSkeleton has 41 <c>.smd</c> files and exactly one <c>.ast</c>, so the
/// skeleton is not derivable from the mesh's name and the .ao is the only thing that knows.
///
/// THE WHOLE CONTAINER, AND NOT THE KEYFRAMES. What this reads is everything up to the track data:
/// the bone tree, each bone's resting place, and one header per animation saying where its frames
/// are. The frames themselves sit in a BUNDLE embedded in the same file, and unpacking one needs
/// Oodle - which needs the game. See <see cref="Tracks"/>, which hands that job to the caller.
///
///     8 bytes     version, bones, ?, animations (U16), ?, ?, lights
///     per bone    sibling U8, child U8, 4x4 matrix (16 F32, row-major), name length U8,
///                 one more U8 from version 8, then the name
///     per anim    tracks U8, ? U8, framerate U8, kind U8, ? U8 (v10+), name length U8,
///                 parent name length U8 (v11+), offset U32 and length U32 (v8+), both names
///     the rest    a standard bundle - see BundleFile - holding every animation's keyframes
///
/// MEASURED AGAINST Art/Models/MONSTERS/BasicSkeleton/rig.ast, WHICH IS VERSION 12, and the
/// published diagram at poe_data_tools/FORMATS.md was right in outline and wrong by one byte at
/// the front: reading the header as nine bytes instead of eight puts every bone's name inside the
/// next bone's matrix, which looks like a mangled string rather than like an off-by-one. What
/// settled it was the SPACING: from one name's first byte to the next is always the name's own
/// length plus 68, over all 49 bones, which pins the fixed part exactly and the header with it.
///
/// THREE THINGS AGREE ABOUT THE TRACK REGION, which is what makes it safe to trust:
///   - the animation offsets chain end to end, 0 then 42117 then 92686, with no gap;
///   - the largest offset plus its length is 11,954,976, and the embedded bundle's header says
///     its unpacked size is 11,954,976;
///   - that bundle's own payload size plus its header is exactly the bytes left in the file.
/// Any one of those could be a coincidence. Three cannot.
///
/// EVERY VERSION BUT 12 IS THE DIAGRAM'S WORD AND NOT A MEASUREMENT. The gates below - a byte from
/// version 8, another from 10, another from 11 - reproduce the 15-byte animation header that was
/// counted on the real file, which is some evidence they are right; it is not the same as having
/// read one. A file that does not walk cleanly says so in <see cref="Why"/> rather than throwing.
/// </remarks>
public sealed class AnimationSkeleton
{
    /// <summary>The fixed part of the file's own header.</summary>
    public const int HeaderBytes = 8;

    /// <summary>How many bones a file may claim, past which it is not a skeleton.</summary>
    /// <remarks>The count is a single byte, so this is the format's own limit rather than a policy.</remarks>
    public const int MostBones = 255;

    /// <summary>The value a sibling or child index takes when there is none.</summary>
    public const int NoBone = 255;

    private readonly BundleFile? _tracks;

    private AnimationSkeleton()
    {
        Bones = [];
        Animations = [];
    }

    /// <summary>Nothing read.</summary>
    public static AnimationSkeleton None { get; } = Fault("nothing to read");

    /// <summary>A file that did not read, and what stopped it.</summary>
    /// <remarks>
    /// A REASON AND NOT AN EXCEPTION, for the reason every reader in this folder answers that
    /// way: the caller is a survey over thousands of the game's own files, and one malformed
    /// file has to cost a line of the report rather than the run.
    /// </remarks>
    private static AnimationSkeleton Fault(string why, int version = 0)
        => new() { Why = why, Version = version };

    /// <summary>The file's own version. 12 is what PoE 2 ships and the only one measured.</summary>
    public int Version { get; private init; }

    /// <summary>The joints, in the order the file lists them - which is what the indices count in.</summary>
    public IReadOnlyList<SkeletonBone> Bones { get; private init; }

    /// <summary>Every animation hung on this skeleton.</summary>
    public IReadOnlyList<SkeletonAnimation> Animations { get; private init; }

    /// <summary>How many lights the file carries. Zero on every monster rig seen.</summary>
    public int Lights { get; private init; }

    /// <summary>Where the embedded bundle of keyframes begins, or -1 where there is none.</summary>
    public int TracksAt { get; private init; } = -1;

    /// <summary>How many bytes the keyframes come to once unpacked.</summary>
    public int TrackBytes => _tracks?.Uncompressed ?? 0;

    /// <summary>Why nothing was read, or empty where something was.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether there is a skeleton here.</summary>
    public bool Ready => Bones.Count > 0;

    /// <summary>
    /// One animation's keyframes, unpacked.
    /// </summary>
    /// <param name="one">Which animation. Its At and Length index the unpacked track region.</param>
    /// <param name="decompress">
    /// How to undo Oodle - the same one the game's own bundles are read with. It is handed in
    /// rather than reached for because there is no Oodle without the game, and everything else
    /// about this file can be read without one.
    /// </param>
    /// <remarks>
    /// ONLY THE BLOCKS THAT ANIMATION TOUCHES are unpacked, which is the whole reason this asks
    /// the bundle for a RANGE. One rig holds 252 animations and twelve megabytes of keyframes;
    /// unpacking all of it to play a walk cycle would be most of a second and most of that memory.
    /// </remarks>
    public byte[]? Tracks(
        SkeletonAnimation one, Func<ReadOnlyMemory<byte>, int, byte[]?> decompress)
        => _tracks?.Read(one.At, one.Length, decompress);

    /// <summary>Reads one out of an open install.</summary>
    public static AnimationSkeleton Read(GameFiles? files, string? path)
        => files is null || string.IsNullOrWhiteSpace(path)
            ? None
            : Read(files.Read(path));

    /// <summary>Reads one out of the bytes of an <c>.ast</c>.</summary>
    public static AnimationSkeleton Read(byte[]? content)
    {
        if (content is not { Length: > HeaderBytes } file)
        {
            return Fault($"a skeleton is more than {HeaderBytes} bytes");
        }

        var span = new ReadOnlySpan<byte>(file);
        int version = span[0];
        int count = span[1];
        int animations = BinaryPrimitives.ReadUInt16LittleEndian(span[3..]);
        int lights = span[7];

        if (count is 0 or > MostBones)
        {
            return Fault($"a skeleton of {count} bones is not one");
        }

        var bones = new SkeletonBone[count];
        var at = HeaderBytes;

        for (var one = 0; one < count; one++)
        {
            if (Bone(span, ref at, version) is not { } bone)
            {
                return Fault($"bone {one} of {count} ran off the end at byte {at}", version);
            }

            bones[one] = bone;
        }

        var hung = new SkeletonAnimation[animations];
        for (var one = 0; one < animations; one++)
        {
            if (Animation(span, ref at, version) is not { } move)
            {
                return Fault($"animation {one} of {animations} ran off the end at byte {at}", version);
            }

            hung[one] = move;
        }

        // THE TAIL IS A BUNDLE, and an .ast that has run out of file by here is one with no
        // keyframes rather than a broken one - the headers are the part this type is for.
        BundleFile? tracks = at < file.Length ? BundleFile.Open(Window(file, at)) : null;

        return new AnimationSkeleton(tracks)
        {
            Version = version,
            Lights = lights,
            Bones = bones,
            Animations = hung,
            TracksAt = tracks is null ? -1 : at,
        };
    }

    private AnimationSkeleton(BundleFile? tracks)
        : this()
        => _tracks = tracks;

    /// <summary>
    /// A view of the file from a given byte on, which is what the bundle reads its ranges through.
    /// </summary>
    /// <remarks>
    /// A WINDOW RATHER THAN A COPY OF THE TAIL. <c>file[at..]</c> reads the same, and duplicates
    /// every keyframe in the file to do it - six megabytes on one monster rig, on the large object
    /// heap, for a survey that walks hundreds of these one after another. The bundle only ever asks
    /// for RANGES, so handing it a reader over the bytes already in hand costs the header and the
    /// chunk table and nothing else.
    ///
    /// It does keep the whole file alive for as long as the skeleton is, which is the point rather
    /// than a cost: unpacking an animation later needs those bytes, and the copy kept them twice.
    /// </remarks>
    private static Func<int, int, byte[]?> Window(byte[] file, int from)
        => (at, length) => at >= 0 && length >= 0 && from + at + (long)length <= file.Length
            ? file[(from + at)..(from + at + length)]
            : null;

    /// <summary>One bone record, or null where the file ends inside it.</summary>
    private static SkeletonBone? Bone(ReadOnlySpan<byte> file, ref int at, int version)
    {
        // sibling, child, the matrix, the name's length, and from version 8 one byte nobody has
        // explained. 68 on a version 12 file, which is what the spacing between names measures.
        int fixedPart = 2 + (16 * 4) + 1 + (version >= 8 ? 1 : 0);
        if (at + fixedPart > file.Length)
        {
            return null;
        }

        int sibling = file[at];
        int child = file[at + 1];

        Span<float> cells = stackalloc float[16];
        for (var one = 0; one < 16; one++)
        {
            cells[one] = BinaryPrimitives.ReadSingleLittleEndian(file[(at + 2 + (one * 4))..]);
        }

        int length = file[at + 2 + (16 * 4)];
        at += fixedPart;

        if (at + length > file.Length)
        {
            return null;
        }

        string name = Text(file.Slice(at, length));
        at += length;

        return new SkeletonBone(
            name,
            sibling == NoBone ? -1 : sibling,
            child == NoBone ? -1 : child,

            // ROW MAJOR, which System.Numerics also is: M11..M14 is the first row and M41..M44
            // the translation. Handing these to Matrix4x4 in file order is therefore the identity
            // and not a transpose - the bone positions come out in the model's own space, where a
            // hip sits at z -97.6 and a chest at -139.7, the same negative-z the mesh uses.
            new Matrix4x4(
                cells[0], cells[1], cells[2], cells[3],
                cells[4], cells[5], cells[6], cells[7],
                cells[8], cells[9], cells[10], cells[11],
                cells[12], cells[13], cells[14], cells[15]));
    }

    /// <summary>One animation header, or null where the file ends inside it.</summary>
    private static SkeletonAnimation? Animation(ReadOnlySpan<byte> file, ref int at, int version)
    {
        int fixedPart = 4
            + (version >= 10 ? 1 : 0)
            + 1
            + (version >= 11 ? 1 : 0)
            + (version >= 8 ? 8 : 0);

        if (at + fixedPart > file.Length)
        {
            return null;
        }

        int tracks = file[at];
        int rate = file[at + 2];
        int kind = file[at + 3];

        int cursor = at + 4 + (version >= 10 ? 1 : 0);
        int length = file[cursor];
        cursor++;

        int parent = version >= 11 ? file[cursor++] : 0;

        var from = 0;
        var bytes = 0;
        if (version >= 8)
        {
            from = BinaryPrimitives.ReadInt32LittleEndian(file[cursor..]);
            bytes = BinaryPrimitives.ReadInt32LittleEndian(file[(cursor + 4)..]);
        }

        at += fixedPart;
        if (at + length + parent > file.Length || from < 0 || bytes < 0)
        {
            return null;
        }

        string name = Text(file.Slice(at, length));
        at += length;

        string blend = Text(file.Slice(at, parent));
        at += parent;

        return new SkeletonAnimation(name, blend, tracks, rate, kind, from, bytes);
    }

    /// <summary>
    /// The game's own ASCII, which is what these names are.
    /// </summary>
    /// <remarks>
    /// LATIN-1 AND NOT UTF-8, for the reason the rest of this reader does not throw: a byte that
    /// is not ASCII here means the walk has drifted, and the useful answer to that is a name that
    /// LOOKS wrong rather than an exception from the middle of a file survey.
    /// </remarks>
    private static string Text(ReadOnlySpan<byte> said)
        => said.Length == 0 ? string.Empty : Encoding.Latin1.GetString(said);
}
