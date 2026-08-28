using System.Numerics;
using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>
/// Every colour the tool writes MEANING in, decided once.
/// </summary>
/// <remarks>
/// THE CHROME WAS SHARED AND THE MEANING WAS NOT. <c>OverlayTheme</c> has always been one
/// palette applied to ImGui's whole set - one panel colour, one border, one accent. What it
/// never covered was the colour a window uses to say something: "this worked", "be careful",
/// "this read failed", "this is money". Every window invented those for itself, and by the time
/// this was written there were twenty-two private copies of them across the overlay:
///
/// - SIX oranges for a warning - (1, .60, .35), (1, .70, .35), (1, .62, .35), (1, .72, .42),
///   (1, .72, .30) and (1, .60, .20). Nobody chose six; each was chosen once, beside the last
///   one nobody could see.
/// - THREE greens for "good", four blues for "this is a pointer", two golds for money and two
///   more hand-copied out of the accent.
/// - TWO rarity ladders that disagree with each other, in the two windows that print item
///   names side by side.
/// - AND A QUIET THAT WAS COLD. The live readout's own dim text was (.70, .75, .80) - a BLUE
///   grey - where every other window's is the warm (.72, .70, .65). The most-looked-at surface
///   in the tool was the one writing in a different ink from the rest of it.
///
/// That is the same failure the <see cref="Quiet"/> note in <c>OverlayTheme</c> already
/// describes and cured for exactly one colour. This file is that cure applied to the rest.
///
/// IN THIS LAYER, not beside ImGui, for the reason <see cref="InterfaceStyle.Tinted"/> is here:
/// a palette is arithmetic about the interface's own appearance, nothing about it needs a
/// render thread or a context to be reasoned about, and a rule that can be argued with in a
/// test is worth more than one that can only be argued with in a screenshot. See
/// <c>OverlayInkTests</c>, which is where the two rules below are actually enforced.
///
/// THE TWO RULES, and both are checked rather than asserted in prose:
///
/// 1. EVERY INK IS READABLE ON WHAT IT IS DRAWN ON. Not on some notional dark grey - on this
///    tool's own <see cref="Panel"/>, and on <see cref="Selected"/>, the band under a picked
///    row. The second one is where this started: at the old selection colour the game's unique
///    orange sat at 2.2:1, which is not a dim colour but an unreadable one. The band was pulled
///    down the warm ray until the game's own darkest ink cleared 3:1 on it, and that is the
///    whole reason <see cref="Selected"/> is <c>Warm(0.20)</c> rather than the <c>Warm(0.30)</c>
///    it used to be.
/// 2. THE INKS THAT SAY A STATUS ARE FURTHER APART THAN THE GAME'S OWN CLOSEST PAIR. Good, warn
///    and bad appear in one column and are read at a glance, so "how far apart is far enough"
///    has to be a number. The number is not ours: it is the distance between the game's unique
///    and currency colours - the tightest pair in a ladder players have been reading since the
///    first magic item. Anything the game itself ships as two colours is two colours.
///
/// WHAT IS DELIBERATELY NOT SEPARATED. <see cref="Name"/> and <see cref="Money"/> are both in
/// the gold family and are closer than that floor. They never appear in the same window - one
/// is what the dissector and the two browsers call a thing, the other is what the stash, the
/// rates and the wealth pages count in - and moving one of them out of the family to satisfy a
/// rule that no reader can ever be caught by would be churn bought with a worse-looking tool.
/// <see cref="Accent"/> is exempt for a harder reason: it is never a word. It is a checkmark, a
/// tab's overline, a slider's grab - chrome, which is never standing in a column of coloured
/// text waiting to be told apart from the row above it.
/// </remarks>
public static class OverlayInk
{
    // ---- the grounds -------------------------------------------------------------------
    //
    // Near-neutral with a hint of cool in the blue channel, which is what keeps the warm ray
    // below reading as warm. A truly neutral ground would make the gilt look merely dirty.

    /// <summary>What a window is. Warm near-black, a shade off neutral.</summary>
    public static readonly Vector4 Panel = new(0.07f, 0.07f, 0.08f, 1f);

