using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Writes on the endgame atlas: what each map is called, what is in it, and how to get there.
/// </summary>
/// <remarks>
/// The atlas is a web of unnamed nodes with no indication of which line leads anywhere worth
/// going. This puts the name back on each one, lists what the game says is in it, and draws the
/// way to whichever kinds of map somebody is looking for.
///
/// EVERYTHING IS ALREADY DECIDED by the time it arrives here. Which maps to draw, what group
/// each is in, where a route runs - all of it comes from <see cref="AtlasWatch"/> on the reader
/// thread, because that is memory reading and this is the thread drawing frames. What happens
/// here is placement and colour.
///
/// DRAWN IN LAYERS, and it has to be. A label is opaque, a route crosses the whole screen, and
/// in node order every line lands on top of half the labels it passes. So it goes over the list
/// three times - connections, then routes, then labels - rather than drawing each map whole.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AtlasLayer
{
    /// <summary>How faint a finished map goes, on the occasions they are shown at all.</summary>
    private const float DoneFade = 0.4f;

    /// <summary>
    /// How everything here looks. Shared with the overlay, so one editor covers all of it.
    /// </summary>
    /// <remarks>
    /// The group colours are NOT in here - they belong to the groups, which are somebody's own
    /// list. This covers what a map with no group uses and what no group can colour: the plate,
    /// the connections, and the dot on the near end of a route.
    /// </remarks>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>
    /// Write each map's name on it.
    /// </summary>
    /// <remarks>
    /// Off still draws the contents and the routes, which is the point of having it: the
    /// names are the busiest thing on the atlas, and somebody who only wants to see WHERE the
    /// breaches are does not want two hundred labels as well.
    /// </remarks>
    public bool ShowNames { get; set; } = true;

    /// <summary>
    /// How big the writing is, against the interface's own size.
    /// </summary>
    /// <remarks>
    /// The atlas is a screen full of small text and a 4K monitor makes it smaller. Whether the
    /// default reads at a real resolution is not something that can be decided from here, so it
    /// is a number somebody can turn up rather than one to be guessed right.
    /// </remarks>
    public float TextScale { get; set; } = 1f;

    /// <summary>Where the label sits relative to the node it names.</summary>
    /// <remarks>
    /// Nudged UP, off the node's own art. The game draws an icon in the middle of each node,
    /// and a plate centred exactly on it hides the thing it is labelling.
    /// </remarks>
    public Vector2 Nudge { get; set; } = new(0f, -20f);

    /// <summary>
    /// Point at routing targets that are off the screen, from the edge nearest them.
    /// </summary>
    /// <remarks>
    /// THE ATLAS IS BIGGER THAN THE SCREEN and that is what makes a route hard to use: the line
    /// leaves the edge and there is nothing to say whether the thing it leads to is one screen
    /// away or twenty, or whether it was worth scrolling after at all. A pointer on the edge
    /// answers both without moving the view.
    /// </remarks>
    public bool Pointers { get; set; } = true;

    /// <summary>
    /// How far off the screen a pointer may still reach, counted in screenfuls. 0 for no limit.
    /// </summary>
    /// <remarks>
    /// SCREENS RATHER THAN HOPS, which is the mistake worth not repeating. Hops are counted
    /// from the maps you can enter NOW, and those are scattered over the whole atlas - so a map
    /// on the far side of the world is routinely three hops away and its pointer would send
    /// somebody scrolling for a minute. Distance on the screen is the thing actually being
    /// asked about, and it follows the atlas's own zoom for free.
    /// </remarks>
    public float PointerRange { get; set; } = 5f;

    /// <summary>
    /// Draw what is in a map as the game's own pictures, where there are any.
    /// </summary>
    /// <remarks>
    /// A ROW OF ICONS INSTEAD OF A STACK OF SENTENCES. Three contents is three lines of text
    /// under a map name, which is taller than the map's own node and pushes into the next one;
    /// the same three as icons is one short row, and it is the row the game itself draws, so it
    /// is read without reading. What has no picture keeps its line, so nothing is lost.
    /// </remarks>
    public bool Icons { get; set; } = true;

    /// <summary>
    /// The picture for a content's art name, or zero when there is none to draw.
    /// </summary>
    /// <remarks>
    /// Handed in rather than looked up here: where the art comes from - a folder somebody
    /// filled, or the installed game's own packed files - is a question about this machine, and
    /// the answer takes a cache, a background fetch and a decoder that this layer has no
    /// business knowing about. See <see cref="AtlasArt"/>.
    /// </remarks>
    public Func<string, IntPtr>? Art { get; set; }

    /// <summary>
    /// What the cursor is resting on, for whoever draws tooltips. Null when it is on nothing.
    /// </summary>
    /// <remarks>
    /// REPORTED RATHER THAN DRAWN HERE, and it has to be: a tooltip is an ImGui window and this
    /// draws into the background list, so one raised from inside here would be built while the
    /// frame is mid-flight. The overlay puts it up after this returns.
    ///
    /// An icon is the reason this exists. A name says what a content is; a picture does not,
    /// until you have learnt it - and the game's own wording for it is already in hand.
    /// </remarks>
    public AtlasSaid? Told { get; private set; }

    /// <summary>
    /// Where on the screen this may draw: everything, less whatever the game paints on top.
    /// </summary>
    /// <remarks>
    /// THE ATLAS HAS THE SAME PROBLEM THE LARGE MAP HAD, in a different shape. The panel is
    /// drawn across the whole window and the game paints its own interface over it - the orbs
    /// and bars at the bottom, an open inventory down one side, an atlas skill panel down the
    /// other - so the web, the routes and the labels all land on top of those unless something
    /// says otherwise. Set by the overlay every frame; see <c>EntityOverlay.KeepOutOf</c>.
    /// </remarks>
    public ScreenRegion? Region { get; set; }

    /// <summary>
    /// Where a clipped line ends up, reused between frames.
    /// </summary>
    /// <remarks>
    /// A field rather than a local because this is called a couple of thousand times a frame on
    /// a full atlas, and a list per call is a list per connection per frame.
    /// </remarks>
    private readonly List<(Vector2 From, Vector2 To)> _pieces = [];

    /// <summary>
    /// How many routes run along each stretch of the atlas, and how many have drawn on it yet.
    /// </summary>
    /// <remarks>
    /// ROUTES SHARE THEIR LAST FEW STEPS constantly - everything reachable from one accessible
    /// map leaves it the same way - and chevrons drawn at the same spacing on the same stretch
    /// land on the same pixels, so the last route drawn stamps out every one before it. Whoever
    /// drew last then appears to be the only route there.
    ///
    /// Counting first and dealing out a phase each means a shared stretch shows every colour on
    /// it, alternating. Fields rather than locals because this happens every frame.
    ///
    /// The key is the pair of ENDPOINTS, which is exact rather than approximate: both routes
    /// took the position from the same table, so the two copies are the same bits.
    /// </remarks>
    private readonly Dictionary<(Vector2 From, Vector2 To), int> _busy = [];

    private readonly Dictionary<(Vector2 From, Vector2 To), int> _drawn = [];

    /// <summary>
    /// The picture for each art name asked about THIS FRAME.
    /// </summary>
    /// <remarks>
    /// A hundred maps carrying three contents each is three hundred lookups, and every one of
    /// them is asked three times over - once to decide the content has a picture, once to
    /// measure the row, once to draw it. That is a thousand calls a frame into a cache that
    /// builds a string key to look itself up with, so it is a thousand strings a frame for
    /// forty distinct answers. Kept for the frame, it is forty.
    /// </remarks>
    private readonly Dictionary<string, IntPtr> _pictures = new(StringComparer.Ordinal);

    /// <summary>The nearest off-screen target of each routing group, by the group's name.</summary>
    /// <remarks>
    /// ONE POINTER PER GROUP rather than one per map. Twenty unrun unique maps in the same
    /// direction is twenty pills stacked on one edge, saying the same thing twenty times; what
    /// is being asked is "where is the nearest one", and that has a single answer. The pill
    /// then names that MAP - the group is already said by the colour it is drawn in, and the
    /// map name is what tells two citadels apart.
    /// </remarks>
    private readonly Dictionary<string, AtlasMark> _aimed = new(StringComparer.Ordinal);

    /// <summary>
    /// Draws the atlas view over the game's own.
    /// </summary>
    /// <remarks>
    /// Takes the view as it was published rather than reading it twice: the reader thread
    /// replaces it whole, so reading it once here means every line and label in a frame agrees
    /// about where the atlas was.
    /// </remarks>
    public void Draw(ImDrawListPtr draw, AtlasView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        Told = null;

        // Emptied whether or not anything is drawn: a texture is a handle the cache can drop
        // and rebuild, and one remembered across frames would eventually be drawn after it
        // stopped being a picture.
        _pictures.Clear();

        if (!view.Anything)
        {
            return;
        }

        Vector2 screen = ImGui.GetIO().DisplaySize;
        Vector2 cursor = ImGui.GetMousePos();

        if (Style.Visible(StyleCatalogue.Keys.AtlasWeb))
        {
            uint web = Style.Colour(StyleCatalogue.Keys.AtlasWeb);
            float thin = Style.Width(StyleCatalogue.Keys.AtlasWeb, 1.5f);
            foreach ((Vector2 from, Vector2 to) in view.Web)
            {
                if (Worth(from, to, screen))
                {
                    Line(draw, from, to, web, thin);
                }
            }
        }

        if (Style.Visible(StyleCatalogue.Keys.AtlasRoute))
        {
            Crowding(view.Marks);

            foreach (AtlasMark mark in view.Marks)
            {
                if (mark.Route.Count > 0)
                {
                    DrawRoute(draw, mark, screen);
                }
            }
        }

        if (Style.Visible(StyleCatalogue.Keys.AtlasLabel))
        {
            foreach (AtlasMark mark in view.Marks)
            {
                DrawLabel(draw, mark, cursor);
            }
        }

        // LAST, so a pill on the edge sits over the lines that reach it rather than under
        // them - it is the thing being read there, and a route arriving at the same corner
        // would otherwise be drawn across its text.
        if (Pointers)
        {
            DrawPointers(draw, view.Marks, screen);
        }
    }

    /// <summary>Counts how many routes run along each stretch, before any of them is drawn.</summary>
    private void Crowding(IReadOnlyList<AtlasMark> marks)
    {
        _busy.Clear();
        _drawn.Clear();

        foreach (AtlasMark mark in marks)
        {
            foreach (IReadOnlyList<Vector2> run in mark.Route)
            {
                for (int i = 1; i < run.Count; i++)
                {
                    (Vector2, Vector2) stretch = Stretch(run[i - 1], run[i]);
                    _busy[stretch] = _busy.TryGetValue(stretch, out int already) ? already + 1 : 1;
                }
            }
        }
    }

    /// <summary>
    /// One stretch of atlas, named the same way from either end.
    /// </summary>
    /// <remarks>
    /// Two routes meeting on a connection may walk it in opposite directions, and unordered
    /// they would count as two different stretches with one route each - which is exactly the
    /// case the counting exists for.
    /// </remarks>
    private static (Vector2 From, Vector2 To) Stretch(Vector2 a, Vector2 b)
        => a.X < b.X || (a.X == b.X && a.Y <= b.Y) ? (a, b) : (b, a);

    /// <summary>
    /// Draws a line, in as many pieces as the game's interface leaves room for.
    /// </summary>
    /// <remarks>
    /// CUT RATHER THAN DROPPED. A connection that clips the corner of an open panel is still
    /// most of a connection, and on the atlas the lines ARE the content - dropping one because
    /// it touches the inventory would quietly remove a route somebody is reading.
    /// </remarks>
    private void Line(ImDrawListPtr draw, Vector2 from, Vector2 to, uint colour, float width)
    {
        if (Region is not ScreenRegion region)
        {
            draw.AddLine(from, to, colour, width);
            return;
        }

        _pieces.Clear();
        region.ClipSegment(from, to, _pieces);
        foreach ((Vector2 start, Vector2 end) in _pieces)
        {
            draw.AddLine(start, end, colour, width);
        }
    }

    /// <summary>Whether a box of writing may be drawn where it wants to go.</summary>
    /// <remarks>
    /// ALL OR NOTHING for text, unlike the lines above: half a name plate cut off by the edge
    /// of the inventory is a word broken mid-letter, which is worse to look at than a label
    /// that is simply not there. The map underneath is still named by the game.
    /// </remarks>
    private bool Fits(Vector2 from, Vector2 to)
        => Region is not ScreenRegion region
           || region.Clear(new ScreenRect(from.X, from.Y, to.X, to.Y));

    /// <summary>Draws the way to one map, in its group's colour.</summary>
    /// <remarks>
    /// RUN BY RUN, with the holes left as holes. A route can cross a map the panel has no
    /// position for, and a line joining the two sides of that gap runs along no connection
    /// anybody can walk - which is what "some of the lines are arbitrary" was.
    ///
    /// With the marker on the end NEAREST YOU rather than on the destination. The destination
    /// is already named and coloured; what a route is actually asked is "which of the maps I
    /// can enter now does this start from", and that end is otherwise just where a line stops.
    /// </remarks>
    private void DrawRoute(ImDrawListPtr draw, AtlasMark mark, Vector2 screen)
    {
        uint colour = ColourOf(mark, 0.55f);
        float width = Style.Width(StyleCatalogue.Keys.AtlasRoute, 4f);

        // The chevrons are the SAME colour at full strength rather than a colour of their own.
        // A route's colour is what says which group it belongs to; giving its arrows another
        // one would have a single route reading as two.
        uint pointing = ColourOf(mark, 1f);
        float chevron = MathF.Max(3f, width * 1.4f);

        foreach (IReadOnlyList<Vector2> run in mark.Route)
        {
            for (int i = 1; i < run.Count; i++)
            {
                if (!Worth(run[i - 1], run[i], screen))
                {
                    continue;
                }

                Line(draw, run[i - 1], run[i], colour, width);
                Chevrons(draw, run[i - 1], run[i], pointing, chevron);
            }
        }

        // The near end of the FIRST run, which is the nearest end that could be placed. With
        // the start of the route missing there is nothing honest to mark, and the dot is
        // better absent than sitting on a map the route does not begin at.
        Vector2 entry = mark.Route[0][0];
        if (!Style.Visible(StyleCatalogue.Keys.AtlasEntry) || !On(entry, screen))
        {
            return;
        }

        float radius = Style.Sized(StyleCatalogue.Keys.AtlasEntry, MathF.Max(3f, width * 1.3f));
        var reach = new Vector2(radius, radius);
        if (!Fits(entry - reach, entry + reach))
        {
            return;
        }

        draw.AddCircleFilled(entry, radius, Style.Colour(StyleCatalogue.Keys.AtlasEntry));
        draw.AddCircle(entry, radius, 0xFF00_0000, 0, MathF.Max(1f, radius * 0.35f));
    }

    /// <summary>How far apart the arrows on a route sit, as a multiple of their own size.</summary>
    /// <remarks>
    /// Wide, because these are not decoration: one arrow per stretch of atlas is enough to say
    /// which way it runs, and a dotted line of them buries the route's own colour.
    /// </remarks>
    private const float ChevronGap = 7f;

    /// <summary>
    /// Arrows along one stretch, offset so routes sharing it do not stamp on each other.
    /// </summary>
    /// <remarks>
    /// Spaced by DISTANCE rather than by step: atlas connections are wildly uneven on screen -
    /// the same hop is a few pixels zoomed out and half a screen zoomed in - so one arrow per
    /// hop would be a solid line of them in one place and nothing in another.
    /// </remarks>
    private void Chevrons(ImDrawListPtr draw, Vector2 from, Vector2 to, uint colour, float size)
    {
        Vector2 along = to - from;
        float length = along.Length();
        float gap = size * ChevronGap;
        if (length < 0.01f || gap <= 0f)
        {
            return;
        }

        // Which of the routes on this stretch this one is. The share decides where its arrows
        // start, so two routes on one connection interleave instead of overprinting.
        (Vector2, Vector2) stretch = Stretch(from, to);
        int crowd = _busy.TryGetValue(stretch, out int many) ? Math.Max(1, many) : 1;
        int mine = _drawn.TryGetValue(stretch, out int taken) ? taken : 0;
        _drawn[stretch] = mine + 1;

        Vector2 direction = along / length;
        float phase = gap * (mine % crowd) / crowd;

        // From HALF a gap in, so a short stretch still gets one rather than putting it on the
        // node at the end - which is where the next stretch's first arrow lands anyway.
        for (float at = (gap * 0.5f) + phase; at < length; at += gap)
        {
            Chevron(draw, from + (direction * at), direction, colour, size);
        }
    }

    /// <summary>One arrow, pointing the way the route runs.</summary>
    private void Chevron(ImDrawListPtr draw, Vector2 at, Vector2 direction, uint colour, float size)
    {
        var reach = new Vector2(size, size);
        if (!Fits(at - reach, at + reach))
        {
            return;
        }

        var side = new Vector2(-direction.Y, direction.X);
        Vector2 tip = at + (direction * size);
        Vector2 left = at - (direction * size * 0.6f) + (side * size * 0.8f);
        Vector2 right = at - (direction * size * 0.6f) - (side * size * 0.8f);

        draw.AddTriangleFilled(tip, left, right, colour);
    }

    /// <summary>Draws one map's name, and what is in it underneath.</summary>
    private void DrawLabel(ImDrawListPtr draw, AtlasMark mark, Vector2 cursor)
    {
        if (mark.Name.Length == 0)
        {
            return;
        }

        float alpha = mark.State == AtlasNodeState.Completed ? DoneFade : 1f;
        uint text = ColourOf(mark, alpha);
        uint plate = OverlaySettings.Fade(Style.Colour(StyleCatalogue.Keys.AtlasPlate), alpha);

        // With the names off, the contents start where the name would have been - otherwise
        // they hang below a gap nothing is in.
        Vector2 at = mark.Where + Nudge;
        float below = at.Y;

        if (ShowNames)
        {
            below = DrawName(draw, mark, mark.Name, at, text, plate, alpha);
        }

        if (mark.Contents.Count == 0 || !Style.Visible(StyleCatalogue.Keys.AtlasContent))
        {
            return;
        }

        uint said = OverlaySettings.Fade(Style.Colour(StyleCatalogue.Keys.AtlasContent), alpha);

        float font = Font;

        // The pictures first, as one row, and then whatever had none as lines under it. Mixing
        // the two down a column would put a 20-pixel icon on a line of its own between two
        // sentences, which is taller than both and reads as neither.
        below = DrawArt(draw, mark, below, font, alpha, cursor);

        foreach (AtlasSaid line in mark.Contents)
        {
            if (Drawn(line))
            {
                continue;
            }

            Vector2 wide = Measure(line.Text, font);
            var where = new Vector2(mark.Where.X + Nudge.X - (wide.X * 0.5f), below);

            // Each line asked separately, and the height advanced either way: a map whose name
            // clears the interface but whose contents run under it keeps the lines that fit,
            // and the ones that do not leave a gap where they were rather than shifting the
            // rest up onto the panel.
            if (Fits(where - new Vector2(3f, 1f), where + wide + new Vector2(3f, 1f)))
            {
                draw.AddRectFilled(where - new Vector2(3f, 1f), where + wide + new Vector2(3f, 1f), plate, 3f);
                draw.AddText(ImGui.GetFont(), font, where, said, line.Text);

                // A written line answers for itself, so only its DETAIL is worth a tooltip -
                // and that is the sentence a badge's short name stands for.
                if (line.Detail.Length > 0 && Over(cursor, where - new Vector2(3f, 1f), where + wide))
                {
                    Told = line;
                }
            }

            below += wide.Y + 2f;
        }
    }

    /// <summary>How tall a content picture is drawn, against the writing beside it.</summary>
    /// <remarks>
    /// Bigger than the text on purpose - these are read at a glance across a whole atlas, and
    /// at text height the game's icons are a smudge. Tied to the font rather than fixed so the
    /// size slider moves them with everything else.
    /// </remarks>
    private const float ArtScale = 1.8f;

    /// <summary>The gap between two pictures in a row.</summary>
    private const float ArtGap = 2f;

    /// <summary>
    /// Whether a content is drawn as a picture, and so needs no line of its own.
    /// </summary>
    /// <remarks>
    /// Asked twice per content per frame - once to lay the row out, once to skip the line - and
    /// both answers must agree, or a content is either drawn twice or not at all.
    /// </remarks>
    private bool Drawn(AtlasSaid said) => Picture(said.Icon) != IntPtr.Zero;

    /// <summary>The picture for an art name, asked at most once a frame per name.</summary>
    private IntPtr Picture(string name)
    {
        if (!Icons || name.Length == 0 || Art is not { } art)
        {
            return IntPtr.Zero;
        }

        if (_pictures.TryGetValue(name, out IntPtr known))
        {
            return known;
        }

        IntPtr texture = art(name);
        _pictures[name] = texture;
        return texture;
    }

    /// <summary>
    /// The row of pictures under a map's name, and where the next line starts.
    /// </summary>
    /// <remarks>
    /// CENTRED ON THE NODE like everything else here, and measured whole before any of it is
    /// drawn: a row laid out left to right from the node's centre would sit to the right of
    /// every map it belongs to.
    ///
    /// The row is asked to fit as ONE rectangle rather than icon by icon. Unlike the text
    /// lines, half a row is not a partial answer - it is a map that appears to contain two
    /// things when it contains four.
    /// </remarks>
    private float DrawArt(ImDrawListPtr draw, AtlasMark mark, float top, float font, float alpha, Vector2 cursor)
    {
        if (!Icons || Art is null)
        {
            return top;
        }

        float size = font * ArtScale;
        float wide = 0f;
        int many = 0;

        foreach (AtlasSaid said in mark.Contents)
        {
            if (Picture(said.Icon) != IntPtr.Zero)
            {
                wide += size + ArtGap;
                many++;
            }
        }

        if (many == 0)
        {
            return top;
        }

        wide -= ArtGap;
        float left = mark.Where.X + Nudge.X - (wide * 0.5f);
        if (!Fits(new Vector2(left, top), new Vector2(left + wide, top + size)))
        {
            return top;
        }

        // Faded with the map rather than drawn at full strength: a finished map's pictures are
        // the loudest thing left on it otherwise, and the whole point of the fade is that a
        // finished map recedes.
        uint tint = OverlaySettings.Fade(0xFFFF_FFFF, alpha);

        foreach (AtlasSaid said in mark.Contents)
        {
            IntPtr texture = Picture(said.Icon);
            if (texture == IntPtr.Zero)
            {
                continue;
            }

            var corner = new Vector2(left, top);
            var opposite = new Vector2(left + size, top + size);
            draw.AddImage(texture, corner, opposite, Vector2.Zero, Vector2.One, tint);

            if (Over(cursor, corner, opposite))
            {
                Told = said;
            }

            left += size + ArtGap;
        }

        return top + size + ArtGap;
    }

    /// <summary>Whether the cursor is inside a rectangle.</summary>
    private static bool Over(Vector2 cursor, Vector2 from, Vector2 to)
        => cursor.X >= from.X && cursor.X <= to.X && cursor.Y >= from.Y && cursor.Y <= to.Y;

    /// <summary>Draws the name plate, and says where the next line down starts.</summary>
    /// <remarks>
    /// The rating rides ON THE PLATE, after the name, rather than on a line of its own. It is
    /// one or two characters and it is read together with the name - "is this worth running" is
    /// asked about a map, not about a row - so a second line would put an answer somewhere the
    /// question was not.
    ///
    /// TWO BORDERS, on purpose, and they say different things: the group is what somebody asked
    /// to be shown, the biome is what the map IS. So the group keeps the plate's own edge and
    /// the biome rings it from outside, clear of it - which is where the reference draws its
    /// biome border too. Sharing one edge would mean the setting somebody switched on quietly
    /// hides the fact underneath it.
    /// </remarks>
    private float DrawName(
        ImDrawListPtr draw, AtlasMark mark, string title, Vector2 middle, uint text, uint plate, float alpha)
    {
        float font = Font;
        Vector2 size = Measure(title, font);

        // The pill's width is taken BEFORE the plate is placed, so the whole thing stays
        // centred on the node. Measured and then added to the middle instead, the name would
        // shift left every time a map turned out to have a rating.
        // A pill on every map while a scale is in force, INCLUDING the ones nobody has rated.
        // "No pill" would otherwise mean two different things - the ratings are off, or this map
        // has not been judged - and the second is the one worth seeing: it is a map to go and
        // form an opinion about, and it looks identical to a map somebody deliberately skipped.
        string rated = mark.BestRating <= 0 ? string.Empty
            : mark.Rating is int worth ? worth.ToString(CultureInfo.InvariantCulture)
            : Unrated;

        Vector2 pillText = rated.Length > 0 ? Measure(rated, font) : Vector2.Zero;

        // AS WIDE AS THE WIDEST RATING rather than as wide as its own number, so every pill on
        // the atlas is the same shape. Hugging the text makes a "7" pill visibly smaller than a
        // "10" one, and a size that means nothing reads as though it means something.
        float inside = rated.Length > 0 ? MathF.Max(pillText.X, Measure(Widest, font).X) : 0f;

        // The gap is between the NAME and the pill's edge, not between the name and its number.
        // Counted from the number, the padding ate the gap and the pill sat against the last
        // letter - which is what it did.
        float pillWide = rated.Length > 0 ? PillGap + inside + (PillPad * 2f) : 0f;

        Vector2 at = middle - (new Vector2(size.X + pillWide, size.Y) * 0.5f);
        var pad = new Vector2(5f, 2f);
        Vector2 from = at - pad;
        Vector2 to = at + size + new Vector2(pillWide, 0f) + pad;

        // The height is returned WHETHER OR NOT the plate is drawn, so the contents underneath
        // stay where they belong. Moving them up into the space a skipped name left would put
        // them exactly where the name was refused.
        if (!Fits(from, to))
        {
            return at.Y + size.Y + 3f;
        }

        if (AtlasBiomes.Of(mark.Biome) is { } biome)
        {
            // Outside the plate by the ring's own width, so the ring lies entirely clear of the
            // group's border rather than half on top of it. Rounded wider to match, or the
            // corners cut across the plate's.
            var beyond = new Vector2(BiomeRing, BiomeRing);
            draw.AddRect(
                from - beyond,
                to + beyond,
                OverlaySettings.Fade(biome.Colour, alpha),
                3f + BiomeRing,
                ImDrawFlags.RoundCornersAll,
                BiomeRing);
        }

        draw.AddRectFilled(from, to, plate, 3f);

        // A group's colour goes on the EDGE, not on the text. Group colours are chosen to be
        // told apart rather than to be read at ten pixels - as text half of them are illegible
        // on a dark plate, and as a border every one of them is obvious.
        if (mark.Group is { } group)
        {
            uint edge = OverlaySettings.Fade(OverlaySettings.ParseColour(group.Colour), alpha);
            if (edge != 0)
            {
                draw.AddRect(from, to, edge, 3f, ImDrawFlags.RoundCornersAll, 2f);
            }
        }

        draw.AddText(ImGui.GetFont(), font, at, text, title);

        if (rated.Length > 0)
        {
            DrawPill(
                draw,
                new Vector2(at.X + size.X + PillGap + PillPad, at.Y),
                inside,
                pillText,
                rated,
                mark,
                font,
                alpha);
        }

        DrawHops(draw, mark, from, at.Y, size.Y, font, alpha);

        return at.Y + size.Y + 3f;
    }

    /// <summary>
    /// How many maps away this one is, on a tab hanging off the left of the plate.
    /// </summary>
    /// <remarks>
    /// IT USED TO BE PART OF THE NAME - "The Copper Citadel (3)" - and that was wrong in two
    /// ways at once. It made the plate wider by however many digits the distance happened to
    /// have, so the same map's label changed width as you ran towards it and the name slid
    /// sideways under its own node; and it read as though the map were called that.
    ///
    /// Off the plate's left edge instead, with an arrow pointing AT the name: the pill says
    /// "three maps to get here", and it belongs to the route rather than to the map. The left
    /// is deliberate - the rating is on the right, and the two answer different questions
    /// ("how far" against "is it worth it"), so they should never be read as one row of numbers.
    ///
    /// Only when there is a distance to give: a map you can enter now is nought hops away, and
    /// a tab saying so on every accessible map is a screenful of noughts.
    /// </remarks>
    private void DrawHops(
        ImDrawListPtr draw, AtlasMark mark, Vector2 plateFrom, float top, float tall, float font, float alpha)
    {
        if (mark.Hops <= 0)
        {
            return;
        }

        string hops = mark.Hops.ToString(CultureInfo.InvariantCulture);
        Vector2 size = Measure(hops, font);

        // The arrow is DRAWN rather than written, and that is not a preference. The overlay
        // builds its font over ImGui's default glyph range, which is Latin-1 and stops well
        // short of "→" - written, it would be an empty box on every routed map. A triangle also
        // matches the chevrons on the route the number belongs to.
        float head = font * 0.3f;
        float wide = size.X + (head * 2.2f);

        var pad = new Vector2(PillPad, 1f);
        var to = new Vector2(plateFrom.X - PillGap, top + tall + pad.Y);
        var from = new Vector2(to.X - wide - (pad.X * 2f), top - pad.Y);

        if (!Fits(from, to))
        {
            return;
        }

        uint ink = OverlaySettings.Fade(ColourOf(mark, 1f), alpha);
        float round = (to.Y - from.Y) * 0.5f;
        draw.AddRectFilled(from, to, OverlaySettings.Fade(HopPlate, alpha), round);
        draw.AddText(ImGui.GetFont(), font, new Vector2(from.X + pad.X, top), ink, hops);

        var point = new Vector2(to.X - pad.X - (head * 1.1f), top + (tall * 0.5f));
        Chevron(draw, point, new Vector2(1f, 0f), ink, head);
    }

    /// <summary>What the distance tab is drawn on - the plate's colour, a shade darker.</summary>
    private const uint HopPlate = 0xD9_0D0D0D;

    /// <summary>How much room the pill leaves around its number, and before it.</summary>
    private const float PillPad = 4f;
    private const float PillGap = 7f;

    /// <summary>How thick the biome ring is, and how far outside the plate it sits.</summary>
    private const float BiomeRing = 2f;

    /// <summary>
    /// The widest rating a pill has to hold, for sizing rather than for showing.
    /// </summary>
    /// <remarks>
    /// Two digits, which is what a scale out of ten needs. Measured rather than assumed at a
    /// pixel count because the writing scales, and taken from digits rather than from the
    /// question mark because the digits are the wider of the two.
    /// </remarks>
    private const string Widest = "00";

    /// <summary>What a map nobody has judged carries instead of a number.</summary>
    /// <remarks>
    /// A question mark rather than a dash or a blank, because it asks the right thing: this is
    /// not a map rated nothing, it is one nobody has looked at - and the difference matters to
    /// whoever is filling the file in.
    /// </remarks>
    private const string Unrated = "?";

    /// <summary>
    /// The rating, as a filled pill from red at the worst to green at the best.
    /// </summary>
    /// <remarks>
    /// A FILLED shape rather than coloured text, because the colour is the whole message: a
    /// number is read one map at a time and a colour is read across the whole atlas at once,
    /// which is what somebody scanning for where to go next is actually doing. The number is
    /// there for when the answer matters rather than the impression.
    ///
    /// Black text on it, always. The ramp runs through yellow, and yellow is where white text
    /// stops being readable - so the one colour that works on all of it is the one that works
    /// on none of the plates, which is why this is the only text here that is not the plate's.
    /// </remarks>
    /// <param name="at">The top left of the pill's inside - its own padding goes outside this.</param>
    /// <param name="inside">
    /// How wide that inside is, which is the widest rating rather than this one. The number is
    /// centred in it, so a one-digit rating sits in the middle of a pill sized for two.
    /// </param>
    private void DrawPill(
        ImDrawListPtr draw,
        Vector2 at,
        float inside,
        Vector2 size,
        string rated,
        AtlasMark mark,
        float font,
        float alpha)
    {
        var pad = new Vector2(PillPad, 1f);
        Vector2 from = at - pad;
        Vector2 to = at + new Vector2(inside, size.Y) + pad;
        float round = (to.Y - from.Y) * 0.5f;
        var where = new Vector2(at.X + ((inside - size.X) * 0.5f), at.Y);

        // AN UNRATED MAP IS NOT A BAD ONE, so its pill is off the ramp entirely: a slate plate
        // with a pale question mark and an outline, rather than any shade of red. Put anywhere
        // on the scale it would be an opinion nobody holds - and at the red end it would be the
        // strongest one in the file.
        if (mark.Rating is not int worth)
        {
            draw.AddRectFilled(from, to, OverlaySettings.Fade(0xC0_2A2A32, alpha), round);
            draw.AddRect(
                from, to, OverlaySettings.Fade(0xFF_7A7A88, alpha), round,
                ImDrawFlags.RoundCornersAll, 1f);
            draw.AddText(ImGui.GetFont(), font, where, OverlaySettings.Fade(0xFF_C8C8D2, alpha), rated);
            return;
        }

        // Against the top of the scale the ratings themselves set, so any scale works: rate out
        // of five and five is green. Guarded, because a file where everything is nought would
        // otherwise divide by it.
        float share = mark.BestRating > 0 ? Math.Clamp((float)worth / mark.BestRating, 0f, 1f) : 0f;

        draw.AddRectFilled(from, to, Worth(share, alpha), round);
        draw.AddText(ImGui.GetFont(), font, where, OverlaySettings.Fade(0xFF10_1010, alpha), rated);
    }

    /// <summary>Red at nothing, amber in the middle, green at the top.</summary>
    /// <remarks>
    /// Through AMBER rather than straight from red to green, which is the difference between a
    /// scale somebody can read a middle value off and one where everything between the ends is
    /// a muddy brown. It is also the one ramp nobody has to be taught.
    /// </remarks>
    private static uint Worth(float share, float alpha)
    {
        (float red, float green) = share < 0.5f
            ? (1f, share * 2f * 0.75f)
            : (1f - ((share - 0.5f) * 2f * 0.85f), 0.75f + ((share - 0.5f) * 2f * 0.05f));

        uint colour = 0xFF00_0000
                      | ((uint)(0.15f * 255f) << 16)
                      | ((uint)(Math.Clamp(green, 0f, 1f) * 255f) << 8)
                      | (uint)(Math.Clamp(red, 0f, 1f) * 255f);

        return OverlaySettings.Fade(colour, alpha);
    }

    /// <summary>How far in from the screen's edge a pointer sits.</summary>
    /// <remarks>
    /// Enough for the pill and its arrow to be whole. Pinned exactly to the edge, half of every
    /// pointer is off the screen and what is left reads as a stray rectangle.
    /// </remarks>
    private const float PointerInset = 34f;

    /// <summary>
    /// Pills on the screen's edge for the routing targets that are off it.
    /// </summary>
    /// <remarks>
    /// WHAT THIS IS FOR: with the atlas scrolled anywhere, most of what a route leads to is off
    /// the screen, and a line running off the edge says only that something is out there. The
    /// pill names the map and how many away it is, in its group's own colour, so the answer to
    /// "is there a citadel near here" does not need the atlas dragged about to find out.
    ///
    /// ONLY WHERE A ROUTE ACTUALLY GOES. A target with no placed route behind it is one the
    /// atlas cannot currently reach - across the fog, or on the other side of the sea - and a
    /// pointer at one is an arrow towards somewhere you cannot walk. The reference learned this
    /// one the hard way and says so in its own settings.
    /// </remarks>
    private void DrawPointers(ImDrawListPtr draw, IReadOnlyList<AtlasMark> marks, Vector2 screen)
    {
        _aimed.Clear();

        var middle = new Vector2(screen.X * 0.5f, screen.Y * 0.5f);
        Vector2 inset = new Vector2(screen.X, screen.Y) * 0.5f - new Vector2(PointerInset, PointerInset);
        if (inset.X <= 0f || inset.Y <= 0f)
        {
            return;
        }

        foreach (AtlasMark mark in marks)
        {
            if (mark.Group is not { } group || mark.Hops <= 0 || mark.Route.Count == 0)
            {
                continue;
            }

            // On the screen already, so its own label is the pointer. The same margin the
            // labels are culled by, so there is never a gap where a map has neither.
            if (On(mark.Where, screen))
            {
                continue;
            }

            Vector2 away = mark.Where - middle;
            if (Beyond(away, screen))
            {
                continue;
            }

            // The nearest of the group wins, and ties go to whichever came first - the marks
            // are in the atlas's own order, so the same map keeps the pointer between frames
            // rather than flickering between two the same distance away.
            if (!_aimed.TryGetValue(group.Name, out AtlasMark? held) || mark.Hops < held.Hops)
            {
                _aimed[group.Name] = mark;
            }
        }

        foreach (AtlasMark mark in _aimed.Values)
        {
            DrawPointer(draw, mark, middle, inset);
        }
    }

    /// <summary>Whether a target is further off the screen than the range allows.</summary>
    /// <remarks>
    /// Measured per axis in screenfuls rather than as a straight distance, because that is how
    /// the atlas is actually scrolled - sideways or up, rarely diagonally - and a diagonal
    /// distance would cut off a target one screen to the left and one screen up while keeping
    /// one that is nearly two screens straight left.
    /// </remarks>
    private bool Beyond(Vector2 away, Vector2 screen)
    {
        if (PointerRange <= 0f)
        {
            return false;
        }

        float screens = MathF.Max(
            screen.X > 0f ? MathF.Abs(away.X) / screen.X : 0f,
            screen.Y > 0f ? MathF.Abs(away.Y) / screen.Y : 0f);

        return screens > PointerRange;
    }

    /// <summary>One pointer, on the edge in the direction of its map.</summary>
    private void DrawPointer(ImDrawListPtr draw, AtlasMark mark, Vector2 middle, Vector2 inset)
    {
        Vector2 away = mark.Where - middle;

        // Where the line from the middle to the map crosses the inset edge. The smaller of the
        // two crossings is the one on the rectangle rather than past its corner.
        float reach = MathF.Min(
            MathF.Abs(away.X) > 0.01f ? inset.X / MathF.Abs(away.X) : float.MaxValue,
            MathF.Abs(away.Y) > 0.01f ? inset.Y / MathF.Abs(away.Y) : float.MaxValue);

        if (reach is float.MaxValue or <= 0f)
        {
            return;
        }

        Vector2 edge = middle + (away * reach);
        Vector2 heading = Vector2.Normalize(away);

        float font = Font;

        // A middle dot rather than an arrow, for the glyph-range reason in DrawHops - and it is
        // the right character anyway: the pill's own arrow, outside it, already points the way,
        // so what is left to say is "this many maps, that one".
        string label = $"{mark.Hops.ToString(CultureInfo.InvariantCulture)} · {mark.Name}";
        Vector2 size = Measure(label, font);

        var pad = new Vector2(PillPad + 1f, 2f);
        Vector2 half = (size * 0.5f) + pad;

        // Pulled back along its own direction by its own size, so the pill lies INSIDE the
        // screen rather than centred on the edge with half of it outside.
        Vector2 centre = edge - (heading * MathF.Max(half.X, half.Y));
        Vector2 from = centre - half;
        Vector2 to = centre + half;

        if (!Fits(from, to))
        {
            return;
        }

        uint colour = ColourOf(mark, 1f);
        draw.AddRectFilled(from, to, Style.Colour(StyleCatalogue.Keys.AtlasPlate), 4f);
        draw.AddRect(from, to, colour, 4f, ImDrawFlags.RoundCornersAll, 1.5f);
        draw.AddText(ImGui.GetFont(), font, centre - (size * 0.5f), colour, label);

        // The arrow OUTSIDE the pill, pointing off the screen the way the map lies. Without it
        // a pill on the top edge and one on the left edge look the same, and which edge a thing
        // is pinned to is not something anybody reads deliberately.
        Chevron(draw, edge - (heading * 6f), heading, colour, MathF.Max(5f, font * 0.4f));
    }

    /// <summary>How far outside the screen a point still counts as being on it.</summary>
    /// <remarks>
    /// A margin rather than the exact edge, so a line whose far end is just past the border
    /// still anchors properly instead of popping in as the atlas is dragged.
    /// </remarks>
    private const float Margin = 64f;

    /// <summary>Whether a point is on the screen, give or take the margin.</summary>
    private static bool On(Vector2 at, Vector2 screen)
        => at.X >= -Margin && at.Y >= -Margin && at.X <= screen.X + Margin && at.Y <= screen.Y + Margin;

    /// <summary>
    /// Whether a segment is worth drawing at all - one end on the screen is enough.
    /// </summary>
    /// <remarks>
    /// A full atlas is a couple of thousand connections and most of them are somewhere else
    /// entirely, so this is what keeps the cost proportional to what is being looked at rather
    /// than to how much of the atlas has been revealed. One end is enough on purpose: a route
    /// that starts off-screen still has to be seen entering.
    /// </remarks>
    private static bool Worth(Vector2 from, Vector2 to, Vector2 screen)
        => On(from, screen) || On(to, screen);

    /// <summary>The size to write at, from the interface's own and the chosen scale.</summary>
    private float Font => ImGui.GetFontSize() * (TextScale > 0f ? TextScale : 1f);

    /// <summary>How big a line is at a size other than the interface's own.</summary>
    /// <remarks>
    /// Scaled from a measurement at the current size, which is what ImGui offers - measuring
    /// at an arbitrary size would mean pushing a font, and a bitmap font scales linearly.
    /// </remarks>
    private static Vector2 Measure(string text, float font)
        => ImGui.CalcTextSize(text) * (font / ImGui.GetFontSize());

    /// <summary>A map's colour: its group's, or the plain label colour when it has no group.</summary>
    private uint ColourOf(AtlasMark mark, float alpha)
    {
        uint chosen = mark.Group is { } group ? OverlaySettings.ParseColour(group.Colour) : 0;
        return OverlaySettings.Fade(
            chosen == 0 ? Style.Colour(StyleCatalogue.Keys.AtlasLabel) : chosen,
            alpha);
    }
}
