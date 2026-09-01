using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The one line that says whether the tool is working, drawn above the tabs on every page.
/// </summary>
/// <remarks>
/// THE READOUT WAS A PAGE, AND A PAGE IS SOMEWHERE YOU HAVE TO BE. "Is it reading the game" and
/// "which area does it think I am in" are questions asked WHILE doing something else - while
/// configuring markers, while reading the dissector, while wondering why a flask did not fire -
/// and the answer lived on a tab you had to leave what you were doing to go and look at. So
/// either the tool sat on its status page and the other twelve were out of reach, or it sat on
/// a tool page and a blank overlay was ambiguous again.
///
/// A strip above the tab bar is on screen on every page, costs one line, and answers the four
/// or five questions that are worth answering constantly. The status PAGE keeps everything
/// else - the belt, the panels, the projection probe, the whole debugging pass - because those
/// are read deliberately, once, while working something out.
///
/// AND IT IS WHAT LETS THE WINDOW STOP RESIZING ITSELF. The readout being a page is why this
/// window had two sizes and switched between them: the readout auto-sized to its dozen short
/// lines and every other page was forced to 940x620, which meant that leaving the readout
/// OVERWROTE whatever size the window had been dragged to. Somebody who sized the dissector to
/// fit beside their game got that size thrown away every time they glanced at the readout and
/// came back. With the live part up here the window has one size, and that size is the user's -
/// see <c>ToolTabs.Render</c>.
///
/// CHIPS RATHER THAN A SENTENCE. Each fact is a short coloured word in a fixed order, so the
/// strip is read by SHAPE after the first day - the eye goes to the third position for the
/// entity count rather than reading a line of prose to find it. A fact that does not fit the
/// window is dropped rather than wrapped, which keeps this one line tall at every window size;
/// they are drawn most-important-first, so what survives a narrow window is what matters.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StatusBar
{
    /// <summary>Whether a chip has been drawn on this strip yet, so the first gets no divider.</summary>
    /// <remarks>
    /// Static because the strip is drawn once per frame on the render thread, in one place,
    /// between <see cref="Begin"/> and <see cref="End"/> - the same thread-and-moment argument
    /// the font stack is built on. Nothing outside the render thread may touch this.
    /// </remarks>
    private static bool _started;

    /// <summary>Opens the strip.</summary>
    public static void Begin() => _started = false;

    /// <summary>
    /// Closes the strip and rules it off from the tabs below.
    /// </summary>
    /// <remarks>
    /// A rule rather than a gap, and it is doing real work: without it the strip and the tab bar
    /// read as one block of chrome, and the strip's words look like more tabs. It is the same
    /// one-pixel separator the rest of the tool uses, so nothing new has been invented for it.
    /// </remarks>
    public static void End()
    {
        // Nothing was drawn - no line either, or the window opens with a rule under nothing.
        if (!_started)
        {
            return;
        }

        ImGui.Separator();
    }

    /// <summary>One fact, in its own colour, after a divider.</summary>
    /// <remarks>
    /// DROPPED RATHER THAN WRAPPED when the window is too narrow for it. ImGui's
    /// <c>SameLine</c> does not wrap, so a strip too long for the window would simply run off
    /// the right edge - which is the same as being dropped, except that it also stretches the
    /// window's content width and gives every page a horizontal scrollbar it does not need.
    /// Measuring first and stopping is the same outcome without that cost.
    ///
    /// The measurement includes the divider, because a chip that fits only by losing its
    /// divider would run into its neighbour.
    /// </remarks>
    /// <param name="text">The fact. Already formatted - this draws it, it does not decide it.</param>
    /// <param name="ink">What it is drawn in. The colour IS the state for most of these.</param>
    public static void Chip(string text, Vector4 ink)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return;
        }

        float divider = ImGui.GetFontSize() * 1.6f;
        float wanted = ImGui.CalcTextSize(text).X + divider;

        // MEASURED IN SCREEN SPACE, from the right edge of the chip just drawn to the window's
        // inner right edge. The cursor cannot be asked instead: a text item leaves it on the
        // NEXT line, so its X is the left margin rather than where this strip has got to.
        // GetItemRectMax is the previous chip's own right edge, which is exactly the question -
        // and it stays correct across a chip that was skipped, since the skipped one submitted
        // no item.
        if (_started)
        {
            float edge = ImGui.GetWindowPos().X + ImGui.GetWindowWidth()
                         - ImGui.GetStyle().WindowPadding.X - ImGui.GetStyle().ScrollbarSize;
            if (ImGui.GetItemRectMax().X + wanted > edge)
            {
                return;
            }

            ImGui.SameLine(0f, divider * 0.5f);
            ImGui.TextColored(OverlayInk.Edge, "|");
            ImGui.SameLine(0f, divider * 0.5f);
        }

        _started = true;

        // Pushed rather than passed to TextColored: ImGui's text calls are printf, and these
        // strings carry area names and percent signs straight out of the game. See ImGuiText.
        ImGui.PushStyleColor(ImGuiCol.Text, ink);
        try
        {
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.PopStyleColor();
        }
    }

    /// <summary>A fact in the quiet ink, for the ones that are never good or bad news.</summary>
    public static void Chip(string text) => Chip(text, OverlayInk.Quiet);

    /// <summary>
    /// A measured figure, in the mono face so it stops twitching as it counts.
    /// </summary>
    /// <remarks>
    /// THE WHOLE REASON THE MONO FACE EXISTS IN THIS TOOL, and this strip is where it earns its
    /// keep hardest: these numbers change every frame, they are on screen the entire time, and
    /// in a proportional face a count going from 999 to 1000 is WIDER - so every chip to its
    /// right shifts sideways, several times a second, forever. Fixed-width digits are what stop
    /// the strip moving while it is being watched.
    /// </remarks>
    public static void Figure(string text, Vector4 ink)
    {
        OverlayFonts.PushMono();
        try
        {
            Chip(text, ink);
        }
        finally
        {
            OverlayFonts.PopMono();
        }
    }

    /// <summary>The same, in the ink that means "this tool worked it out".</summary>
    public static void Figure(string text) => Figure(text, OverlayInk.Measured);
}
