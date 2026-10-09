using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// The game's specular light - its GGX lobe for a lamp, and its split-sum term for the environment - as the picture's lamp and a uniform environment give it.
/// </summary>
/// <remarks>
/// LINE FOR LINE FROM THE GAME'S SOURCES, shaders/include/lighting.hlsl and renderer/lighting.ffx:
/// <list type="bullet">
/// <item>A LAMP adds <c>GGXSpecular(light, normal, view, glossiness, specular)</c> times its
/// intensity - Walter et al.'s microfacet lobe with the game's eliminations: no 1/pi in the
/// distribution, Smith's term with k = alpha / 2, Schlick's Fresnel by its exp2 fit and laid from
/// it towards one by the specular colour, alpha the roughness squared and never under 2e-3.</item>
/// <item>THE ENVIRONMENT adds the specular cube's colour in the reflected direction times
/// <c>GetPrefilterGGX(NdotV, glossiness, specular)</c>, a lookup into a table the engine binds as
/// <c>environment_ggx_tex</c>, read as <c>r + g * specular</c>.</item>
/// </list>
/// THE TABLE IS WORKED OUT, NOT READ: the engine binds it from no file this tool has. What it holds
/// is written down in the same sources, though - ComputeEnvironmentGGXNumerical, the engine's own
/// check of it, averages the cube times <c>GGXSpecular / pi * 2 pi</c> over the hemisphere - so for
/// a uniform environment its entry is that lobe integrated, split where the Fresnel lays it towards
/// one into the part the specular colour scales and the part it does not. Integrated here by the
/// lobe's own importance sampling (GenerateGGXImportanceSampleH, in the same file), which carries
/// the narrow lobes of a high gloss a plain grid would miss.
///
/// THE ENVIRONMENT IS THE ASSUMPTION. An area's cube is its own and a book has none, so the
/// environment is taken as uniform and as bright as the picture's ambient already is - see
/// MeshPicture's shading - which leaves every shape without a specular colour exactly as it was.
/// </remarks>
public static class GlossLight
{
    /// <summary>How finely the environment term is tabled each way: by the cosine to the eye and by the gloss.</summary>
    private const int Side = 32;

    /// <summary>How many directions each entry is integrated over.</summary>
    private const int Samples = 512;

    /// <summary>The engine's floor on alpha, so a gloss of one is not a lobe of no width.</summary>
    private const float LeastAlpha = 2e-3f;

    /// <summary>Each entry's part the specular colour does not scale, then the part it does - see the remarks.</summary>
    private static readonly (float[] Bias, float[] Scale) Table = Tabled();

    /// <summary>The side of the environment's tables, which <see cref="Environment"/> reads by gloss down and facing across.</summary>
    public static int TableSide => Side;

    /// <summary>The environment's bias, <see cref="TableSide"/> squared, a row per gloss - read the same way elsewhere, the graphics card's.</summary>
    public static ReadOnlySpan<float> BiasTable => Table.Bias;

    /// <summary>The environment's scale, laid out as <see cref="BiasTable"/>.</summary>
    public static ReadOnlySpan<float> ScaleTable => Table.Scale;

    /// <summary>The game's Fresnel - Schlick's, by its exp2 fit.</summary>
    public static float Fresnel(float vdoth) => float.Exp2(((-5.55473f * vdoth) - 6.98316f) * vdoth);

    /// <summary>
    /// The lamp's lobe at one pixel, everything of GGXSpecular but its Fresnel: the light is this times
    /// <c>fresnel + specular * (1 - fresnel)</c>, times the lamp.
    /// </summary>
    /// <param name="normal">The normal, unit length, turned to face the eye.</param>
    /// <param name="toEye">The cosine between it and the way to the eye.</param>
    /// <param name="lamp">The way to the lamp, unit length.</param>
    /// <param name="half">Halfway between the way to the eye and the way to the lamp.</param>
    /// <param name="gloss">The glossiness the graphs left.</param>
    public static float Lobe(Vector3 normal, float toEye, Vector3 lamp, Vector3 half, float gloss)
    {
        float lit = Vector3.Dot(normal, lamp);
        if (lit <= 0f)
        {
            return 0f;
        }

        lit = MathF.Min(lit, 1f);
        float roughness = 1f - Saturate(gloss);
        float alpha = MathF.Max(roughness * roughness, LeastAlpha);
        float alpha2 = alpha * alpha;
        float facing = Saturate(Vector3.Dot(normal, half));
        float spread = ((alpha2 - 1f) * facing * facing) + 1f;
        float k = alpha * 0.5f;
        float shadowing = 1f / (((toEye * (1f - k)) + k) * ((lit * (1f - k)) + k));
        return alpha2 / (spread * spread) * shadowing * lit * 0.25f;
    }

