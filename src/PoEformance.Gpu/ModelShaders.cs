using PoEformance.Game.Files;

namespace PoEformance.Gpu;

/// <summary>
/// The HLSL a model's picture is drawn with - MeshPicture's arithmetic, pixel for pixel where a card can match it.
/// </summary>
/// <remarks>
/// SHADER MODEL 4, so a device the overlay could only get at feature level 10 still draws: nothing
/// here needs more. Compiled at run time like the overlay's own ImGui shaders, through the
/// d3dcompiler_47.dll every Windows since 8 carries.
///
/// THE COLOURS ARE sRGB NUMBERS THROUGHOUT, as MeshPicture keeps them: a texture is read as it is
/// stored (an R8G8B8A8_UNORM view, no conversion) and shaded by multiplying those numbers, so the two
/// pictures agree. The picture's own lamp and ambient are MeshPicture.Lamp and MeshPicture.Ambient.
///
/// TRANSLUCENT SHAPES ARE ADDED IN PREMULTIPLIED TERMS into a float target and turned back into
/// straight colour at the end, the order MeshPicture.Drawing.Over works in: a mixed layer covers its
/// alpha's worth, an added one adds its colour times its alpha and grows the coverage by the brightest
/// channel of what it added.
///
/// THE GAME'S LIGHT IS SceneLight.Shade, line for line (Lighted): the sun through its shadow map, the
/// point lights through the same grid, the flat ambient or the diffuse cube, a glossy material's lobe
/// and environment, the exposure and the colour grade. Every table the processor reads by - sRGB to
/// light and back, the grade's gamma, the gloss environment - is handed over as it is rather than
/// worked out again, so both pictures read the same entry; and the cube and the grade are read texel
/// by texel as CubeMap.Sample and ColourGrade.Sample read them, not through the card's own filtering,
/// which rounds its weights and crosses a cube's seams.
///
/// A MATERIAL'S PROGRAM IS A SHADER OF ITS OWN (<see cref="Program"/>): <see cref="Common"/>, the
/// program as ShadeHlsl writes it, and <see cref="Programmed"/>'s entry points around it - the solid
/// and the mixed pass, under the lamp or the game's light, as MeshPicture.Drawing runs a program in
/// each.
/// </remarks>
internal static class ModelShaders
{
    /// <summary>The slot the solid depth behind is read from by a mixed program - see ModelTarget.Behind.</summary>
    public const int BehindSlot = 7;

    /// <summary>The slot a program's first sheet is read from; the rest follow.</summary>
    public const int FirstSheet = 8;

