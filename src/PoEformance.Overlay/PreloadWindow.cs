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
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);

    private static readonly Vector4[] ByWeight =
    [
        new(0.75f, 0.8f, 0.88f, 1f),   // notable
        new(1f, 0.85f, 0.4f, 1f),      // valuable
        new(1f, 0.45f, 0.4f, 1f),      // dangerous
    ];

    private readonly PreloadWatch _watch;
    private readonly Action _lookAgain;
    private string _search = string.Empty;

    /// <param name="lookAgain">Runs the walk again, for when it needs forcing.</param>
    public PreloadWindow(PreloadWatch watch, Action lookAgain)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(lookAgain);
        _watch = watch;
        _lookAgain = lookAgain;
    }

    /// <summary>Whether the window is on screen.</summary>
    public bool Visible { get; set; }

    /// <summary>Draws the window.</summary>
    public void Render()
    {
        if (!Visible)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(620f, 460f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(150f, 140f), ImGuiCond.FirstUseEver);

        bool open = Visible;
        bool expanded = ImGui.Begin("What is in this area", ref open, ImGuiWindowFlags.NoFocusOnAppearing);

        // End in a finally: an exception between Begin and End leaves ImGui's stack
        // unbalanced and the assert that follows takes the process down.
        try
        {
            if (expanded)
            {
                Draw();
            }
        }
        finally
        {
            ImGui.End();
        }

        Visible = open;
    }

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

        ImGui.Separator();

        IReadOnlyList<PreloadFinding> findings = _watch.Findings;
        if (findings.Count == 0)
        {
            ImGui.TextColored(DimText, all.Count > 0
                ? "nothing here is on the list of things worth a line - search below to see what is"
                : "nothing to say about this area");
        }

        foreach (PreloadFinding finding in findings)
        {
            ImGui.TextColored(ByWeight[(int)finding.Weight], finding.Name);
            ImGui.SameLine();
            ImGui.TextColored(DimText, finding.Path);
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(240f);
        ImGui.InputText("search the raw list", ref _search, 96);
        ImGui.SameLine();
        ImGui.TextColored(DimText, "click a path to copy it");

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

                // Copying is the point of the list: a path worth a line goes into the
                // meanings, and typing one of these out by hand is not something anybody
                // would do twice.
                if (ImGui.Selectable($"{path}###preload{shown}"))
                {
                    ImGui.SetClipboardText(path);
                }

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
}
