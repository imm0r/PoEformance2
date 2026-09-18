using System.Buffers.Binary;
using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// One bone's keys inside a block of frames, as offsets into the block's flat key array.
/// </summary>
/// <param name="Bone">Which bone this moves, indexing the skeleton's own list.</param>
/// <param name="At">Where this track's keys start in <see cref="AnimationTracks"/>' array.</param>
/// <param name="Scales">How many scale keys, each four floats: time, then x, y, z.</param>
/// <param name="Rotations">How many rotation keys, each five: time, then x, y, z, w.</param>
/// <param name="Positions">How many position keys, each four: time, then x, y, z.</param>
public readonly record struct BoneTrack(int Bone, int At, int Scales, int Rotations, int Positions);

/// <summary>
/// One animation's keyframes, read and ready to sample.
/// </summary>
/// <remarks>
/// WHAT THE FLOATS MEAN, MEASURED OVER 985 TRACKS of two real rigs rather than assumed:
///
///   - THE TIME IS THE FIRST FLOAT of every key, in FRAMES, starting at nought and never going
///     backwards. Not one of those 985 tracks breaks that. An animation's last key sits on its
///     frame count - 30 at 30fps is one second - so its length is that over <c>Rate</c>.
///   - A ROTATION IS (time, x, y, z, W LAST) and is a unit quaternion: every rotation key in both
///     files has length 1 to within a thousandth. Reading w first gives a quaternion of the same
///     LENGTH, which is why this had to be checked on the numbers rather than reasoned about.
///   - A POSITION REPLACES THE BONE'S BIND TRANSLATION rather than adding to it. On one 43-bone
///     rig 33 tracks start exactly on their own bind offset and ten do not - the ten are bones the
///     animation genuinely moves, and root starts at (2.77, -3.86, -96.00) against a bind of
///     (0, 0, -107.58). Treating these as an offset would double every one of them.
///   - THE THREE UNNAMED GROUPS ARE NOT PART OF THE POSE. Two of them never appear at all. The
///     third appears on every track of one file and holds a CONSTANT there - (0,0,0) on the root
///     and (0,0,1) on each weapon bone, the same value in all 142 animations - so it describes the
///     bone, not its movement, and is stepped over. See <see cref="AnimationSkeleton.Walk"/>.
///
/// THE KEYS ARE COPIED OUT ONCE. A block is read per animation and sampled per frame, so reading
/// floats back out of the bytes on every frame would be the work of the walk again, thirty times
/// a second. What this holds is one flat array, and the tracks index into it.
/// </remarks>
public sealed class AnimationTracks
{
    /// <summary>Floats in one scale or position key: the time, then three components.</summary>
    public const int Straight = 4;

    /// <summary>Floats in one rotation key: the time, then a quaternion.</summary>
    public const int Turned = 5;

    private readonly float[] _keys;
    private readonly BoneTrack[] _tracks;

    private AnimationTracks(float[] keys, BoneTrack[] tracks)
    {
        _keys = keys;
        _tracks = tracks;
    }

    /// <summary>Nothing read.</summary>
    public static AnimationTracks None { get; } = new([], []);

    /// <summary>One track per bone the animation moves, in the order the block lists them.</summary>
    public IReadOnlyList<BoneTrack> Tracks => _tracks;

    /// <summary>Whether there is anything to sample.</summary>
    public bool Ready => _tracks.Length > 0;

    /// <summary>The last time any key sits at, which is the animation's length in frames.</summary>
    public float Frames { get; private init; }

