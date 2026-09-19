using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The black rim the game's map icons have, put round a rendered one.
/// </summary>
/// <remarks>
/// A SQUARE IN A FIELD OF NOTHING is the whole fixture here, and it is enough: what the
/// outline has to get right is where it stops - out to the radius and no further, on the
/// correct side of the silhouette, without moving the picture it is drawn around. Each of
/// those is a pixel at a known place.
/// </remarks>
public class PictureOutlineTests
{
    private const int Side = 40;
    private const int Block = 10;

    /// <summary>A solid white square in the middle of a transparent picture.</summary>
    private static byte[] Square(byte alpha = 255)
    {
        var rgba = new byte[Side * Side * 4];
        for (int y = Block; y < Side - Block; y++)
        {
            for (int x = Block; x < Side - Block; x++)
            {
                int at = ((y * Side) + x) * 4;
                rgba[at] = 200;
                rgba[at + 1] = 200;
                rgba[at + 2] = 200;
                rgba[at + 3] = alpha;
            }
        }

        return rgba;
    }

    private static (byte R, byte G, byte B, byte A) At(byte[] rgba, int x, int y)
    {
        int at = ((y * Side) + x) * 4;
        return (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]);
    }

    [Fact]
    public void GrownOutwardsItSurroundsThePictureWithoutTouchingIt()
    {
        byte[] rgba = Square();
        PictureOutline.Apply(rgba, Side, Side, 2f, OutlineSide.Outward);

        // Two pixels out from the left edge of the square: black, and now part of the shape.
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 255), At(rgba, Block - 2, Side / 2));

        // Five out: still nothing at all. An outline that does not stop is a blob.
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 0), At(rgba, Block - 5, Side / 2));

        // And the picture itself is exactly what it was, which is the reason to grow outwards.
        Assert.Equal<(byte, byte, byte, byte)>((200, 200, 200, 255), At(rgba, Block, Side / 2));
        Assert.Equal<(byte, byte, byte, byte)>((200, 200, 200, 255), At(rgba, Side / 2, Side / 2));
    }

    [Fact]
    public void PaintedInwardsItEatsTheOutermostPixelsAndNoMore()
    {
        byte[] rgba = Square();
        PictureOutline.Apply(rgba, Side, Side, 2f, OutlineSide.Inward);

        // The square's own edge is black now, and still solid - the shape did not move.
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 255), At(rgba, Block, Side / 2));
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 255), At(rgba, Block + 1, Side / 2));

        // Three in, the picture is untouched.
        Assert.Equal<(byte, byte, byte, byte)>((200, 200, 200, 255), At(rgba, Block + 3, Side / 2));

        // And nothing was added outside it.
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 0), At(rgba, Block - 1, Side / 2));
    }

    [Fact]
    public void TheDistanceIsRoundRatherThanSquare()
    {
        byte[] rgba = Square();
        PictureOutline.Apply(rgba, Side, Side, 2f, OutlineSide.Outward);

        // Two out along the axis is inside the radius; two out along BOTH axes is 2.83 away and
        // is not. A transform that counted steps rather than distance would fill this in, and
        // the outline would have corners the art never has.
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 255), At(rgba, Block, Block - 2));
        Assert.Equal<(byte, byte, byte, byte)>((0, 0, 0, 0), At(rgba, Block - 2, Block - 2));
    }

    [Fact]
    public void WhatIsPartlyThereIsCompositedRatherThanOverwritten()
    {
        // A picture whose edge fades out - a texture with a soft alpha - beside the square.
        byte[] rgba = Square();
        int at = (((Side / 2) * Side) + Block - 1) * 4;
        rgba[at] = 100;
        rgba[at + 1] = 100;
        rgba[at + 2] = 100;
        rgba[at + 3] = 51;      // a fifth there

        PictureOutline.Apply(rgba, Side, Side, 2f, OutlineSide.Outward);

        // A fifth of the colour it had, over black, and solid: the outline shows through what
        // is missing instead of covering what is present.
        (byte r, byte g, byte b, byte a) = At(rgba, Block - 1, Side / 2);
        Assert.Equal(20, r);
        Assert.Equal(20, g);
        Assert.Equal(20, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void NoneAndNoWidthBothLeaveItAlone()
    {
        byte[] none = Square();
        PictureOutline.Apply(none, Side, Side, 2f, OutlineSide.None);
        Assert.Equal(Square(), none);

        byte[] nowidth = Square();
        PictureOutline.Apply(nowidth, Side, Side, 0f, OutlineSide.Outward);
        Assert.Equal(Square(), nowidth);
    }

    [Fact]
    public void AnEmptyPictureGrowsNothing()
    {
        // Nothing to draw round is not an error and must not paint the whole frame black,
        // which is what a distance transform with no seeds does if nobody checks.
        var empty = new byte[Side * Side * 4];
        PictureOutline.Apply(empty, Side, Side, 2f, OutlineSide.Outward);

        Assert.All(empty, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ABufferShorterThanItClaimsIsRefused()
    {
        // The size comes from a caller, and reading past the end of a picture on the draw
        // thread is the one mistake here that ends a session rather than looking wrong.
        var stub = new byte[16];
        PictureOutline.Apply(stub, Side, Side, 2f, OutlineSide.Outward);

        Assert.All(stub, b => Assert.Equal(0, b));
    }
}