    /// <summary>The environment term at one pixel, as the engine's table would give it: <c>bias + scale * specular</c>.</summary>
    /// <remarks>Read as the engine reads its texture: bilinear between entries at their centres, clamped at the edges.</remarks>
    public static void Environment(float toEye, float gloss, out float bias, out float scale)
    {
        float x = Math.Clamp((Saturate(toEye) * Side) - 0.5f, 0f, Side - 1);
        float y = Math.Clamp((Saturate(gloss) * Side) - 0.5f, 0f, Side - 1);
        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Side - 1);
        int y1 = Math.Min(y0 + 1, Side - 1);
        float fx = x - x0;
        float fy = y - y0;
        (float[] biases, float[] scales) = Table;
        bias = Mixed(biases, x0, x1, y0, y1, fx, fy);
        scale = Mixed(scales, x0, x1, y0, y1, fx, fy);
    }

    private static float Mixed(float[] table, int x0, int x1, int y0, int y1, float fx, float fy)
    {
        float top = table[(y0 * Side) + x0] + ((table[(y0 * Side) + x1] - table[(y0 * Side) + x0]) * fx);
        float bottom = table[(y1 * Side) + x0] + ((table[(y1 * Side) + x1] - table[(y1 * Side) + x0]) * fx);
        return top + ((bottom - top) * fy);
    }

    /// <summary>
    /// The table: for each gloss and cosine to the eye, the lobe integrated over the hemisphere and over pi.
    /// </summary>
    /// <remarks>
    /// WHY THE SUM IS SO SHORT. A direction is drawn with the probability GGX gives its half vector,
    /// <c>D NdotH / (4 VdotH)</c> with D the true distribution - the game's is pi times it - so the
    /// lobe over its probability, over pi, is <c>F G NdotL VdotH / NdotH</c>.
    /// </remarks>
    private static (float[] Bias, float[] Scale) Tabled()
    {
        var bias = new float[Side * Side];
        var scale = new float[Side * Side];
        for (var row = 0; row < Side; row++)
        {
            float roughness = 1f - ((row + 0.5f) / Side);
            float alpha = MathF.Max(roughness * roughness, LeastAlpha);
            float alpha2 = alpha * alpha;
            float k = alpha * 0.5f;
            for (var column = 0; column < Side; column++)
            {
                float toEye = (column + 0.5f) / Side;
                var eye = new Vector3(MathF.Sqrt(1f - (toEye * toEye)), 0f, toEye);
                double unscaled = 0;
                double scaled = 0;
                for (var one = 0; one < Samples; one++)
                {
                    float around = MathF.Tau * one / Samples;
                    float up = Hammersley(one);
                    float cosine = MathF.Sqrt((1f - up) / (1f + ((alpha2 - 1f) * up)));
                    float sine = MathF.Sqrt(MathF.Max(0f, 1f - (cosine * cosine)));
                    var half = new Vector3(sine * MathF.Cos(around), sine * MathF.Sin(around), cosine);
                    float eyeHalf = Vector3.Dot(eye, half);
                    Vector3 light = (2f * eyeHalf * half) - eye;
                    if (light.Z <= 0f || half.Z <= 0f)
                    {
                        continue;
                    }

                    float lit = MathF.Min(light.Z, 1f);
                    eyeHalf = Saturate(eyeHalf);
                    float shadowing = 1f / (((toEye * (1f - k)) + k) * ((lit * (1f - k)) + k));
                    float weight = shadowing * lit * eyeHalf / half.Z;
                    float fresnel = Fresnel(eyeHalf);
                    unscaled += fresnel * weight;
                    scaled += (1f - fresnel) * weight;
                }

                bias[(row * Side) + column] = (float)(unscaled / Samples);
                scale[(row * Side) + column] = (float)(scaled / Samples);
            }
        }

        return (bias, scale);
    }

    /// <summary>The base-two radical inverse - the second coordinate of a Hammersley point.</summary>
    private static float Hammersley(int index)
    {
        uint bits = (uint)index;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    private static float Saturate(float value) => value > 0f ? (value < 1f ? value : 1f) : 0f;
}
