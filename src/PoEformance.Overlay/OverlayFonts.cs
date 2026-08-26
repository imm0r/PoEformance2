using System.Runtime.Versioning;
using ImGuiNET;

namespace PoEformance.Overlay;

/// <summary>
/// The two faces the interface is set in, and the one that is not the default.
/// </summary>
/// <remarks>
/// ONE SIZE FOR EVERYTHING was the whole typography of this tool until now: a tab, a section
/// title, a table of hex and a paragraph of explanation were all the same height, so the only
/// thing separating a heading from its content was a horizontal rule. That is why a page reads
/// as a wall - nothing on it says "this line ranks above the next".
///
/// A SECOND SIZE OF THE SAME FACE rather than a second family. The tool is drawn over a game
/// whose own lettering is carved and gilded, and two typefaces arguing with each other on top
/// of that is a worse problem than the one being fixed. Scaled by a quarter, which is enough
/// for the eye to rank two lines without the heading becoming a banner.
///
/// THE POINTERS ARE ONLY VALID BETWEEN REBUILDS. The atlas is cleared and rebuilt whenever the
/// text size changes (see <c>EntityOverlay.WearASerif</c>), which invalidates every ImFontPtr
/// handed out before it. That is safe here only because of WHEN it happens: the rebuild runs on
/// the render thread after a frame is presented, and a window can only push a font while it is
/// drawing one - the two never interleave. Nothing outside the render thread may touch this.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class OverlayFonts
{
    private static ImFontPtr _heading;
    private static bool _have;

    /// <summary>Says the atlas was rebuilt and which font came out of it as the heading.</summary>
    /// <remarks>
    /// Called from inside the font-load delegate, which is the only moment the pointer is
    /// known - and the only moment a stale one would still be being held.
    /// </remarks>
    public static void Rebuilt(ImFontPtr heading)
    {
        _heading = heading;
        _have = true;
    }

    /// <summary>Says there is no heading face, so everything falls back to the body one.</summary>
    /// <remarks>
    /// The honest state rather than an assumption: the fonts come from the machine's own
    /// Windows folder and a machine without any of them keeps ImGui's built-in face, where
    /// there is nothing to push. Every caller below is a no-op then, and the interface simply
    /// looks the way it looked before this existed.
    /// </remarks>
    public static void None() => _have = false;

    /// <summary>Whether a heading face is available at all.</summary>
    public static bool HasHeading => _have;

    /// <summary>Draws whatever the callback draws in the heading face.</summary>
    /// <remarks>
    /// A callback rather than a Push/Pop pair offered to the caller, because an unbalanced
    /// pair is a font stack that never unwinds - and the symptom of that is the whole interface
    /// silently growing, one frame at a time, with nothing to point at.
    /// </remarks>
    public static void Heading(Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);

        if (!_have)
        {
            draw();
            return;
        }

        ImGui.PushFont(_heading);
        try
        {
            draw();
        }
        finally
        {
            ImGui.PopFont();
        }
    }

    /// <summary>A titled rule, drawn in the heading face.</summary>
    /// <remarks>
    /// The one this tool uses most: every boundary between two blocks of a page is one of
    /// these, and they are exactly the lines that should outrank what follows them.
    /// </remarks>
    public static void SectionTitle(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        Heading(() => ImGui.SeparatorText(label));
    }

    /// <summary>A collapsing header in the heading face. Says whether it is open.</summary>
    /// <remarks>
    /// Its own method rather than <see cref="Heading"/> with a callback, because this one
    /// ANSWERS: the caller draws the section's contents only when it returns true, and those
    /// contents belong in the body face. The push and pop stay inside, which is the whole
    /// point - a caller holding a pair around a branch is a pair that leaks on the branch
    /// nobody tested.
    /// </remarks>
    public static bool SectionHeader(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        ArgumentNullException.ThrowIfNull(label);

        if (!_have)
        {
            return ImGui.CollapsingHeader(label, flags);
        }

        ImGui.PushFont(_heading);
        try
        {
            return ImGui.CollapsingHeader(label, flags);
        }
        finally
        {
            ImGui.PopFont();
        }
    }

    /// <summary>A tab item's label in the heading face. Says whether that tab is in front.</summary>
    /// <remarks>
    /// THE WHOLE BAR, not one label. A tab bar reserves its height from the font in force when
    /// it BEGINS, so pushing the face around individual labels gives a bar sized for the small
    /// face with big labels overflowing it. That is why the tools window pushes this around
    /// the bar and draws each page's contents after the bar has ended - see its DrawPages.
    /// </remarks>
    public static void PushHeading()
    {
        if (_have)
        {
            ImGui.PushFont(_heading);
        }
    }

    /// <summary>Undoes exactly one <see cref="PushHeading"/>.</summary>
    public static void PopHeading()
    {
        if (_have)
        {
            ImGui.PopFont();
        }
    }
}
