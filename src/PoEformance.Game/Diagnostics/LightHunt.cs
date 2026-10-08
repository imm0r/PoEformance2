using System.Globalization;
using System.Numerics;
using System.Text;
using PoEformance.Core.Diagnostics;
using PoEformance.Game.Files;

namespace PoEformance.Game.Diagnostics;

/// <summary>What a light hunt settled: the report, and the readings it found if it found exactly one.</summary>
/// <param name="Report">Every needle and where it was found, for the person reading it and for the clipboard.</param>
/// <param name="Summary">One line of it.</param>
/// <param name="Sun">The sun reading whose vector the game holds, where exactly one's is found - an index into <see cref="SceneLight.SunReading"/>.</param>
/// <param name="Cube">The cube reading whose turn the game holds as written, where exactly one's is - an index into <see cref="SceneLight.CubeReading"/>.</param>
public sealed record LightHuntVerdict(string Report, string Summary, int? Sun, int? Cube);

/// <summary>
/// Asks the game which candidate reading of an environment's sun and cube angles it uses, by looking for each reading's vector and matrix in its memory.
/// </summary>
/// <remarks>
/// WHY THIS CAN ANSWER WHAT A SCREENSHOT ONLY SUGGESTS: the .env gives the sun as two angles and the
/// cube's turn as two more, and the game turns them into a vector and a matrix on its processor
/// before handing them to its shaders - so each candidate reading's vector, worked out here, either
/// is in the game's memory or is not. <see cref="FloatHunt"/> looks.
///
/// WHAT A SUN VECTOR IN MEMORY MEANS: the shader is handed the way the light travels - lighting.ffx's
/// ComputeDirectionalLightParams turns <c>light_direction_data</c> round (<c>direction =
/// -light_direction_data.xyz</c>) to get the way to the light - so the copy the processor keeps is
/// that vector, and each reading's needle is the way ITS light travels (<see cref="SceneLight.SunFrom"/>).
/// The two readings that differ only in which way the vector points are each other's negatives;
/// finding both says the game keeps the vector both ways, and then this cannot choose between them.
///
/// WHAT A CUBE MATRIX IN MEMORY MEANS: the shaders read the cube by <c>mul(float4(dir, 0),
/// env_map_rotation)</c>, a row vector times the matrix - System.Numerics' own convention, and the
/// camera matrix at WorldData + 0x1A0 is kept in the same order (its clip components are dots with
/// the flat array's columns). So a matrix found AS WRITTEN is a reading the picture can use as it
/// stands; one found only transposed is reported and not taken, since it is the inverse turn.
///
/// THE RAW ANGLES ARE LOOKED FOR TOO, exactly, so a vector found near its environment's phi and theta
/// reads as the environment's own rather than a coincidence elsewhere in the heap.
/// </remarks>
public sealed class LightHunt
{
    /// <summary>How far a computed float may be from the game's - its sine and cosine are not this one's to the last bit.</summary>
    public const float Tolerance = 1e-4f;

    /// <summary>How near an angle must sit to a vector or matrix to be said to be beside it, in bytes.</summary>
    public const int Beside = 4096;

    private static readonly string[] CubeOrders = ["", "turned by hor, then tipped about x by vert", "tipped about x by vert, then turned by hor", "turned by hor, then tipped about y by vert", "tipped about y by vert, then turned by hor"];

    private readonly List<Entry> _entries;
    private readonly Func<int, string> _sunName;
    private readonly Dictionary<int, int> _sameCube;

    private LightHunt(string path, EnvironmentSettings environment, List<Entry> entries, Dictionary<int, int> sameCube, Func<int, string> sunName)
    {
        Path = path;
        Environment = environment;
        _entries = entries;
        _sameCube = sameCube;
        _sunName = sunName;
        Needles = [.. entries.Select(one => one.Needle)];
    }

    /// <summary>The environment's path.</summary>
    public string Path { get; }

