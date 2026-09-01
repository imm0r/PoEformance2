using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Rings the ground effects a person has said they want to see coming.
/// </summary>
/// <remarks>
/// PORTED FROM the GameHelper2 Tracker plugin's GroundEffectLogic. Its value is the half of the
/// game that is hard to see: a patch of damaging ground under a fight is drawn as one more
/// bright thing among the spell effects, and the first sign of it is usually the health bar.
///
/// WHY IT CAN DRAW NOTHING WHILE BEING CONFIGURED CORRECTLY, which is the question this feature
/// generates: the entities it rings have to be in the snapshot, and the reader drops two classes
/// of them on purpose. A hostile thing that expires on its own and cannot be targeted is
/// reclassified and dropped unless <see cref="WorldReader.KeepEffects"/> is on - that rule is
/// what stops a Firewall build covering its own screen - and the noise filter refuses the
/// engine's <c>/fx/</c> and <c>/mat/</c> nodes before their components are read at all. The
/// tracker's tab says both, next to the switch.
///
/// TWO WAYS OF FINDING THE GROUND, kept apart because they are not the same claim. The RULES
/// match a metadata path somebody typed and ring it at a radius in SCREEN PIXELS - the
/// reference's figures are pixel figures, and a pixel ring does not shrink as the camera pulls
/// back. The COMPONENT path rings whatever carries a GroundEffect, at a radius in WORLD units
/// read out of the game and projected onto the floor, which does. See DrawKnownGroundEffects.
///
/// THEY MUST NOT BOTH FIRE ON ONE ENTITY, and for a while they did. The shipped rule is spelled
/// as the EXACT path that carries a GroundEffect component in the sweep capture, so with both
/// switches on the DEFAULT configuration drew every tagged patch twice: once as a projected
/// world ring with an X and a countdown, once as a flat pixel circle of whatever size the rule
/// happened to carry. Two rings of different sizes on one patch, which is exactly the "I cannot
/// tell what these circles are" this feature is supposed to end.
///
/// THE RULE WINS, and it is worth saying why that survived the correction below. The component
/// identifies the KIND of ground precisely - better than any typed path can - so the first
/// instinct was to let it win. But a rule is an explicit instruction somebody wrote down, with a
/// colour and a size they chose, and silently overriding it is how a setting becomes a lie. The
/// component pass keeps every entity no rule speaks for, which is nearly all of them. See
/// TrackerSettings.ComponentShouldRing.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GroundDangerLayer
{
    /// <summary>Most rings drawn in one frame. A backstop on a count that is unbounded.</summary>
    /// <remarks>
    /// A prefix typed one character short - <c>Metadata/Effects</c> rather than the whole path -
    /// matches every effect entity in the area, and there are hundreds. The cap turns that from
    /// a frame nobody can afford into a screen somebody can see is wrong.
    /// </remarks>
    private const int MostRings = 300;

    /// <summary>How many segments a ring is drawn with. 0 lets ImGui choose from the radius.</summary>
    private const int Segments = 0;

    /// <summary>Points around a projected ground ring. Enough that an ellipse reads as smooth.</summary>
    private const int RingSegments = 48;

    /// <summary>Where the quartering arms start and stop, as a fraction of the ring.</summary>
    private const float ArmInner = 0.18f;
    private const float ArmOuter = 0.97f;

    /// <summary>Stroke width as a fraction of the ring's own size on screen.</summary>
    private const float StrokeOfSpan = 0.018f;

    /// <summary>How much wider the soft outer pass is drawn than the bright one.</summary>
    private const float GlowWidth = 2.8f;

    /// <summary>The projected ring, reused every frame rather than allocated per effect.</summary>
    private readonly Vector2[] _ring = new Vector2[RingSegments];

    /// <summary>What to ring, and how. Shared with the tracker's other layers.</summary>
    public TrackerSettings Settings { get; set; } = TrackerSettings.Default;

    /// <summary>The game's own names for the kinds of ground, when the install could be read.</summary>
    /// <remarks>
    /// Optional by design. Without it the label falls back to the entity path, which is where
    /// this feature started and is still better than nothing; with it the label says what a
    /// patch actually IS, because the path is the same generic string on every ground effect
    /// the project has ever recorded.
    /// </remarks>
    public GroundEffectTypeTable? GroundTypes { get; set; }

    /// <summary>Draws a ring on every entity a rule is watching for.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        int drawn = DrawKnownGroundEffects(draw, snapshot, width, height);

        if (!Settings.ShowGroundDanger)
        {
            return;
        }

        IReadOnlyList<GroundDangerRule> rules = Settings.GroundDangerOrDefault;
        if (rules.Count == 0)
        {
            return;
        }

        foreach (WorldEntity entity in snapshot.Entities)
        {
            // Refuses a remembered sighting, and refuses anything the component pass owns. See
            // TrackerSettings.RulesShouldRing - it lives there so it can be tested.
            if (!Settings.RulesShouldRing(entity))
            {
                continue;
            }

            GroundDangerRule? found = null;
            foreach (GroundDangerRule rule in rules)
            {
                if (rule.Matches(entity))
                {
                    found = rule;
                    break;
                }
            }

            if (found is not GroundDangerRule danger)
            {
                continue;
            }

            uint colour = OverlaySettings.ParseColour(danger.Colour);
            if (colour == 0)
            {
                continue;
            }

            // The entity's own base height: this is a ring drawn ON THE GROUND, and the ground
            // is where Render puts it.
            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);

            if (!at.OnScreen)
            {
                continue;
            }

            var centre = new Vector2(at.X, at.Y);
            if (danger.Filled)
            {
                draw.AddCircleFilled(centre, danger.Radius, colour, Segments);
            }
            else
            {
                draw.AddCircle(centre, danger.Radius, colour, Segments, danger.Thickness);
            }

            if (++drawn >= MostRings)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Rings every entity the GAME calls a ground effect, and says how long it has left.
    /// </summary>
    /// <remarks>
    /// WHAT THIS MARKS, after one wrong turn that is worth keeping on the record. It rings every
    /// entity carrying a GroundEffect component, and the component means the game considers this
    /// one of ITS OWN GROUND-EFFECT KINDS - Ignited Ground, Chilled Ground, Caustic Ground. The
    /// row index at +0x48 names which, and every one of the table's 53 rows applies a buff.
    ///
    /// THE WRONG TURN: this was briefly documented as "a decorative decal, nothing to do with
    /// damage", on two pieces of evidence that both collapsed. A screenshot showed a ring on an
    /// Abyssal Arsenal - but its countdown read 0.0 s, and by this project's own measurement a
    /// countdown sits at 0.0 for 0.38 s after expiring, so it was a spent effect rather than a
    /// harmless one. And 5880 of 5916 readings were attributed to a hideout - but those frames
    /// carry area level 0 and area hash 0, which is a LOADING state where the name is still the
    /// previous area's. They were never hideout decorations. Two mis-readings pointing the same
    /// way felt like corroboration; neither was evidence.
    ///
    /// A THIRD OF THEM HAVE NO NUMBER, and the ring is drawn anyway. 33 of 72 entities held NaN
    /// in the countdown slot for their whole life and 39 held a real one, with no entity crossing
    /// between the two - so an absent timer is a kind of effect rather than a failed read.
    ///
    /// THE TIMER READS 0.0 FOR A BEAT before the ring goes. That is not a rounding artefact -
    /// the game's countdown reaches zero a measured 0.38 s before it stops listing the entity.
    /// Left visible rather than hidden below some floor, because "about to be safe" is exactly
    /// the moment the number is worth reading.
    /// </remarks>
    private int DrawKnownGroundEffects(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        if (!Settings.ShowGroundEffects)
        {
            return 0;
        }

        uint colour = OverlaySettings.ParseColour(Settings.GroundEffectColour);
        if (colour == 0)
        {
            return 0;
        }

        int drawn = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            // Remembered sightings are ground that is safe again - the one mistake this
            // feature must not make. Then PRESENCE, not the timer. Gating this on GroundSeconds was the first version and
            // it was wrong: a third of ground effects carry no readable countdown, and those
            // patches burn exactly as much as the timed ones. Ringing only the ones that can be
            // counted down would leave a third of the hazard unmarked - the failure mode this
            // whole feature exists to remove.
            // Refuses a remembered sighting, and stands aside where a RULE is already ringing
            // this entity - see TrackerSettings.ComponentShouldRing for why that way round.
            if (!Settings.ComponentShouldRing(entity))
            {
                continue;
            }

            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);
            if (!at.OnScreen)
            {
                continue;
            }

            float radius = Settings.GroundEffectUseGameRadius && entity.GroundRadius is { } given
                ? given
                : Settings.GroundEffectRadius;

            if (!ProjectRing(snapshot, entity, radius, width, height))
            {
                continue;
            }

            DrawRing(draw, new Vector2(at.X, at.Y), colour, entity.GroundSeconds);

            if (Settings.ShowGroundEffectLabels)
            {
                DrawLabel(draw, new Vector2(at.X, at.Y), colour, entity, radius);
            }

            if (++drawn >= MostRings)
            {
                break;
            }
        }

        return drawn;
    }

    /// <summary>
    /// Projects a circle of world radius around the effect, giving the ring its perspective.
    /// </summary>
    /// <remarks>
    /// A WORLD circle rather than a screen one, and that is the whole difference: a screen
    /// circle is a coin standing on its edge in a game drawn from above, it does not shrink as
    /// the camera pulls back, and it cannot say anything about how much ground is covered. A
    /// projected one lies on the floor, foreshortens exactly as the floor does, and IS a claim
    /// about the area - which is what makes it the experiment that settles the radius: the ring
    /// either hugs the burning patch or it does not, and a screenshot answers it.
    ///
    /// Returns false when any point of the ring is behind the camera. Project reports that as
    /// the exact point (0, 0) marked off screen, which nothing real can be - screen (0, 0) is a
    /// corner of the viewport and would come back ON screen - so the sentinel is safe to test.
    /// </remarks>
    private bool ProjectRing(WorldSnapshot snapshot, WorldEntity entity, float radius, int width, int height)
    {
        if (!float.IsFinite(radius) || radius <= 0)
        {
            return false;
        }

        for (int i = 0; i < RingSegments; i++)
        {
            double angle = i * 2 * Math.PI / RingSegments;
            ScreenPoint p = WorldToScreen.Project(
                snapshot.Matrix,
                entity.WorldX + (float)(radius * Math.Cos(angle)),
                entity.WorldY + (float)(radius * Math.Sin(angle)),
                entity.WorldZ,
                width,
                height);

            if (!p.OnScreen && p.X == 0 && p.Y == 0)
            {
                return false;
            }

            _ring[i] = new Vector2(p.X, p.Y);
        }

        return true;
    }

    /// <summary>The ring, the four arms that quarter it, and the seconds left in the middle.</summary>
    /// <remarks>
    /// THE ARMS ARE PICKED IN SCREEN SPACE, by taking the ring point nearest each screen
    /// diagonal, rather than by stepping four fixed world angles. Those two agree only for one
    /// camera orientation: with world angles the cross came out as a PLUS on the first attempt,
    /// because this game's isometric camera maps the world axes onto the screen diagonals. Doing
    /// it from the projected ring makes the X an X whatever the camera does.
    ///
    /// Nothing is filled. The reference this follows leaves the interior clear so the floor
    /// stays visible through it, which is also what keeps a screen full of these readable during
    /// the fight they turn up in.
    /// </remarks>
    private void DrawRing(ImDrawListPtr draw, Vector2 centre, uint colour, float? seconds)
    {
        // Stroke from the ring's own size on screen, so a patch in the distance is drawn with a
        // proportionate line rather than the same heavy one as a patch underfoot.
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 p in _ring)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        float stroke = Math.Clamp(Math.Max(maxX - minX, maxY - minY) * StrokeOfSpan, 1.5f, 8f);
        uint glow = WithAlpha(colour, 0.22f);

        for (int i = 0; i < RingSegments; i++)
        {
            Vector2 a = _ring[i];
            Vector2 b = _ring[(i + 1) % RingSegments];
            draw.AddLine(a, b, glow, stroke * GlowWidth);
            draw.AddLine(a, b, colour, stroke);
        }

        for (int k = 0; k < 4; k++)
        {
            double want = (Math.PI / 4) + (k * Math.PI / 2);
            var wanted = new Vector2((float)Math.Cos(want), (float)Math.Sin(want));

            Vector2 best = _ring[0];
            float bestDot = -2f;
            foreach (Vector2 p in _ring)
            {
                Vector2 v = p - centre;
                float len = v.Length();
                if (len < 0.001f)
                {
                    continue;
                }

                float dot = Vector2.Dot(v / len, wanted);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = p;
                }
            }

            Vector2 arm = best - centre;
            Vector2 from = centre + (arm * ArmInner);
            Vector2 to = centre + (arm * ArmOuter);
            draw.AddLine(from, to, glow, stroke * GlowWidth);
            draw.AddLine(from, to, colour, stroke);
        }

        if (Settings.ShowGroundEffectTimer && seconds is { } left)
        {
            string text = left.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            Vector2 size = ImGui.CalcTextSize(text);
            draw.AddText(centre - (size / 2), colour, text);
        }
    }

    /// <summary>
    /// Writes what the ring actually is, under it, for working out why it is where it is.
    /// </summary>
    /// <remarks>
    /// A DEBUG AID AND SHAPED LIKE ONE. Two rings on a busy screen are two rings; which patch of
    /// fire each belongs to, and whether a ring is missing entirely, cannot be worked out by
    /// looking at circles. The full metadata path is written rather than the short name because
    /// the path is what a person needs next: to type into a GroundDangerRule, to search a
    /// recording for, or to say "this is not the thing I am standing in".
    ///
    /// THE NUMBERS BESIDE IT ARE THE OPEN QUESTIONS. The radius is printed because it is still a
    /// candidate and a ring drawn at the wrong size is the symptom that settles it; the entity
    /// id because two effects of one kind are otherwise indistinguishable; the countdown because
    /// its absence is a fact about the effect rather than a gap - a third of them never have one.
    ///
    /// Below the ring, not on it: the ring is the thing being judged, and text through the
    /// middle of it is text in the way of the judgement.
    /// </remarks>
    private void DrawLabel(ImDrawListPtr draw, Vector2 centre, uint colour, WorldEntity entity, float radius)
    {
        float bottom = centre.Y;
        foreach (Vector2 p in _ring)
        {
            bottom = Math.Max(bottom, p.Y);
        }

        string detail = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"#{entity.Id}  r={radius:0.##}  {(entity.GroundSeconds is { } left ? $"{left:0.0}s" : "no timer")}");

        // WHAT KIND OF GROUND THIS IS, first, because it is the only line that varies: the path
        // below it is the same generic string on every ground effect on file, so on a screen
        // with three rings it distinguishes nothing.
        //
        // THE BUFF NAME, NOT A BUFF COUNT. Counting them was the first idea and it discriminates
        // nothing - every one of the table's 53 rows applies a buff - where the buff's NAME is
        // the phrase on the player's own screen while they stand in it: "Ignited Ground",
        // "Sacred Ashes". That is the difference between a debug aid and a readable one.
        GroundEffectType? kind = GroundTypes?.Find(entity.GroundType);
        string? type = kind is null
            ? entity.GroundType is { } row ? $"type {row} - not in the table" : null
            : kind.Describe;

        string[] lines = type is null ? [entity.Path, detail] : [type, entity.Path, detail];

        var at = new Vector2(centre.X, bottom + LabelGap);
        foreach (string line in lines)
        {
            Vector2 size = ImGui.CalcTextSize(line);
            var origin = new Vector2(at.X - (size.X / 2), at.Y);

            // A dark plate behind it. Ground effects are bright by nature and this text lands on
            // top of one, where thin glyphs in the effect's own colour are unreadable.
            draw.AddRectFilled(
                origin - new Vector2(3, 1),
                origin + size + new Vector2(3, 1),
                LabelBackground);
            draw.AddText(origin, colour, line);
            at.Y += size.Y + 1;
        }
    }

    /// <summary>Pixels between the bottom of the ring and the first line of the label.</summary>
    private const float LabelGap = 3f;

    /// <summary>The plate drawn behind label text, so it reads over a bright effect.</summary>
    private const uint LabelBackground = 0xC0000000;

    /// <summary>The same colour at a different alpha. ImGui packs alpha in the top byte.</summary>
    private static uint WithAlpha(uint colour, float scale)
    {
        uint alpha = (colour >> 24) & 0xFF;
        return (colour & 0x00FFFFFFu) | ((uint)Math.Clamp(alpha * scale, 0, 255) << 24);
    }
}
