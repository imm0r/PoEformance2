using System.Globalization;
using System.Numerics;
using System.Text;

namespace PoEformance.Game.Files;

/// <summary>
/// A shade program written out as HLSL - the same steps <see cref="ShadeProgram"/> runs, one statement each, for a graphics card to run instead.
/// </summary>
/// <remarks>
/// NOT A SECOND READING OF THE GAME'S GRAPHS. The graphs are read once, by
/// <see cref="ShadeProgram.Compile"/>: the stages ordered, the fragments followed, what the colour
/// does not read dropped. What comes out is a list of steps over registers, and this writes that list
/// out - a register a variable, a step a statement, each as <c>ShadeProgram.Run</c> does it and with
/// the same helpers line for line (<see cref="Library"/>) - so the card's picture is the processor's
/// by construction, not by a second implementation that happens to agree.
///
/// WHY NOT THE GAME'S OWN HLSL: the install ships it only as fragments, one per node, in the game's
/// own fragment language - macros, includes and stage hooks the engine assembles per material at run
/// time inside its own renderer. The program is already that assembly.
///
/// THE CONSTANTS ARE WRITTEN IN, to the bit (see <see cref="Number"/>): the compiler folds what it
/// can, and a program is compiled once per material.
///
/// WHAT THE INCLUDING SHADER PROVIDES: <c>float LinearOf(float)</c> and <c>float SrgbOf(float)</c>,
/// ShadeProgram.Linear and Srgb by the same tables; and a pixel shader to call <c>ShadeRun</c> from,
/// since a texture read takes its level from the screen's derivatives - the game's own way, and the
/// processor's on every triangle whose coordinates are straight. Everything else is in what this
/// writes: the sheets as <c>ShadeSheet0</c> on, from the slot asked for, read through the sampler
/// handed in, and what the drawing needs to know of the program as constants - <c>ShadeHasAlpha</c>,
/// <c>ShadeHasSpecular</c>, <c>ShadeHasGloss</c>, <c>ShadeUsesDepth</c>.
/// </remarks>
public static class ShadeHlsl
{
    /// <summary>The largest float, where nothing solid lies behind - ShadeProgram.Preset's.</summary>
    public const string Nothing = "3.40282347e38";

