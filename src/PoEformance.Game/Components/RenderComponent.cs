using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>The world position and model bounds read off an entity's Render component.</summary>
/// <param name="Z">
/// The entity's BASE height - its feet. GameHelper2's HealthBars plugin starts from this and
/// subtracts <paramref name="ModelBoundsZ"/> to reach the top of the model, which is where the
/// game floats the health bar; so this is the bottom, not the bar.
/// </param>
/// <param name="ModelBoundsZ">
/// Height of the model above its base. Subtracted from <paramref name="Z"/> (up is negative)
/// to get the health-bar height.
/// </param>
/// <param name="TerrainHeight">
/// Ground height in the MAP's coordinate space. Used by the map radar, not by the
/// world-to-screen projection - see the two systems described in WorldSnapshot.
/// </param>
public readonly record struct RenderComponent(float X, float Y, float Z, float TerrainHeight, float ModelBoundsZ)
{
    /// <summary>
    /// Where the game draws this entity's floating health bar: the top of its model.
    /// </summary>
    /// <remarks>
    /// Straight from GameHelper2's HealthBars plugin
    /// (<c>curPos.Z -= rComp.ModelBounds.Z</c>). Projecting this must land on the bar the
    /// game itself drew, which makes it a free pixel-accurate check on the whole chain.
    /// </remarks>
    public float HealthBarZ => Z - ModelBoundsZ;
}

/// <summary>Reads the Render component (world position). Offsets come from the schema.</summary>
public sealed class RenderReader
{
    private readonly IMemoryReader _reader;
    private readonly int _worldPos;
    private readonly int _terrainHeight;
    private readonly int _modelBounds;

    public RenderReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef render = schema.Structs["Render"];
        _worldPos = render.OffsetOf("CurrentWorldPosition"); // x; y at +4, z at +8
        _terrainHeight = render.OffsetOf("TerrainHeight");
        _modelBounds = render.OffsetOf("CharacterModelBounds"); // x; y at +4, z at +8
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
        float modelBoundsZ = _reader.Read<float>(componentAddress + (ulong)_modelBounds + 8);
        return new RenderComponent(pos[0], pos[1], pos[2], terrainHeight, modelBoundsZ);
    }
}
