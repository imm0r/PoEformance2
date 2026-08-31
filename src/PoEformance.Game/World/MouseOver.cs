using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>
/// What the cursor is on, asked of the game instead of worked out from geometry.
/// </summary>
/// <remarks>
/// The game keeps the hovered ENTITY in a slot, which this project had written off for years
/// on the strength of a hunt for a hovered UI ELEMENT that found something else. The two
/// questions read alike and are answered in different places: there is still no hovered-element
/// slot, and there has always been a hovered-entity one. See MouseOverHostPtr in the schema for
/// the capture that settled it - 143 of 143 non-null readings were addresses the game was
/// listing in AwakeEntities that same frame, and it was null on the other 789.
///
/// WHY THIS IS WORTH THREE READS A FRAME rather than being computed: the geometric answer -
/// project every entity and find the one nearest the cursor - is not the same answer. It cannot
/// see that the cursor is over a monster's arm and not its feet, it has no idea what the game
/// considers pickable, and it will always name SOMETHING, where the game's own slot goes empty
/// over floor. The measured emptiness is the part that cannot be reproduced by picking a
/// nearest: 85% of frames in an area holding 96 monsters.
/// </remarks>
public sealed class MouseOverReader
{
    private readonly IMemoryReader _reader;
    private readonly int _host;
    private readonly int _sub;
    private readonly int _entity;

    public MouseOverReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _host = schema.Structs["InGameState"].OffsetOf("MouseOverHostPtr");
        _sub = schema.Structs["MouseOverHost"].OffsetOf("SubStructPtr");
        _entity = schema.Structs["MouseOverSub"].OffsetOf("HoveredEntity");
    }

    /// <summary>The entity under the cursor, or 0 for none.</summary>
    /// <remarks>
    /// Zero is the ordinary answer and covers three cases which a caller has no reason to tell
    /// apart: nothing is hovered, the chain did not resolve, or the recording being replayed
    /// never read it. All three mean the same thing to anything drawing a highlight.
    ///
    /// The address is returned rather than a WorldEntity because the caller already has the
    /// entity list and can join on <see cref="WorldEntity.Address"/> - looking the entity up
    /// here would mean reading its header a second time in the same frame.
    /// </remarks>
    public ulong Read(ulong inGameState)
    {
        if (inGameState == 0)
        {
            return 0;
        }

        ulong host = _reader.ReadPointer(inGameState + (ulong)_host);
        if (!MemoryReaderExtensions.IsPlausiblePointer(host))
        {
            return 0;
        }

        ulong sub = _reader.ReadPointer(host + (ulong)_sub);
        if (!MemoryReaderExtensions.IsPlausiblePointer(sub))
        {
            return 0;
        }

        ulong entity = _reader.ReadPointer(sub + (ulong)_entity);
        return MemoryReaderExtensions.IsPlausiblePointer(entity) ? entity : 0;
    }
}