    /// <summary>
    /// The helpers the steps call - ShadeProgram's, line for line, under names of their own.
    /// </summary>
    /// <remarks>
    /// NOT A NUMBER IS KEPT AS THE PROCESSOR KEEPS IT, which is as a card does: saturate makes it
    /// nought, a comparison with it is false, pow of a negative is not a number - the processor's
    /// helpers were written to the card's rules in the first place (see ShadeProgram.Pow), and the
    /// shaders are compiled with IEEE strictness so the compiler does not reason it away.
    /// </remarks>
    public const string Library = """
        struct ShadeOut
        {
            float3 colour;
            float alpha;
            bool dropped;
            float3 specular;
            float gloss;
        };

        // ShadeProgram.Read's: an sRGB sheet's colour linearised by the table, its alpha as it is.
        float4 ShadeLinearised(float4 texel)
        {
            return float4(LinearOf(texel.x), LinearOf(texel.y), LinearOf(texel.z), texel.w);
        }

        float4 ShadeDivided(float4 a, float4 b)
        {
            return float4(
                abs(b.x) > 0.0 ? a.x / b.x : 0.0,
                abs(b.y) > 0.0 ? a.y / b.y : 0.0,
                abs(b.z) > 0.0 ? a.z / b.z : 0.0,
                abs(b.w) > 0.0 ? a.w / b.w : 0.0);
        }

        float4 ShadeSigned(float4 v)
        {
            return float4(v > 0.0) - float4(v < 0.0);
        }

        float ShadeDotted(float4 a, float4 b, int width)
        {
            return width == 2 ? (a.x * b.x) + (a.y * b.y)
                : width == 3 ? (a.x * b.x) + (a.y * b.y) + (a.z * b.z)
                : dot(a, b);
        }

        float4 ShadeNormalized(float4 v, int width)
        {
            float4 part = width == 2 ? float4(v.xy, 0.0, 0.0) : width == 3 ? float4(v.xyz, 0.0) : v;
            return part / (sqrt(dot(part, part)) + 1e-7);
        }

        float ShadeFitted(float value, float inMin, float inMax, float outMin, float outMax)
        {
            float divisor = inMax - inMin;
            return ((value - inMin) * (outMax - outMin) / (abs(divisor) > 0.0 ? divisor : 1.0)) + outMin;
        }

        float ShadeSmoothed(float center, float steepness, float value)
        {
            float power = clamp((2.0 / max(1e-3, 1.0 - steepness)) - 1.0, -1e5, 1e5);
            float said = 1.0 - center
                + ((-pow(saturate((1.0 - max(center, value)) / (1.0 - center)), power) * (1.0 - center))
                   + (pow(saturate(min(center, value) / center), power) * center));
            return isnan(said) ? center : said;
        }

        float ShadeHermite(float low, float high, float value)
        {
            float t = saturate((value - low) / (high - low));
            return t * t * (3.0 - (2.0 * t));
        }

        float ShadeCompared(float a, float b, int how)
        {
            bool said = how == 0 ? a == b
                : how == 1 ? a > b
                : how == 2 ? a < b
                : how == 3 ? a != 0.0 && b != 0.0
                : how == 4 ? a != 0.0 || b != 0.0
                : how == 5 ? a == 0.0
                : false;
            return said ? 1.0 : 0.0;
        }

        float4 ShadeTurn(float hue, float saturation, float brightness)
        {
            float angle = hue / 180.0 * 3.1425;
            return float4(cos(angle), sin(angle), saturation * 2.0, (brightness * 2.0) - 1.0);
        }

        float4 ShadeRemapped(float4 colour, float4 turn)
        {
            const float k = 0.57735;
            float3 c = colour.xyz;
            float cosine = turn.x;
            float3 crossed = float3(k * (c.z - c.y), k * (c.x - c.z), k * (c.y - c.x));
            float3 hue = (c * cosine) + (crossed * turn.y) + (float3(k, k, k) * (k * (c.x + c.y + c.z)) * (1.0 - cosine));
            float3 bright = hue + turn.www;
            float intensity = dot(bright, float3(0.299, 0.587, 0.114));
            return float4(intensity.xxx + ((bright - intensity.xxx) * turn.z), 0.0);
        }

        float ShadeHash33X(float3 p3)
        {
            p3 *= float3(0.1031, 0.1030, 0.0973);
            p3 -= floor(p3);
            float d = (p3.x * (p3.y + 19.19)) + (p3.y * (p3.x + 19.19)) + (p3.z * (p3.z + 19.19));
            p3 += d.xxx;
            float h = (p3.x + p3.y) * p3.z;
            return h - floor(h);
        }

        float ShadeValueNoise(float3 x)
        {
            float3 p = floor(x);
            float3 f = x - p;
            f = f * f * (3.0 - (2.0 * f));
            float a = ShadeHash33X(p);
            float b = ShadeHash33X(p + float3(1.0, 0.0, 0.0));
            float c = ShadeHash33X(p + float3(0.0, 1.0, 0.0));
            float d = ShadeHash33X(p + float3(1.0, 1.0, 0.0));
            float e = ShadeHash33X(p + float3(0.0, 0.0, 1.0));
            float g = ShadeHash33X(p + float3(1.0, 0.0, 1.0));
            float h = ShadeHash33X(p + float3(0.0, 1.0, 1.0));
            float i = ShadeHash33X(p + float3(1.0, 1.0, 1.0));
            float near = (a + (f.x * (b - a))) + (f.y * ((c + (f.x * (d - c))) - (a + (f.x * (b - a)))));
            float far = (e + (f.x * (g - e))) + (f.y * ((h + (f.x * (i - h))) - (e + (f.x * (g - e)))));
            return near + (f.z * (far - near));
        }

        uint3 ShadePcg(uint3 v)
        {
            v = (v * 1664525u) + 1013904223u;
            v.x += v.y * v.z;
            v.y += v.z * v.x;
            v.z += v.x * v.y;
            v ^= v >> 16;
            v.x += v.y * v.z;
            v.y += v.z * v.x;
            v.z += v.x * v.y;
            return v;
        }

        float ShadeHashUnit(uint hash)
        {
            return saturate(asfloat((hash >> 9) | 0x3f800000u) - 1.0);
        }

        float ShadeGradient(int3 cell, float3 pos)
        {
            uint3 h = ShadePcg(asuint(cell));
            float3 grad = float3(ShadeHashUnit(h.x) - 0.5, ShadeHashUnit(h.y) - 0.5, ShadeHashUnit(h.z) - 0.5);
            grad /= sqrt(dot(grad, grad));
            return dot(pos - float3(cell), grad);
        }

        float ShadePerlinNoise(float3 pos)
        {
            int3 b = int3(floor(pos));
            float3 ratio = pos - floor(pos);
            float3 r2 = ratio * ratio;
            float3 r3 = r2 * ratio;
            ratio = (3.0 * r2) - (2.0 * r3);
            float3 inverse = 1.0 - ratio;
            float res = 0.0;
            res += ShadeGradient(b, pos) * inverse.x * inverse.y * inverse.z;
            res += ShadeGradient(b + int3(1, 0, 0), pos) * ratio.x * inverse.y * inverse.z;
            res += ShadeGradient(b + int3(1, 1, 0), pos) * ratio.x * ratio.y * inverse.z;
            res += ShadeGradient(b + int3(0, 1, 0), pos) * inverse.x * ratio.y * inverse.z;
            res += ShadeGradient(b + int3(0, 0, 1), pos) * inverse.x * inverse.y * ratio.z;
            res += ShadeGradient(b + int3(1, 0, 1), pos) * ratio.x * inverse.y * ratio.z;
            res += ShadeGradient(b + int3(1, 1, 1), pos) * ratio.x * ratio.y * ratio.z;
            res += ShadeGradient(b + int3(0, 1, 1), pos) * inverse.x * ratio.y * ratio.z;
            return (res / (sqrt(3.0) / 2.0) * 0.5) + 0.5;
        }

        float4 ShadeVibrant(float value, float4 colour)
        {
            float lift = saturate((3.0 * value * value) - (2.0 * value * value * value));
            return float4(pow(lift, 1.0 / (colour.x + 1e-6)), pow(lift, 1.0 / (colour.y + 1e-6)), pow(lift, 1.0 / (colour.z + 1e-6)), 0.0);
        }

        float4 ShadeRotated(float angle, float4 uv, float4 centre)
        {
            float s;
            float c;
            sincos(angle, s, c);
            float u = uv.x - centre.x;
            float v = uv.y - centre.y;
            return float4((c * u) + (s * v) + centre.x, (-s * u) + (c * v) + centre.y, 0.0, 0.0);
        }

        float4 ShadePolar(float4 radius)
        {
            float len = sqrt((radius.x * radius.x) + (radius.y * radius.y));
            return float4(len, (atan2(radius.y / len, radius.x / len) / 3.1415 * 0.5) + 0.5, 0.0, 0.0);
        }

        float4 ShadeTriplanar(float4 alongX, float4 alongY, float4 alongZ, float4 normal, bool absolute)
        {
            float3 n = normal.xyz;
            float3 weights;
            if (absolute)
            {
                weights = abs(n / (length(n) + 1e-7));
                weights /= max(1e-7, weights.x + weights.y + weights.z);
            }
            else
            {
                n /= max(1e-5, length(n));
                weights = n * n;
                weights /= max(1e-5, weights.x + weights.y + weights.z);
            }

            return (alongX * weights.x) + (alongY * weights.y) + (alongZ * weights.z);
        }

        float4 ShadeHardLit(float4 one, float4 other)
        {
            return float4(
                other.x < 0.5 ? 2.0 * one.x * other.x : 1.0 - (2.0 * (1.0 - one.x) * (1.0 - other.x)),
                other.y < 0.5 ? 2.0 * one.y * other.y : 1.0 - (2.0 * (1.0 - one.y) * (1.0 - other.y)),
                other.z < 0.5 ? 2.0 * one.z * other.z : 1.0 - (2.0 * (1.0 - one.z) * (1.0 - other.z)),
                other.w < 0.5 ? 2.0 * one.w * other.w : 1.0 - (2.0 * (1.0 - one.w) * (1.0 - other.w)));
        }

        float ShadeDeep(float4 eye, float4 at)
        {
            return (at.x * eye.x) + (at.y * eye.y) + (at.z * eye.z) + eye.w;
        }
        """;