    /// <summary>The environment the needles were worked out from.</summary>
    public EnvironmentSettings Environment { get; }

    /// <summary>What to look for, in the order the result's sightings name.</summary>
    public IReadOnlyList<FloatNeedle> Needles { get; }

    /// <summary>
    /// The needles for an environment: every sun reading's vector, every cube reading's matrix and the raw angles - or null with why there is nothing to look for.
    /// </summary>
    /// <param name="path">The environment's path, for the report.</param>
    /// <param name="environment">The environment, read.</param>
    /// <param name="sunName">What a sun reading is called in the panel, by its index.</param>
    /// <param name="why">Why there is nothing to look for, or empty.</param>
    public static LightHunt? For(string path, EnvironmentSettings environment, Func<int, string> sunName, out string why)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(sunName);
        why = string.Empty;
        if (!environment.Ready)
        {
            why = environment.Why.Length > 0 ? environment.Why : "the environment was not read";
            return null;
        }

        var entries = new List<Entry>();
        if (environment.Phi is { } phi && environment.Theta is { } theta)
        {
            foreach (SceneLight.SunReading reading in Enum.GetValues<SceneLight.SunReading>())
            {
                Vector3 travels = SceneLight.SunFrom(phi, theta, reading);
                entries.Add(new Entry(What.Sun, (int)reading, false, false,
                    new FloatNeedle($"sun reading {(int)reading + 1}", [travels.X, travels.Y, travels.Z], Tolerance)));
            }

            entries.Add(Angle("phi", phi));
            entries.Add(Angle("theta", theta));
        }

        var sameCube = new Dictionary<int, int>();
        float horizontal = environment.HorAngle ?? 0f, vertical = environment.VertAngle ?? 0f;
        if (horizontal != 0f || vertical != 0f)
        {
            var built = new List<(int Reading, Matrix4x4 Turn)>();
            foreach (SceneLight.CubeReading reading in Enum.GetValues<SceneLight.CubeReading>())
            {
                if (reading == SceneLight.CubeReading.None)
                {
                    continue;
                }

                Matrix4x4 turn = SceneLight.CubeTurnFrom(horizontal, vertical, reading);
                int same = built.FindIndex(one => Near(one.Turn, turn));
                if (same >= 0)
                {
                    sameCube[(int)reading] = built[same].Reading;
                    continue;
                }

                built.Add(((int)reading, turn));
                foreach (bool transposed in (ReadOnlySpan<bool>)[false, true])
                {
                    Matrix4x4 laid = transposed ? Matrix4x4.Transpose(turn) : turn;
                    entries.Add(new Entry(What.Cube, (int)reading, transposed, false,
                        new FloatNeedle($"cube reading {(int)reading + 1}{(transposed ? " transposed" : string.Empty)}, 3 by 3", Rows(laid, padded: false), Tolerance)));
                    entries.Add(new Entry(What.Cube, (int)reading, transposed, true,
                        new FloatNeedle($"cube reading {(int)reading + 1}{(transposed ? " transposed" : string.Empty)}, rows of four", Rows(laid, padded: true), Tolerance)));
                }
            }

            if (environment.HorAngle is { } h)
            {
                entries.Add(Angle("hor_angle", h));
            }

            if (environment.VertAngle is { } v)
            {
                entries.Add(Angle("vert_angle", v));
            }
        }

        if (!entries.Exists(one => one.What is What.Sun or What.Cube))
        {
            why = "the environment gives neither a sun's angles nor a cube turn";
            return null;
        }

