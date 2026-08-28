using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The palette and the spacing, handed to ImGui.
/// </summary>
/// <remarks>
/// WHAT THE COLOURS ARE is not decided here any more - it is <see cref="OverlayInk"/>, one layer
/// down, where it can be argued with in a test. What is decided HERE is which of ImGui's hundred
/// or so slots each ink goes into, and that is a different question with different mistakes
/// available in it. Splitting the two is what stopped the theme being a place where a colour
/// could be invented: a slot below either names an ink or names a stop on the ink's own ramp,
/// and there is no third option.
///
/// WRITTEN OUT RATHER THAN LEFT AT THE DEFAULT, because ImGui's own dark theme is a theme for a
/// debug window on a desktop, and this is not one. Two of its decisions are actively wrong over
/// a game:
///
/// - A WINDOW BACKGROUND THAT IS NOT QUITE SOLID. The default is 94% of near-black, and the
///   remaining 6% is a game rendering foliage, firelight and monsters at the exact scale of the
///   letters in front of it. Over a desktop that is a tasteful hint of what is behind; over Path
///   of Exile it is noise inside the glyphs. Every panel here is solid unless somebody asks for
///   otherwise, and asking is <see cref="InterfaceStyle.PanelOpacity"/>.
/// - TEXTDISABLED AT HALF GREY. In ImGui's own windows that colour carries the odd hint. In this
///   tool it carries most of the PROSE, because that is how the drawing code distinguishes
///   explanation from data. Left at 0.5 grey, the tool's own explanations are the least readable
///   thing in it. It is <see cref="OverlayInk.Quiet"/> here.
///
/// AND THREE MORE THAT WERE LEFT AT THE DEFAULT UNTIL NOW, all of them shape rather than colour,
/// and all of them visible on every page:
///
/// - <c>SeparatorTextBorderSize</c> IS 3 PIXELS BY DEFAULT. Every titled rule in this tool -
///   which is every boundary between two blocks of a page - was a three-pixel slab, thicker than
///   the stem of the letters sitting on it and the loudest line in the interface. It is the
///   quietest thing on a page that has a job to do, so it is one pixel.
/// - <c>TabBarOverlineSize</c> AND <c>TabBarBorderSize</c> decide how the strip of chrome that
///   holds seven pages is bounded. Set explicitly, because "which tab is in front" is the one
///   question the bar exists to answer and the answer should not be whatever a library version
///   bump decides next.
/// - <c>DisabledAlpha</c> IS 0.6, which is ImGui multiplying a control's own colours down until
///   it is plainly off. Over a moving picture that lands on top of it, 0.6 of a dark grey button
///   is not a disabled button - it is a button that has gone. Raised, so a control somebody
///   cannot use yet is still a control they can see and reason about.
///
/// The colours themselves are a warm near-black with a gilt accent, chosen to sit beside what
/// the game already draws rather than to fight it - the same reasoning that put the interface in
/// a serif face (see <c>EntityOverlay.WearASerif</c>). That part is taste. The contrast ratios
/// are not, and they are checked: see <c>OverlayInkTests</c>.
///
/// APPLIED ON THE RENDER THREAD, always. ImGui's style is global mutable state owned by the
/// context, and the context belongs to the thread that draws - see the note on
/// <see cref="EntityOverlay.Interface"/> about why nothing sets this from the wiring.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class OverlayTheme
{
    private static readonly Vector4 Nothing = new(0f, 0f, 0f, 0f);

    /// <summary>Applies the whole palette and the spacing that goes with it.</summary>
    /// <param name="style">
    /// What the user chose. The opacity reaches the backgrounds and the text size reaches the
    /// spacing; the FONT is a different mechanism entirely - see <c>EntityOverlay.WearASerif</c>.
    /// </param>
    public static void Apply(InterfaceStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        ImGuiStylePtr theme = ImGui.GetStyle();
        float solid = style.PanelOpacityOr;

        Shape(theme);
        Space(theme, InterfaceMetrics.Of(style));
        Paint(theme, solid);
    }

    /// <summary>Corners, borders and the rules that separate one block from the next.</summary>
    /// <remarks>
    /// SHAPE FIRST. Rounded corners and a visible edge are not decoration here: they are what
    /// tells the eye where a panel stops, on a background that has no reason to be any
    /// particular colour where the window happens to end.
    ///
    /// Not scaled with the text, unlike everything in <see cref="Space"/>, and the difference is
    /// the point. A radius and a border are about the PIXEL GRID - a one-pixel line is one pixel
    /// whatever is written next to it, and a four-pixel corner is the same shape at every text
    /// size. Padding is about the letters, so it follows them; these do not.
    /// </remarks>
    private static void Shape(ImGuiStylePtr theme)
    {
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
        // behind it are two dark greys, and over a moving picture the eye stops resolving which
        // is which; one lit pixel around the edge settles it without adding a colour.
        theme.FrameBorderSize = 1f;

        // ONE PIXEL, down from the default three. See the note at the top: a titled rule is the
        // quietest thing on a page that still has a job, and at three pixels it outweighed the
        // heading standing on it.
        theme.SeparatorTextBorderSize = 1f;

        // Hard left, so a page's section titles line up with the text underneath them rather
        // than floating in the middle of their own rules. Said out loud because it is ImGui's
        // default and a default is not a decision.
        theme.SeparatorTextAlign = new Vector2(0f, 0.5f);

        // NO EDGE ON EVERY TAB. The overline and the fill already say which page is in front,
        // and a border on each one turns the bar into a row of boxes - seven rectangles
        // competing with the one piece of information the bar carries.
        theme.TabBorderSize = 0f;
        theme.TabBarBorderSize = 1f;

        // The gilt strip over the tab in front, and the whole of how the bar answers its one
        // question. Two pixels: one reads as a rendering artefact next to a one-pixel border,
        // three starts to look like a second underline.
        theme.TabBarOverlineSize = 2f;

        // Never scaled globally. ImGui multiplies EVERYTHING by this, borders and text alike,
        // and a window faded that way loses its text and its background together - which is the
        // one arrangement in which a see-through panel is unreadable at any setting. What the
        // user asked for is applied to the BACKGROUNDS only, in Paint.
        theme.Alpha = 1f;

        // Raised from ImGui's 0.6 - see the note at the top. A disabled control still has to be
        // legible enough to explain why it is disabled.
        theme.DisabledAlpha = 0.75f;
    }

    /// <summary>Every gap and inset, sized against the text rather than fixed.</summary>
    /// <remarks>
    /// Text needs air around it to be read at a glance rather than deciphered, and these windows
    /// are read at a glance during a fight. Kept modest: every pixel here is a pixel of the game
    /// covered, and the dissector's table has hundreds of rows to pay it on.
    ///
    /// WHY THESE FOLLOW THE TEXT SIZE and the shapes above do not is written down in
    /// <see cref="InterfaceMetrics"/>, along with the ratios themselves.
    /// </remarks>
    private static void Space(ImGuiStylePtr theme, InterfaceMetrics room)
    {
        theme.WindowPadding = room.WindowPadding;
        theme.FramePadding = room.FramePadding;
        theme.ItemSpacing = room.ItemSpacing;
        theme.ItemInnerSpacing = room.ItemInnerSpacing;
        theme.CellPadding = room.CellPadding;
        theme.IndentSpacing = room.IndentSpacing;
        theme.ScrollbarSize = room.ScrollbarSize;
        theme.GrabMinSize = room.GrabMinSize;

        // The gap between a titled rule's words and its line, which is spacing rather than shape
        // and so scales with the rest. Narrower across than ImGui's 20 and taller down than its
        // 3: the title wants to start near the left margin, and the rule wants air above it more
        // than it wants a long run-up.
        theme.SeparatorTextPadding = new Vector2(
            room.ItemSpacing.X * 1.5f, room.ItemSpacing.Y * 0.8f);
    }

    /// <summary>Every colour ImGui has a slot for.</summary>
    /// <param name="solid">How solid a panel's background is, as the user set it.</param>
    private static void Paint(ImGuiStylePtr theme, float solid)
    {
        Set(theme, ImGuiCol.Text, OverlayInk.Ink);
        Set(theme, ImGuiCol.TextDisabled, OverlayInk.Quiet);
        Set(theme, ImGuiCol.TextSelectedBg, OverlayInk.Lit with { W = 0.6f });
        Set(theme, ImGuiCol.TextLink, OverlayInk.Accent);

        Set(theme, ImGuiCol.WindowBg, OverlayInk.Panel with { W = solid });
        Set(theme, ImGuiCol.ChildBg, Nothing);

        // POPUPS ARE ALWAYS SOLID, whatever the panels are set to, and that is the one place the
        // setting is overruled. A popup is small, it is on screen for a moment, and it is opened
        // to be read - a combo list or a tooltip showing the hideout through it is the worst case
        // of the whole problem, and it is also the case where nothing is gained by seeing what is
        // behind, since it is about to close.
        Set(theme, ImGuiCol.PopupBg, OverlayInk.Raised);

        Set(theme, ImGuiCol.Border, OverlayInk.Edge);
        Set(theme, ImGuiCol.BorderShadow, Nothing);

        // A HOLE, NOT A PLATE, which is why the frames take the neutral ray and the buttons below
        // take the warm one. An input box that warms towards gold when it is pointed at looks
        // like it is about to catch fire; what it should do is come up a step in the same
        // material the window is made of.
        Set(theme, ImGuiCol.FrameBg, OverlayInk.Sunk(0.13f));
        Set(theme, ImGuiCol.FrameBgHovered, OverlayInk.Sunk(0.21f));
        Set(theme, ImGuiCol.FrameBgActive, OverlayInk.Sunk(0.28f));

        // The title bar keeps its own solidity rather than the panel's: it is the strip the
        // window is dragged by and the one that carries the lock and click-through icons, so it
        // has to be findable even on a panel somebody has made faint.
        Set(theme, ImGuiCol.TitleBg, new Vector4(0.11f, 0.10f, 0.09f, 1f));
        Set(theme, ImGuiCol.TitleBgActive, OverlayInk.Chrome);
        Set(theme, ImGuiCol.TitleBgCollapsed, new Vector4(0.11f, 0.10f, 0.09f, 0.9f));
        Set(theme, ImGuiCol.MenuBarBg, new Vector4(0.10f, 0.10f, 0.11f, 1f));

        Set(theme, ImGuiCol.ScrollbarBg, OverlayInk.Sunken with { W = 0.6f });
        Set(theme, ImGuiCol.ScrollbarGrab, OverlayInk.Warm(0.29f));
        Set(theme, ImGuiCol.ScrollbarGrabHovered, OverlayInk.Lit);
        Set(theme, ImGuiCol.ScrollbarGrabActive, OverlayInk.Held);

        Set(theme, ImGuiCol.CheckMark, OverlayInk.Accent);
        Set(theme, ImGuiCol.SliderGrab, OverlayInk.Warm(0.62f));
        Set(theme, ImGuiCol.SliderGrabActive, OverlayInk.Accent);

        // ONE RAMP FOR EVERY PRESSABLE THING, and this is the correction the warm ray exists for.
        // A button used to rest at a NEUTRAL grey and light to a warm one, so being pointed at
        // changed its colour rather than its brightness - the hover read as the button being
        // swapped rather than lit. Rest, hover and held are now three stops on one material.
        Set(theme, ImGuiCol.Button, OverlayInk.Warm(0.21f));
        Set(theme, ImGuiCol.ButtonHovered, OverlayInk.Lit);
        Set(theme, ImGuiCol.ButtonActive, OverlayInk.Held);

        // Header is the collapsing headers, the selected rows and every Selectable in the tool -
        // the widest blocks of colour in the interface, and the only ones with the GAME'S OWN
        // colours printed on top of them. It sits lower on the ray than the tab bar for exactly
        // that reason; see the note on OverlayInk.Selected for the 2.2:1 that started it.
        Set(theme, ImGuiCol.Header, OverlayInk.Selected);
        Set(theme, ImGuiCol.HeaderHovered, OverlayInk.Lit);
        Set(theme, ImGuiCol.HeaderActive, OverlayInk.Held);

        Set(theme, ImGuiCol.Separator, OverlayInk.Edge with { W = 0.45f });
        Set(theme, ImGuiCol.SeparatorHovered, OverlayInk.Lit);
        Set(theme, ImGuiCol.SeparatorActive, OverlayInk.Accent);

        Set(theme, ImGuiCol.ResizeGrip, OverlayInk.Warm(0.34f) with { W = 0.5f });
        Set(theme, ImGuiCol.ResizeGripHovered, OverlayInk.Lit);
        Set(theme, ImGuiCol.ResizeGripActive, OverlayInk.Accent);

        // The tab bar is how the one window offers its seven pages, so which tab is in front has
        // to be readable at a glance: a lit strip above the selected one, and a clear step in
        // brightness between selected, hovered and the rest. All four stops are on the same ray,
        // so the bar reads as one strip of material with one page lit.
        Set(theme, ImGuiCol.Tab, OverlayInk.Warm(0.13f));
        Set(theme, ImGuiCol.TabHovered, OverlayInk.Lit);
        Set(theme, ImGuiCol.TabSelected, OverlayInk.Chrome);
        Set(theme, ImGuiCol.TabSelectedOverline, OverlayInk.Accent);
        Set(theme, ImGuiCol.TabDimmed, OverlayInk.Warm(0.10f));
        Set(theme, ImGuiCol.TabDimmedSelected, OverlayInk.Warm(0.22f));
        Set(theme, ImGuiCol.TabDimmedSelectedOverline, OverlayInk.Warm(0.50f));

        // The tables are the dissector, the entity browser and the preload list - the densest
        // things the tool draws. Banding, because a row of hex that the eye loses its place in is
        // a row that has to be counted to; faint, because at the default 6% white it reads as
        // stripes in its own right once there is anything behind the window.
        Set(theme, ImGuiCol.TableHeaderBg, OverlayInk.Warm(0.17f));
        Set(theme, ImGuiCol.TableBorderStrong, new Vector4(0.38f, 0.34f, 0.28f, 1f));
        Set(theme, ImGuiCol.TableBorderLight, new Vector4(0.24f, 0.22f, 0.19f, 1f));
        Set(theme, ImGuiCol.TableRowBg, Nothing);
        Set(theme, ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.035f));

        Set(theme, ImGuiCol.PlotLines, new Vector4(0.78f, 0.72f, 0.55f, 1f));
        Set(theme, ImGuiCol.PlotLinesHovered, OverlayInk.Accent);
        Set(theme, ImGuiCol.PlotHistogram, OverlayInk.Warm(0.68f));
        Set(theme, ImGuiCol.PlotHistogramHovered, OverlayInk.Accent);

        Set(theme, ImGuiCol.DragDropTarget, OverlayInk.Accent);
        Set(theme, ImGuiCol.NavCursor, OverlayInk.Accent);
        Set(theme, ImGuiCol.NavWindowingHighlight, OverlayInk.Ink with { W = 0.7f });
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
