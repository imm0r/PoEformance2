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
/// </remarks>
internal static class SkillTableFixture
{
    /// <summary>Where the table's entries are laid, well away from anything a UiTree places.</summary>
    private const ulong Entries = 0x0000_0500_1000_0000;
    private const ulong Rows = 0x0000_0500_3000_0000;
    private const ulong Texts = 0x0000_0500_4000_0000;

    /// <summary>Longer than anything a reader asks for, so a string read never runs off the end.</summary>
    private const int TextBytes = 512;

    /// <summary>One skill as the table would hold it.</summary>
    /// <param name="Details">The skill object's address - what a slot or a row points at.</param>
    /// <param name="Id">The dat row's id, or empty for a skill whose row is not reachable.</param>
    public readonly record struct Skill(ulong Details, string Id);

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
        int datRow = schema.Structs["ActiveSkillDetails"].OffsetOf("ActiveSkillsDatPtr");

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
            if (skill.Id.Length == 0)
            {
                fake.Place<ulong>(skill.Details + (ulong)datRow, 0UL);
                continue;
            }

            ulong row = Rows + (ulong)(i * 0x100);
            ulong text = Texts + (ulong)(i * 0x400);
            fake.Place<ulong>(skill.Details + (ulong)datRow, row);
            fake.Place<ulong>(row, text);

            var bytes = new byte[TextBytes];
            Encoding.Unicode.GetBytes(skill.Id).CopyTo(bytes, 0);
            fake.Place(text, bytes);
        }

        return actor;
    }
}