        return new LightHunt(path, environment, entries, sameCube, sunName);
    }

    /// <summary>What a finished hunt found, said.</summary>
    public LightHuntVerdict Read(FloatHuntResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var found = new List<ulong>[_entries.Count];
        for (var at = 0; at < found.Length; at++)
        {
            found[at] = [];
        }

        foreach (FloatSighting one in result.Sightings)
        {
            if (one.Needle >= 0 && one.Needle < found.Length)
            {
                found[one.Needle].Add(one.At);
            }
        }

        var capped = new HashSet<int>(result.Capped);
        var unsearched = new HashSet<int>(result.Unsearched);
        var angles = new List<(string Name, ulong At)>();
        for (var at = 0; at < _entries.Count; at++)
        {
            if (_entries[at].What == What.Angle)
            {
                angles.AddRange(found[at].Select(place => (_entries[at].Needle.Name, place)));
            }
        }

        var report = new StringBuilder();
        EnvironmentSettings env = Environment;
        report.Append(CultureInfo.InvariantCulture, $"light hunt for {Path}: phi {Say(env.Phi)} theta {Say(env.Theta)}, hor_angle {Say(env.HorAngle)} vert_angle {Say(env.VertAngle)}").AppendLine();
        report.Append(CultureInfo.InvariantCulture,
            $"looked through {result.BytesScanned / (1024.0 * 1024 * 1024):0.00} GB in {result.RegionsWalked} regions in {result.Took.TotalSeconds:0.0} s{(result.Truncated ? " - stopped at the budget, so a reading not found may lie past it" : string.Empty)}").AppendLine();

        int? sun = null;
        var sunFound = new List<int>();
        if (_entries.Exists(one => one.What == What.Sun))
        {
            report.AppendLine("sun - each reading's vector is the way its light travels, which is what the shader is handed (lighting.ffx: direction = -light_direction_data):");
            for (var at = 0; at < _entries.Count; at++)
            {
                Entry entry = _entries[at];
                if (entry.What != What.Sun)
                {
                    continue;
                }

                if (found[at].Count > 0)
                {
                    sunFound.Add(entry.Reading);
                }

                report.Append(CultureInfo.InvariantCulture, $"  {_sunName(entry.Reading)}: {Places(found[at], capped.Contains(at), unsearched.Contains(at), angles)}").AppendLine();
            }

            sun = sunFound.Count == 1 ? sunFound[0] : null;
        }

        foreach (int at in Enumerable.Range(0, _entries.Count).Where(one => _entries[one].What == What.Angle))
        {
            report.Append(CultureInfo.InvariantCulture, $"{_entries[at].Needle.Name} {_entries[at].Needle.Values[0]:0.#####}, exactly: {Places(found[at], capped.Contains(at), unsearched.Contains(at), null)}").AppendLine();
        }

        int? cube = null;
        var cubeFound = new List<int>();
        if (_entries.Exists(one => one.What == What.Cube))
        {
            report.AppendLine("cube - each reading's env_map_rotation, the shaders' mul(float4(dir, 0), env_map_rotation): as written is the order the picture uses, transposed the inverse turn:");
            for (var at = 0; at < _entries.Count; at++)
            {
                Entry entry = _entries[at];
                if (entry.What != What.Cube)
                {
                    continue;
                }

                if (found[at].Count > 0 && !entry.Transposed && !cubeFound.Contains(entry.Reading))
                {
                    cubeFound.Add(entry.Reading);
                }

                report.Append(CultureInfo.InvariantCulture, $"  {entry.Needle.Name} ({CubeOrders[entry.Reading]}): {Places(found[at], capped.Contains(at), unsearched.Contains(at), angles)}").AppendLine();
            }

            foreach ((int reading, int same) in _sameCube)
            {
                report.Append(CultureInfo.InvariantCulture, $"  cube reading {reading + 1} is reading {same + 1}'s turn for these angles - one of them is nought - so the two cannot be told apart here").AppendLine();
            }

            cube = cubeFound.Count == 1 ? cubeFound[0] : null;
        }

        string sunSaid = !_entries.Exists(one => one.What == What.Sun) ? "no sun angles"
            : sun is { } s ? $"sun: {_sunName(s)}"
            : sunFound.Count == 0 ? "sun: no reading's vector found"
            : sunFound.Count == 2 && Math.Abs(sunFound[0] - sunFound[1]) == 4 ? $"sun: both {_sunName(sunFound[0])} and {_sunName(sunFound[1])} - the game keeps the vector both ways"
            : $"sun: {sunFound.Count} readings found - {string.Join(", ", sunFound.Select(one => one + 1))}";
        string cubeSaid = !_entries.Exists(one => one.What == What.Cube) ? "no cube turn"
            : cube is { } c ? $"cube: reading {c + 1}{(_sameCube.ContainsValue(c) ? " (or one that is the same for these angles)" : string.Empty)}"
            : cubeFound.Count == 0 ? "cube: no reading's matrix found as written"
            : $"cube: {cubeFound.Count} readings found - {string.Join(", ", cubeFound.Select(one => one + 1))}";
        string summary = $"{sunSaid} · {cubeSaid}";
        report.Append("verdict: ").Append(summary).AppendLine();
        if (sun is null && cube is null)
        {
            report.AppendLine("nothing settled: the game may keep these where this cannot see, in another form, or for another environment than the area's own");
        }

        return new LightHuntVerdict(report.ToString(), summary, sun, cube);
    }

    private static Entry Angle(string name, float value) => new(What.Angle, -1, false, false, new FloatNeedle(name, [value], 0f));

    /// <summary>A matrix's first three rows, each three wide - or four wide with the fourth left unchecked.</summary>
    private static float[] Rows(Matrix4x4 m, bool padded) => padded
        ? [m.M11, m.M12, m.M13, float.NaN, m.M21, m.M22, m.M23, float.NaN, m.M31, m.M32, m.M33]
        : [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33];

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        float[] x = Rows(a, false), y = Rows(b, false);
        for (var at = 0; at < x.Length; at++)
        {
            if (MathF.Abs(x[at] - y[at]) > 1e-6f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Where a needle was found, and for a vector or matrix the nearest raw angle beside it.</summary>
    private static string Places(List<ulong> places, bool capped, bool unsearched, List<(string Name, ulong At)>? angles)
    {
        if (unsearched)
        {
            return "not looked for - nothing in it but nought and one";
        }

        if (places.Count == 0)
        {
            return "not found";
        }

        var said = new StringBuilder();
        said.Append(CultureInfo.InvariantCulture, $"{places.Count}{(capped ? "+" : string.Empty)} place{(places.Count == 1 ? string.Empty : "s")}: ");
        foreach (ulong place in places.Take(8))
        {
            said.Append(CultureInfo.InvariantCulture, $"0x{place:X}");
            if (angles is not null && Nearest(place, angles) is { } beside)
            {
                long off = (long)(beside.At - place);
                said.Append(CultureInfo.InvariantCulture, $" ({beside.Name} {Math.Abs(off)} bytes {(off < 0 ? "before" : "after")})");
            }

            said.Append(", ");
        }

        said.Length -= 2;
        if (places.Count > 8)
        {
            said.Append(CultureInfo.InvariantCulture, $" and {places.Count - 8} more");
        }

        return said.ToString();
    }

    private static (string Name, ulong At)? Nearest(ulong place, List<(string Name, ulong At)> angles)
    {
        (string Name, ulong At)? best = null;
        ulong bestDistance = ulong.MaxValue;
        foreach ((string Name, ulong At) one in angles)
        {
            ulong distance = one.At > place ? one.At - place : place - one.At;
            if (distance <= Beside && distance < bestDistance)
            {
                best = one;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static string Say(float? value) => value is { } one ? one.ToString("0.#####", CultureInfo.InvariantCulture) : "absent";

    private enum What
    {
        Sun,
        Cube,
        Angle,
    }

    private readonly record struct Entry(What What, int Reading, bool Transposed, bool Padded, FloatNeedle Needle);
}
