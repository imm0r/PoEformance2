namespace PoEformance.Features;

/// <summary>
/// Reports what is actually in the game's config file, so a binding that did not parse
/// says WHY instead of just falling back.
/// </summary>
/// <remarks>
/// The same idea as the flask-belt and matrix probes, applied to a file instead of memory:
/// when a read comes back empty the useful question is "what WAS there", and answering it
/// by asking the user to open the file and describe it costs a round trip every time.
///
/// It is also the guard against the failure this feature is most exposed to. The key names
/// PoE2 writes are not documented anywhere; the AHK tool's own parser handles four spellings
/// it learned the hard way, and its skill-key parser openly admits the names are unconfirmed.
/// If the game renames them, this dump is what identifies the new one.
/// </remarks>
public static class KeyBindingProbe
{
    /// <summary>Key-name fragments worth showing even when nothing parsed.</summary>
    /// <remarks>
    /// Deliberately wider than the parser: it exists to catch the case where the parser is
    /// looking for the wrong word, so it must not be limited to the word the parser knows.
    /// </remarks>
    private static readonly string[] Interesting =
        ["flask", "belt", "quickslot", "quick_slot", "potion", "use_bound", "input_mode"];

    public static void Report(TextWriter output, string? configPath = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        string path = configPath ?? FlaskKeyBindings.DefaultConfigPath;

        output.WriteLine();
        output.WriteLine("key bindings");
        output.WriteLine($"  file    {path}");

        if (!File.Exists(path))
        {
            output.WriteLine("  --      not there. The game writes this file when a binding is CHANGED,");
            output.WriteLine("          so a player who never rebound anything will not have one. The");
            output.WriteLine("          1-5 defaults then match what the game itself is using.");
            return;
        }

        string[] lines;
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
            lines = File.ReadAllLines(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"  FAIL    cannot read it: {error.Message}");
            return;
        }

        var sections = new List<string>();
        var interesting = new List<string>();
        int content = 0;
        string section = "(no section)";

        foreach (string raw in lines)
        {
            string line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            content++;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line;
                sections.Add(line);
                continue;
            }

            foreach (string needle in Interesting)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    interesting.Add($"  {section,-22} {line}");
                    break;
                }
            }
        }

        output.WriteLine($"  size    {bytes} bytes, {lines.Length} lines, {content} of them content");
        if (content == 0)
        {
            // Three bytes is a UTF-8 byte-order mark and nothing else - an "empty" file that
            // still exists. Naming the exact case saves guessing at an encoding problem.
            output.WriteLine(bytes <= 4
                ? "  --      EMPTY (a byte-order mark, no settings). Nothing to read, and the game"
                : "  --      no content lines. Nothing to read, and the game");
            output.WriteLine("          is running its own defaults - which the 1-5 fallback matches.");
            return;
        }

        output.WriteLine($"  sections {(sections.Count > 0 ? string.Join(", ", sections) : "(none)")}");
        output.WriteLine();

        if (interesting.Count == 0)
        {
            output.WriteLine("  Nothing matching flask / belt / quickslot / use_bound. The flask binds are");
            output.WriteLine("  named something this does not recognise - the section list above is the");
            output.WriteLine("  place to look, and the new name goes into FlaskKeyBindings.");
            return;
        }

        output.WriteLine("  section                line                          -> reads as");
        foreach (string line in interesting)
        {
            int equals = line.IndexOf('=');
            string reads = equals > 0
                ? Read(line[(equals + 1)..])
                : "(no value)";

            output.WriteLine($"{line,-56}  -> {reads}");
        }

        FlaskKeys keys = FlaskKeyBindings.Parse(lines, path);
        output.WriteLine();
        output.WriteLine($"  result  {keys.Source} - {keys.Detail}");
        for (int slot = 1; slot <= AutoFlaskSettings.SlotCount; slot++)
        {
            keys.BySlot.TryGetValue(slot, out ushort key);
            output.WriteLine($"    slot {slot}  {FlaskKeyBindings.Describe(key)}");
        }
    }

    private static string Read(string value)
    {
        ushort key = FlaskKeyBindings.ToVirtualKey(value);
        return key == 0 ? "(not a key this can send)" : FlaskKeyBindings.Describe(key);
    }
}
