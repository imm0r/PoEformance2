using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// The point lights a room's doodads carry, read from each one's .ao and put where the doodad is.
/// </summary>
/// <remarks>
/// WHERE A ROOM'S LIGHT IS. The capture of Seepage's offices printed it: a doodad's .ao has a client
/// <c>Lights</c> block, and in it a <c>light = "point_light"</c> line whose children name a bone, a
/// profile, and one or more <c>state</c> lines - each state with its own colour, position, radius and
/// penumbra. A monster's .ao writes the same block (the Prisoner's spear: <c>state =
/// "spear_ultimate_light_off"</c> with <c>default_state = true</c> and no colour at all).
///
/// ONE STATE IS DRAWN: the one marked <c>default_state</c>, else the first. A state with no colour
/// line is a light switched off - the Prisoner's default state is exactly that, and its "on" state
/// is the one with a colour. What switches a state at play time is an animation event
/// (LightStateEventType), which a still picture has none of.
///
/// ATTACHED PIECES CARRY LIGHTS TOO: VaalPotCluster01_Light hangs BrazierFire_01.ao on itself with an
/// AOSet's <c>fixed_ao</c> line, and the fire is the brazier's. The line reads as a position, a turn
/// about x, y and z, a scale and a flag, then the piece; the piece's lights are put through the
/// scale, the turn in that order (the one MonsterModels.Local applies to an attachment, equally
/// unsettled) and the shift. The light in the one sample sits 12 units off its piece's origin, so
/// the turn moves it by that much at most.
///
/// A LIGHT ON A BONE (<c>bone_name</c>) stands where that bone rests in the doodad's rig, its
/// position line counted in the bone's own frame. The checkpoint's torch is such a light.
///
/// WHAT IS NOT DRAWN IS COUNTED: spot lights, lights left in the rig itself (the .ast's mesh lights,
/// whose bytes are not read yet - <c>disable_mesh_lights</c> turns them off where a block says so),
/// and states with no colour.
/// </remarks>
public static class RoomLights
{
    /// <summary>Most lights kept for one room - a guard, well past any room read so far.</summary>
    public const int MostLights = 2048;

    /// <summary>How deep fixed_ao pieces are followed.</summary>
    private const int MostDeep = 4;

    /// <summary>One doodad's lights in its own frame, and what was left out of them.</summary>
    internal sealed record Lit(IReadOnlyList<PointLight> Lights, int Spots, int RigLights, int Off, int Unboned)
    {
        public static Lit None { get; } = new([], 0, 0, 0, 0);
    }

    /// <summary>
    /// The lights of a room's placed doodads, gathered as they are laid - see RoomModels.Lay.
    /// </summary>
    public sealed class Gathered(Func<string, byte[]?> read)
    {
        private readonly Dictionary<string, Lit> _cache = new(StringComparer.OrdinalIgnoreCase);
        private int _spots, _rig, _off, _unboned, _capped, _doodads;

        /// <summary>Every light so far, in the room's space.</summary>
        public List<PointLight> Lights { get; } = [];

        /// <summary>A doodad's lights, put where the doodad is.</summary>
        public void Add(string ao, Matrix4x4 place)
        {
            Lit lit = Of(read, ao, _cache, 0);
            _spots += lit.Spots;
            _rig += lit.RigLights;
            _off += lit.Off;
            _unboned += lit.Unboned;
            _doodads += lit.Lights.Count > 0 ? 1 : 0;
            foreach (PointLight one in lit.Lights)
            {
                if (Lights.Count >= MostLights)
                {
                    _capped++;
                    continue;
                }

                Lights.Add(one with { Position = Vector3.Transform(one.Position, place) });
            }
        }

        /// <summary>The line under a lit picture.</summary>
        public string Said()
        {
            var said = string.Create(CultureInfo.InvariantCulture, $"{Lights.Count} point lights from {_doodads} doodads");
            var left = new List<string>();
            if (_off > 0)
            {
                left.Add(string.Create(CultureInfo.InvariantCulture, $"{_off} in a state with no colour (off)"));
            }

            if (_spots > 0)
            {
                left.Add(string.Create(CultureInfo.InvariantCulture, $"{_spots} spot lights not drawn"));
            }

            if (_rig > 0)
            {
                left.Add(string.Create(CultureInfo.InvariantCulture, $"{_rig} lights in rigs (.ast) not read"));
            }

            if (_unboned > 0)
            {
                left.Add(string.Create(CultureInfo.InvariantCulture, $"{_unboned} on a bone the rig does not have, put at the doodad's origin"));
            }

            if (_capped > 0)
            {
                left.Add(string.Create(CultureInfo.InvariantCulture, $"{_capped} past {MostLights} left out"));
            }

            return left.Count == 0 ? said : said + "; " + string.Join(", ", left);
        }
    }

