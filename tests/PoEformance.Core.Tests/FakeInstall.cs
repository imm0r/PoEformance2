using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// An installed copy of the game, in memory, holding the stat-description files.
/// </summary>
/// <remarks>
/// SHARED BECAUSE TWO DIFFERENT QUESTIONS NEED IT. One is whether the .csd format parses at all
/// (StatDescriptionFilesTests); the other is whether the atlas CHECK runs to the end with an
/// install-backed table in force (AtlasObjectiveSessionTests) - which is the shape a machine with
/// the game running has, and the one a check with no install never reaches.
///
/// Built from <see cref="Packed"/>, so it is a real archive holding real bundles under a real
/// index with the spelled-out paths at the end. Nothing here shortcuts the four formats.
/// </remarks>
internal static class FakeInstall
{
    /// <summary>The general file, whose wordings win wherever a stat appears twice.</summary>
    public const string GeneralCsd = """
        description
        	1 map_num_extra_shrines
        		1 1 "Area contains an additional Shrine"
        description
        	1 base_maximum_life
        		1 # "{0} to maximum Life"
        description
        	2 heat_consumption_amount heat_extra
        		1 # "Damage Gained as Fire on {0} Heat Consumption@{1}%"
        """;

    /// <summary>A specific file, which says something else about a stat the general one covers.</summary>
    public const string SkillsCsd = """
        description
        	1 map_num_extra_shrines
        		1 1 "this skill's own wording, which must not win"
        description
        	1 skill_only_stat
        		1 # "Skill does {0} things"
        """;

    /// <summary>A .csd file as the game writes them: UTF-16 with a byte-order mark.</summary>
    public static byte[] Utf16(string text)
        => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    /// <summary>An install holding two .csd files, spelled-out paths and all.</summary>
    /// <remarks>
    /// The general file is listed SECOND on purpose: which of the two decides a shared stat's
    /// wording must come from the read's own sorting rather than from walk order.
    /// </remarks>
    public static GameFiles? Open()
    {
        byte[] general = Utf16(GeneralCsd);
        byte[] skills = Utf16(SkillsCsd);

        var content = new byte[8192];
        general.CopyTo(content, 0);
        skills.CopyTo(content, 4096);

        var archive = new Archive();
        archive.Add("data.bundle.bin", Packed.Bundle(content, chunkSize: 512));
        archive.Add("_.index.bin", Packed.Bundle(
            Packed.Index(
                ["data"],
                [
                    new("data/statdescriptions/stat_descriptions.csd", 0, 0, general.Length),
                    new("data/statdescriptions/skills/skill_stat_descriptions.csd", 0, 4096, skills.Length),
                ],
                paths: Packed.Paths(
                    ["data/statdescriptions/"],
                    [(0, "stat_descriptions.csd"), (0, "skills/skill_stat_descriptions.csd")])),
            chunkSize: 512));

        return GameFiles.Open(archive, Packed.AsIs);
    }

    /// <summary>The archive under it: a dictionary of paths, read whole or in ranges.</summary>
    private sealed class Archive : IGameArchive
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool Ready => true;

        public string Describe => "a made-up install";

        public void Add(string path, byte[] bytes) => _files[path] = bytes;

        public byte[]? Read(string path) => _files.GetValueOrDefault(path);

        public byte[]? Read(string path, int at, int length)
            => !_files.TryGetValue(path, out byte[]? bytes) || at < 0 || length < 0
               || at + (long)length > bytes.Length
                ? null
                : bytes[at..(at + length)];
    }
}
