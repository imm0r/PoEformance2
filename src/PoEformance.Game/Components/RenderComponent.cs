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
    /// <summary>Longest name taken seriously. Anything longer is a bad read, not a name.</summary>
    public const int LongestName = 128;

    private readonly IMemoryReader _reader;
    private readonly int _worldPos;
    private readonly int _terrainHeight;
    private readonly int _modelBounds;
    private readonly int _name;
    private readonly int _rotation;

    public RenderReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef render = schema.Structs["Render"];
        _worldPos = render.OffsetOf("CurrentWorldPosition"); // x; y at +4, z at +8
        _terrainHeight = render.OffsetOf("TerrainHeight");
        _modelBounds = render.OffsetOf("CharacterModelBounds"); // x; y at +4, z at +8
        _name = render.OffsetOf("Name");
        _rotation = render.OffsetOf("RotationCurrent"); // the turn target is the next float
    }

    /// <summary>
    /// Which way this entity faces, and which way it is turning to face. Null when unreadable.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="Read"/>, and not folded into its span, which would be one read
    /// rather than two and would break every recording already committed. A replay only serves
    /// reads the running build actually performed: those files hold twelve bytes at the world
    /// position, so a widened span would fail outright and take every entity in them with it.
    /// The cost of the extra call is only paid where somebody asked for facing at all.
    ///
    /// The two floats are adjacent, so one call brings both. See <c>Facing</c> for what the
    /// angle means - it is not measured from the axis anybody would guess.
    /// </remarks>
    public (float Angle, float Turning)? ReadFacing(ulong componentAddress)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(componentAddress))
        {
            return null;
        }

        Span<float> pair = stackalloc float[2];
        return _reader.TryRead(
            componentAddress + (ulong)_rotation,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pair))
            ? (pair[0], pair[1])
            : null;
    }

    /// <summary>
    /// The name the game shows for this entity, or empty when it has none.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="Read"/> on purpose. A position is read for every entity on
    /// every snapshot; a name never changes while an entity exists, so reading it in the same
    /// call would pay for a string thousands of times to learn what it said the first time.
    /// Whoever calls this is expected to remember the answer.
    /// </remarks>
    public string ReadName(ulong componentAddress)
        => MemoryReaderExtensions.IsPlausiblePointer(componentAddress)
            ? _reader.ReadStdWString(componentAddress + (ulong)_name, LongestName)
            : string.Empty;

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