    /// <summary>
    /// What every shader here shares: the constants, the resources, the vertex shader, and the light - the picture's lamp and the game's.
    /// </summary>
    public static readonly string Common = $$"""
        cbuffer Frame : register(b0)
        {
            row_major float4x4 Clip;
            row_major float4x4 View;
            float4 Lamp;      // xyz: the picture's lamp, in view space
            float4 Ink;
            float4 Shade;     // x: the ambient on the sRGB value; y: the ambient as light; z: the lamp as light; w: the lamp's Fresnel
            float4 Halfway;   // xyz: between the eye and the lamp, in view space
            float4 Clock;     // x: the clock - ShadeProgram.Clock
            float4 Eye;       // the way into the picture in model space and the origin's depth - ShadeProgram.Eye
            float4 Depth;     // x: the reach the depth buffer was laid over
            float4 Dust;      // xyz: the area's dust colour - ShadeProgram.Dust
        };

        cbuffer Part : register(b1)
        {
            // x: one where the shape wears a texture; y: the alpha under which a cut-out drops a pixel, or below nought.
            float4 Flags;
        };

        // SceneLight's numbers, model space - ModelGpu.SceneConstants, field for field.
        cbuffer Scene : register(b2)
        {
            row_major float4x4 CubeTurn;
            float4 SunColour;   // rgb: the sun's light; w: one where it shines
            float4 SunTravels;  // xyz: the way its light travels; w: one where its shadow map is read
            float4 ToEye;       // xyz: the way to the eye
            float4 Surround;    // x: the flat ambient; y: cube brightness; z: direct light env ratio; w: gi env occlusion
            float4 Finish;      // x: the exposure
            float4 CellLeast;   // xyz: the light grid's corner; w: a cell's side
            float4 ShadowU;     // xyz: the shadow map's x axis; w: its corner along it
            float4 ShadowV;     // xyz: its y axis; w: its corner along it
            float4 ShadowW;     // xyz: the way depth grows; w: the depth the map starts at
            float4 ShadowSize;  // x: texels per unit; y: units per texel; z: texels across
            int4 Ambient;       // x: nought none, one flat, two the cube; y: one where graded; z: the cube's side; w: point lights
            int4 Cells;         // xyz: the light grid's cells each way; w: where the cells' entries start in Reach
            int4 Graded;        // xyz: the grade's texels each way
        };

        Texture2D Skin : register(t0);
        Buffer<float> Tabled : register(t1);
        Buffer<float4> Points : register(t2);
        Buffer<uint> Reach : register(t3);
        Buffer<float> Cube : register(t4);
        Buffer<float> Grade : register(t5);
        Texture2D<float> Shadow : register(t6);
        Texture2D<float> Behind : register(t{{BehindSlot}});
        SamplerState Wrap : register(s0);

        // THE TABLES, one after another in Tabled - ModelGpu.Tables lays them out.
        static const int LinearSteps = {{ShadeProgram.TableSteps}};
        static const int GammaSteps = {{ColourGrade.EncodingSteps}};
        static const int GlossSide = {{GlossLight.TableSide}};
        static const int SrgbAt = LinearSteps;
        static const int GammaAt = 2 * LinearSteps;
        static const int BiasAt = GammaAt + GammaSteps + 1;
        static const int ScaleAt = BiasAt + (GlossSide * GlossSide);

        struct Corner
        {
            float3 position : POSITION;
            float3 normal : NORMAL;
            float2 spot : TEXCOORD0;
            float4 tint : COLOR0;
        };

        struct Pixel
        {
            float4 position : SV_Position;
            float3 facing : NORMAL;
            float2 spot : TEXCOORD0;
            float3 place : TEXCOORD1;
            float3 turn : TEXCOORD2;
            float4 tint : COLOR0;
        };

        // AFFINE IS EXACT: the picture is orthographic, so w is one and the card's interpolation is
        // MeshPicture's barycentric one.
        Pixel Placed(Corner corner)
        {
            Pixel pixel;
            pixel.position = mul(float4(corner.position, 1.0), Clip);
            pixel.facing = mul(float4(corner.normal, 0.0), View).xyz;
            pixel.spot = corner.spot;
            pixel.place = corner.position;
            pixel.turn = corner.normal;
            pixel.tint = corner.tint;
            return pixel;
        }

        // ShadeProgram.Linear and Srgb: the table's nearest entry, nought for not a number as saturate gives it.
        float LinearOf(float value)
        {
            return Tabled.Load((int)((saturate(value) * (LinearSteps - 1)) + 0.5));
        }

        float SrgbOf(float value)
        {
            return Tabled.Load(SrgbAt + (int)((saturate(value) * (LinearSteps - 1)) + 0.5));
        }

        float3 Linear(float3 colour)
        {
            return float3(LinearOf(colour.r), LinearOf(colour.g), LinearOf(colour.b));
        }

        float3 Srgb(float3 colour)
        {
            return float3(SrgbOf(colour.r), SrgbOf(colour.g), SrgbOf(colour.b));
        }

        // GlossLight, line for line: the Fresnel, the lobe, and the environment read from its table.
        float GlossFresnel(float vdoth)
        {
            return exp2(((-5.55473 * vdoth) - 6.98316) * vdoth);
        }

        float GlossLobe(float3 normal, float toEye, float3 lamp, float3 halfway, float gloss)
        {
            float lit = dot(normal, lamp);
            if (lit <= 0.0)
            {
                return 0.0;
            }

            lit = min(lit, 1.0);
            float roughness = 1.0 - saturate(gloss);
            float alpha = max(roughness * roughness, 2e-3);
            float alpha2 = alpha * alpha;
            float facing = saturate(dot(normal, halfway));
            float spread = ((alpha2 - 1.0) * facing * facing) + 1.0;
            float k = alpha * 0.5;
            float shadowing = 1.0 / (((toEye * (1.0 - k)) + k) * ((lit * (1.0 - k)) + k));
            return alpha2 / (spread * spread) * shadowing * lit * 0.25;
        }

        float GlossMixed(int from, int x0, int x1, int y0, int y1, float fx, float fy)
        {
            float a = Tabled.Load(from + (y0 * GlossSide) + x0);
            float b = Tabled.Load(from + (y0 * GlossSide) + x1);
            float c = Tabled.Load(from + (y1 * GlossSide) + x0);
            float d = Tabled.Load(from + (y1 * GlossSide) + x1);
            float top = a + ((b - a) * fx);
            float bottom = c + ((d - c) * fx);
            return top + ((bottom - top) * fy);
        }

        void GlossEnvironment(float toEye, float gloss, out float bias, out float scale)
        {
            float x = clamp((saturate(toEye) * GlossSide) - 0.5, 0.0, GlossSide - 1.0);
            float y = clamp((saturate(gloss) * GlossSide) - 0.5, 0.0, GlossSide - 1.0);
            int x0 = (int)x;
            int y0 = (int)y;
            int x1 = min(x0 + 1, GlossSide - 1);
            int y1 = min(y0 + 1, GlossSide - 1);
            float fx = x - x0;
            float fy = y - y0;
            bias = GlossMixed(BiasAt, x0, x1, y0, y1, fx, fy);
            scale = GlossMixed(ScaleAt, x0, x1, y0, y1, fx, fy);
        }

        // MeshPicture.Drawing.Lit's light: the ambient plus the rest times the lamp's cosine, two-sided.
        float LampShade(float3 facing, out float3 normal)
        {
            normal = dot(facing, facing) > 1e-6 ? normalize(facing) : facing;
            float lit = abs(dot(normal, Lamp.xyz));
            return Shade.x + ((1.0 - Shade.x) * lit);
        }

        // MeshPicture.Drawing.Glossed: the colour under the shading as light, the lamp's lobe and the environment added, and back.
        float3 LampGlossed(float3 colour, float shade, float3 normal, float3 specular, float gloss)
        {
            float toEye = -normal.z;
            if (toEye < 0.0)
            {
                normal = -normal;
                toEye = -toEye;
            }

            float lobe = GlossLobe(normal, toEye, Lamp.xyz, Halfway.xyz, gloss) * Shade.z;
            float bias;
            float scale;
            GlossEnvironment(toEye, gloss, bias, scale);
            float unscaled = (lobe * Shade.w) + (Shade.y * bias);
            float scaled = (lobe * (1.0 - Shade.w)) + (Shade.y * scale);
            return float3(
                SrgbOf(LinearOf(colour.x * shade) + unscaled + (scaled * specular.x)),
                SrgbOf(LinearOf(colour.y * shade) + unscaled + (scaled * specular.y)),
                SrgbOf(LinearOf(colour.z * shade) + unscaled + (scaled * specular.z)));
        }

        // MeshPicture.Drawing.Flat: the specular colour past a dielectric's laid onto the albedo.
        float3 Flattened(float3 colour, float3 specular)
        {
            return float3(
                SrgbOf(LinearOf(colour.x) + max(specular.x - 0.04, 0.0)),
                SrgbOf(LinearOf(colour.y) + max(specular.y - 0.04, 0.0)),
                SrgbOf(LinearOf(colour.z) + max(specular.z - 0.04, 0.0)));
        }

        // ShadowMap.Lit: a texel and a half off the surface, two texels of bias, four texels compared and lerped.
        float Open(int x, int y, float depth)
        {
            return depth <= Shadow.Load(int3(x, y, 0)) ? 1.0 : 0.0;
        }

        float SunReaches(float3 place, float3 normal)
        {
            float texel = ShadowSize.y;
            float3 off = place + (normal * (texel * 1.5));
            float x = ((dot(off, ShadowU.xyz) - ShadowU.w) * ShadowSize.x) - 0.5;
            float y = ((dot(off, ShadowV.xyz) - ShadowV.w) * ShadowSize.x) - 0.5;
            float depth = dot(off, ShadowW.xyz) - ShadowW.w - (texel * 2.0);
            float last = ShadowSize.z - 1.0;
            if (x < 0.0 || y < 0.0 || x >= last || y >= last)
            {
                return 1.0;
            }

            int x0 = (int)x;
            int y0 = (int)y;
            float fx = x - x0;
            float fy = y - y0;
            float top = lerp(Open(x0, y0, depth), Open(x0 + 1, y0, depth), fx);
            float bottom = lerp(Open(x0, y0 + 1, depth), Open(x0 + 1, y0 + 1, depth), fx);
            return lerp(top, bottom, fy);
        }

        // CubeMap.Sample: Direct3D's face selection, bilinear within the face, clamped at its edge.
        float3 CubeTexel(int at)
        {
            return float3(Cube.Load(at * 3), Cube.Load((at * 3) + 1), Cube.Load((at * 3) + 2));
        }

        float3 CubeRead(float3 direction)
        {
            float3 size3 = abs(direction);
            int face;
            float sc;
            float tc;
            float ma;
            if (size3.x >= size3.y && size3.x >= size3.z)
            {
                face = direction.x >= 0.0 ? 0 : 1;
                sc = direction.x >= 0.0 ? -direction.z : direction.z;
                tc = -direction.y;
                ma = size3.x;
            }
            else if (size3.y >= size3.z)
            {
                face = direction.y >= 0.0 ? 2 : 3;
                sc = direction.x;
                tc = direction.y >= 0.0 ? direction.z : -direction.z;
                ma = size3.y;
            }
            else
            {
                face = direction.z >= 0.0 ? 4 : 5;
                sc = direction.z >= 0.0 ? direction.x : -direction.x;
                tc = -direction.y;
                ma = size3.z;
            }

            if (!(ma > 0.0))
            {
                return float3(0.0, 0.0, 0.0);
            }

            int size = Ambient.z;
            float u = clamp((((sc / ma) + 1.0) * 0.5 * size) - 0.5, 0.0, size - 1.0);
            float v = clamp((((tc / ma) + 1.0) * 0.5 * size) - 0.5, 0.0, size - 1.0);
            int x0 = (int)u;
            int y0 = (int)v;
            int x1 = min(x0 + 1, size - 1);
            int y1 = min(y0 + 1, size - 1);
            float fx = u - x0;
            float fy = v - y0;
            int start = face * size * size;
            float3 top = lerp(CubeTexel(start + (y0 * size) + x0), CubeTexel(start + (y0 * size) + x1), fx);
            float3 bottom = lerp(CubeTexel(start + (y1 * size) + x0), CubeTexel(start + (y1 * size) + x1), fx);
            return lerp(top, bottom, fy);
        }

        // ColourGrade.Encoded: pow(x, 1/2.2) read between the gamma table's steps.
        float Encoded(float value)
        {
            if (!(value > 0.0))
            {
                return 0.0;
            }

            if (value >= 1.0)
            {
                return 1.0;
            }

            float at = value * GammaSteps;
            int step = (int)at;
            return lerp(Tabled.Load(GammaAt + step), Tabled.Load(GammaAt + step + 1), at - step);
        }

        // ColourGrade.Sample: trilinear, each step along an axis a texel or none at the far edge.
        float3 GradeTexel(int at)
        {
            return float3(Grade.Load(at * 3), Grade.Load((at * 3) + 1), Grade.Load((at * 3) + 2));
        }

        float3 GradeRead(float u, float v, float w)
        {
            int3 size = Graded.xyz;
            float x = clamp((u * size.x) - 0.5, 0.0, size.x - 1.0);
            float y = clamp((v * size.y) - 0.5, 0.0, size.y - 1.0);
            float z = clamp((w * size.z) - 0.5, 0.0, size.z - 1.0);
            int x0 = (int)x;
            int y0 = (int)y;
            int z0 = (int)z;
            float fx = x - x0;
            float fy = y - y0;
            float fz = z - z0;
            int stepX = x0 < size.x - 1 ? 1 : 0;
            int stepY = y0 < size.y - 1 ? size.x : 0;
            int stepZ = z0 < size.z - 1 ? size.x * size.y : 0;
            int a = (((z0 * size.y) + y0) * size.x) + x0;
            float3 c00 = lerp(GradeTexel(a), GradeTexel(a + stepX), fx);
            float3 c10 = lerp(GradeTexel(a + stepY), GradeTexel(a + stepY + stepX), fx);
            int b = a + stepZ;
            float3 c01 = lerp(GradeTexel(b), GradeTexel(b + stepX), fx);
            float3 c11 = lerp(GradeTexel(b + stepY), GradeTexel(b + stepY + stepX), fx);
            return lerp(lerp(c00, c10, fy), lerp(c01, c11, fy), fz);
        }

        // ColourGrade.Apply: the brightest channel above one taken out, looked up, and put back.
        float3 Graded3(float3 colour)
        {
            float most = max(max(colour.r, colour.g), max(colour.b, 1.0));
            float share = 1.0 / most;
            return GradeRead(Encoded(colour.r * share), Encoded(colour.g * share), Encoded(colour.b * share)) * most;
        }

        // SceneLight.Lit: one light's diffuse share, and its GGX lobe where the material is glossy.
        void Light(float3 light, float3 towards, float3 normal, float facing, bool glossy, float3 specular, float gloss, inout float3 diffuse, inout float3 shine)
        {
            float lit = dot(normal, towards);
            if (lit <= 0.0)
            {
                return;
            }

            diffuse += light * min(lit, 1.0);
            if (glossy)
            {
                float3 halfway = normalize(ToEye.xyz + towards);
                float lobe = GlossLobe(normal, facing, towards, halfway, gloss);
                float fresnel = GlossFresnel(saturate(dot(ToEye.xyz, halfway)));
                shine += light * lobe * (fresnel + (specular * (1.0 - fresnel)));
            }
        }

        // SceneLight.Shade: every light, the surround, a glossy material's environment, the exposure, the grade.
        float3 Lighted(float3 albedo, float3 place, float3 normal, float sun, bool glossy, float3 specular, float gloss)
        {
            float3 diffuse = float3(0.0, 0.0, 0.0);
            float3 shine = float3(0.0, 0.0, 0.0);
            float3 total = float3(0.0, 0.0, 0.0);
            float facing = max(dot(normal, ToEye.xyz), 1e-4);
            if (SunColour.w > 0.5 && sun > 0.0)
            {
                float3 light = SunColour.rgb * sun;
                Light(light, -SunTravels.xyz, normal, facing, glossy, specular, gloss, diffuse, shine);
                total += light;
            }

            if (Ambient.w > 0)
            {
                float3 at = (place - CellLeast.xyz) / CellLeast.w;
                if (at.x >= 0.0 && at.y >= 0.0 && at.z >= 0.0)
                {
                    int3 cell = int3(at);
                    if (cell.x < Cells.x && cell.y < Cells.y && cell.z < Cells.z)
                    {
                        int index = (((cell.z * Cells.y) + cell.y) * Cells.x) + cell.x;
                        int upto = (int)Reach.Load(index + 1);
                        for (int entry = (int)Reach.Load(index); entry < upto; entry++)
                        {
                            int one = (int)Reach.Load(Cells.w + entry) * 3;
                            float4 where = Points.Load(one);
                            float4 tint = Points.Load(one + 1);
                            float zero = Points.Load(one + 2).x;
                            float3 offset = where.xyz - place;
                            float squared = dot(offset, offset);
                            if (squared >= where.w * where.w)
                            {
                                continue;
                            }

                            float distance = sqrt(squared);
                            float ratio = distance / where.w;
                            float fade = max(0.0, 1.0 - (ratio * ratio * ratio * ratio));
                            float divisor = (distance / tint.w) + 1.0;
                            float attenuation = min(10.0, zero * fade * fade / (divisor * divisor));
                            if (!(attenuation > 0.0))
                            {
                                continue;
                            }

                            float3 light = tint.rgb * attenuation;
                            float3 towards = distance > 1e-4 ? offset / distance : normal;
                            Light(light, towards, normal, facing, glossy, specular, gloss, diffuse, shine);
                            total += light;
                        }
                    }
                }
            }

            float3 colour = diffuse * albedo;
            float3 surround = float3(0.0, 0.0, 0.0);
            if (Ambient.x == 2)
            {
                surround = (CubeRead(mul(float4(normal, 0.0), CubeTurn).xyz) * (Surround.y + (total * Surround.z)) * (1.0 - Surround.w))
                    + (Surround.x * Surround.w);
            }
            else if (Ambient.x == 1)
            {
                surround = Surround.xxx;
            }

            colour += surround * albedo;
            if (glossy)
            {
                float bias;
                float scale;
                GlossEnvironment(facing, gloss, bias, scale);
                float level = (surround.x + surround.y + surround.z) / 3.0;
                shine += (level * bias).xxx + (level * scale * specular);
            }

            float3 exposed = (colour + shine) * Finish.x;
            return Ambient.y != 0 ? Graded3(exposed) : exposed;
        }

        // MeshPicture.Drawing.Scened's normal: the model's own, two-sided, turned to the eye.
        float3 Toward(float3 turn)
        {
            float3 normal = dot(turn, turn) > 1e-12 ? normalize(turn) : ToEye.xyz;
            return dot(normal, ToEye.xyz) < 0.0 ? -normal : normal;
        }
        """;

