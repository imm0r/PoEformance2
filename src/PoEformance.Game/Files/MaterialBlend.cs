namespace PoEformance.Game.Files;

/// <summary>How a material's pixels are put over what is already drawn.</summary>
public enum MaterialBlend : byte
{
    /// <summary>Covers what is behind it - every material the game does not say otherwise about.</summary>
    Opaque,

    /// <summary>Mixed with what is behind it by the texture's own alpha.</summary>
    Alpha,

    /// <summary>Added to what is behind it - glow, fire, lightning.</summary>
    Additive,
}

/// <summary>
/// What a material's blend mode, as the game spells it, is taken to mean.
/// </summary>
/// <remarks>
/// WHERE THE SPELLING COMES FROM IS ESTABLISHED; WHAT IT SAYS IS NOT. zao's <c>libpoe/poe/format/mat.cpp</c>
/// reads <c>overriden_blend_mode</c> - a string - off a material's <c>defaultgraph</c> and off any
/// <c>.fxgraph</c>, and nothing in any reference lists its values. So the reading here is a RULE ON
/// WORDS, chosen in so many words by the person this tool is for and labelled as a guess wherever it
/// shows: a value naming "add" is additive, one naming alpha, blend or transparency is mixed, anything
/// else is opaque. The raw value is always kept beside it, so the first real effect says whether the
/// rule holds - and one that does not is a line in this method rather than a new idea somewhere else.
/// </remarks>
public static class MaterialBlends
{
    /// <summary>The blend a mode's name is taken to mean. Empty or unknown is opaque.</summary>
    public static MaterialBlend Of(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return MaterialBlend.Opaque;
        }

        // ADDITIVE IS ASKED FIRST, because the game could well spell it "AdditiveBlend" - and that
        // carries "blend" too, which the mixed rule would otherwise take.
        if (mode.Contains("add", StringComparison.OrdinalIgnoreCase))
        {
            return MaterialBlend.Additive;
        }

        return mode.Contains("alpha", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("blend", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("transp", StringComparison.OrdinalIgnoreCase)
            ? MaterialBlend.Alpha
            : MaterialBlend.Opaque;
    }
}
