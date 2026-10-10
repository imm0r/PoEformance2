using System.Globalization;
using System.Text;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.Items;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Every monster the capture's world read listed, with what the game has PUT on it: its mods, its
/// buffs, its targetable byte - and every marked place beside them.
/// </summary>
/// <remarks>
/// THE QUESTION IT WAS WRITTEN FOR. An essence-imprisoned monster reads as a rare with a full bar
/// and a targetable byte, and the autocast rules count it like any other - a Spark rule fired at
/// a prison the moment the cursor came near. Nothing in its component list says it cannot be hit.
/// If the game says so on the monster at all, it is as a mod or a buff, and the data files hold
/// one candidate: EncasedMonsterNoActionSpeed, a nameless mod worth action_speed -100%. Which it
/// is, and whether it leaves when the essence is clicked, is two captures - one before the click
/// and one after - held side by side. The Entity Browser answers the same question one monster at
/// a time; this answers it for every monster in the area at once, and keeps the answer.
///
/// FROM THE PASS, NOT THE FRAME. The overlay's own read leaves monster buffs off until something
/// asks and reads them for rares and up; the pass reads the world with every switch on, so every
/// monster here carries its buffs. The mods are read here and nowhere else in the tool: a vector
/// walk per monster, which the pass pays once and the per-frame read never should.
///
/// THE MARKED PLACES ARE THE OTHER HALF. The map draws the essence icon off a SECOND entity at the
/// monster's position, the prison object, which carries the MinimapIcon the monster does not. So a
/// monster and the marked place standing on it are listed together, with the place's own
/// targetable byte - whether the game says it is still there.
/// </remarks>
public static class CaptureMonsters
{
    /// <summary>How near a marked place must stand to a monster to be listed under it, in world units.</summary>
    /// <remarks>
    /// A prison stands ON its monster - the two icons sit on one spot - so anything larger than a
    /// monster's own footprint is enough, and a hundred leaves room for a model's centre and its
    /// feet disagreeing by a step.
    /// </remarks>
    public const float PlaceReach = 100f;

    /// <summary>The report: every monster nearest first, then every marked place nearest first.</summary>
    /// <param name="reader">The recording's reader, so the mod walk lands in it.</param>
    /// <param name="schema">The offsets.</param>
    /// <param name="snapshot">The pass's fresh world read.</param>
    /// <param name="names">The item names, for what the game calls a mod - or null, and ids stand alone.</param>
    public static string Report(IMemoryReader reader, OffsetSchema schema, WorldSnapshot snapshot, ItemNames? names)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(snapshot);

        float px = snapshot.Player?.WorldX ?? 0f;
        float py = snapshot.Player?.WorldY ?? 0f;
        var entities = new EntityReader(reader, schema);
        var mods = new ModListReader(reader, schema);

        List<WorldEntity> monsters = [.. snapshot.Entities.Where(one => one.Kind == EntityKind.Monster).OrderBy(one => Distance(one, px, py))];
        List<WorldEntity> places = [.. snapshot.Entities.Where(one => one.Kind != EntityKind.Monster && one.MapIcon.Length > 0).OrderBy(one => Distance(one, px, py))];

        var said = new StringBuilder();
        said.Append(Say(monsters.Count)).Append(" monsters among ").Append(Say(snapshot.Entities.Count))
            .AppendLine(" entities, read fresh inside the recording with every switch on, nearest the player first; distance and position in world units");
        said.AppendLine("mods are the game's own ids off ObjectMagicProperties, named where its Mods table names them; buffs carry their clocks; targetable is the game's own byte");
        said.Append("a marked place within ").Append(Say((int)PlaceReach)).AppendLine(" of a monster is listed under it - an essence prison stands on its monster and carries the icon the monster does not");
        said.AppendLine();

