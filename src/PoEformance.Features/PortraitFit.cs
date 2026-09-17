namespace PoEformance.Features;

/// <summary>
/// Where the monster's picture sits beside the words, and how big it may be.
/// </summary>
/// <param name="Column">How much width the words keep, from the pane's left edge.</param>
/// <param name="Left">Where the picture starts, from the same edge.</param>
/// <param name="Side">How many pixels square the picture is shown at.</param>
/// <param name="Shown">Whether there is room for a picture at all.</param>
/// <remarks>
/// BESIDE THE WORDS AND NOT AGAINST THE RIGHT EDGE. The picture used to be flush right and capped
/// at half the pane, which put a band of nothing between the two: the words want about 210 pixels,
/// and a 1030-pixel pane gave the picture 515 of them hard right. That gap was the reason the
/// window had to be dragged so wide - it was paying for empty space twice over.
///
/// THE COLUMN IS MEASURED, NOT RESERVED. A fixed one is either too wide for a monster with short
/// names or too narrow for one with long ones, and nothing in this pane wraps - every section is a
/// bullet list that simply runs on - so what the words want is a question ImGui can answer exactly
/// once they have been drawn. The caller measures a frame behind; see MonsterBookWindow.Detail.
///
/// IT IS ARITHMETIC, SO IT LIVES WHERE A TEST CAN REACH IT. The last version of this sum lived in
/// the overlay, which is Windows-only and which the test project does not reference, and it
/// shipped a cap of zero that hid every monster's model with no message at all. See PictureLadder
/// for that story. Anything here that is a sum rather than a call into ImGui belongs on this side
/// of the line.
/// </remarks>
public readonly record struct PortraitFit(float Column, float Left, float Side, bool Shown)
{
    /// <summary>Below this the picture is not worth the width it costs.</summary>
    public const float LeastPortrait = 110f;

    /// <summary>What the words are assumed to want before they have been measured.</summary>
    /// <remarks>
    /// GENEROUS ON PURPOSE, because it is wrong in only one direction for only one frame: too
    /// wide costs a picture that is briefly smaller than it could be, too narrow draws the words
    /// across it.
    /// </remarks>
    public const float LeastColumn = 240f;

    /// <summary>
    /// And the widest, as a share of the pane.
    /// </summary>
    /// <remarks>
    /// A CEILING ON THE WORDS AND NOT ON THE PICTURE. Without it a single long modifier line would
    /// push the picture down to nothing, and how big a monster's portrait came out would depend on
    /// the wording of its mods. Past this the words run under the picture instead: ugly for one
    /// row, rather than wrong for the whole pane.
    /// </remarks>
    public const float MostColumn = 0.5f;

    /// <summary>The breathing room between the words and the picture.</summary>
    public const float Gutter = 24f;

    /// <summary>Nothing fits.</summary>
    public static PortraitFit None { get; } = new(0f, 0f, 0f, false);

    /// <summary>
    /// Works out the layout for one pane.
    /// </summary>
    /// <param name="pane">How wide the detail pane is.</param>
    /// <param name="words">How wide the words measured last frame, or zero before they have.</param>
    /// <param name="most">The biggest the picture may be drawn, from <c>PictureLadder</c>.</param>
    public static PortraitFit Of(float pane, float words, int most)
    {
        // A PANE THAT IS NOT A NUMBER IS NOT A NARROW PANE. ImGui's content region can report one
        // before a pane has been laid out, and every comparison below would then answer false -
        // which reads as "there is room" rather than "ask again next frame".
        if (!float.IsFinite(pane) || pane <= 0f || most <= 0)
        {
            return None;
        }

        // A FALLBACK, NOT A FLOOR. Once the words have been measured their own width IS the
        // answer, and forcing a minimum on it just puts the gap back in a smaller size: these
        // sections often want only about 150. LeastColumn is for the one frame before anything
        // has been measured, where a guess that is too wide costs nothing and one that is too
        // narrow draws the words over the picture.
        float measured = float.IsFinite(words) && words > 0f ? words : LeastColumn;
        float column = MathF.Min(measured, MathF.Max(pane * MostColumn, LeastColumn));
        float side = MathF.Min(most, pane - column - Gutter);

        return side < LeastPortrait ? None : new PortraitFit(column, column + Gutter, side, true);
    }
}