    /// <summary>
    /// The program as HLSL: its sheets, <see cref="Library"/>, and <c>ShadeOut ShadeRun(...)</c> - the colour as ShadeProgram.Colour returns it.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="firstSheet">The texture slot its first sheet is read from; the rest follow.</param>
    /// <remarks>
    /// <c>ShadeRun(SamplerState wrap, float2 uv, float3 place, float3 turn, float4 tint, float behind, float clock, float4 eye)</c>:
    /// the coordinates, the place and the interpolated normal in the model's space, the vertex
    /// colour, the solid depth behind (<see cref="Nothing"/> where none), the clock and the way into
    /// the picture - ShadeProgram's fixed registers, in its order.
    /// </remarks>
    public static string Of(ShadeProgram program, int firstSheet)
    {
        ArgumentNullException.ThrowIfNull(program);
        var hlsl = new StringBuilder(4096);
        for (var sheet = 0; sheet < program.Textures.Count; sheet++)
        {
            hlsl.Append(CultureInfo.InvariantCulture, $"Texture2D ShadeSheet{sheet} : register(t{firstSheet + sheet});").AppendLine();
        }

        // WHAT THE DRAWING ASKS OF THE PROGRAM, as constants the compiler folds - see ShadeProgram's own.
        hlsl.Append("static const bool ShadeHasAlpha = ").Append(Truth(program.HasAlpha)).AppendLine(";");
        hlsl.Append("static const bool ShadeHasSpecular = ").Append(Truth(program.HasSpecular)).AppendLine(";");
        hlsl.Append("static const bool ShadeHasGloss = ").Append(Truth(program.HasGloss)).AppendLine(";");
        hlsl.Append("static const bool ShadeUsesDepth = ").Append(Truth(program.UsesDepth)).AppendLine(";");
        hlsl.AppendLine(Library);
        hlsl.AppendLine("ShadeOut ShadeRun(SamplerState wrap, float2 uv, float3 place, float3 turn, float4 tint, float behind, float clock, float4 eye)");
        hlsl.AppendLine("{");

        // THE FIXED REGISTERS, as Colour and Preset set them.
        hlsl.AppendLine("    float4 r0 = float4(uv, 0.0, 0.0);");
        hlsl.AppendLine("    float4 r1 = float4(place, 0.0);");
        hlsl.AppendLine("    float4 r2 = float4(dot(turn, turn) > 1e-12 ? normalize(turn) : turn, 0.0);");
        hlsl.AppendLine("    float4 r3 = float4(turn, 0.0);");
        hlsl.AppendLine("    float4 r4 = tint;");
        hlsl.AppendLine("    float4 r5 = clock.xxxx;");
        hlsl.AppendLine("    float4 r6 = behind.xxxx;");
        hlsl.AppendLine("    float4 r7 = eye;");
        if (program.UsesTangents)
        {
            // FixModelTBN's frame from the screen's derivatives - GetScreenspaceTangentFrame, the
            // engine's own: dp = T du + B dv along x and along y, solved. On a flat triangle it is
            // ShadeProgram.Spanned's answer from the edges.
            hlsl.AppendLine("    float2 uvx = ddx(uv);");
            hlsl.AppendLine("    float2 uvy = ddy(uv);");
            hlsl.AppendLine("    float3 px = ddx(place);");
            hlsl.AppendLine("    float3 py = ddy(place);");
            hlsl.AppendLine("    float spans = (uvx.x * uvy.y) - (uvx.y * uvy.x);");
            hlsl.AppendLine("    float4 r8 = abs(spans) > 0.0 ? float4(((px * uvy.y) - (py * uvx.y)) / spans, 0.0) : float4(0.0, 0.0, 0.0, 0.0);");
            hlsl.AppendLine("    float4 r9 = abs(spans) > 0.0 ? float4(((py * uvx.x) - (px * uvy.x)) / spans, 0.0) : float4(0.0, 0.0, 0.0, 0.0);");
        }
        else
        {
            hlsl.AppendLine("    float4 r8 = float4(0.0, 0.0, 0.0, 0.0);");
            hlsl.AppendLine("    float4 r9 = float4(0.0, 0.0, 0.0, 0.0);");
        }

        hlsl.AppendLine("    float4 r10 = float4(0.0, 0.0, 0.0, 0.0);");

        // EVERY OTHER REGISTER STARTS AT NOUGHT, as the processor's zeroed scratch does - a swizzle
        // writes part of one and leaves the rest as it found it.
        ReadOnlySpan<int> constantAt = program.ConstantRegisters;
        ReadOnlySpan<Vector4> constants = program.ConstantValues;
        var constant = new Vector4?[program.Registers];
        for (var one = 0; one < constantAt.Length; one++)
        {
            constant[constantAt[one]] = constants[one];
        }

        for (var register = FixedRegisters; register < program.Registers; register++)
        {
            hlsl.Append(CultureInfo.InvariantCulture, $"    float4 r{register} = ")
                .Append(constant[register] is { } value ? Vector(value) : "float4(0.0, 0.0, 0.0, 0.0)")
                .AppendLine(";");
        }

        int parallax = 0;
        foreach (ShadeProgram.Step step in program.Steps)
        {
            Step(hlsl, program, step, ref parallax);
        }

        hlsl.AppendLine("    ShadeOut said;");
        hlsl.Append(CultureInfo.InvariantCulture, $"    float4 colour = r{program.Result};").AppendLine();
        hlsl.AppendLine("    said.colour = float3(SrgbOf(colour.x), SrgbOf(colour.y), SrgbOf(colour.z));");
        hlsl.AppendLine("    said.alpha = colour.w;");
        hlsl.AppendLine(program.Discards ? "    said.dropped = r10.x > 0.0;" : "    said.dropped = false;");
        hlsl.AppendLine(program.SpecularRegister >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"    said.specular = r{program.SpecularRegister}.xyz;")
            : "    said.specular = float3(0.0, 0.0, 0.0);");
        hlsl.AppendLine(program.GlossRegister >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"    said.gloss = r{program.GlossRegister}.x;")
            : "    said.gloss = 0.0;");
        hlsl.AppendLine("    return said;");
        hlsl.AppendLine("}");
        return hlsl.ToString();
    }

    /// <summary>The registers the mesh and the drawing set before the program's own - ShadeProgram's Fixed.</summary>
    private const int FixedRegisters = ShadeProgram.Dropped + 1;

    /// <summary>One step as one statement - ShadeProgram.Run's case for its op.</summary>
    private static void Step(StringBuilder hlsl, ShadeProgram program, ShadeProgram.Step step, ref int parallax)
    {
        string to = R(step.To), a = R(step.A), b = R(step.B), c = R(step.C), d = R(step.D), e = R(step.E);
        string? said = step.Op switch
        {
            ShadeProgram.Op.Clear => "float4(0.0, 0.0, 0.0, 0.0)",
            ShadeProgram.Op.Add => step.C >= 0 ? $"{a} + {b} + {c}" : $"{a} + {b}",
            ShadeProgram.Op.Subtract => $"{a} - {b}",
            ShadeProgram.Op.Multiply => $"{a} * {b}",
            ShadeProgram.Op.Divide => $"ShadeDivided({a}, {b})",
            ShadeProgram.Op.Min => $"min({a}, {b})",
            ShadeProgram.Op.Max => $"max({a}, {b})",
            ShadeProgram.Op.Fmod => $"fmod({a}, {b})",
            ShadeProgram.Op.MultiplyAdd => $"({a} * {b}) + {c}",
            ShadeProgram.Op.Clamp => $"min(max({a}, {b}), {c})",
            ShadeProgram.Op.Power => $"pow(max(abs({a}.x), 1e-7), {b}.x).xxxx",
            ShadeProgram.Op.Step => $"({b}.x >= {a}.x ? 1.0 : 0.0).xxxx",
            ShadeProgram.Op.Negate => $"-{a}",
            ShadeProgram.Op.OneMinus => $"1.0 - {a}",
            ShadeProgram.Op.Saturate => $"saturate({a})",
            ShadeProgram.Op.Abs => $"abs({a})",
            ShadeProgram.Op.Floor => $"floor({a})",
            ShadeProgram.Op.Ceil => $"ceil({a})",

            // TO EVEN AT A HALF, which is what round compiles to.
            ShadeProgram.Op.Round => $"round({a})",
            ShadeProgram.Op.Truncate => $"trunc({a})",
            ShadeProgram.Op.Frac => $"{a} - floor({a})",
            ShadeProgram.Op.Sqrt => $"sqrt({a})",
            ShadeProgram.Op.Sign => $"ShadeSigned({a})",
            ShadeProgram.Op.Normalize => Invariant($"ShadeNormalized({a}, {step.Extra})"),
            ShadeProgram.Op.Dot => Invariant($"ShadeDotted({a}, {b}, {step.Extra}).xxxx"),
            ShadeProgram.Op.Length => Invariant($"sqrt(ShadeDotted({a}, {a}, {step.Extra})).xxxx"),
            ShadeProgram.Op.Lerp => $"{a} + (({b} - {a}) * {c}.x)",
            ShadeProgram.Op.Fit => $"ShadeFitted({a}.x, {b}.x, {c}.x, {d}.x, {e}.x).xxxx",
            ShadeProgram.Op.SmoothStep => $"ShadeSmoothed({a}.x, {b}.x, {c}.x).xxxx",
            ShadeProgram.Op.Hermite => $"ShadeHermite({a}.x, {b}.x, {c}.x).xxxx",

            // NOT A NUMBER IS NEITHER GREATER NOR LESSER, so it takes the equal branch.
            ShadeProgram.Op.If => $"({a}.x > {b}.x ? {c}.x : {a}.x < {b}.x ? {e}.x : {d}.x).xxxx",
            ShadeProgram.Op.Select => $"({c}.x != 0.0 ? {b} : {a})",
            ShadeProgram.Op.Compare => Invariant($"ShadeCompared({a}.x, {(step.B >= 0 ? b + ".x" : "0.0")}, {step.Extra}).xxxx"),
            ShadeProgram.Op.Pick => $"({b}.x == 0.0 ? {a}.x : {b}.x == 1.0 ? {a}.y : {b}.x == 2.0 ? {a}.z : {b}.x == 3.0 ? {a}.w : 0.0).xxxx",
            ShadeProgram.Op.HueTurn => $"ShadeTurn({a}.x, {b}.x, {c}.x)",
            ShadeProgram.Op.RemapHue => $"ShadeRemapped({a}, {b})",
            ShadeProgram.Op.Noise => step.Extra == 0 ? $"ShadeValueNoise({a}.xyz).xxxx" : $"ShadePerlinNoise({a}.xyz).xxxx",
            ShadeProgram.Op.Vibrance => $"ShadeVibrant({a}.x, {b})",
            ShadeProgram.Op.Rotate => $"ShadeRotated({a}.x, {b}, {(step.C >= 0 ? c : "float4(0.0, 0.0, 0.0, 0.0)")})",
            ShadeProgram.Op.Polar => $"ShadePolar({a})",
            ShadeProgram.Op.Triplanar => $"ShadeTriplanar({a}, {b}, {c}, {d}, {(step.Extra != 0 ? "true" : "false")})",
            ShadeProgram.Op.HardLight => $"ShadeHardLit({a}, {b})",
            ShadeProgram.Op.Sine => $"sin({a})",
            ShadeProgram.Op.Sample => Read(program, step.Extra, $"ShadeSheet{Invariant(step.Extra)}.Sample(wrap, {a}.xy)"),

            // A LEVEL THAT IS NOT A NUMBER READS THE TOP ONE, and max gives the number where one is not.
            ShadeProgram.Op.SampleLod => Read(program, step.Extra, $"ShadeSheet{Invariant(step.Extra)}.SampleLevel(wrap, {a}.xy, max({b}.x, 0.0))"),
            _ => null,
        };

        if (said is not null)
        {
            hlsl.Append("    ").Append(to).Append(" = ").Append(said).AppendLine(";");
        }
        else
        {
            switch (step.Op)
            {
                case ShadeProgram.Op.Swizzle:
                {
                    // FROM A COPY, so a register swizzled into itself reads what it held before.
                    hlsl.Append("    { float4 from = ").Append(a).AppendLine(";");
                    int packed = step.Extra;
                    int mask = packed >> 8;
                    for (var part = 0; part < 4; part++)
                    {
                        if ((mask & (1 << part)) != 0)
                        {
                            hlsl.Append("      ").Append(to).Append('.').Append(Part(part))
                                .Append(" = from.").Append(Part((packed >> (part * 2)) & 3)).AppendLine(";");
                        }
                    }

                    hlsl.AppendLine("    }");
                    break;
                }

                case ShadeProgram.Op.Depth:
                    // length(bottom - camera) - length(world_pos - camera): on a parallel view, the
                    // difference of the two depths along it.
                    hlsl.Append("    { float distance = r6.x - ShadeDeep(r7, ").Append(a).AppendLine(");");
                    if (step.Extra != 0)
                    {
                        hlsl.AppendLine("      if (distance < 0.0) { r10 = float4(1.0, 1.0, 1.0, 1.0); }");
                    }

                    hlsl.Append("      ").Append(to).AppendLine(" = distance.xxxx; }");
                    break;

                case ShadeProgram.Op.ContactFade:
                    hlsl.Append("    { float4 albedo = ").Append(a).AppendLine(";");
                    hlsl.Append("      float surface = r7.z * (r6.x - ShadeDeep(r7, ").Append(b).AppendLine("));");
                    hlsl.AppendLine("      if (surface < 0.0) { r10 = float4(1.0, 1.0, 1.0, 1.0); }");
                    hlsl.Append("      float fade = ").Append(d).Append(".x * 10.0 * (1.0 - pow(saturate(1.0 - abs(").Append(c).AppendLine(".z)), 0.2));");
                    hlsl.AppendLine("      float ratio = fade > 0.0 ? saturate(surface / fade) : surface > 0.0 ? 1.0 : 0.0;");
                    hlsl.AppendLine("      albedo.w *= ratio;");
                    hlsl.Append("      ").Append(to).AppendLine(" = albedo; }");
                    break;

                case ShadeProgram.Op.Parallax:
                    Parallax(hlsl, program, step, parallax++);
                    break;

                // A STEP LEFT OUT WOULD BE A COLOUR GONE WRONG WITHOUT A WORD - an op added to the
                // program and not here fails the first time it is written instead.
                default:
                    throw new InvalidOperationException($"no HLSL for the {step.Op} step");
            }
        }

        if (step.Splat)
        {
            hlsl.Append("    ").Append(to).Append(" = ").Append(to).AppendLine(".xxxx;");
        }
    }

    /// <summary>
    /// ParallaxUvSpace with UvSpaceRaymarchChord, as ShadeProgram.Parallaxed marches it - the height read at the level the starting coordinates step at, the fragment's SAMPLE_TEX2DGRAD.
    /// </summary>
    private static void Parallax(StringBuilder hlsl, ShadeProgram program, ShadeProgram.Step step, int which)
    {
        string uv = R(step.A), way = R(step.C), packed = R(step.D), to = R(step.To);
        string sheet = $"ShadeSheet{Invariant(step.Extra)}";
        string read = Read(program, step.Extra, $"{sheet}.SampleGrad(wrap, along.xy, startX, startY)");
        string n = Invariant(which);
        hlsl.Append("    { float4 start = ").Append(uv).AppendLine(";");
        hlsl.Append("      float4 packed = ").Append(packed).AppendLine(";");
        hlsl.AppendLine("      float2 startX = ddx(start.xy);");
        hlsl.AppendLine("      float2 startY = ddy(start.xy);");
        hlsl.AppendLine("      float3 normal = r3.xyz;");
        hlsl.AppendLine("      float size = length(normal);");
        hlsl.AppendLine("      float3 m0 = r8.xyz;");
        hlsl.AppendLine("      float3 m1 = r9.xyz;");
        hlsl.AppendLine("      float3 m2 = normal / (size + 1e-7) * packed.x;");
        hlsl.AppendLine("      float3 c0 = cross(m1, m2);");
        hlsl.AppendLine("      float3 c1 = cross(m2, m0);");
        hlsl.AppendLine("      float3 c2 = cross(m0, m1);");
        hlsl.AppendLine("      float det = dot(m0, c0);");
        hlsl.AppendLine("      if (!(abs(det) > 0.0))");
        hlsl.AppendLine("      {");
        hlsl.Append("        ").Append(to).AppendLine(" = float4(start.xy, 0.0, 0.0);");
        hlsl.AppendLine("      }");
        hlsl.AppendLine("      else");
        hlsl.AppendLine("      {");
        hlsl.Append("        float3 toward = ").Append(way).AppendLine(".xyz;");
        hlsl.AppendLine("        float3 dir = float3(dot(toward, c0), dot(toward, c1), dot(toward, c2)) / det;");
        hlsl.AppendLine("        float dz = abs(dir.z) > 1e-7 ? dir.z : 1e-7;");
        hlsl.AppendLine("        float offsetScale = packed.y / dz;");
        hlsl.AppendLine("        float3 origin = float3(start.x + (dir.x * offsetScale), start.y + (dir.y * offsetScale), 1.0);");
        hlsl.AppendLine("        int straight = (int)packed.z;");
        hlsl.AppendLine("        int refine = (int)packed.w;");
        hlsl.AppendLine("        float most = -origin.z / dz;");
        hlsl.AppendLine("        float linearStep = 1.0 / (straight + 1);");
        hlsl.AppendLine("        float2 low = float2(0.0, -1.0);");
        hlsl.AppendLine("        float2 high = float2(1.0, 1.0);");
        hlsl.AppendLine("        float chord = 0.0;");
        hlsl.AppendLine("        bool found = false;");
        hlsl.Append("        [loop] for (int at").Append(n).Append(" = 0; at").Append(n).Append(" < straight + refine + 2; at").Append(n).AppendLine("++)");
        hlsl.AppendLine("        {");
        hlsl.Append("          float x = found ? chord : linearStep * at").Append(n).AppendLine(";");
        hlsl.AppendLine("          float3 along = origin + (dir * (most * x));");
        hlsl.Append("          float y = (").Append(read).AppendLine(").x - along.z;");
        hlsl.AppendLine("          if (y > 0.0) { found = true; high = float2(x, y); } else { low = float2(x, y); }");
        hlsl.AppendLine("          chord = low.x + ((high.x - low.x) * saturate(-low.y / max(1e-7, -low.y + high.y)));");
        hlsl.AppendLine("        }");
        hlsl.AppendLine("        float reached = (chord * most) + offsetScale;");
        hlsl.Append("        ").Append(to).AppendLine(" = float4(start.x + (dir.x * reached), start.y + (dir.y * reached), reached, 0.0);");
        hlsl.AppendLine("      }");
        hlsl.AppendLine("    }");
    }

    /// <summary>A sheet's read, linearised by the table where the sheet is sRGB - ShadeProgram.Read.</summary>
    private static string Read(ShadeProgram program, int sheet, string sample)
        => program.Textures[sheet].Srgb
            ? $"ShadeLinearised({sample})"
            : sample;

    private static string R(int register) => register >= 0 ? "r" + register.ToString(CultureInfo.InvariantCulture) : "float4(0.0, 0.0, 0.0, 0.0)";

    private static char Part(int part) => "xyzw"[part];

    private static string Truth(bool value) => value ? "true" : "false";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Vector(Vector4 value) => $"float4({Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}, {Number(value.W)})";

    /// <summary>A float written so the compiler reads back the very same bits - the infinities and not-a-number by their bits.</summary>
    public static string Number(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return string.Create(CultureInfo.InvariantCulture, $"asfloat(0x{BitConverter.SingleToUInt32Bits(value):X8}u)");
        }

        string said = value.ToString("G9", CultureInfo.InvariantCulture);
        return said.Contains('.', StringComparison.Ordinal) || said.Contains('E', StringComparison.Ordinal) ? said : said + ".0";
    }
}
