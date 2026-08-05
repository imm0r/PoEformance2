using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>The world position and model bounds read off an entity's Render component.</summary>
public readonly record struct RenderComponent(float X, float Y, float Z, float TerrainHeight);

/// <summary>Reads the Render component (world position). Offsets come from the schema.</summary>
public sealed class RenderReader
{
    private readonly IMemoryReader _reader;
    private readonly int _worldPos;
    private readonly int _terrainHeight;

    public RenderReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef render = schema.Structs["Render"];
        _worldPos = render.OffsetOf("CurrentWorldPosition"); // x; y at +4, z at +8
        _terrainHeight = render.OffsetOf("TerrainHeight");
    }

    /// <summary>Reads the world position from a Render component address, or null on failure.</summary>
    public RenderComponent? Read(ulong componentAddress)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(componentAddress))
        {
            return null;
        }

        Span<float> pos = stackalloc float[3];
        if (!_reader.TryRead(componentAddress + (ulong)_worldPos, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pos)))
        {
            return null;
        }

        float terrainHeight = _reader.Read<float>(componentAddress + (ulong)_terrainHeight);
        return new RenderComponent(pos[0], pos[1], pos[2], terrainHeight);
    }
}