    /// <summary>Why nothing was read, or empty where something was.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>
    /// Reads a block of frames - <see cref="AnimationSkeleton.Tracks"/>' answer - as tracks.
    /// </summary>
    /// <param name="frames">The keyframes, loose from the file or unpacked from the bundle.</param>
    /// <param name="tracks">How many to expect, which the animation's own header says.</param>
    /// <param name="version">The file's version, which decides one field in a track header.</param>
    /// <remarks>
    /// THE BLOCK HAS TO COME OUT EXACTLY, which is the same check the survey makes over an install:
    /// a track says its own size, so sizing one wrongly lands the next one's header in float data.
    /// A block with bytes left over is refused rather than half-read.
    /// </remarks>
    public static AnimationTracks Read(ReadOnlySpan<byte> frames, int tracks, int version)
    {
        if (tracks <= 0)
        {
            return frames.Length == 0
                ? None
                : new AnimationTracks([], []) { Why = "no tracks, but there are frames" };
        }

        var found = new BoneTrack[tracks];
        var floats = 0;
        var at = 0;

        // The headers first, to size the array before anything is copied into it.
        for (var one = 0; one < tracks; one++)
        {
            int fixedPart = 1 + (7 * 4) + (version >= 10 ? 4 : 0);
            if (at + fixedPart > frames.Length)
            {
                return new AnimationTracks([], []) { Why = $"track {one} of {tracks} runs past the frames" };
            }

            int bone = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(at + 1)..]);
            int scales = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(at + 5)..]);
            int turns = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(at + 9)..]);
            int places = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(at + 13)..]);
            int spare = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(at + 17)..]);
            int spareTurns = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(at + 21)..]);
            int spareMore = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(at + 25)..]);

            if (scales < 0 || turns < 0 || places < 0 || spare < 0 || spareTurns < 0 || spareMore < 0)
            {
                return new AnimationTracks([], []) { Why = $"track {one} claims a negative key count" };
            }

            long mine = ((long)scales * Straight) + ((long)turns * Turned) + ((long)places * Straight);
            long stepped = ((long)spare * Straight) + ((long)spareTurns * Turned) + ((long)spareMore * Straight);

            at += fixedPart;
            if (at + ((mine + stepped) * sizeof(float)) > frames.Length)
            {
                return new AnimationTracks([], []) { Why = $"track {one} of {tracks} runs past the frames" };
            }

            found[one] = new BoneTrack(bone, floats, scales, turns, places);
            floats += (int)mine;

            // The three groups nobody has named are stepped over, not kept - see the remarks.
            at += (int)((mine + stepped) * sizeof(float));
        }

        if (at != frames.Length)
        {
            return new AnimationTracks([], [])
            {
                Why = $"the tracks came to {at} bytes and the block holds {frames.Length}",
            };
        }

        // And now the keys, in one pass, into one array.
        var keys = new float[floats];
        var into = 0;
        at = 0;
        var last = 0f;

        for (var one = 0; one < tracks; one++)
        {
            BoneTrack track = found[one];
            at += 1 + (7 * 4) + (version >= 10 ? 4 : 0);

            int mine = (track.Scales * Straight) + (track.Rotations * Turned) + (track.Positions * Straight);
            for (var cell = 0; cell < mine; cell++)
            {
                keys[into + cell] = BinaryPrimitives.ReadSingleLittleEndian(frames[(at + (cell * sizeof(float)))..]);
            }

            // The last key of each group is the latest time in it, the times being ordered.
            if (track.Scales > 0)
            {
                last = Math.Max(last, keys[into + ((track.Scales - 1) * Straight)]);
            }

            if (track.Rotations > 0)
            {
                last = Math.Max(last, keys[into + (track.Scales * Straight) + ((track.Rotations - 1) * Turned)]);
            }

            if (track.Positions > 0)
            {
                last = Math.Max(
                    last,
                    keys[into + (track.Scales * Straight) + (track.Rotations * Turned)
                        + ((track.Positions - 1) * Straight)]);
            }

            into += mine;
            at = TrackEnd(frames, at, track, version);
        }

        return new AnimationTracks(keys, found) { Frames = last };
    }

    /// <summary>Where one track's keys end, the unnamed groups included.</summary>
    private static int TrackEnd(ReadOnlySpan<byte> frames, int at, BoneTrack track, int version)
    {
        int back = at - (1 + (7 * 4) + (version >= 10 ? 4 : 0));
        int spare = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(back + 17)..]);
        int spareTurns = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(back + 21)..]);
        int spareMore = (int)BinaryPrimitives.ReadUInt32LittleEndian(frames[(back + 25)..]);

        int mine = (track.Scales * Straight) + (track.Rotations * Turned) + (track.Positions * Straight);
        int stepped = (spare * Straight) + (spareTurns * Turned) + (spareMore * Straight);
        return at + ((mine + stepped) * sizeof(float));
    }

    /// <summary>Where this bone rests at a moment, in its parent's space.</summary>
    /// <remarks>
    /// A TRACK WITH NO KEYS OF A KIND LEAVES THAT PART ALONE, which is why each of these takes the
    /// value to fall back on rather than inventing one: the caller has the bone's bind pose and
    /// that is what an unkeyed channel means.
    /// </remarks>
    public Vector3 Scale(BoneTrack track, float time, Vector3 unkeyed)
        => track.Scales == 0 ? unkeyed : Straights(track.At, track.Scales, time);

    /// <summary>Which way it faces at a moment.</summary>
    public Quaternion Rotation(BoneTrack track, float time, Quaternion unkeyed)
    {
        if (track.Rotations == 0)
        {
            return unkeyed;
        }

        int from = track.At + (track.Scales * Straight);
        (int one, float along) = Bracket(from, track.Rotations, Turned, time);

        var before = new Quaternion(_keys[one + 1], _keys[one + 2], _keys[one + 3], _keys[one + 4]);
        if (along <= 0f)
        {
            return before;
        }

        int next = one + Turned;
        var after = new Quaternion(_keys[next + 1], _keys[next + 2], _keys[next + 3], _keys[next + 4]);

        // SLERP AND NOT LERP. The keys are a second apart at 30fps on a slow turn and a quarter
        // turn between two of them is common; a straight blend of two quaternions cuts the corner
        // and speeds up in the middle, which reads as a joint snapping. Slerp costs a trig call
        // per bone per frame, which at 255 bones is nothing beside the rasteriser.
        return Quaternion.Slerp(before, after, along);
    }

    /// <summary>Where it sits at a moment, in its parent's space.</summary>
    public Vector3 Position(BoneTrack track, float time, Vector3 unkeyed)
        => track.Positions == 0
            ? unkeyed
            : Straights(track.At + (track.Scales * Straight) + (track.Rotations * Turned), track.Positions, time);

    /// <summary>A three-component channel, blended.</summary>
    private Vector3 Straights(int from, int count, float time)
    {
        (int one, float along) = Bracket(from, count, Straight, time);
        var before = new Vector3(_keys[one + 1], _keys[one + 2], _keys[one + 3]);
        if (along <= 0f)
        {
            return before;
        }

        int next = one + Straight;
        var after = new Vector3(_keys[next + 1], _keys[next + 2], _keys[next + 3]);
        return Vector3.Lerp(before, after, along);
    }

    /// <summary>
    /// The key at or before a time, and how far past it the time is.
    /// </summary>
    /// <remarks>
    /// A BINARY SEARCH RATHER THAN A CURSOR. A cursor is cheaper while an animation plays forward
    /// and wrong the moment anything scrubs, loops or blends - and this is called a few hundred
    /// times a frame against a list of at most a few hundred, where the difference is nanoseconds
    /// beside the rasteriser it feeds. Cheap and always right beats cheaper and conditionally so.
    /// </remarks>
    private (int At, float Along) Bracket(int from, int count, int width, float time)
    {
        if (count == 1 || time <= _keys[from])
        {
            return (from, 0f);
        }

        int lastAt = from + ((count - 1) * width);
        if (time >= _keys[lastAt])
        {
            return (lastAt, 0f);
        }

        var low = 0;
        int high = count - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (_keys[from + (middle * width)] <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        int at = from + (low * width);
        float span = _keys[at + width] - _keys[at];

        // Two keys at the same time is a step rather than a blend, and dividing by that span is
        // how a rig with a held pose would come out as NaN and vanish.
        return (at, span > 0f ? (time - _keys[at]) / span : 0f);
    }
}
