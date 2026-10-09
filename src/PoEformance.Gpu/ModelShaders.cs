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
/// THE GAME'S LIGHT IS SceneLight.Shade, line for line (Scened): the sun through its shadow map, the
/// point lights through the same grid, the flat ambient or the diffuse cube, the exposure and the
/// colour grade. Every table the processor reads by - sRGB to light and back, the grade's gamma - is
/// handed over as it is rather than worked out again with pow, so both pictures read the same entry;
/// and the cube and the grade are read texel by texel as CubeMap.Sample and ColourGrade.Sample read
/// them, not through the card's own filtering, which rounds its weights and crosses a cube's seams.
/// </remarks>
internal static class ModelShaders
{
    /// <summary>The vertex shaders and the pixel shaders a model is drawn with, the sun's shadow map among them.</summary>
    public const string Model = """
        cbuffer Frame : register(b0)
        {
            row_major float4x4 Clip;
            row_major float4x4 View;
            float4 Lamp;
            float4 Ink;
            float4 Shade;
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
            int4 Tables;        // x: the sRGB tables' entries; y: the gamma table's steps
        };

        Texture2D Skin : register(t0);
        Buffer<float> Tabled : register(t1);
        Buffer<float4> Points : register(t2);
        Buffer<uint> Reach : register(t3);
        Buffer<float> Cube : register(t4);
        Buffer<float> Grade : register(t5);
        Texture2D<float> Shadow : register(t6);
        SamplerState Wrap : register(s0);

        struct Corner
        {
            float3 position : POSITION;
            float3 normal : NORMAL;
            float2 spot : TEXCOORD0;
        };

        struct Pixel
        {
            float4 position : SV_Position;
            float3 facing : NORMAL;
            float2 spot : TEXCOORD0;
            float3 place : TEXCOORD1;
            float3 turn : TEXCOORD2;
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
            return pixel;
        }

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

            float3 normal = dot(pixel.facing, pixel.facing) > 1e-6 ? normalize(pixel.facing) : pixel.facing;
            float lit = abs(dot(normal, Lamp.xyz));
            return float4(colour * (Shade.x + ((1.0 - Shade.x) * lit)), 1.0);
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

        // ShadeProgram.Linear and Srgb: the table's nearest entry, nought for not a number as saturate gives it.
        float FromTable(float value, int from)
        {
            return Tabled.Load(from + (int)((saturate(value) * (Tables.x - 1)) + 0.5));
        }

        float3 Linear(float3 colour)
        {
            return float3(FromTable(colour.r, 0), FromTable(colour.g, 0), FromTable(colour.b, 0));
        }

        float3 Srgb(float3 colour)
        {
            return float3(FromTable(colour.r, Tables.x), FromTable(colour.g, Tables.x), FromTable(colour.b, Tables.x));
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

            float at = value * Tables.y;
            int step = (int)at;
            int from = 2 * Tables.x;
            return lerp(Tabled.Load(from + step), Tabled.Load(from + step + 1), at - step);
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

        // SceneLight.Shade without a gloss: every light's diffuse share, the surround, the exposure, the grade.
        float3 Lighted(float3 albedo, float3 place, float3 normal, float sun)
        {
            float3 diffuse = float3(0.0, 0.0, 0.0);
            float3 total = float3(0.0, 0.0, 0.0);
            if (SunColour.w > 0.5 && sun > 0.0)
            {
                float3 light = SunColour.rgb * sun;
                float lit = dot(normal, -SunTravels.xyz);
                if (lit > 0.0)
                {
                    diffuse += light * min(lit, 1.0);
                }

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
                            float lit = dot(normal, towards);
                            if (lit > 0.0)
                            {
                                diffuse += light * min(lit, 1.0);
                            }

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
            float3 exposed = colour * Finish.x;
            return Ambient.y != 0 ? Graded3(exposed) : exposed;
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

            float3 normal = dot(pixel.turn, pixel.turn) > 1e-12 ? normalize(pixel.turn) : ToEye.xyz;
            if (dot(normal, ToEye.xyz) < 0.0)
            {
                normal = -normal;
            }

            float sun = SunTravels.w > 0.5 ? SunReaches(pixel.place, normal) : 1.0;
            return float4(Srgb(Lighted(Linear(colour), pixel.place, normal, sun)), 1.0);
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
