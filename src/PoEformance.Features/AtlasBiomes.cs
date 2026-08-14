namespace PoEformance.Features;

/// <summary>What a biome is called, and the colour that stands for it.</summary>
/// <param name="Name">The biome's name, for the settings window rather than the atlas.</param>
/// <param name="Colour">Its colour, packed as ImGui wants it.</param>
public sealed record AtlasBiome(string Name, uint Colour);

/// <summary>
/// Which biome each number means, and what colour to draw it in.
/// </summary>
/// <remarks>
/// FROM THE REFERENCE, which ships this table as <c>Plugins/Atlas2/json/biome.json</c> - the ids
/// are the game's own and there is nothing to derive them from. Their order is confirmed by the
/// game's own data from the other side: the six "Also counts as a ... Area" tablet effects in
/// <c>data/atlas-content.json</c> run Water, Mountain, Grass, Forest, Swamp, Desert, which is
/// exactly ids 0 to 5 here.
///
/// TWO COLOURS ARE NOT THE REFERENCE'S, and both for the same reason - this is drawn as a
/// two-pixel ring on a dark plate, where the reference draws it on a coloured background:
///   - Swamp carries byte-for-byte the same blue as Water there, so the two biomes cannot be
///     told apart at all. A muddy green is the point of colouring them.
///   - Forest is 0.0/0.266/0.097, which at two pixels against a dark plate is invisible. Same
///     hue, lifted until it can be seen.
/// The rest are the reference's, including the three city biomes sharing white: a city reading
/// as a city is a family rather than a collision.
///
/// A CODE TABLE rather than a data file, unlike the ratings. A rating is somebody's opinion and
/// changes with taste; this is what the game means by a number, and it changes when the game
/// does - the same reason the shipped groups live in code.
/// </remarks>
public static class AtlasBiomes
{
    /// <summary>What a mark carries when there is no biome to draw - switched off, or unread.</summary>
    /// <remarks>
    /// NOT ZERO, which is Water: a real biome. A sentinel inside the range of the thing it is a
    /// sentinel for would paint every map the overlay knows nothing about as a lake.
    /// </remarks>
    public const int None = -1;

    private static readonly Dictionary<int, AtlasBiome> Known = new()
    {
        [0] = Biome("Water", "#0700E8"),
        [1] = Biome("Mountain", "#D7D7D7"),
        [2] = Biome("Grass", "#00EB55"),
        [3] = Biome("Forest", "#1E9E4C"),
        [4] = Biome("Swamp", "#8FB300"),
        [5] = Biome("Desert", "#CB8400"),
        [6] = Biome("Ezomyte City", "#FFFFFF"),
        [7] = Biome("Faridun City", "#FFFFFF"),
        [8] = Biome("Vaal City", "#DEA700"),
        [9] = Biome("Breach City", "#FF33BD"),
        [10] = Biome("Ocean", "#0066CC"),
        [11] = Biome("Island", "#33B399"),
        [12] = Biome("Oriath City", "#FFFFFF"),
    };

    /// <summary>Every biome the game is known to number, for anything that lists them.</summary>
    public static IReadOnlyDictionary<int, AtlasBiome> All => Known;

    /// <summary>
    /// The biome a number means, or null when it means nothing here.
    /// </summary>
    /// <remarks>
    /// Null for an id past the end of the table rather than a fallback colour, and the drawing
    /// then draws no ring at all. A league adding a biome would otherwise give every map in it a
    /// confident border in a colour that stands for nothing.
    /// </remarks>
    public static AtlasBiome? Of(int id) => Known.TryGetValue(id, out AtlasBiome? biome) ? biome : null;

    private static AtlasBiome Biome(string name, string colour)
        => new(name, OverlaySettings.ParseColour(colour));
}