    /// <summary>The entry points every picture is drawn with: the plain shapes, the sun's shadow map.</summary>
    public const string Fixed = """
        // MeshPicture.Drawing.Lit without a program: two-sided, the ambient plus the rest times the lamp's cosine.
        float4 Solid(Pixel pixel) : SV_Target
        {
            float3 colour = Ink.rgb;
            if (Flags.x > 0.5)
            {
                float4 texel = Skin.Sample(Wrap, pixel.spot);
                if (texel.a < Flags.y)
                {
                    discard;
                }

                colour = texel.rgb;
            }

            float3 normal;
            float shade = LampShade(pixel.facing, normal);
            return float4(colour * shade, 1.0);
        }

        // Unlit, as MeshPicture lays translucent shapes: an effect's light is its own.
        float4 Plain(Pixel pixel)
        {
            return Flags.x > 0.5 ? Skin.Sample(Wrap, pixel.spot) : float4(Ink.rgb, 0.5);
        }

        float4 Mixed(Pixel pixel) : SV_Target
        {
            float4 colour = Plain(pixel);
            float alpha = saturate(colour.a);
            return float4(colour.rgb * alpha, alpha);
        }

        float4 Added(Pixel pixel) : SV_Target
        {
            float4 colour = Plain(pixel);
            float3 added = colour.rgb * saturate(colour.a);
            return float4(added, max(added.r, max(added.g, added.b)));
        }

        // MeshPicture.Drawing.Scened without a program: two-sided, the normal in model space, the colour as light and back.
        float4 Scened(Pixel pixel) : SV_Target
        {
            float3 colour = Ink.rgb;
            if (Flags.x > 0.5)
            {
                float4 texel = Skin.Sample(Wrap, pixel.spot);
                if (texel.a < Flags.y)
                {
                    discard;
                }

                colour = texel.rgb;
            }

            float3 normal = Toward(pixel.turn);
            float sun = SunTravels.w > 0.5 ? SunReaches(pixel.place, normal) : 1.0;
            return float4(Srgb(Lighted(Linear(colour), pixel.place, normal, sun, false, float3(0.0, 0.0, 0.0), 0.0)), 1.0);
        }

        // THE SUN'S SHADOW MAP - ShadowMap.Build: the mesh drawn along the light onto the map's texels,
        // each keeping its nearest depth by the blend's minimum.
        struct Cast
        {
            float4 position : SV_Position;
            float2 spot : TEXCOORD0;
            float away : TEXCOORD1;
        };

        Cast Casting(Corner corner)
        {
            Cast cast;
            float x = (dot(corner.position, ShadowU.xyz) - ShadowU.w) * ShadowSize.x;
            float y = (dot(corner.position, ShadowV.xyz) - ShadowV.w) * ShadowSize.x;
            cast.position = float4(((2.0 * x) / ShadowSize.z) - 1.0, 1.0 - ((2.0 * y) / ShadowSize.z), 0.5, 1.0);
            cast.spot = corner.spot;
            cast.away = dot(corner.position, ShadowW.xyz) - ShadowW.w;
            return cast;
        }

        float Away(Cast cast) : SV_Target
        {
            if (Flags.x > 0.5 && Skin.Sample(Wrap, cast.spot).a < Flags.y)
            {
                discard;
            }

            return max(0.0, cast.away);
        }
        """;

