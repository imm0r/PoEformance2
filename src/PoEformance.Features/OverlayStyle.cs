using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>How one drawn thing looks.</summary>
/// <remarks>
/// EVERY FIELD'S ZERO MEANS "NOT CHANGED", and that is load-bearing rather than tidy. A
/// struct has an all-zero value that nothing can stop you reaching - `default`, an
/// uninitialised array slot, a dictionary miss through TryGetValue, a JSON payload that omits
/// a key - and none of those go through a constructor, so a default written as a constructor
/// parameter is not the default this type actually has.
///
/// Written the other way round, with a `Visible = true` parameter, the zero value means
/// invisible at zero scale: markers DISAPPEAR down whichever of those paths was taken, with
/// nothing to see but a map missing things it drew yesterday. So the flag is <see
/// cref="Hidden"/> rather than Visible, and zero is the ordinary appearance no matter how the
/// value was arrived at.
/// </remarks>
/// <param name="Hidden">Do not draw this at all.</param>
/// <param name="Colour">
/// <c>#RRGGBB</c> or <c>#AARRGGBB</c>. Empty keeps whatever the catalogue's default is, which
/// is how a style file stays small - an entry only says what was CHANGED.
/// </param>
/// <param name="Width">Line thickness in pixels, for the things drawn as lines. 0 for the usual.</param>
/// <param name="Scale">How much bigger or smaller than its ordinary size. 0 for the usual.</param>
/// <param name="Icon">
/// A file to draw instead of the built-in shape. Empty for the shape. This is the one setting
/// that can fail at RUNTIME rather than at load - the file may be gone, or not an image - so
/// whoever draws it falls back to the shape rather than to nothing.
/// </param>
public readonly record struct LayerStyle(
    [property: JsonPropertyName("hidden")] bool Hidden = false,
    [property: JsonPropertyName("colour")] string Colour = "",
    [property: JsonPropertyName("width")] float Width = 0f,
    [property: JsonPropertyName("scale")] float Scale = 0f,
    [property: JsonPropertyName("icon")] string Icon = "")
{
    /// <summary>Draw it, as it comes.</summary>
    public static LayerStyle Default => default;

    /// <summary>Whether it is drawn.</summary>
    /// <remarks>
    /// Ignored by the serialiser, as is <see cref="SaysNothing"/> below: both are derived, and
    /// the source generator otherwise writes every readable property, so a style file carried
    /// a <c>"Visible"</c> and a <c>"SaysNothing"</c> next to each entry that nothing reads back.
    /// </remarks>
    [JsonIgnore]
    public bool Visible => !Hidden;

    /// <summary>Whether this says anything at all.</summary>
    /// <remarks>
    /// Spelled out rather than compared against <see cref="Default"/>, because the zero value's
    /// strings are null while a loaded or constructed one's are empty. Those mean the same
    /// thing and are not equal, so comparing records would call a hand-written empty entry a
    /// change and store it - which is the one thing the store exists to avoid.
    /// </remarks>
    [JsonIgnore]
    public bool SaysNothing
        => !Hidden
            && Width <= 0f
            && Scale <= 0f
            && string.IsNullOrEmpty(Colour)
            && string.IsNullOrEmpty(Icon);

    /// <summary>The colour to use, falling back to a default when none was chosen.</summary>
    public uint ColourOr(uint fallback)
    {
        uint chosen = OverlaySettings.ParseColour(Colour);
        return chosen == 0 ? fallback : chosen;
    }

    /// <summary>The width to use, falling back when none was chosen.</summary>
    public float WidthOr(float fallback) => Width > 0f ? Width : fallback;

    /// <summary>The size to use, with the scale applied.</summary>
    public float Sized(float baseSize) => baseSize * (Scale > 0f ? Scale : 1f);
}

/// <summary>What a style key controls, so an editor can offer the right things.</summary>
[Flags]
public enum StyleTraits
{
    None = 0,
    Colour = 1,
    Width = 2,
    Scale = 4,
    Icon = 8,
}

/// <summary>One thing the overlay draws, and what can be changed about it.</summary>
/// <param name="Key">Stable, and stored - renaming one loses whatever was set for it.</param>
/// <param name="Group">For grouping in an editor. Not part of the identity.</param>
public sealed record StyleEntry(string Key, string Group, string Label, StyleTraits Traits, uint Fallback);

/// <summary>
/// Everything the overlay draws, and how each of it looks.
/// </summary>
/// <remarks>
/// The point is that "all of it can be changed" has to be TRUE rather than nearly true, and
/// the way to make it true is a catalogue: every drawn thing is an entry here, the editor is
/// generated from the catalogue rather than hand-written, and a new drawn thing is not
/// finished until it has one. Hand-written settings pages are how a tool ends up with
/// fourteen configurable things and three that are not, with no way to tell which is which.
///
/// A style file holds only what was CHANGED. An entry says nothing until somebody sets it, so
/// the defaults stay in the catalogue where they can be corrected in a release, rather than
/// frozen into every user's file the first time they open the editor.
/// </remarks>
public sealed class OverlayStyle
{
    private readonly Dictionary<string, LayerStyle> _byKey;