    /// <summary>Below the panel: a scrollbar's channel, a plot's plate.</summary>
    public static readonly Vector4 Sunken = new(0.05f, 0.05f, 0.06f, 1f);

    /// <summary>Above the panel: a popup, an input box, a menu.</summary>
    public static readonly Vector4 Raised = new(0.13f, 0.13f, 0.15f, 1f);

    /// <summary>The one lit pixel around a panel and around every control in it.</summary>
    public static readonly Vector4 Edge = new(0.42f, 0.39f, 0.33f, 0.9f);

    // ---- the warm ray ------------------------------------------------------------------

    /// <summary>
    /// One warm material at a given brightness - the ramp every pressable thing lives on.
    /// </summary>
    /// <remarks>
    /// A RAY THROUGH COLOUR SPACE RATHER THAN A LIST OF COLOURS, and this is a discovery
    /// written down rather than a new idea. The three warm colours the theme already had -
    /// (.30, .24, .14), (.44, .35, .18) and (.54, .43, .22) - are, to within a rounding error,
    /// the same chromaticity at three brightnesses. Nobody wrote that down, so the ray was
    /// only ever a coincidence that happened to hold; the fourth stop somebody added would
    /// have landed beside it rather than on it.
    ///
    /// WHY IT MATTERS BEYOND TIDINESS: a control that is pointed at should get LIGHTER, not
    /// become a different colour. The buttons broke that rule outright - they sat at a neutral
    /// (.20, .19, .18) and jumped to a warm (.44, .35, .18) on hover, so the hover read as the
    /// button being swapped for a different button rather than as the same one lit. On the ray
    /// there is only one thing that can change, which is how hard it is lit.
    ///
    /// The two ratios are the ray's direction and the only numbers in it. They are the average
    /// of what the three original colours already held, so every stop the theme used before is
    /// reproduced by this to within a pixel value.
    /// </remarks>
    /// <param name="lit">How bright, as the red channel. Every other channel follows.</param>
    public static Vector4 Warm(float lit) => new(lit, lit * 0.80f, lit * 0.44f, 1f);

    /// <summary>
    /// The same for the near-neutral things: an input box and its two lit states.
    /// </summary>
    /// <remarks>
    /// A SECOND RAY, because an input box is a HOLE and a button is a PLATE. A text field that
    /// warms towards gold when it is pointed at looks like it is about to catch fire; what it
    /// should do is get slightly lighter, in the same not-quite-neutral the panel is. The cool
    /// cast in the blue channel is the panel's own, so a frame and the window behind it are the
    /// same material at two depths rather than two greys that nearly match.
    /// </remarks>
    public static Vector4 Sunk(float lit) => new(lit, lit, lit * 1.12f, 1f);

    /// <summary>
    /// The band under a picked row, a collapsing header, a selectable.
    /// </summary>
    /// <remarks>
    /// LOWER ON THE RAY THAN THE TAB BAR'S, and that split is the point rather than a detail.
    /// Both used to be <c>Warm(0.30)</c>, so a selected row and the tab in front carried the
    /// same weight - but they hold different things. A tab holds the tool's own ink and nothing
    /// else; a selected ROW holds whatever that table prints, and this tool's tables print the
    /// game's rarity colours. At <c>Warm(0.30)</c> a selected unique item sat at 2.2:1 against
    /// the band under it, which is a name you cannot read on the row you just clicked.
    ///
    /// <c>Warm(0.20)</c> is where the game's darkest ink clears 3:1 and its magic blue clears
    /// 4.5:1, and it is still 1.31:1 against the panel - faint, but a band. Going one stop
    /// further down buys the rarity colours very little and starts costing the selection its
    /// visibility, which is the other way to make a table unreadable.
    /// </remarks>
    public static readonly Vector4 Selected = Warm(0.20f);

    /// <summary>The tab in front, and the title bar of the window being used.</summary>
    public static readonly Vector4 Chrome = Warm(0.30f);

    /// <summary>Anything on the ray while the pointer is on it.</summary>
    public static readonly Vector4 Lit = Warm(0.44f);

    /// <summary>Anything on the ray while the button is held down on it.</summary>
    public static readonly Vector4 Held = Warm(0.54f);