    /// <summary>The fixed shaders' source: <see cref="Common"/> and <see cref="Fixed"/>.</summary>
    public static readonly string Model = Common + Fixed;

    /// <summary>
    /// A program's entry points: MeshPicture.Drawing's solid and mixed passes running it, under the picture's lamp or the game's light.
    /// </summary>
    /// <remarks>
    /// THE SOLID PASS cuts a cut-out shape on the alpha its graphs leave where they set one and on its
    /// texture's where they do not, and drops what a graph discarded; THE MIXED PASS covers by the
    /// program's alpha where its graphs set one and by the texture's or a half where they do not, and
    /// measures a ground layer against the solid depth behind it - ModelTarget's copy of the depth the
    /// solid pass left, since a translucent shape writes none.
    /// </remarks>
    public const string Programmed = """
        float BehindAt(float4 position)
        {
            float held = Behind.Load(int3(position.xy, 0));
            return held >= 1.0 ? 3.40282347e38 : (held - 0.5) * 2.0 * Depth.x;
        }

        ShadeOut Ran(Pixel pixel, bool translucent)
        {
            float behind = translucent && ShadeUsesDepth ? BehindAt(pixel.position) : 3.40282347e38;
            return ShadeRun(Wrap, pixel.spot, pixel.place, pixel.turn, pixel.tint, behind, Clock.x, Eye, Dust.xyz);
        }

        // MeshPicture.Drawing.Lit with a program.
        float3 LampLit(float3 colour, float3 specular, float gloss, float3 facing)
        {
            float3 normal;
            float shade = LampShade(facing, normal);
            if (ShadeHasSpecular)
            {
                if (ShadeHasGloss)
                {
                    return LampGlossed(colour, shade, normal, specular, gloss);
                }

                colour = Flattened(colour, specular);
            }

            return colour * shade;
        }

        // MeshPicture.Drawing.Scened with a program.
        float3 SceneLit(float3 colour, float3 specular, float gloss, float3 place, float3 turn)
        {
            float3 normal = Toward(turn);
            float3 albedo = Linear(colour);
            if (ShadeHasSpecular && !ShadeHasGloss)
            {
                albedo += max(specular - 0.04, 0.0);
            }

            float sun = SunTravels.w > 0.5 ? SunReaches(place, normal) : 1.0;
            return Srgb(Lighted(albedo, place, normal, sun, ShadeHasSpecular && ShadeHasGloss, specular, gloss));
        }

        float4 Solidly(Pixel pixel, bool scened)
        {
            bool cut = Flags.y >= 0.0;
            float edge = Flags.x > 0.5 ? Skin.Sample(Wrap, pixel.spot).a : 1.0;
            if (cut && !ShadeHasAlpha && edge < Flags.y)
            {
                discard;
            }

            ShadeOut said = Ran(pixel, false);
            if ((cut && ShadeHasAlpha && said.alpha < Flags.y) || said.dropped)
            {
                discard;
            }

            float3 shown = scened
                ? SceneLit(said.colour, said.specular, said.gloss, pixel.place, pixel.turn)
                : LampLit(said.colour, said.specular, said.gloss, pixel.facing);
            return float4(shown, 1.0);
        }

        float4 Mixing(Pixel pixel, bool scened)
        {
            float skinned = Flags.x > 0.5 ? Skin.Sample(Wrap, pixel.spot).a : 0.5;
            ShadeOut said = Ran(pixel, true);
            float cover = ShadeHasAlpha ? said.alpha : skinned;
            if (said.dropped || !(cover > 0.0))
            {
                discard;
            }

            float3 shown = scened
                ? SceneLit(said.colour, said.specular, said.gloss, pixel.place, pixel.turn)
                : LampLit(said.colour, said.specular, said.gloss, pixel.facing);
            float alpha = saturate(cover);
            return float4(shown * alpha, alpha);
        }

        float4 ProgramSolid(Pixel pixel) : SV_Target
        {
            return Solidly(pixel, false);
        }

        float4 ProgramSolidScened(Pixel pixel) : SV_Target
        {
            return Solidly(pixel, true);
        }

        float4 ProgramMixed(Pixel pixel) : SV_Target
        {
            return Mixing(pixel, false);
        }

        float4 ProgramMixedScened(Pixel pixel) : SV_Target
        {
            return Mixing(pixel, true);
        }
        """;

