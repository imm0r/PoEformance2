using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PoEformance.Gpu;

/// <summary>
/// Where one picture of a model is drawn on the graphics card, kept from frame to frame like MeshPicture.Canvas.
/// </summary>
/// <remarks>
/// THE PREMULTIPLIED FLOAT TARGET the shapes are drawn and mixed into, its depth, and the straight
/// eight-bit picture they are turned into at the end - the one ImGui is handed, in the format the
/// processor's pictures are uploaded in, so the pane draws either the same way. Made once per size
/// and kept: a drag redraws every frame, and textures made and dropped per frame are the card's
/// version of the garbage MeshPicture.Canvas exists to avoid.
///
/// THE DEPTH CARRIES A STENCIL - the card's MeshPicture.Canvas.Stamps, which lets a translucent shape
/// cover a pixel once however many of its triangles reach it - and has a COPY (<see cref="Behind"/>):
/// a mixed ground layer measures itself against the solid depth under it, and the depth buffer cannot
/// be read while the translucent pass is still testing against it.
/// </remarks>
public sealed class ModelTarget : IDisposable
{
    private readonly ID3D11Texture2D _accumulated;
    private readonly ID3D11Texture2D _depth;
    private readonly ID3D11Texture2D _behind;
    private readonly ID3D11Texture2D _shown;
    private bool _disposed;

    internal ModelTarget(ID3D11Device device, int size)
    {
        Size = size;
        _accumulated = device.CreateTexture2D(new Texture2DDescription(
            Format.R32G32B32A32_Float, size, size, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        AccumulatedTarget = device.CreateRenderTargetView(_accumulated);
        AccumulatedView = device.CreateShaderResourceView(_accumulated);

        // THIRTY-TWO BITS OF FLOAT DEPTH AS BEFORE, and the stencil beside them - not twenty-four bits
        // of fixed depth, which would move every tie the processor's float depth settles.
        _depth = device.CreateTexture2D(new Texture2DDescription(Format.R32G8X24_Typeless, size, size, 1, 1, BindFlags.DepthStencil));
        Depth = device.CreateDepthStencilView(_depth, new DepthStencilViewDescription(DepthStencilViewDimension.Texture2D, Format.D32_Float_S8X24_UInt));
        _behind = device.CreateTexture2D(new Texture2DDescription(Format.R32G8X24_Typeless, size, size, 1, 1, BindFlags.ShaderResource));
        Behind = device.CreateShaderResourceView(_behind, new ShaderResourceViewDescription
        {
            Format = Format.R32_Float_X8X24_Typeless,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        });
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

    /// <summary>The solid pass's depth, copied for the mixed pass to read - see <see cref="Kept"/>.</summary>
    internal ID3D11ShaderResourceView Behind { get; }

    internal ID3D11RenderTargetView ShownTarget { get; }

    /// <summary>Copies the depth the solid pass left into <see cref="Behind"/>.</summary>
    internal void Kept(ID3D11DeviceContext context) => context.CopyResource(_behind, _depth);

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
        Behind.Dispose();
        _behind.Dispose();
        Depth.Dispose();
        _depth.Dispose();
        AccumulatedView.Dispose();
        AccumulatedTarget.Dispose();
        _accumulated.Dispose();
    }
}