    // ---- what the tool writes in --------------------------------------------------------

    /// <summary>What the tool writes in: a warm off-white, not the pure one.</summary>
    /// <remarks>
    /// The colour every other one here is judged against, and the one several are derived from
    /// - see <see cref="Measured"/> and <see cref="AccentInk"/>. A copy of these three numbers
    /// somewhere else is a copy that stays where it is when this one is adjusted.
    /// </remarks>
    public static readonly Vector4 Ink = new(0.94f, 0.93f, 0.89f, 1f);

    /// <summary>
    /// What the tool says in a quieter voice: explanations, units, labels, "not in an area".
    /// </summary>
    /// <remarks>
    /// THIS CARRIES MOST OF THE PROSE. Every explanation of what a switch does, every "the
    /// reader drops them", the whole of the window list's instructions - because that is how the
    /// drawing code distinguishes explanation from data. At ImGui's own half grey the tool's own
    /// explanations are the least readable thing in it, which is precisely backwards, so this
    /// sits well above it.
    /// </remarks>
    public static readonly Vector4 Quiet = new(0.72f, 0.70f, 0.65f, 1f);

    /// <summary>The tool's accent: gilt, to sit beside what the game already paints in.</summary>
    /// <remarks>
    /// CHROME, NOT TEXT. A checkmark, the strip over the tab in front, a slider's grab, a
    /// link. It is deliberately near <see cref="Warn"/> in colour and that costs nothing,
    /// because the two are never both words in the same column - see the note at the top about
    /// which inks the separation rule applies to.
    /// </remarks>
    public static readonly Vector4 Accent = new(0.85f, 0.68f, 0.34f, 1f);

    /// <summary>The ink with a cast of the accent in it. What the tab bar's labels are set in.</summary>
    /// <remarks>
    /// A TINT, NOT A SECOND COLOUR. The tab bar is the one strip of the tools window that is not
    /// part of any page - it is the thing you leave the page to use - and until it was tinted it
    /// said so with nothing at all: its labels were the same off-white as the sentence
    /// underneath them, so the eye had to find the boundary from the tab shapes alone.
    ///
    /// How FAR it leans is <see cref="InterfaceStyle.AccentTint"/>, which is where the rule
    /// about it being a tint rather than a second accent is written down and checked.
    /// </remarks>
    public static readonly Vector4 AccentInk = InterfaceStyle.Tinted(Ink, Accent);

    /// <summary>An address, a pointer, a handle - something that resolved to somewhere.</summary>
    /// <remarks>
    /// Blue because it is the one hue nothing else in this tool means anything by, and because
    /// it is as far from the game's rarity ladder as the ladder leaves room for.
    /// </remarks>
    public static readonly Vector4 Reference = new(0.55f, 0.80f, 1.00f, 1f);

    /// <summary>
    /// A figure this tool WORKED OUT, as against one it read: a percentage walked, a count.
    /// </summary>
    /// <remarks>
    /// THE INK, COOLED, and derived rather than picked for the same reason <see
    /// cref="AccentInk"/> is. It began life as a green-grey, which was the whole problem: green
    /// means "this is good" everywhere else in the tool, so a row reading "walked 43%" in a
    /// green-grey was making a judgement it had no business making. Leaning the ordinary ink a
    /// quarter of the way towards the pointer blue says "derived" without saying anything about
    /// whether 43% is good news.
    /// </remarks>
    public static readonly Vector4 Measured = InterfaceStyle.Tinted(Ink, Reference);

    /// <summary>The game's own name for a thing: a metadata path, a field, an element id.</summary>
    public static readonly Vector4 Name = new(0.85f, 0.78f, 0.45f, 1f);

    /// <summary>Currency, and anything priced in it.</summary>
    /// <remarks>
    /// Gold, and the loudest member of the gold family on purpose - a total is the figure the
    /// page exists to show. Nobody has to learn this one either.
    /// </remarks>
    public static readonly Vector4 Money = new(1.00f, 0.83f, 0.42f, 1f);

    /// <summary>It worked, it is up, it is done.</summary>
    public static readonly Vector4 Good = new(0.55f, 0.90f, 0.62f, 1f);

