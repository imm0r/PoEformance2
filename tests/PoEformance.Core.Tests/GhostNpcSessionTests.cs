using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The capture of the two phantom NPCs (<c>tests/fixtures/session-2026-09-ghostnpc.rec</c>:
/// 4,886 frames, 771 KB, 2026-09-13).
/// </summary>
/// <remarks>
/// WHY IT WAS MADE, and what it settled by NOT showing what it was made to show. A quest NPC
/// was drawn standing at a Vaal chest with nobody there - Alva, whom the game spawns once at a
/// player's first Vaal chest and never again - and the natural reading was that the targetable
/// byte had stopped answering. It had not. The reader has both of these right, and the capture
/// is what proved it: present=False and isPlace=False on Alva and on the Lurking Creature, the
/// 2026-08 phantom the byte was originally verified against, in one file.
///
/// So nothing upstream was wrong, and the marker on the screen was never the place marker. It
/// was the entity DOT, which ran a filter of its own that had never been told about absent
/// places - the same omission the opened-chest clause in that filter was written to fix.
///
/// Pinned here because the reader half is the half a test can reach: the overlay is Windows-only
/// and needs an ImGui draw list, so what stops the dot coming back is a clause in EntityOverlay
/// that this cannot see. What this CAN do is fail the moment the reader stops marking these two
/// absent, which is the input that clause depends on.
/// </remarks>
public class GhostNpcSessionTests
{
    private const string Alva = "Metadata/NPC/League/Incursion/AlvaIncursionWild";
    private const string Lurking =
        "Metadata/NPC/Hideout/JusticeOracleIdentifier/AbyssJusticeOracleIdentifierWild";

    /// <summary>The first frame of the capture holding each - facts about a frozen file.</summary>
    private const uint LurkingFrame = 5;
    private const uint AlvaFrame = 2171;

    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-ghostnpc.rec");
        }
    }

    /// <summary>
    /// The two NPCs, read from the frames that hold them.
    /// </summary>
    /// <remarks>
    /// SEEKING TWO FRAMES RATHER THAN WALKING 4,886. The fixture is frozen, so the frame a
    /// given entity first appears in is a fact about the file and not a guess - and scanning
    /// the whole capture to rediscover it each run cost a minute of the suite for an answer
    /// that cannot change. If a frame ever stops holding its entity, the emptiness assertions
    /// below say so.
    /// </remarks>
    private static List<WorldEntity> Ghosts()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.LiveSchema();
        var found = new List<WorldEntity>();

        foreach (uint frame in (uint[])[LurkingFrame, AlvaFrame])
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = new WorldReader(replay, schema).Read(
                replay.ResolvedStatics["GameStates"]);

            foreach (WorldEntity entity in snapshot.Entities)
            {
                if (entity.Path == Alva || entity.Path == Lurking)
                {
                    found.Add(entity);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Both NPCs are read as absent.
    /// </summary>
    /// <remarks>
    /// The assertion that matters is <c>Present == false</c> rather than IsPlace: IsPlace is
    /// derived from it and would keep passing if the derivation were reversed, while this is
    /// the byte the game actually answered with. Both are checked so a change to either shows.
    /// </remarks>
    [Fact]
    public void TheGameSaysNeitherNpcIsThere()
    {
        List<WorldEntity> ghosts = Ghosts();

        // Not vacuous: a capture that stopped containing them, or a reader that stopped
        // listing them, would satisfy every assertion below by never entering the loop.
        Assert.Contains(ghosts, g => g.Path == Alva);
        Assert.Contains(ghosts, g => g.Path == Lurking);

        foreach (WorldEntity ghost in ghosts)
        {
            Assert.False(ghost.Present, $"{ghost.Path} read as present");
            Assert.False(ghost.IsPlace, $"{ghost.Path} counted as a place");

            // The game marks them all the same, which is why neither the icon nor the kind can
            // be what tells them apart from an NPC who is really standing there.
            Assert.Equal("NPC", ghost.MapIcon);
            Assert.Equal(PoiKind.Npc, ghost.Poi);
            Assert.Equal(EntityKind.Npc, ghost.Kind);
        }
    }
}
