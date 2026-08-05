using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.World;

namespace PoEformance.Game.Diagnostics;

/// <summary>The result of probing the local player and projecting its position.</summary>
public sealed record PlayerProbeResult(
    bool InGame,
    string Path,
    int ComponentCount,
    bool HasRender,
    float WorldX,
    float WorldY,
    float WorldZ,
    bool Projected,
    double NdcMagnitude)
{
    /// <summary>
    /// The camera follows the player, so the player's own position must project near the
    /// centre of the view.
    /// </summary>
    /// <remarks>
    /// NECESSARY BUT NOT SUFFICIENT, and that distinction cost a wrong conclusion once: a
    /// matrix that inflates w to millions drives every numerator to ~0, so the player lands
    /// dead centre while the entire scene collapses onto the same pixel. This check alone
    /// called that "MATRIX PROVEN". The conclusive test needs the OTHER entities to spread
    /// out too and lives in <c>MatrixHunt</c>; here the w plausibility band is the cheap
    /// guard that catches the collapsed case with only one point available.
    /// </remarks>
    public bool ProjectsToCentre => Projected && NdcMagnitude < 0.15;
}

/// <summary>
/// Reads the local player through the component machinery and projects its position with
/// the world-to-screen matrix - the end-to-end check that ties the whole read chain
/// together and finally proves the matrix.
/// </summary>
/// <remarks>
/// This is deliberately in the Game layer (it needs the entity/component readers), and it
/// runs the reads that a recording must contain for the proof to be replayable. Running it
/// during a <c>--record</c> session is what lets the projection be verified offline later.
/// </remarks>
public sealed class PlayerProbe
{
    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;
    private readonly RenderReader _render;
    private readonly int _w2sMatrix;

    public PlayerProbe(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
        _render = new RenderReader(reader, schema);
        _w2sMatrix = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
    }

    /// <summary>
    /// Resolves the player from <paramref name="gameStatesStatic"/>, reads its components
    /// and position, and projects it. A viewport of 2x2 makes the projection's off-centre
    /// fraction equal the NDC magnitude, so the proof needs no real window size.
    /// </summary>
    public PlayerProbeResult Probe(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            return new PlayerProbeResult(false, "", 0, false, 0, 0, 0, false, double.NaN);
        }

        Entity? player = _entities.Read(chain.PlayerEntity);
        if (player is null)
        {
            return new PlayerProbeResult(false, "", 0, false, 0, 0, 0, false, double.NaN);
        }

        ulong renderAddress = player.Component("Render");
        RenderComponent? render = renderAddress != 0 ? _render.Read(renderAddress) : null;
        if (render is null)
        {
            return new PlayerProbeResult(true, player.Path, player.Components.Count, false, 0, 0, 0, false, double.NaN);
        }

        Span<float> matrix = stackalloc float[16];
        bool haveMatrix = _reader.TryRead(
            chain.WorldData + (ulong)_w2sMatrix,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(matrix));

        double ndc = double.NaN;
        bool projected = false;
        if (haveMatrix)
        {
            // TerrainHeight, not the position's own z: the latter is where the game floats
            // the healthbar, about a character's height up, and projecting it puts the
            // player 0.18 off centre where the ground position gives 0.07.
            ScreenPoint point = WorldToScreen.Project(
                matrix, render.Value.X, render.Value.Y, render.Value.TerrainHeight, 2, 2);
            ndc = WorldToScreen.OffCentreFraction(point, 2, 2);
            projected = point.OnScreen;
        }

        return new PlayerProbeResult(
            true, player.Path, player.Components.Count, true,
            render.Value.X, render.Value.Y, render.Value.Z,
            projected, ndc);
    }

    /// <summary>Runs the probe and writes a human-readable summary.</summary>
    public PlayerProbeResult ProbeAndReport(ulong gameStatesStatic, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        PlayerProbeResult r = Probe(gameStatesStatic);

        output.WriteLine();
        output.WriteLine("player probe");
        if (!r.InGame)
        {
            output.WriteLine("  --    not in an area (no player entity) - enter the game to probe.");
            return r;
        }

        output.WriteLine($"  path        {r.Path}");
        output.WriteLine($"  components  {r.ComponentCount}");
        if (!r.HasRender)
        {
            output.WriteLine("  FAIL  player has no Render component - component lookup or offset wrong.");
            return r;
        }

        output.WriteLine($"  world pos   ({r.WorldX:F1}, {r.WorldY:F1}, {r.WorldZ:F1})");
        if (!r.Projected)
        {
            output.WriteLine("  FAIL  player projected behind the camera (w <= 0) - matrix wrong.");
            return r;
        }

        output.WriteLine($"  projection  NDC magnitude {r.NdcMagnitude:F3} "
            + (r.ProjectsToCentre
                ? "-> player is at screen centre. MATRIX PROVEN."
                : "-> player is NOT centred; matrix or position read is off."));
        return r;
    }
}
