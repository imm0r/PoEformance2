using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What every window of the tool looks like: one palette, applied to ImGui's whole set.
/// </summary>
/// <remarks>
/// WRITTEN OUT RATHER THAN LEFT AT THE DEFAULT, because ImGui's own dark theme is a theme for
/// a debug window on a desktop, and this is not one. Two of its decisions are actively wrong
/// over a game:
///
/// - A WINDOW BACKGROUND THAT IS NOT QUITE SOLID. The default is 94% of near-black, and the
///   remaining 6% is a game rendering foliage, firelight and monsters at the exact scale of
///   the letters in front of it. Over a desktop that is a tasteful hint of what is behind;
///   over Path of Exile it is noise inside the glyphs. Every panel here is solid unless
///   somebody asks for otherwise, and asking is <see cref="InterfaceStyle.PanelOpacity"/>.
/// - TEXTDISABLED AT HALF GREY. In ImGui's own windows that colour carries the odd hint. In
///   this tool it carries most of the PROSE - every explanation of what a switch does, every
///   "the reader drops them", the whole of the window list's instructions - because that is
///   how the drawing code distinguishes explanation from data. Left at 0.5 grey, the tool's
///   own explanations are the least readable thing in it, which is precisely backwards.
///
/// The colours themselves are a warm near-black with a gilt accent, chosen to sit beside what
/// the game already draws rather than to fight it - the same reasoning that put the interface
/// in a serif face (see <c>EntityOverlay.WearASerif</c>). That part is taste. The contrast
/// ratios are not: text against panel, and every state of a control against its neighbours,
/// are what makes the difference between a window that is glanced at and one that is squinted
/// at.
///
/// APPLIED ON THE RENDER THREAD, always. ImGui's style is global mutable state owned by the
/// context, and the context belongs to the thread that draws - see the note on
/// <see cref="EntityOverlay.Interface"/> about why nothing sets this from the wiring.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class OverlayTheme
{
    // The palette, named by what each colour IS rather than by where it is used, so that a
    // colour used in two places cannot drift into being two colours.

    /// <summary>What the tool writes in: a warm off-white, not the pure one.</summary>
    /// <remarks>
    /// PUBLIC so that the colours derived from it are derived rather than transcribed - see
    /// <see cref="GiltInk"/>. A copy of these three numbers somewhere else is a copy that stays
    /// where it is when this one is adjusted.
    /// </remarks>
    public static readonly Vector4 Ink = new(0.94f, 0.93f, 0.89f, 1f);

    /// <summary>
    /// What the tool says in a quieter voice: explanations, units, "not in an area".
    /// </summary>
    /// <remarks>
    /// PUBLIC, and the one place it is decided. It began as a private copy in each of sixteen
    /// windows, all holding the same grey, which is sixteen places to miss when the answer to
    /// "this is hard to read" is that the quiet text is too quiet. It is also ImGui's own
    /// <c>TextDisabled</c>, so the two ways a window has of saying something quietly say it in
    /// the same colour.
    /// </remarks>
    public static readonly Vector4 Quiet = new(0.72f, 0.70f, 0.65f, 1f);
    private static readonly Vector4 Panel = new(0.07f, 0.07f, 0.08f, 1f);
    private static readonly Vector4 Sunken = new(0.05f, 0.05f, 0.06f, 1f);
    private static readonly Vector4 Raised = new(0.13f, 0.13f, 0.15f, 1f);
    private static readonly Vector4 Edge = new(0.42f, 0.39f, 0.33f, 0.9f);
    private static readonly Vector4 Gilt = new(0.85f, 0.68f, 0.34f, 1f);

    /// <summary>
    /// The ink with a cast of the gilt in it. What the tab bar's labels are set in.
    /// </summary>
    /// <remarks>
    /// A TINT, NOT A SECOND COLOUR. The tab bar is the one strip of the tools window that is
    /// not part of any page - it is the thing you leave the page to use - and until now it said
    /// so with nothing at all: its labels were the same off-white as the sentence underneath
    /// them, so the eye had to find the boundary from the tab shapes alone. Set apart, the bar
    /// reads as the frame rather than as the first line of the content.
    ///
    /// A QUARTER OF THE WAY TO THE ACCENT and no further, which is the whole of the tuning. Far
    /// enough that the two are different colours side by side; near enough that a tab label is
    /// still the tool's own ink rather than a gold heading, which over a game that already
    /// paints in gold would read as a second accent competing with the real one.
    ///
    /// DERIVED rather than written out, so it cannot drift: adjust <see cref="Ink"/> for
    /// legibility and this follows it instead of staying behind as a colour nobody remembers
    /// choosing. It is DARKENED BY THE MIX, not brightened - the accent is darker than the ink
    /// in every channel - so it stays under the ink's contrast rather than shouting over it.
    ///
    /// How FAR it leans is <see cref="InterfaceStyle.AccentTint"/>, which is where the rule
    /// about it being a tint rather than a second colour is written down and checked.
    /// </remarks>
    public static readonly Vector4 GiltInk = InterfaceStyle.Tinted(Ink, Gilt);

    private static readonly Vector4 Warm = new(0.30f, 0.24f, 0.14f, 1f);
    private static readonly Vector4 WarmLit = new(0.44f, 0.35f, 0.18f, 1f);
    private static readonly Vector4 WarmHeld = new(0.54f, 0.43f, 0.22f, 1f);
    private static readonly Vector4 Nothing = new(0f, 0f, 0f, 0f);

    /// <summary>Applies the whole palette and the spacing that goes with it.</summary>
    /// <param name="style">
    /// What the user chose. Only the opacity reaches the colours here; the text size is the
    /// font, which is a different mechanism entirely - see <c>EntityOverlay.WearASerif</c>.
    /// </param>
    public static void Apply(InterfaceStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        ImGuiStylePtr theme = ImGui.GetStyle();
        float solid = style.PanelOpacityOr;

        // SHAPE FIRST. Rounded corners and a visible edge are not decoration here: they are
        // what tells the eye where a panel stops, on a background that has no reason to be
        // any particular colour where the window happens to end.
        theme.WindowRounding = 4f;
        theme.ChildRounding = 4f;
        theme.FrameRounding = 3f;
        theme.PopupRounding = 4f;
        theme.ScrollbarRounding = 4f;
        theme.GrabRounding = 3f;
        theme.TabRounding = 4f;

        theme.WindowBorderSize = 1f;
        theme.ChildBorderSize = 1f;
        theme.PopupBorderSize = 1f;

        // A border on every control as well, which ImGui leaves off. A text box and the panel
        // behind it are two dark greys, and over a moving picture the eye stops resolving
        // which is which; one lit pixel around the edge settles it without adding a colour.
        theme.FrameBorderSize = 1f;

        // Roomier than the default by a couple of pixels in each direction. Text needs air
        // around it to be read at a glance rather than deciphered, and these windows are read
        // at a glance during a fight. Kept modest: every pixel here is a pixel of the game
        // covered, and the dissector's table has hundreds of rows to pay it on.
        theme.WindowPadding = new Vector2(10f, 8f);
        theme.FramePadding = new Vector2(7f, 4f);
        theme.ItemSpacing = new Vector2(8f, 5f);
        theme.ItemInnerSpacing = new Vector2(6f, 4f);
        theme.CellPadding = new Vector2(6f, 3f);
        theme.ScrollbarSize = 14f;

        // Never scaled globally. ImGui multiplies EVERYTHING by this, borders and text alike,
        // and a window faded that way loses its text and its background together - which is
        // the one arrangement in which a see-through panel is unreadable at any setting. What
        // the user asked for is applied to the BACKGROUNDS only, below.
        theme.Alpha = 1f;

        Set(theme, ImGuiCol.Text, Ink);
        Set(theme, ImGuiCol.TextDisabled, Quiet);
        Set(theme, ImGuiCol.TextSelectedBg, WarmLit with { W = 0.6f });
        Set(theme, ImGuiCol.TextLink, Gilt);

        Set(theme, ImGuiCol.WindowBg, Panel with { W = solid });
        Set(theme, ImGuiCol.ChildBg, Nothing);

        // POPUPS ARE ALWAYS SOLID, whatever the panels are set to, and that is the one place
        // the setting is overruled. A popup is small, it is on screen for a moment, and it is
        // opened to be read - a combo list or a tooltip showing the hideout through it is the
        // worst case of the whole problem, and it is also the case where nothing is gained by
        // seeing what is behind, since it is about to close.
        Set(theme, ImGuiCol.PopupBg, Raised);

        Set(theme, ImGuiCol.Border, Edge);
        Set(theme, ImGuiCol.BorderShadow, Nothing);

        Set(theme, ImGuiCol.FrameBg, Raised);
        Set(theme, ImGuiCol.FrameBgHovered, new Vector4(0.22f, 0.21f, 0.22f, 1f));
        Set(theme, ImGuiCol.FrameBgActive, new Vector4(0.29f, 0.27f, 0.26f, 1f));

        // The title bar keeps its own solidity rather than the panel's: it is the strip the
        // window is dragged by and the one that carries the lock and click-through icons, so
        // it has to be findable even on a panel somebody has made faint.
        Set(theme, ImGuiCol.TitleBg, new Vector4(0.11f, 0.10f, 0.09f, 1f));
        Set(theme, ImGuiCol.TitleBgActive, Warm);
        Set(theme, ImGuiCol.TitleBgCollapsed, new Vector4(0.11f, 0.10f, 0.09f, 0.9f));
        Set(theme, ImGuiCol.MenuBarBg, new Vector4(0.10f, 0.10f, 0.11f, 1f));

        Set(theme, ImGuiCol.ScrollbarBg, Sunken with { W = 0.6f });
        Set(theme, ImGuiCol.ScrollbarGrab, new Vector4(0.30f, 0.28f, 0.24f, 1f));
        Set(theme, ImGuiCol.ScrollbarGrabHovered, new Vector4(0.42f, 0.38f, 0.30f, 1f));
        Set(theme, ImGuiCol.ScrollbarGrabActive, WarmHeld);

        Set(theme, ImGuiCol.CheckMark, Gilt);
        Set(theme, ImGuiCol.SliderGrab, new Vector4(0.62f, 0.51f, 0.28f, 1f));
        Set(theme, ImGuiCol.SliderGrabActive, Gilt);

        Set(theme, ImGuiCol.Button, new Vector4(0.20f, 0.19f, 0.18f, 1f));
        Set(theme, ImGuiCol.ButtonHovered, WarmLit);
        Set(theme, ImGuiCol.ButtonActive, WarmHeld);

        // Header is the collapsing headers, the selected rows and every Selectable in the
        // tool - the widest blocks of colour in the interface. Warm and dark, so that white
        // text keeps its contrast on top of them.
        Set(theme, ImGuiCol.Header, Warm);
        Set(theme, ImGuiCol.HeaderHovered, WarmLit);
        Set(theme, ImGuiCol.HeaderActive, WarmHeld);

        Set(theme, ImGuiCol.Separator, new Vector4(0.35f, 0.32f, 0.27f, 0.7f));
        Set(theme, ImGuiCol.SeparatorHovered, WarmLit);
        Set(theme, ImGuiCol.SeparatorActive, Gilt);

        Set(theme, ImGuiCol.ResizeGrip, new Vector4(0.35f, 0.32f, 0.26f, 0.5f));
        Set(theme, ImGuiCol.ResizeGripHovered, WarmLit);
        Set(theme, ImGuiCol.ResizeGripActive, Gilt);

        // The tab bar is how the one window offers its seven pages, so which tab is in front
        // has to be readable at a glance: a lit strip above the selected one, and a clear step
        // in brightness between selected, hovered and the rest.
        Set(theme, ImGuiCol.Tab, new Vector4(0.13f, 0.12f, 0.11f, 1f));
        Set(theme, ImGuiCol.TabHovered, WarmLit);
        Set(theme, ImGuiCol.TabSelected, Warm);
        Set(theme, ImGuiCol.TabSelectedOverline, Gilt);
        Set(theme, ImGuiCol.TabDimmed, new Vector4(0.10f, 0.10f, 0.09f, 1f));
        Set(theme, ImGuiCol.TabDimmedSelected, new Vector4(0.22f, 0.19f, 0.13f, 1f));
        Set(theme, ImGuiCol.TabDimmedSelectedOverline, new Vector4(0.50f, 0.42f, 0.24f, 1f));

        // The tables are the dissector, the entity browser and the preload list - the densest
        // things the tool draws. Banding, because a row of hex that the eye loses its place in
        // is a row that has to be counted to; faint, because at the default 6% white it reads
        // as stripes in its own right once there is anything behind the window.
        Set(theme, ImGuiCol.TableHeaderBg, new Vector4(0.17f, 0.15f, 0.12f, 1f));
        Set(theme, ImGuiCol.TableBorderStrong, new Vector4(0.38f, 0.34f, 0.28f, 1f));
        Set(theme, ImGuiCol.TableBorderLight, new Vector4(0.24f, 0.22f, 0.19f, 1f));
        Set(theme, ImGuiCol.TableRowBg, Nothing);
        Set(theme, ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.035f));

        Set(theme, ImGuiCol.PlotLines, new Vector4(0.78f, 0.72f, 0.55f, 1f));
        Set(theme, ImGuiCol.PlotLinesHovered, Gilt);
        Set(theme, ImGuiCol.PlotHistogram, new Vector4(0.68f, 0.55f, 0.28f, 1f));
        Set(theme, ImGuiCol.PlotHistogramHovered, Gilt);

        Set(theme, ImGuiCol.DragDropTarget, Gilt);
        Set(theme, ImGuiCol.NavCursor, Gilt);
        Set(theme, ImGuiCol.NavWindowingHighlight, Ink with { W = 0.7f });
        Set(theme, ImGuiCol.NavWindowingDimBg, new Vector4(0.05f, 0.05f, 0.05f, 0.5f));
        Set(theme, ImGuiCol.ModalWindowDimBg, new Vector4(0.05f, 0.05f, 0.05f, 0.6f));
    }

    /// <summary>
    /// One colour in ImGui's table.
    /// </summary>
    /// <remarks>
    /// Named rather than written out at every line, because <c>Colors</c> is indexed by an int
    /// and the cast is the sort of noise that hides a wrong index among two hundred right ones.
    /// </remarks>
    private static void Set(ImGuiStylePtr theme, ImGuiCol which, Vector4 colour)
        => theme.Colors[(int)which] = colour;
}