    /// <summary>One .ao's lights in its own frame, its fixed_ao pieces included - read once per file.</summary>
    internal static Lit Of(Func<string, byte[]?> read, string path, Dictionary<string, Lit> cache, int depth)
    {
        if (cache.TryGetValue(path, out Lit? known))
        {
            return known;
        }

        // A PIECE THAT HANGS ITSELF is cut off by the entry going in first.
        cache[path] = Lit.None;
        AnimatedObject ao = AnimatedObject.Read(read(path.Replace('\\', '/').Trim()));
        if (!ao.Ready)
        {
            return Lit.None;
        }

        List<AnimatedObject> chain = ModelDump.Whole(read, ao);
        var lights = new List<PointLight>();
        int spots = 0, off = 0, unboned = 0, rigLights = 0;

        // THE NEAREST Lights BLOCK THAT SAYS ANYTHING: most doodads extend a base whose block is
        // empty, and a block written in the doodad's own file is the one that applies.
        AoStruct? block = chain.SelectMany(one => one.Structs)
            .FirstOrDefault(one => one.Name.Equals("Lights", StringComparison.OrdinalIgnoreCase) && one.Entries.Count > 0);
        bool meshLightsOff = false;
        SkeletonPose? rest = null;
        AnimationSkeleton? rig = null;
        string skeleton = ModelDump.Entryed(chain, "ClientAnimationController", "skeleton");
        if (block is not null)
        {
            foreach (AoEntry entry in block.Entries)
            {
                if (entry.Key.Equals("disable_mesh_lights", StringComparison.OrdinalIgnoreCase))
                {
                    meshLightsOff = entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!entry.Key.Equals("light", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!entry.Value.Equals("point_light", StringComparison.OrdinalIgnoreCase))
                {
                    spots++;
                    continue;
                }

                AoEntry? state = entry.Children.FirstOrDefault(one => one.Key.Equals("state", StringComparison.OrdinalIgnoreCase)
                        && one.Children.Any(child => child.Key.Equals("default_state", StringComparison.OrdinalIgnoreCase)
                            && child.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                    ?? entry.Children.FirstOrDefault(one => one.Key.Equals("state", StringComparison.OrdinalIgnoreCase));
                IReadOnlyList<AoEntry> said = state?.Children ?? entry.Children;
                if (Three(Value(said, "colour")) is not { } colour || colour == Vector3.Zero)
                {
                    off++;
                    continue;
                }

                Vector3 position = Three(Value(said, "position")) ?? Three(Value(entry.Children, "position")) ?? Vector3.Zero;
                string bone = Value(entry.Children, "bone_name");
                if (bone.Length > 0)
                {
                    if (rig is null && skeleton.Length > 0)
                    {
                        rig = AnimationSkeleton.Read(read(skeleton.Replace('\\', '/').Trim()));
                        rest = SkeletonPose.Of(rig);
                    }

                    int at = rig is null ? -1 : IndexOf(rig, bone);
                    if (rest is not null && at >= 0 && at < rest.BindModel.Count)
                    {
                        position = Vector3.Transform(position, rest.BindModel[at]);
                    }
                    else
                    {
                        unboned++;
                    }
                }

                float radius = Number(Value(said, "radius")) ?? Number(Value(entry.Children, "radius")) ?? 0f;
                float penumbra = Number(Value(said, "penumbra_dist")) ?? 0f;
                string named = state is null ? path : $"{path} ({(state.Value.Length > 0 ? state.Value : "unnamed state")})";
                lights.Add(new PointLight(position, colour, radius, penumbra, named));
            }
        }

        if (!meshLightsOff && skeleton.Length > 0)
        {
            rig ??= AnimationSkeleton.Read(read(skeleton.Replace('\\', '/').Trim()));
            rigLights += rig.Lights.Count;
        }

        // AND WHAT ITS AOSETS HANG ON IT, each piece's lights put through the line's place.
        if (depth < MostDeep)
        {
            foreach ((AoStruct _, AoEntry entry) in chain.SelectMany(one => one.Entries()))
            {
                if (!entry.Key.Equals("fixed_ao", StringComparison.OrdinalIgnoreCase) || ModelDump.Attached(entry.Value) is not { } piece
                    || Placed(piece.Where) is not { } place)
                {
                    continue;
                }

                Lit hung = Of(read, piece.Path, cache, depth + 1);
                spots += hung.Spots;
                rigLights += hung.RigLights;
                off += hung.Off;
                unboned += hung.Unboned;
                foreach (PointLight one in hung.Lights)
                {
                    lights.Add(one with { Position = Vector3.Transform(one.Position, place) });
                }
            }
        }

        var lit = new Lit(lights, spots, rigLights, off, unboned);
        cache[path] = lit;
        return lit;
    }

    /// <summary>
    /// Where a fixed_ao line puts its piece: a shift, a turn about x, y and z, and a scale - or null where the words are not that.
    /// </summary>
    internal static Matrix4x4? Placed(string where)
    {
        string[] words = where.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 6)
        {
            return null;
        }

        Span<float> numbers = stackalloc float[7];
        int count = Math.Min(words.Length, 7);
        for (var at = 0; at < count; at++)
        {
            if (!float.TryParse(words[at], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[at]))
            {
                return null;
            }
        }

        float scale = count >= 7 && numbers[6] > 0f ? numbers[6] : 1f;
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationX(numbers[3])
            * Matrix4x4.CreateRotationY(numbers[4])
            * Matrix4x4.CreateRotationZ(numbers[5])
            * Matrix4x4.CreateTranslation(numbers[0], numbers[1], numbers[2]);
    }

    private static int IndexOf(AnimationSkeleton rig, string bone)
    {
        for (var at = 0; at < rig.Bones.Count; at++)
        {
            if (rig.Bones[at].Name.Equals(bone, StringComparison.OrdinalIgnoreCase))
            {
                return at;
            }
        }

        return -1;
    }

    private static string Value(IReadOnlyList<AoEntry> entries, string key)
    {
        foreach (AoEntry one in entries)
        {
            if (one.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return one.Value.Trim();
            }
        }

        return string.Empty;
    }

    private static float? Number(string said)
        => float.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : null;

    private static Vector3? Three(string said)
    {
        string[] parts = said.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                ? new Vector3(x, y, z)
                : null;
    }
}