    /// <summary>Look at this: unknown, worst, nearly out, not what was expected.</summary>
    /// <remarks>
    /// Pulled off the gold it had drifted onto. Two of the six oranges this replaces - (1, .60,
    /// .20) worst among them - were near enough to <see cref="Accent"/> that a warning row read
    /// as decoration rather than as a warning.
    /// </remarks>
    public static readonly Vector4 Warn = new(1.00f, 0.64f, 0.30f, 1f);

    /// <summary>It failed, it is down, the read gave up.</summary>
    /// <remarks>
    /// Brighter than the reds it replaces, and that is the one place the consolidation moved a
    /// colour rather than averaging it. At (1, .35, .38) a failure was the QUIETEST thing in a
    /// status column - dimmer than the green saying everything was fine - which is a hierarchy
    /// exactly upside down. This clears <see cref="Warn"/> by more than the game's own closest
    /// pair while staying unmistakably red.
    /// </remarks>
    public static readonly Vector4 Bad = new(1.00f, 0.46f, 0.42f, 1f);

    /// <summary>Yours: your projectile, your effect, the thing you put there.</summary>
    /// <remarks>
    /// THE SAME COLOUR AS <see cref="Reference"/> RATHER THAN ANOTHER BLUE, and named separately
    /// so the call site says which of the two it means. The effects page and the projectiles page
    /// each had a pair for this - one of them wrote hostile as (1, .55, .40) and so did the other,
    /// exactly, while their two friendlies differed by a hundredth in one channel. Two windows
    /// having independently arrived at almost the same pair is the clearest possible sign that it
    /// is one idea, so it is written down once.
    /// </remarks>
    public static readonly Vector4 Friendly = Reference;

    /// <summary>Theirs, and pointed at you.</summary>
    /// <remarks>
    /// <see cref="Bad"/>, because that is what a hostile projectile is. The pair it replaces put
    /// hostile in the same orange as five different warnings elsewhere in the tool, so a screen
    /// full of incoming projectiles read as a screen full of cautions.
    /// </remarks>
    public static readonly Vector4 Hostile = Bad;

    // ---- the game's ladder ---------------------------------------------------------------

    /// <summary>
    /// What each rarity looks like, in the game's own colours.
    /// </summary>
    /// <remarks>
    /// NOT OURS TO TUNE, which is what makes it the one part of this file with no argument in
    /// it. A player has been reading white, blue, yellow and orange since the first magic item
    /// dropped, and a tool that prints item names in its own idea of those colours is a tool
    /// that has to be learned. So these are the game's hex values rather than anything chosen
    /// here, and where the two copies this replaces disagreed, the one matching the game won -
    /// the unique orange is #AF6025 exactly, which is the value the damage page had and the
    /// stash page had drifted a shade off.
    ///
    /// INDEXED BY THE GAME'S OWN NUMBER, including 4, which <see cref="ItemRarity"/> does not
    /// name. The stash reads a raw rarity off an item and has always indexed a six-entry table
    /// with it; leaving the gap out here would silently shift currency onto quest's colour.
    /// </remarks>
    private static readonly Vector4[] ByRarity =
    [
        new(0.86f, 0.86f, 0.86f, 1f),    // normal
        new(0.533f, 0.533f, 1f, 1f),     // magic    - #8888FF
        new(1f, 1f, 0.467f, 1f),         // rare     - #FFFF77
        new(0.686f, 0.376f, 0.145f, 1f), // unique   - #AF6025
        new(0.29f, 0.78f, 0.29f, 1f),    // quest
        new(0.67f, 0.55f, 0.40f, 1f),    // currency
    ];

    /// <summary>How many rarities the ladder has a colour for.</summary>
    /// <remarks>
    /// Not called <c>Rarities</c>, which is already a class in <c>PoEformance.Game.Components</c>
    /// - and every window that prints an item name has it in scope.
    /// </remarks>
    public static int RarityCount => ByRarity.Length;

    /// <summary>The colour for a rarity the game numbered. Anything unknown reads as normal.</summary>
    /// <remarks>
    /// Falls back rather than throws, because the number comes from memory: a wrong offset or a
    /// rarity added in a patch would otherwise take the whole stash grid down over a colour.
    /// </remarks>
    public static Vector4 Rarity(int rarity)
        => rarity >= 0 && rarity < ByRarity.Length ? ByRarity[rarity] : ByRarity[0];

