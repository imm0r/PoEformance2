using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PoEformance.Gpu;

/// <summary>
/// Where one picture of a model is drawn on the graphics card, kept from frame to frame like MeshPicture.Canvas.
/// </summary>
/// <remarks>
/// THREE TEXTURES: the premultiplied float target the shapes are drawn and mixed into, its depth, and
/// the straight eight-bit picture they are turned into at the end - the one ImGui is handed, in the
/// format the processor's pictures are uploaded in, so the pane draws either the same way. Made once
/// per size and kept: a drag redraws every frame, and textures made and dropped per frame are the
/// card's version of the garbage MeshPicture.Canvas exists to avoid.
/// </remarks>
public sealed class ModelTarget : IDisposable
{
    private readonly ID3D11Texture2D _accumulated;
    private readonly ID3D11Texture2D _depth;
    private readonly ID3D11Texture2D _shown;
    private bool _disposed;

    internal ModelTarget(ID3D11Device device, int size)
    {
        Size = size;
        _accumulated = device.CreateTexture2D(new Texture2DDescription(
            Format.R32G32B32A32_Float, size, size, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        AccumulatedTarget = device.CreateRenderTargetView(_accumulated);
        AccumulatedView = device.CreateShaderResourceView(_accumulated);
        _depth = device.CreateTexture2D(new Texture2DDescription(Format.D32_Float, size, size, 1, 1, BindFlags.DepthStencil));
        Depth = device.CreateDepthStencilView(_depth);
        _shown = device.CreateTexture2D(new Texture2DDescription(
            Format.R8G8B8A8_UNorm, size, size, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        ShownTarget = device.CreateRenderTargetView(_shown);
        Shown = device.CreateShaderResourceView(_shown);
    }

    /// <summary>How many pixels each way.</summary>
    public int Size { get; }

    /// <summary>The finished picture, straight RGBA bytes - what ImGui draws.</summary>
    public ID3D11ShaderResourceView Shown { get; }

    internal ID3D11Texture2D ShownTexture => _shown;

    internal ID3D11RenderTargetView AccumulatedTarget { get; }

    internal ID3D11ShaderResourceView AccumulatedView { get; }

    internal ID3D11DepthStencilView Depth { get; }

    internal ID3D11RenderTargetView ShownTarget { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shown.Dispose();
        ShownTarget.Dispose();
        _shown.Dispose();
        Depth.Dispose();
        _depth.Dispose();
        AccumulatedView.Dispose();
        AccumulatedTarget.Dispose();
        _accumulated.Dispose();
    }
}
