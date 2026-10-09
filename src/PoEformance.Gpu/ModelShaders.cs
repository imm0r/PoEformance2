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
/// </remarks>
internal static class ModelShaders
{
    /// <summary>The vertex shader and the three pixel shaders a model is drawn with.</summary>
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

        Texture2D Skin : register(t0);
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
        };

        Pixel Placed(Corner corner)
        {
            Pixel pixel;
            pixel.position = mul(float4(corner.position, 1.0), Clip);
            pixel.facing = mul(float4(corner.normal, 0.0), View).xyz;
            pixel.spot = corner.spot;
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