    /// <summary>A program's shader source: <see cref="Common"/>, the program, and <see cref="Programmed"/>.</summary>
    public static string Program(ShadeProgram program) => Common + ShadeHlsl.Of(program, FirstSheet) + Programmed;

    /// <summary>The pass that turns the premultiplied float target into the straight bytes ImGui shows.</summary>
    /// <remarks>
    /// TRUNCATED TO A BYTE, NOT ROUNDED, because MeshPicture's Byte truncates - and a value k/255 is
    /// written back as exactly k by the target's conversion, so the two pictures meet on the byte.
    /// </remarks>
    public const string Resolve = """
        Texture2D Accumulated : register(t0);

        float4 Whole(uint corner : SV_VertexID) : SV_Position
        {
            float2 at = float2((corner << 1) & 2, corner & 2);
            return float4((at * float2(2.0, -2.0)) + float2(-1.0, 1.0), 0.0, 1.0);
        }

        float4 Straight(float4 position : SV_Position) : SV_Target
        {
            float4 held = Accumulated.Load(int3(position.xy, 0));
            float alpha = min(held.a, 1.0);
            if (!(alpha > 0.0))
            {
                return float4(0.0, 0.0, 0.0, 0.0);
            }

            float4 straight = float4(saturate(held.rgb / alpha), alpha);
            return floor(straight * 255.0) / 255.0;
        }
        """;
}