    public OverlayStyle(IDictionary<string, LayerStyle>? styles = null)
        => _byKey = styles is null
            ? new Dictionary<string, LayerStyle>(StringComparer.Ordinal)
            : new Dictionary<string, LayerStyle>(styles, StringComparer.Ordinal);

    /// <summary>What has been changed. Everything else is the catalogue's default.</summary>
    public IReadOnlyDictionary<string, LayerStyle> Changed => _byKey;

    /// <summary>The style for a key - the chosen one, or the default.</summary>
    public LayerStyle this[string key]
        => _byKey.TryGetValue(key, out LayerStyle style) ? style : LayerStyle.Default;

    /// <summary>Whether a thing is drawn at all.</summary>
    public bool Visible(string key) => this[key].Visible;

    /// <summary>The colour for a key, falling back to the catalogue's.</summary>
    public uint Colour(string key) => this[key].ColourOr(StyleCatalogue.Fallback(key));

    /// <summary>The line width for a key.</summary>
    public float Width(string key, float fallback) => this[key].WidthOr(fallback);

    /// <summary>A size with the key's scale applied.</summary>
    public float Sized(string key, float baseSize) => this[key].Sized(baseSize);

    /// <summary>
    /// How much of its opacity a marker keeps once it is drawn from memory rather than read.
    /// </summary>
    /// <remarks>
    /// Dim enough to be told apart from a live marker at a glance, and nowhere near faint
    /// enough to be missed: what it is saying is "this was here", which is still the most
    /// useful thing on an unexplored map. Shared by every layer that draws a sighting, so the
    /// two kinds of marker cannot drift into meaning different amounts of fade in each.
    /// </remarks>
    public const float RememberedAlpha = 0.55f;

    /// <summary>
    /// The same colour, carrying part of its opacity. What a marker drawn from memory uses.
    /// </summary>
    /// <remarks>
    /// The HUE is left alone deliberately: the colour is how a marker says what it is, and a
    /// remembered chest that stopped being chest-coloured would have to be read twice. Only
    /// how solid it looks changes, which is the thing being said - the position is as true as
    /// it was, and whether it is still there is now last-known rather than current.
    ///
    /// ImGui packs colours as ABGR with alpha in the high byte, which is the reverse of the
    /// #AARRGGBB a settings page sends - see <see cref="OverlaySettings.ParseColour"/>, where
    /// getting that backwards produces something that looks deliberate and is wrong.
    /// </remarks>
    public static uint Faded(uint colour, float by)
    {
        if (by >= 1f)
        {
            return colour;
        }

        uint alpha = (colour >> 24) & 0xFF;
        return (colour & 0x00FFFFFF) | ((uint)Math.Clamp(alpha * by, 0f, 255f) << 24);
    }

    /// <summary>Sets one key. An entry equal to the default is REMOVED rather than stored.</summary>
    /// <remarks>
    /// So that resetting something actually resets it: a stored default looks identical today
    /// and silently pins the value if the catalogue's default is ever corrected.
    /// </remarks>
    public void Set(string key, LayerStyle style)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (style.SaysNothing)
        {
            _byKey.Remove(key);
            return;
        }

        _byKey[key] = style;
    }

    /// <summary>Puts a key back to the catalogue's default.</summary>
    public void Reset(string key) => _byKey.Remove(key);

    /// <summary>Puts everything back.</summary>
    public void ResetAll() => _byKey.Clear();
}

/// <summary>Loads and saves the overlay's appearance next to the executable.</summary>
public static class OverlayStyleStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "overlay-style.json");

    /// <summary>Reads the style, falling back to an unchanged one on any problem.</summary>
    /// <remarks>
    /// Never throws. A style file is cosmetic, and refusing to start over a stray comma in it
    /// trades a wrong colour for no tool at all. Entries that say nothing are dropped on the
    /// way in, so a hand-edited file cannot pin a value by writing out the default.
    /// </remarks>
    public static OverlayStyle Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return new OverlayStyle();
            }

            using FileStream stream = File.OpenRead(file);
            Dictionary<string, LayerStyle>? loaded =
                JsonSerializer.Deserialize(stream, OverlayStyleJsonContext.Default.DictionaryStringLayerStyle);

            var style = new OverlayStyle();
            foreach ((string key, LayerStyle layer) in loaded ?? [])
            {
                style.Set(key, layer);
            }

            return style;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new OverlayStyle();
        }
    }

    /// <summary>Writes what was changed, returning false when it could not.</summary>
    public static bool Save(OverlayStyle style, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(
                stream,
                new Dictionary<string, LayerStyle>(style.Changed, StringComparer.Ordinal),
                OverlayStyleJsonContext.Default.DictionaryStringLayerStyle);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the style survives Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, LayerStyle>))]
public sealed partial class OverlayStyleJsonContext : JsonSerializerContext;
