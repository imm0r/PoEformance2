using System.Buffers.Binary;
using System.Text;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// Lays a player's granted-skill table into fake memory, at every offset the schema names.
/// </summary>
/// <remarks>
/// Through the schema rather than with constants, for the reason <see cref="UiTree"/> gives.
/// Shared between the table's own tests and the two readers that match pointers against it.
///
/// Each skill's dat row is reachable the way the test asks - straight from the object, through
/// the GrantedEffectsPerLevel chain at either column, through a GrantedEffects row sitting where
/// the per-level row was said to be, only by a search of the object, or not at all - because
/// which way the game offers is exactly what the reader has to settle.
/// </remarks>
internal static class SkillTableFixture
{
    /// <summary>Where the table's parts are laid, well away from anything a UiTree places.</summary>
    private const ulong Entries = 0x0000_0500_1000_0000;
    private const ulong Rows = 0x0000_0500_3000_0000;
    private const ulong Granted = 0x0000_0500_3100_0000;
    private const ulong PerLevel = 0x0000_0500_3200_0000;
    private const ulong Texts = 0x0000_0500_4000_0000;

    /// <summary>Longer than anything a reader asks for, so a string read never runs off the end.</summary>
    private const int TextBytes = 512;

    /// <summary>Where a skill object keeps its row pointer when it has to be hunted for.</summary>
    public const int HuntedAt = 0x38;

    /// <summary>Where an ActiveSkills row keeps its name when it is not where the schema says.</summary>
    public const int NameElsewhereAt = 0x18;

    /// <summary>How a skill object leads to its dat row.</summary>
    public enum Route
    {
        /// <summary>The direct pointer the references name and do not use.</summary>
        Direct,

        /// <summary>GrantedEffectsPerLevel, then GrantedEffects, then the computed ActiveSkill column.</summary>
        ThroughGrantedEffects,

        /// <summary>The same chain, with the ActiveSkill column where GameHelper2 reads it.</summary>
        ThroughGrantedEffectsPerReference,

        /// <summary>A GrantedEffects row where the references say the per-level row is - one hop shorter.</summary>
        GrantedEffectsRowInPlaceOfPerLevel,

        /// <summary>A direct pointer at <see cref="HuntedAt"/>, where no known place holds one.</summary>
        Hunted,

        /// <summary>Direct, to a row whose name sits at <see cref="NameElsewhereAt"/> rather than the schema's column.</summary>
        NameElsewhere,
    }

    /// <summary>One skill as the table would hold it.</summary>
    /// <param name="Details">The skill object's address - what a slot or a row points at.</param>
    /// <param name="Id">The dat row's id, or empty for a skill whose row is not reachable at all.</param>
    /// <param name="Name">The dat row's DisplayedName - what the Skills panel prints.</param>
    /// <param name="Via">How the object leads to the row.</param>
    public readonly record struct Skill(ulong Details, string Id, string Name = "", Route Via = Route.Direct);

    /// <summary>
    /// Places an actor whose ActiveSkills vector holds these skills, and returns the actor.
    /// </summary>
    public static ulong Place(FakeMemoryReader fake, OffsetSchema schema, ulong actor, params Skill[] skills)
    {
        ArgumentNullException.ThrowIfNull(fake);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(skills);

        int table = schema.Structs["Actor"].OffsetOf("ActiveSkills");
        StructDef entry = schema.Structs["ActiveSkillStructure"];
        int entrySize = (int)entry.Constants["Size"];
        int detailsAt = entry.OffsetOf("ActiveSkillPtr");

        StructDef details = schema.Structs["ActiveSkillDetails"];
        int datRow = details.OffsetOf("ActiveSkillsDatPtr");
        int perLevel = details.OffsetOf("GrantedEffectsPerLevelDatRow");
        int grantedEffect = schema.Structs["GrantedEffectsPerLevelDat"].OffsetOf("GrantedEffect");
        StructDef grantedDat = schema.Structs["GrantedEffectsDat"];
        int activeSkill = grantedDat.OffsetOf("ActiveSkill");
        int activeSkillPerReference = (int)grantedDat.Constants["ActiveSkillPerGameHelper2"];
        int displayedName = schema.Structs["ActiveSkillsDat"].OffsetOf("DisplayedName");

        fake.Place<ulong>(actor + (ulong)table, Entries);
        fake.Place<ulong>(actor + (ulong)(table + 8), Entries + (ulong)(skills.Length * entrySize));

        // The whole table in one block, because that is how it is read: an entry's second
        // pointer is nothing the reader wants and still has to be there to be read past.
        if (skills.Length > 0)
        {
            var entries = new byte[skills.Length * entrySize];
            for (int i = 0; i < skills.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    entries.AsSpan((i * entrySize) + detailsAt), skills[i].Details);
            }

            fake.Place(Entries, entries);
        }

        for (int i = 0; i < skills.Length; i++)
        {
            Skill skill = skills[i];

            // The object exists as a whole first, so a search of it reads, and so the known
            // places read as nothing rather than as unplaced memory.
            fake.Place(skill.Details, new byte[Game.Components.PlayerSkills.HuntBytes]);
            if (skill.Id.Length == 0)
            {
                continue;
            }

            ulong row = Rows + (ulong)(i * 0x100);
            ulong granted = Granted + (ulong)(i * 0x100);
            ulong level = PerLevel + (ulong)(i * 0x100);
            ulong text = Texts + (ulong)(i * 0x1000);

            PlaceText(fake, text, skill.Id);
            PlaceText(fake, text + 0x400, skill.Name);
            PlaceText(fake, text + 0x800, skill.Id + "Player");

            // The rows exist as wholes too, for the name search, under the fields placed after.
            fake.Place(row, new byte[Game.Components.PlayerSkills.NameHuntBytes]);
            fake.Place(granted, new byte[0x80]);
            fake.Place(level, new byte[0x40]);

            // The ActiveSkills row: id, then displayed name. The GrantedEffects row: its own id,
            // and the ActiveSkills row at the column the route says. The per-level row: the
            // GrantedEffects row first.
            fake.Place<ulong>(row, text);
            int nameAt = skill.Via == Route.NameElsewhere ? NameElsewhereAt : displayedName;
            fake.Place<ulong>(row + (ulong)nameAt, skill.Name.Length > 0 ? text + 0x400 : 0UL);
            fake.Place<ulong>(granted, text + 0x800);
            fake.Place<ulong>(
                granted + (ulong)(skill.Via == Route.ThroughGrantedEffectsPerReference ? activeSkillPerReference : activeSkill),
                row);
            fake.Place<ulong>(level + (ulong)grantedEffect, granted);

            switch (skill.Via)
            {
                case Route.Direct:
                case Route.NameElsewhere:
                    fake.Place<ulong>(skill.Details + (ulong)datRow, row);
                    break;
                case Route.ThroughGrantedEffects:
                case Route.ThroughGrantedEffectsPerReference:
                    fake.Place<ulong>(skill.Details + (ulong)perLevel, level);
                    break;
                case Route.GrantedEffectsRowInPlaceOfPerLevel:
                    fake.Place<ulong>(skill.Details + (ulong)perLevel, granted);
                    break;
                case Route.Hunted:
                    fake.Place<ulong>(skill.Details + HuntedAt, row);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(skills), skill.Via, "no such route");
            }
        }

        return actor;
    }

    private static void PlaceText(FakeMemoryReader fake, ulong at, string text)
    {
        var bytes = new byte[TextBytes];
        Encoding.Unicode.GetBytes(text).CopyTo(bytes, 0);
        fake.Place(at, bytes);
    }
}
