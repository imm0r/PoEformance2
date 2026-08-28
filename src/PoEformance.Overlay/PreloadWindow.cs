using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What this area turned out to contain, and the raw list it was worked out from.
/// </summary>
/// <remarks>
/// BOTH HALVES ARE THE FEATURE. The findings are what anybody looks at while playing; the raw
/// list is what the findings are only as good as, and the only way that list of meanings grows
/// is somebody reading what an area actually loaded on the day the tool had nothing to say
/// about it.
///
/// It is also how the read gets CHECKED. Every number in the walk came from another tool's
/// source and none of it can be confirmed offline - so a count, an error line and a searchable
/// list of paths are here on purpose. "It found 1,400 files and here they are" and "it found
/// nothing and here is why" are the two answers that matter, and neither is visible from a
/// summary line.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PreloadWindow
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 GoodText = OverlayInk.Good;
    private static readonly Vector4 WarnText = OverlayInk.Warn;

    private readonly PreloadWatch _watch;
    private readonly Action _lookAgain;
    private readonly Action _sweep;
    private readonly Action _rulesChanged;
    private string _search = string.Empty;
    private string _said = string.Empty;
    private string _saved = string.Empty;

    /// <param name="lookAgain">Runs the walk again, for when it needs forcing.</param>
    /// <param name="sweep">Looks for the count field instead of assuming one - see below.</param>
    /// <param name="rulesChanged">Writes down a rule somebody added from the raw list.</param>
    public PreloadWindow(PreloadWatch watch, Action lookAgain, Action sweep, Action rulesChanged)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(lookAgain);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(rulesChanged);
        _watch = watch;
        _lookAgain = lookAgain;
        _sweep = sweep;
        _rulesChanged = rulesChanged;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab() => Draw();

    private void Draw()
    {
        if (ImGui.Button("look again"))
        {
            _lookAgain();
        }

        ImGui.SameLine();

        if (!_watch.Looked)
        {
            ImGui.TextColored(DimText, "not looked at yet - it runs once when an area loads");
            return;
        }

        IReadOnlyList<string> all = _watch.All;
        ImGui.TextColored(
            all.Count > 0 ? GoodText : WarnText,
            all.Count > 0
                ? $"{all.Count} files loaded for this area"
                : "no files matched this area");

        // The reason, when there is one. A read that comes back empty and a read that never
        // ran look identical from a count alone, and the difference is the whole question
        // when the offsets are new.
        if (_watch.Note.Length > 0)
        {
            ImGui.TextColored(WarnText, _watch.Note);
        }

        // Offered exactly when it is the question. "It walked thousands of slots and matched
        // nothing" has several causes wanting opposite fixes, and the sweep is what tells
        // them apart - so it appears when that has happened and stays out of the way when it
        // has not.
        if (all.Count == 0 && _watch.Looked)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("find the count field"))
            {
                _sweep();
            }
        }

        // The whole list, out to a file. Offered beside the count rather than at the bottom of
        // the raw list, because the reason to want it is the count being interesting.
        if (all.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("save the list"))
            {
                _saved = PreloadAlertStore.Dump(_watch.Area, all, _watch.Note) is { } written
                    ? $"written to {written}"
                    : "could not write the list";
            }

            if (_saved.Length > 0)
            {
                ImGuiText.Wrapped(DimText, _saved);
            }
        }

        foreach (string line in _watch.Sweep)
        {
            ImGui.TextColored(DimText, line);
        }

        // Titled rules between the halves, like the alerts tab: the findings and the raw
        // list are different answers to different questions, and a hairline does not say
        // where one ends.
        ImGui.Spacing();
        OverlayFonts.SectionTitle("what that means");
        ImGui.Spacing();

        IReadOnlyList<PreloadAlertEntry> found = _watch.Found;
        if (found.Count == 0)
        {
            ImGuiText.Wrapped(DimText, all.Count > 0
                ? "nothing here is on the watch list - search below and add what is worth a line"
                : "nothing to say about this area");
        }

        foreach (PreloadAlertEntry entry in found)
        {
            ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(entry.Colour), entry.Shown);

            // Wrapped, because a metadata path is the longest thing on the tab and the one
            // that used to decide how wide the window had to be dragged.
            ImGuiText.Wrapped(DimText, entry.Path);
        }

        ImGui.Spacing();
        OverlayFonts.SectionTitle("every file it loaded");
        ImGui.Spacing();
        DrawSearch();

        if (!ImGui.BeginChild("preload-raw", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            int shown = 0;
            foreach (string path in all)
            {
                if (_search.Length > 0 && !path.Contains(_search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ImGui.PushID(shown);

                // The whole point of the raw list, and the reason it is searchable: a path
                // that turns out to mean something is added from here, without anybody having
                // to type it or wait for a release. THIS EXACT PATH is what gets watched - so
                // what was clicked and what is stored can never disagree, which is the whole
                // bargain of matching exactly.
                if (ImGui.SmallButton("+ watch"))
                {
                    Add(path);
                }

                ImGui.SameLine();

                // Copying still, because a path is also the thing you paste to somebody else
                // when it needs looking at rather than watching for.
                if (ImGui.Selectable(path))
                {
                    ImGui.SetClipboardText(path);
                }

                ImGui.PopID();

                if (++shown >= 400)
                {
                    ImGui.TextColored(DimText, $"...and {all.Count - shown} more - narrow the search");
                    break;
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawSearch()
    {
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 13.5f);
        bool entered = ImGui.InputText(
            "###preload-search", ref _search, 96, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();

        // Adding what was TYPED, not what was clicked. Somebody searching "ritual" has
        // already said what they care about, and it is a better rule than any single path
        // the search turned up - shorter, so it survives the file being renamed next league.
        string term = _search.Trim();
        bool add = ImGui.Button("watch for this") || entered;
        if (add && term.Length > 0)
        {
            Add(term);
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, _said.Length > 0 ? _said : "search, then watch for it - or + on a row");
    }

    /// <summary>Adds a rule for a fragment, and says what happened.</summary>
    /// <remarks>
    /// The answer is shown rather than assumed, because the two ordinary outcomes look
    /// identical from the outside: a rule that was added appears in the findings above only if
    /// THIS area contains it, so "nothing happened" is the correct display for both a
    /// successful add and a duplicate. Saying which is what stops the second click.
    /// </remarks>
    private void Add(string path)
    {
        var entry = new PreloadAlertEntry(path, PreloadMeanings.Suggest(path));
        _said = _watch.Add(entry)
            ? $"watching for \"{entry.Shown}\""
            : $"already watching for \"{entry.Shown}\"";

        _rulesChanged();
    }

    /// <summary>A name for a fragment: its words, with a capital.</summary>
    private static string Pretty(string fragment)
    {
        string trimmed = fragment.Trim().Trim('/');
        return trimmed.Length == 0 ? fragment : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
