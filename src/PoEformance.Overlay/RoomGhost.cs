using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// A place the tile book thinks a room was laid, picked from its list, for the large map to outline.
/// </summary>
/// <remarks>
/// THE OWNER'S CHECK: a room found by its ground alone may fit in more than one place, and the map
/// says at a glance whether a candidate is plainly wrong - an outline straddling a wall, or sitting
/// in open water. Kept with the grid it was found on, so a new area drops it on its own.
/// </remarks>
/// <param name="Grid">The area it was found in, compared by reference.</param>
/// <param name="Room">The room's file, for the label.</param>
/// <param name="Where">The candidate.</param>
/// <param name="Misses">Where it parts with the area - corners whose ground and tiles whose definition are not the room's.</param>
public sealed record RoomGhost(TerrainGrid Grid, string Room, RoomCandidate Where, RoomMisses Misses);
