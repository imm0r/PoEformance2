using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// A status line whose written file or folder is a link: a click opens Explorer there.
/// </summary>
/// <remarks>
/// THE LINE IS DRAWN AS IT WAS, and only the path in it changes - underlined in the link colour, a
/// hand under the pointer, and a click that opens Explorer with the file selected (or the folder
/// open). Which part is the path is WrittenPath's business: found by asking the disk, once per
/// line, so a frame redrawing the same line asks nothing.
///
/// ONE LINE WHERE IT FITS, the path on a line of its own where it does not: a path does not wrap
/// at its separators, and a link half on one line and half on the next is two links.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class PathLink
{
    /// <summary>Most lines remembered - status lines change rarely, and a long session should not grow this.</summary>
    private const int MostRemembered = 128;

    /// <summary>Each line seen, against where its path is and whether that is a file. Drawing thread only.</summary>
    private static readonly Dictionary<string, (int Start, int Length, bool File)> Seen = new(StringComparer.Ordinal);

    /// <summary>A status line in the colour already in force.</summary>
    public static void Line(string text) => Line(ImGui.GetStyle().Colors[(int)ImGuiCol.Text], text);

    /// <summary>A status line in a colour, wrapping at the window's edge, its written path a link.</summary>
    public static void Line(Vector4 ink, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        (int start, int length, bool file) = Found(text);
        if (length == 0)
        {
            ImGuiText.Wrapped(ink, ImGuiText.Escape(text));
            return;
        }

        string before = text[..start];
        string path = text.Substring(start, length);
        string after = text[(start + length)..];

        if (ImGui.CalcTextSize(text).X <= ImGui.GetContentRegionAvail().X)
        {
            if (before.Length > 0)
            {
                Plain(ink, before);
                ImGui.SameLine(0f, 0f);
            }

            Link(path, file);
            if (after.Length > 0)
            {
                ImGui.SameLine(0f, 0f);
                Plain(ink, after);
            }

            return;
        }

        if (before.Trim().Length > 0)
        {
            ImGuiText.Wrapped(ink, ImGuiText.Escape(before.TrimEnd()));
        }

        Link(path, file);
        if (after.Trim().Length > 0)
        {
            ImGuiText.Wrapped(ink, ImGuiText.Escape(after.TrimStart()));
        }
    }

    /// <summary>Where a line's path is, asked of the disk the first time the line is drawn.</summary>
    private static (int Start, int Length, bool File) Found(string text)
    {
        if (Seen.TryGetValue(text, out (int Start, int Length, bool File) known))
        {
            return known;
        }

        (int start, int length) = WrittenPath.Find(text, path => File.Exists(path) || Directory.Exists(path));
        bool file = length > 0 && File.Exists(text.Substring(start, length));
        if (Seen.Count >= MostRemembered)
        {
            Seen.Clear();
        }

        Seen[text] = (start, length, file);
        return (start, length, file);
    }

    private static void Plain(Vector4 ink, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ink);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>The path itself: link-coloured, underlined, and a click away from Explorer.</summary>
    private static void Link(string path, bool file)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, OverlayInk.Reference);
        ImGui.TextUnformatted(path);
        ImGui.PopStyleColor();

        bool hovered = ImGui.IsItemHovered();
        Vector2 least = ImGui.GetItemRectMin();
        Vector2 most = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(least.X, most.Y), most, ImGui.GetColorU32(hovered ? OverlayInk.Ink : OverlayInk.Reference), 1f);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(file ? "Open the folder in Explorer, with this file selected" : "Open this folder in Explorer");
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Open(path, file);
        }
    }

    /// <summary>
    /// Explorer at a path: the folder with the file selected, or the folder itself.
    /// </summary>
    /// <remarks>
    /// THE ARGUMENT AS ONE STRING, because Explorer reads <c>/select,"path"</c> as a single token and a
    /// quoted argument list would quote the comma in. Never throws: a link that does nothing is a
    /// missed convenience, a crash on a click is not.
    /// </remarks>
    private static void Open(string path, bool file)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = file ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Trace.WriteLine($"could not open Explorer at {path}: {exception.Message}");
        }
    }
}
