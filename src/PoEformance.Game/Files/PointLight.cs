using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// One point light a room places, where it is in the picture's model space and what its .ao says about it.
/// </summary>
/// <param name="Position">Where it is, in the same space the model's vertices are in.</param>
/// <param name="Colour">Its <c>colour</c> line - light, so above one where the file says so (a brazier writes 1.27 0.49 0.04).</param>
/// <param name="Radius">Its <c>radius</c> line - what the game's shader calls the median radius, light_colour_data.a.</param>
/// <param name="PenumbraDist">Its <c>penumbra_dist</c> line, kept as one of the readings of the shader's light_position_data.a - see SceneLight.</param>
/// <param name="Source">Which .ao it came from and which state, for the line under the picture.</param>
public readonly record struct PointLight(Vector3 Position, Vector3 Colour, float Radius, float PenumbraDist, string Source);