        foreach (WorldEntity monster in monsters)
        {
            said.Append("### ").Append(Num(Distance(monster, px, py))).Append("  ").Append(monster.Rarity).Append(' ')
                .Append(monster.Name.Length > 0 ? monster.Name : monster.FileName).Append("  (").Append(monster.Path).Append(")  id ")
                .Append(Say((int)monster.Id)).Append("  at 0x").AppendLine(monster.Address.ToString("X", CultureInfo.InvariantCulture));
            said.Append("  position ").Append(Num(monster.WorldX)).Append(' ').Append(Num(monster.WorldY)).Append(' ').Append(Num(monster.WorldZ))
                .Append("   life ").Append(monster.Life.IsValid ? $"{Say(monster.Life.Current)}/{Say(monster.Life.Max)} ({Say(monster.Life.Percent)}%)" : "unread")
                .Append("   targetable ").Append(Said(monster.Targetable))
                .Append("   friendly ").Append(monster.IsFriendly ? "yes" : "no")
                .Append("   effect ").AppendLine(monster.IsEffect ? "yes" : "no");

            said.Append("  mods: ").AppendLine(Mods(entities, mods, monster.Address, names));
            said.Append("  buffs: ").AppendLine(Buffs(monster.Buffs));

            foreach (WorldEntity place in places)
            {
                if (Distance(place, monster.WorldX, monster.WorldY) <= PlaceReach)
                {
                    said.Append("  marked place on it: ").Append(Place(place, px, py)).AppendLine();
                }
            }
        }

        said.AppendLine();
        said.Append("=== ").Append(Say(places.Count)).AppendLine(" marked places - every entity the game gives a map icon, nearest the player first");
        said.AppendLine("distance\ticon\tpresent\tid\taddress\tx\ty\tz\tpath\tname");
        foreach (WorldEntity place in places)
        {
            said.Append(Num(Distance(place, px, py))).Append('\t').Append(place.MapIcon).Append('\t').Append(Said(place.Present))
                .Append('\t').Append(Say((int)place.Id)).Append("\t0x").Append(place.Address.ToString("X", CultureInfo.InvariantCulture))
                .Append('\t').Append(Num(place.WorldX)).Append('\t').Append(Num(place.WorldY)).Append('\t').Append(Num(place.WorldZ))
                .Append('\t').Append(place.Path).Append('\t').AppendLine(place.Name);
        }

        return said.ToString();
    }

    /// <summary>One line for the mods on a monster, or which kind of nothing was found.</summary>
    /// <remarks>
    /// The three kinds kept apart, as the browser keeps them: "no such component", "a component
    /// with no mods on it" and "an entity that did not read" each want a different next step,
    /// and all three used to look like a monster with nothing on it.
    /// </remarks>
    private static string Mods(EntityReader entities, ModListReader mods, ulong address, ItemNames? names)
    {
        Entity? entity = entities.Read(address);
        if (entity is null)
        {
            return "(the entity did not read)";
        }

        ulong magic = entity.Component("ObjectMagicProperties");
        if (magic == 0)
        {
            return "(no ObjectMagicProperties)";
        }

        IReadOnlyList<string> ids = mods.Ids(magic);
        if (ids.Count == 0)
        {
            return "(none readable on its ObjectMagicProperties)";
        }

        var line = new StringBuilder();
        foreach (string id in ids)
        {
            if (line.Length > 0)
            {
                line.Append(", ");
            }

            line.Append(id);
            if (names?.Mod(id) is { Name.Length: > 0 } called)
            {
                line.Append(" \"").Append(called.Name).Append('"');
            }
        }

        return line.ToString();
    }

    private static string Buffs(ActiveBuffs? buffs)
    {
        if (buffs is null)
        {
            return "(not read)";
        }

        if (buffs.All.Count == 0)
        {
            return "(none)";
        }

        var line = new StringBuilder();
        foreach (ActiveBuff buff in buffs.All)
        {
            if (line.Length > 0)
            {
                line.Append(", ");
            }

            line.Append(buff.Name);
            bool finite = float.IsFinite(buff.TimeLeft) && float.IsFinite(buff.TotalTime);
            line.Append(finite && buff.TimeLeft > 0f
                ? $" ({buff.TimeLeft.ToString("0.0", CultureInfo.InvariantCulture)} of {buff.TotalTime.ToString("0.0", CultureInfo.InvariantCulture)}s)"
                : " (permanent)");
            if (buff.Charges > 0)
            {
                line.Append(" x").Append(Say(buff.Charges));
            }
        }

        return line.ToString();
    }

    private static string Place(WorldEntity place, float px, float py)
        => $"{place.MapIcon}  present {Said(place.Present)}  id {Say((int)place.Id)} at 0x{place.Address.ToString("X", CultureInfo.InvariantCulture)}  {Num(Distance(place, px, py))} from the player  ({place.Path})";

    private static string Said(bool? flag) => flag switch { true => "yes", false => "no", null => "unread" };

    private static float Distance(WorldEntity one, float x, float y) => MathF.Sqrt(((one.WorldX - x) * (one.WorldX - x)) + ((one.WorldY - y) * (one.WorldY - y)));

    private static string Num(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);
}
