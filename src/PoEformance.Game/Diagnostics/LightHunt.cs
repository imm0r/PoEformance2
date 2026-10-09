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
///
/// AND THE DUST COLOUR, exactly as area.dust_color gives it: 411 environments set none, and where the
/// game keeps the colour it does use there is the one place that says what it is.
/// </remarks>
public sealed class LightHunt
{
    /// <summary>How far a computed float may be from the game's - its sine and cosine are not this one's to the last bit.</summary>
    public const float Tolerance = 1e-4f;

    /// <summary>How near an angle must sit to a vector or matrix to be said to be beside it, in bytes.</summary>
    public const int Beside = 4096;

    /// <summary>How many bytes either side of the sun's vector are read back, to see what the game keeps beside it.</summary>
    public const int Around = 256;

    /// <summary>Most of the other arrangements of the cube's turn listed when found.</summary>
    private const int MostArrangementsSaid = 24;

    private static readonly string[] CubeOrders =
    [
        "axes swapped to the cube's, turned about its up by -hor",
        "axes swapped, turned about the cube's up by -hor, then tipped about its x by vert",
        "axes swapped, tipped about the cube's x by vert, then turned about its up by -hor",
        "axes swapped, turned about the cube's up by -hor, then tipped about its z by vert",
        "axes swapped, tipped about the cube's z by vert, then turned about its up by -hor",
    ];

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
                    new FloatNeedle($"sun reading {(int)reading + 1}", [travels.X, travels.Y, travels.Z], Tolerance, Around)));
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

            // EVERY OTHER ARRANGEMENT OF THE SAME TWO TURNS: about any axis, either way round, after any
            // swap or flip of the axes - the cube's own frame need not be the world's. The first hunt
            // found no reading's turn in AzmerianRanges, as written or transposed, while the sun's
            // vector was there nine times over.
            entries.AddRange(Arrangements(horizontal, vertical, built.Select(one => one.Turn)));

            if (environment.HorAngle is { } h)
            {
                entries.Add(Angle("hor_angle", h));
            }

            if (environment.VertAngle is { } v)
            {
                entries.Add(Angle("vert_angle", v));
            }
        }

        // THE DUST COLOUR AS THE FILE GIVES IT, exactly, with what lies round it: found once beside the
        // sun or the angles, the same offset reads the colour the engine holds in an area whose file sets
        // none - which is what EnvironmentSettings.AssumedDust stands in for until then.
        if (environment.Dust is { } dust)
        {
            entries.Add(new Entry(What.Dust, -1, false, false, new FloatNeedle("area.dust_color", [dust.X, dust.Y, dust.Z], 0f, Around)));
        }

        if (!entries.Exists(one => one.What is What.Sun or What.Cube or What.CubeOther or What.Dust))
        {
            why = "the environment gives neither a sun's angles, a cube turn nor a dust colour";
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

            // BOTH WAYS ROUND, SIDE BY SIDE, is how AzmerianRanges held it: each copy of a reading's
            // vector had its negative sixteen bytes on. Which of the two is the light's way is then
            // settled by the ground: the sun of a sunlit area stands above it, so its light travels
            // down - plus z, the game's up being minus z.
            if (sunFound.Count == 2 && Math.Abs(sunFound[0] - sunFound[1]) == 4 && env.Phi is { } phi && env.Theta is { } theta)
            {
                int first = _entries.FindIndex(one => one.What == What.Sun && one.Reading == sunFound[0]);
                int second = _entries.FindIndex(one => one.What == What.Sun && one.Reading == sunFound[1]);
                int beside = found[first].Count(place => found[second].Exists(other => Math.Abs((long)other - (long)place) == 16));
                int[] lighting = [.. sunFound.Where(one => SceneLight.SunFrom(phi, theta, (SceneLight.SunReading)one).Z > 0f)];
                report.Append(CultureInfo.InvariantCulture,
                    $"  the two are each other's negatives, {beside} of {found[first].Count} copies side by side, sixteen bytes apart").AppendLine();
                if (lighting.Length == 1)
                {
                    sun = lighting[0];
                    report.Append(CultureInfo.InvariantCulture,
                        $"  {_sunName(lighting[0])} is the one whose sun stands above the ground - its light travels down, plus z - so it is the light's way").AppendLine();
                }
            }

            if (sun is { } settled)
            {
                report.AppendLine(settled == (int)SceneLight.GameSun
                    ? "  that is the reading the picture draws the sun by (SceneLight.GameSun)"
                    : $"  that is NOT the reading the picture draws the sun by - it draws {_sunName((int)SceneLight.GameSun)} (SceneLight.GameSun)");
            }
        }

        foreach (int at in Enumerable.Range(0, _entries.Count).Where(one => _entries[one].What == What.Angle))
        {
            report.Append(CultureInfo.InvariantCulture, $"{_entries[at].Needle.Name} {_entries[at].Needle.Values[0]:0.#####}, exactly: {Places(found[at], capped.Contains(at), unsearched.Contains(at), null)}").AppendLine();
        }

        foreach (int at in Enumerable.Range(0, _entries.Count).Where(one => _entries[one].What == What.Dust))
        {
            // BESIDE THE SUN'S VECTOR AS WELL AS THE ANGLES: the vector is the copy the renderer keeps.
            var marks = new List<(string Name, ulong At)>(angles);
            for (var sunAt = 0; sunAt < _entries.Count; sunAt++)
            {
                if (_entries[sunAt].What == What.Sun)
                {
                    marks.AddRange(found[sunAt].Select(place => (_sunName(_entries[sunAt].Reading), place)));
                }
            }

            float[] rgb = _entries[at].Needle.Values;
            report.Append(CultureInfo.InvariantCulture,
                $"dust - area.dust_color {rgb[0]:0.#####} {rgb[1]:0.#####} {rgb[2]:0.#####}, exactly: {Places(found[at], capped.Contains(at), unsearched.Contains(at), marks)}").AppendLine();
        }

        int? cube = null;
        var cubeFound = new List<int>();
        if (_entries.Exists(one => one.What == What.Cube))
        {
            report.AppendLine("cube - each reading's env_map_rotation, the shaders' mul(float4(dir, 0), env_map_rotation): as written is the order the picture uses, transposed the inverse turn; reading 1 is the game's turn as found, the rest add vert_angle's candidate tips:");
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
                report.Append(CultureInfo.InvariantCulture, $"  cube reading {reading + 1} is reading {same + 1}'s turn for these angles - with vert_angle nought, or one angle nought, the two cannot be told apart here").AppendLine();
            }

            int arrangements = _entries.Count(one => one.What == What.CubeOther);
            var arranged = Enumerable.Range(0, _entries.Count).Where(one => _entries[one].What == What.CubeOther && found[one].Count > 0).ToList();
            report.Append(CultureInfo.InvariantCulture,
                $"  every other arrangement of the same turns - about any axis, either way, after any swap or flip of the axes: {arrangements} looked for, {arranged.Count} found").AppendLine();
            foreach (int at in arranged.Take(MostArrangementsSaid))
            {
                report.Append(CultureInfo.InvariantCulture, $"    {_entries[at].Needle.Name}: {Places(found[at], capped.Contains(at), false, angles)}").AppendLine();
            }

            cube = cubeFound.Count == 1 ? cubeFound[0] : null;
        }

        // WHAT THE GAME KEEPS BESIDE THE SUN'S VECTOR, a row of eight floats at a time, the vector's
        // own in brackets - the cube's turn and the light's other numbers may sit next to it.
        foreach (FloatDump dump in result.Dumps ?? [])
        {
            report.Append(CultureInfo.InvariantCulture, $"beside {_entries[dump.Needle].Needle.Name} at 0x{dump.At:X}:").AppendLine();
            Dumped(report, dump, _entries[dump.Needle].Needle.Values.Length);
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

    /// <summary>
    /// Every arrangement of the environment's two turns but the readings' own: each turn about any of the three axes, either way, in either order, after any of the 48 swaps and flips of the axes - each matrix once, in both layouts.
    /// </summary>
    private static IEnumerable<Entry> Arrangements(float horizontal, float vertical, IEnumerable<Matrix4x4> readings)
    {
        var seen = new List<Matrix4x4>();
        foreach (Matrix4x4 one in readings)
        {
            seen.Add(one);
            seen.Add(Matrix4x4.Transpose(one));
        }

        string[] axes = ["x", "y", "z"];
        var entries = new List<Entry>();
        foreach ((Matrix4x4 swap, string swapSaid) in Swaps())
        {
            for (var first = 0; first < 3; first++)
            {
                for (var second = 0; second < 3; second++)
                {
                    if (second == first && vertical != 0f && horizontal != 0f)
                    {
                        continue;
                    }

                    foreach (float h in (ReadOnlySpan<float>)[horizontal, -horizontal])
                    {
                        foreach (float v in (ReadOnlySpan<float>)[vertical, -vertical])
                        {
                            foreach (bool horFirst in (ReadOnlySpan<bool>)[true, false])
                            {
                                Matrix4x4 round = Turn(first, h), tip = Turn(second, v);
                                Matrix4x4 turn = swap * (horFirst ? round * tip : tip * round);
                                if (seen.Exists(one => Near(one, turn)))
                                {
                                    continue;
                                }

                                seen.Add(turn);
                                string byHor = string.Create(CultureInfo.InvariantCulture, $"about {axes[first]} by {(h < 0f ? "-" : "+")}hor");
                                string byVert = string.Create(CultureInfo.InvariantCulture, $"about {axes[second]} by {(v < 0f ? "-" : "+")}vert");
                                string turns = horizontal == 0f ? byVert : vertical == 0f ? byHor : horFirst ? $"{byHor}, then {byVert}" : $"{byVert}, then {byHor}";
                                string said = $"axes {swapSaid}, turned {turns}";
                                entries.Add(new Entry(What.CubeOther, -1, false, false, new FloatNeedle(said + ", 3 by 3", Rows(turn, padded: false), Tolerance)));
                                entries.Add(new Entry(What.CubeOther, -1, false, true, new FloatNeedle(said + ", rows of four", Rows(turn, padded: true), Tolerance)));
                            }
                        }
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>The 48 swaps and flips of three axes, each as the matrix a row vector is put through first, and how it is said.</summary>
    private static IEnumerable<(Matrix4x4 Swap, string Said)> Swaps()
    {
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        string[] axes = ["x", "y", "z"];
        foreach (int[] order in orders)
        {
            for (var signs = 0; signs < 8; signs++)
            {
                var m = Matrix4x4.Identity;
                m.M11 = m.M22 = m.M33 = 0f;
                var said = new StringBuilder();
                for (var row = 0; row < 3; row++)
                {
                    float sign = (signs & (1 << row)) != 0 ? -1f : 1f;
                    Set(ref m, row, order[row], sign);
                    said.Append(axes[row]).Append('>').Append(sign < 0f ? "-" : string.Empty).Append(axes[order[row]]).Append(row < 2 ? " " : string.Empty);
                }

                yield return (m, said.ToString());
            }
        }
    }

    private static void Set(ref Matrix4x4 m, int row, int column, float value)
    {
        switch ((row * 3) + column)
        {
            case 0: m.M11 = value; break;
            case 1: m.M12 = value; break;
            case 2: m.M13 = value; break;
            case 3: m.M21 = value; break;
            case 4: m.M22 = value; break;
            case 5: m.M23 = value; break;
            case 6: m.M31 = value; break;
            case 7: m.M32 = value; break;
            default: m.M33 = value; break;
        }
    }

    private static Matrix4x4 Turn(int axis, float angle) => axis switch
    {
        0 => Matrix4x4.CreateRotationX(angle),
        1 => Matrix4x4.CreateRotationY(angle),
        _ => Matrix4x4.CreateRotationZ(angle),
    };

    /// <summary>A dump as rows of eight floats, each row's first offset from the needle's place, the needle's own floats in brackets.</summary>
    private static void Dumped(StringBuilder report, FloatDump dump, int length)
    {
        int count = dump.Bytes.Length / sizeof(float);
        long origin = (long)(dump.At - dump.From);
        for (var at = 0; at < count; at += 8)
        {
            long offset = (at * sizeof(float)) - origin;
            report.Append(CultureInfo.InvariantCulture, $"  {(offset < 0 ? "-" : "+")}0x{Math.Abs(offset):X3}:");
            for (int one = at; one < Math.Min(at + 8, count); one++)
            {
                float value = BitConverter.ToSingle(dump.Bytes, one * sizeof(float));
                long place = (one * sizeof(float)) - origin;
                bool own = place >= 0 && place < length * sizeof(float);
                string said = float.IsFinite(value) && (value == 0f || MathF.Abs(value) is >= 1e-6f and < 1e7f)
                    ? value.ToString("0.#####", CultureInfo.InvariantCulture)
                    : string.Create(CultureInfo.InvariantCulture, $"#{BitConverter.ToUInt32(dump.Bytes, one * sizeof(float)):X8}");
                report.Append(own ? " [" : "  ").Append(said).Append(own ? "]" : string.Empty);
            }

            report.AppendLine();
        }
    }

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
        CubeOther,
        Angle,
        Dust,
    }

    private readonly record struct Entry(What What, int Reading, bool Transposed, bool Padded, FloatNeedle Needle);
}