    /// <summary>The same, for the places that already have the rarity as a name.</summary>
    /// <remarks>
    /// <see cref="ItemRarity.Unknown"/> is -1 and lands on normal through the guard above,
    /// which is the same answer the windows reached for by hand before this existed.
    /// </remarks>
    public static Vector4 Rarity(ItemRarity rarity) => Rarity((int)rarity);

    // ---- the arithmetic the two rules are stated in ---------------------------------------

    /// <summary>How much light a colour puts out, by the sRGB definition.</summary>
    /// <remarks>
    /// The WCAG relative luminance, which is standard published arithmetic rather than anything
    /// invented here - and pinned against known values in the tests, so it cannot rot into
    /// something that quietly passes everything.
    ///
    /// Alpha is ignored on purpose: what a half-transparent ink actually looks like depends on
    /// what is behind it, and the inks above are all solid. A rule stated on a number that
    /// depends on the game's current frame would not be a rule.
    /// </remarks>
    public static float Luminance(Vector4 colour)
        => (0.2126f * Straight(colour.X))
            + (0.7152f * Straight(colour.Y))
            + (0.0722f * Straight(colour.Z));

    private static float Straight(float channel)
        => channel <= 0.04045f ? channel / 12.92f : MathF.Pow((channel + 0.055f) / 1.055f, 2.4f);

    /// <summary>How far apart two colours are in brightness, as a contrast ratio.</summary>
    /// <remarks>
    /// 1 for two colours that put out the same light, 21 for black against white. This is what
    /// "readable on the panel" is measured with, and the floors the palette holds itself to are
    /// in <c>OverlayInkTests</c>.
    /// </remarks>
    public static float Contrast(Vector4 one, Vector4 other)
    {
        float first = Luminance(one);
        float second = Luminance(other);
        return (MathF.Max(first, second) + 0.05f) / (MathF.Min(first, second) + 0.05f);
    }

    /// <summary>
    /// How different two colours LOOK, rather than how differently they are written.
    /// </summary>
    /// <remarks>
    /// OKLab, and a perceptual space rather than plain RGB distance because that is the whole
    /// question being asked. Two greens a tenth apart in the green channel are the same colour
    /// to a reader; a green and a blue the same distance apart are not, and RGB cannot tell
    /// those two cases from each other. A rule about whether a person can distinguish two rows
    /// has to be stated in a space built for that.
    ///
    /// The transform is Björn Ottosson's published one, unaltered. What the numbers coming out
    /// of it MEAN for this tool is not a constant borrowed from anywhere: it is the distance
    /// between the game's own unique and currency colours, which is the closest pair a player
    /// is already expected to tell apart. See <c>OverlayInkTests</c>.
    /// </remarks>
    public static float Distance(Vector4 one, Vector4 other)
    {
        (float firstL, float firstA, float firstB) = Oklab(one);
        (float secondL, float secondA, float secondB) = Oklab(other);

        float dl = firstL - secondL;
        float da = firstA - secondA;
        float db = firstB - secondB;
        return MathF.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    private static (float L, float A, float B) Oklab(Vector4 colour)
    {
        float r = Straight(colour.X);
        float g = Straight(colour.Y);
        float b = Straight(colour.Z);

        float longWave = MathF.Cbrt((0.4122214708f * r) + (0.5363325363f * g) + (0.0514459929f * b));
        float midWave = MathF.Cbrt((0.2119034982f * r) + (0.6806995451f * g) + (0.1073969566f * b));
        float shortWave = MathF.Cbrt((0.0883024619f * r) + (0.2817188376f * g) + (0.6299787005f * b));

        return (
            (0.2104542553f * longWave) + (0.7936177850f * midWave) - (0.0040720468f * shortWave),
            (1.9779984951f * longWave) - (2.4285922050f * midWave) + (0.4505937099f * shortWave),
            (0.0259040371f * longWave) + (0.7827717662f * midWave) - (0.8086757660f * shortWave));
    }
}
