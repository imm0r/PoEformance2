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
/// <param name="Kind">
/// A byte nobody has explained - poe_data_tools calls it <c>unk2</c> and so may this. Only 0x6c,
/// 0x6e and 0x6f occur on a file that reads properly, which is itself a check on the walk.
/// </param>
/// <param name="At">
/// Where this animation's keyframes start. From version 8 that is an offset into the UNPACKED track
/// region; before it, the frames sit in the file itself and this is a byte offset into that. Either
/// way <see cref="AnimationSkeleton.Tracks"/> takes it and hands back the bytes.
/// </param>
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
///                 one more U8 from version 8, then the name          (fixed part = 68 at v12)
///     per light   name length U8, 51 bytes, 4 more from version 7, 4 more from version 9,
///                 then the name                                      (fixed part = 60 at v12)
///     per anim    tracks U8, ? U8, framerate U8, kind U8, ? U8 (v10+), name length U8,
///                 parent name length U8 (v11+), offset U32 and length U32 (v8+), both names,
///                 then BELOW VERSION 8 one track per bone, in line    (fixed part = 15 at v12)
///     per track   ? U8, bone U32, then counts of scales, rotations, positions and three more
///                 groups (U32 each), one more U32 from version 10, then the frames themselves:
///                 4 floats per scale and position, 5 per rotation
///     the rest    from version 8, a standard bundle - see BundleFile - of every animation's frames
///
/// THE LAYOUT IS poe_data_tools' OWN PARSER, checked against this project's measurements rather
/// than taken on trust - crates/poe_data_tools-lib/src/file_parsers/ast/. Where the two disagreed
/// the parser was right twice and the FORMATS.md diagram in the same repository was wrong once: the
/// diagram's nine-byte file header is eight, which the parser also says.
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
/// THE LIGHTS WERE FOUND BY A SURVEY OF THE INSTALL AND NOT BY READING. BasicSkeleton carries none,
/// so a reader that walked straight from the bones to the animations read it perfectly and drifted
/// on every rig that has one - fifteen of them, all with <c>lights = 1</c>, and the giveaway was a
/// fault whose "animation name" contained <c>PointLightShape1</c>.
///
/// AND THE LIGHT IS ANIMATED, which is the part nothing here arranges: <c>Tracks</c> on every
/// animation equals BONES PLUS LIGHTS - 9 on an eight-bone rig with one light, 116 on TitanBoss's
/// 115 - so the count in the header is a second, independent statement that the lights were walked.
///
/// BEFORE VERSION 8 THE KEYFRAMES SIT BETWEEN THE HEADERS, interleaved with them: each animation is
/// followed by one TRACK PER BONE, and a track says its own size - a count of scales, of rotations,
/// of positions and of three more groups, then that many fixed-width frames. That is why no single
/// length field could be found for the block: there are seven, and they are inside it.
///
/// SO THERE IS NO REGION FOR OFFSETS TO CHAIN ACROSS BELOW VERSION 8, and the check that replaces
/// tiling is stronger: the walk must land EXACTLY on the end of the file. It does, on both real old
/// rigs measured - 276,698 bytes of an animatedweapon and 382,590 of a blackguard, to the byte,
/// across 142 and 13 animations. A file that does not is drifted and is refused.
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

    /// <summary>
    /// The first version that keeps its keyframes in a bundle, with offsets into it.
    /// </summary>
    /// <remarks>
    /// AND THEREFORE THE FIRST THIS READS ANIMATIONS FROM AT ALL - see the remarks on the type.
    /// Below it the frames sit between the headers at a stride nothing has been shown to give.
    /// </remarks>
    public const int OffsetsFrom = 8;

    private readonly BundleFile? _tracks;
    private readonly byte[]? _inline;
    private readonly int _inlineBytes;

    private AnimationSkeleton()
    {
        Bones = [];
        Animations = [];
        Lights = [];
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

    /// <summary>
    /// What the rig's lights are called - <c>PointLightShape1</c> and the like.
    /// </summary>
    /// <remarks>
    /// A LIGHT IS PART OF THE RIG, not decoration beside it: every animation's track count is
    /// bones plus lights, so the game animates a light exactly as it animates a joint. Most
    /// monsters have none; the ones that glow have one.
    /// </remarks>
    public IReadOnlyList<string> Lights { get; private init; }

    /// <summary>Where the embedded bundle of keyframes begins, or -1 where there is none.</summary>
    public int TracksAt { get; private init; } = -1;

    /// <summary>How many bytes the keyframes come to once unpacked.</summary>
    public int TrackBytes => _tracks?.Uncompressed ?? _inlineBytes;

    /// <summary>
    /// Whether the frames are in the file as they are, rather than in a bundle needing Oodle.
    /// </summary>
    /// <remarks>
    /// Below version 8 they are, which is the one place this reader can hand back real keyframes
    /// on a machine that has the files and not the game. <see cref="Tracks"/> ignores the
    /// decompressor it is given in that case.
    /// </remarks>
    public bool Loose => _tracks is null && _inline is not null;

    /// <summary>
    /// What could not be read, or empty where everything could.
    /// </summary>
    /// <remarks>
    /// NOT ONLY SET ON A FAILURE. A file below version 8 reads its bones perfectly and its
    /// animation list not at all, and this is where it says so - so a caller that finds no
    /// animations can tell "this rig has none" from "this reader cannot walk this layout".
    /// </remarks>
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
    {
        if (_tracks is not null)
        {
            return _tracks.Read(one.At, one.Length, decompress);
        }

        // BELOW VERSION 8 THERE IS NOTHING TO UNPACK. The frames are in the file as they are, so
        // the decompressor is not wanted - and the one caller that has no Oodle can still read
        // these. The offsets are into the file rather than into a track region; see the remarks.
        if (_inline is null || one.At < 0 || one.Length < 0 || one.At + one.Length > _inline.Length)
        {
            return null;
        }

        return _inline[one.At..(one.At + one.Length)];
    }

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

        var lit = new string[lights];
        for (var one = 0; one < lights; one++)
        {
            if (Light(span, ref at, version) is not { } name)
            {
                return Fault($"light {one} of {lights} ran off the end at byte {at}", version);
            }

            lit[one] = name;
        }

        var hung = new SkeletonAnimation[animations];
        long inline = 0;

        for (var one = 0; one < animations; one++)
        {
            if (Animation(span, ref at, version) is not { } move)
            {
                return Fault($"animation {one} of {animations} ran off the end at byte {at}", version);
            }

            // BELOW VERSION 8 THE FRAMES ARE HERE, between this header and the next, so walking
            // them is not optional - it is the only way to find the next header. What comes back
            // is where they sit in the FILE, which is what Tracks slices below.
            if (version < OffsetsFrom)
            {
                int from = at;
                for (var track = 0; track < move.Tracks; track++)
                {
                    if (!Track(span, ref at, version))
                    {
                        return Fault(
                            $"animation {one}'s track {track} of {move.Tracks} ran off the end"
                            + $" at byte {at}",
                            version);
                    }
                }

                move = move with { At = from, Length = at - from };
                inline += move.Length;
            }

            hung[one] = move;
        }

        // AND THE WALK HAS TO LAND ON THE END OF THE FILE. There is no track region for offsets to
        // chain across below version 8, so this is what takes tiling's place - and it is a stricter
        // statement, because every track of every animation had to be sized right to arrive here.
        // Both real old rigs measured land on it exactly: 276,698 bytes and 382,590.
        if (version < OffsetsFrom)
        {
            return at == file.Length
                ? new AnimationSkeleton(file, (int)Math.Min(inline, int.MaxValue))
                {
                    Version = version,
                    Lights = lit,
                    Bones = bones,
                    Animations = hung,
                    TracksAt = HeaderBytes,
                }
                : Fault(
                    $"the walk ended at byte {at} of {file.Length}, so it drifted somewhere",
                    version);
        }

        // THE TAIL IS A BUNDLE, and an .ast that has run out of file by here is one with no
        // keyframes rather than a broken one - the headers are the part this type is for.
        BundleFile? tracks = at < file.Length ? BundleFile.Open(Window(file, at)) : null;

        return new AnimationSkeleton(tracks)
        {
            Version = version,
            Lights = lit,
            Bones = bones,
            Animations = hung,
            TracksAt = tracks is null ? -1 : at,
        };
    }

    private AnimationSkeleton(BundleFile? tracks)
        : this()
        => _tracks = tracks;

    /// <summary>The pre-8 shape: the frames are in the file, so the file is what is kept.</summary>
    private AnimationSkeleton(byte[] file, int bytes)
        : this()
    {
        _inline = file;
        _inlineBytes = bytes;
    }

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

    /// <summary>
    /// Walks a block of keyframes as so many tracks, and says how many bytes they came to.
    /// </summary>
    /// <param name="frames">The keyframes - unpacked, for a file that keeps them in a bundle.</param>
    /// <param name="tracks">How many to expect, which the animation's own header says.</param>
    /// <param name="version">The file's version, which decides one field in a track header.</param>
    /// <returns>How many bytes the tracks took, or -1 where they did not fit.</returns>
    /// <remarks>
    /// THE QUESTION THIS EXISTED TO SETTLE, AND IT IS SETTLED. Below version 8 the frames sit in the
    /// file and are walked as tracks; from version 8 they sit in an embedded bundle, and that the
    /// bundle holds the SAME track structures was a guess - poe_data_tools parses it as a container
    /// and stops, so its parser does not say, and unpacking one needs Oodle, which needs the game.
    ///
    /// A RUN ON A REAL INSTALL ANSWERED IT: 1559 rigs, one animation unpacked from each, every one
    /// walking as its header's track count and coming to exactly the bytes that header claimed.
    /// Nothing here arranges that agreement. The two layouts differ in WHERE the frames are kept
    /// and in nothing else.
    /// </remarks>
    public static int Walk(ReadOnlySpan<byte> frames, int tracks, int version)
    {
        if (tracks < 0)
        {
            return -1;
        }

        var at = 0;
        for (var one = 0; one < tracks; one++)
        {
            if (!Track(frames, ref at, version))
            {
                return -1;
            }
        }

        return at;
    }

    /// <summary>
    /// Steps over one track - a bone's keyframes - and says whether it fitted.
    /// </summary>
    /// <remarks>
    /// A TRACK SAYS ITS OWN SIZE, which is why no single length field could be found for the block
    /// of frames below version 8: there are seven of them and they are inside it. The header is a
    /// byte, the bone this track moves, and six counts - scales, rotations, positions and three
    /// groups nobody has named - with one more U32 from version 10. Then that many frames: four
    /// floats for a scale or a position, FIVE for a rotation, which is a quaternion and the time it
    /// happens at.
    ///
    /// THE COUNTS ARE THE CHECK. Sized wrongly they walk off the end of the file almost at once; on
    /// the two real old rigs measured, every track of every animation lands the walk on the last
    /// byte of the file, and the numbers are the shape a rig ought to have - scale keyframes always
    /// 2, rotations 79 on a 60fps attack and 31 on a 30fps one, leaf bones at the minimum.
    ///
    /// THIS ONLY STEPS OVER THEM. Playing an animation wants the floats, and reading them here
    /// would mean holding every keyframe of every animation of every rig a survey opens.
    /// <see cref="Tracks"/> hands back the bytes for the one animation somebody asks for.
    /// </remarks>
    private static bool Track(ReadOnlySpan<byte> file, ref int at, int version)
    {
        int fixedPart = 1 + (7 * 4) + (version >= 10 ? 4 : 0);
        if (at + fixedPart > file.Length)
        {
            return false;
        }

        // Past the leading byte and the bone index, six counts, each of frames of a known width.
        long bytes = 0;
        ReadOnlySpan<int> widths = [4, 5, 4, 4, 5, 4];
        for (var one = 0; one < widths.Length; one++)
        {
            uint frames = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 5 + (one * 4))..]);
            bytes += (long)frames * widths[one] * sizeof(float);
        }

        long end = at + (long)fixedPart + bytes;
        if (end > file.Length)
        {
            return false;
        }

        at = (int)end;
        return true;
    }

    /// <summary>
    /// One light's name, or null where the file ends inside the record.
    /// </summary>
    /// <remarks>
    /// SIXTY BYTES AT VERSION 12, and the sixty were measured before they were read anywhere: it is
    /// the only length under which four real rigs - two at version 11, two at 12 - read their
    /// animation headers and account for their embedded bundles to the byte. What is IN them is a
    /// colour, a radius and a great deal of zero, none of it wanted here; the rig's geometry is the
    /// bones, and the light matters to this reader only because it is in the way.
    ///
    /// THE GATES CAME LATER AND CORRECTED A GUESS. This said "no version gate, deliberately" on the
    /// grounds that every file seen with a light was 11 or 12, so a gate for 9 or 10 would be
    /// invented. That reasoning was sound and the conclusion was wrong - poe_data_tools' parser
    /// splits the record at exactly those two places, 51 bytes then 4 from version 7 then 4 more
    /// from 9, and an old rig with a light would have been read four or eight bytes short.
    /// ABSENCE OF A COUNTEREXAMPLE IN WHAT WAS LOOKED AT IS NOT ABSENCE IN THE GAME, which is the
    /// same lesson this project's own notes open with.
    /// </remarks>
    private static string? Light(ReadOnlySpan<byte> file, ref int at, int version)
    {
        // Fifty-one bytes of it always, four more from version 7 and four more again from 9, which
        // makes 60 on the versions a light has ever been seen on.
        int fixedPart = 1 + 51 + (version >= 7 ? 4 : 0) + (version >= 9 ? 4 : 0);

        if (at + fixedPart > file.Length)
        {
            return null;
        }

        int length = file[at];
        at += fixedPart;

        if (at + length > file.Length)
        {
            return null;
        }

        string name = Text(file.Slice(at, length));
        at += length;
        return name;
    }

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
