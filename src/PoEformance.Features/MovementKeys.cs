using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>Which movement keys a roll is steered with, as a set.</summary>
/// <remarks>
/// A FLAGS SET RATHER THAN AN ANGLE, because the thing that eventually happens is that keys go
/// down: an angle would have to be turned back into keys somewhere, and the eight combinations
/// below are exactly what the keyboard can express. It also makes the decision legible in a
/// status line - "Up|Right" says what the tool did, where "-45 degrees" says what it meant to.
/// </remarks>
[Flags]
public enum MoveDirection
{
    /// <summary>No key - the roll is not steered.</summary>
    None = 0,

    /// <summary>Away from the camera, up the screen. W, by default.</summary>
    Up = 1,

    /// <summary>Right across the screen. D.</summary>
    Right = 2,

    /// <summary>Towards the camera. S.</summary>
    Down = 4,

    /// <summary>Left across the screen. A.</summary>
    Left = 8,
}

/// <summary>
/// The four movement keys, as virtual-key codes.
/// </summary>
/// <remarks>
/// THE DEFAULTS ARE AN ASSUMPTION AND ARE MARKED AS ONE, the same way the dodge key is - see
/// <see cref="DodgeKeyHints"/> for the full argument. W A S D is what the game ships with and
/// what most people leave alone, but a rebound key here does not fail loudly: the tool would hold
/// a key the game ignores and the roll would go wherever the cursor points, which looks exactly
/// like steering that decided on a different direction. So these are editable, the config's own
/// candidate lines are offered beside them, and steering starts switched off.
///
/// WHY VIRTUAL-KEY CODES AND NOT LETTERS: it is what <see cref="InputSender"/>-shaped code sends
/// and what <c>GetAsyncKeyState</c> answers about, and a letter would have to be converted at
/// both ends against a keyboard layout this tool has no business knowing about.
/// </remarks>
/// <param name="Up">Forward, away from the camera. 0x57 is W.</param>
/// <param name="Left">0x41 is A.</param>
/// <param name="Down">0x53 is S.</param>
/// <param name="Right">0x44 is D.</param>
public sealed record MovementKeys(
    [property: JsonPropertyName("up")] int Up = 0x57,
    [property: JsonPropertyName("left")] int Left = 0x41,
    [property: JsonPropertyName("down")] int Down = 0x53,
    [property: JsonPropertyName("right")] int Right = 0x44)
{
    /// <summary>W A S D, which is what the game ships with.</summary>
    public static MovementKeys Default { get; } = new();

    /// <summary>Every direction the keyboard can express, in a stable order.</summary>
    /// <remarks>
    /// The four axes and the four diagonals. Both matter: with a beam down one axis and a slam on
    /// another, the only place left can be between two keys, and a tool offering four directions
    /// would have to pick the best of a worse set without ever saying so.
    /// </remarks>
    public static IReadOnlyList<MoveDirection> Compass { get; } =
    [
        MoveDirection.Up,
        MoveDirection.Up | MoveDirection.Right,
        MoveDirection.Right,
        MoveDirection.Down | MoveDirection.Right,
        MoveDirection.Down,
        MoveDirection.Down | MoveDirection.Left,
        MoveDirection.Left,
        MoveDirection.Up | MoveDirection.Left,
    ];

    /// <summary>Whether all four are set, which steering needs before it can do anything.</summary>
    /// <remarks>
    /// ALL FOUR, not the ones a particular direction happens to use. A missing key silently
    /// removes three of the eight options, and the tool would then pick the best of what is left
    /// and roll there with no sign that a better direction was never considered.
    /// </remarks>
    public bool IsComplete => Up != 0 && Left != 0 && Down != 0 && Right != 0;

    /// <summary>The keys to hold for one direction, in a stable order.</summary>
    public IReadOnlyList<ushort> KeysFor(MoveDirection direction)
    {
        var keys = new List<ushort>(2);

        if ((direction & MoveDirection.Up) != 0 && Up != 0)
        {
            keys.Add((ushort)Up);
        }

        if ((direction & MoveDirection.Down) != 0 && Down != 0)
        {
            keys.Add((ushort)Down);
        }

        if ((direction & MoveDirection.Left) != 0 && Left != 0)
        {
            keys.Add((ushort)Left);
        }

        if ((direction & MoveDirection.Right) != 0 && Right != 0)
        {
            keys.Add((ushort)Right);
        }

        return keys;
    }

    /// <summary>All four, for the code that has to release and restore what the player holds.</summary>
    public IReadOnlyList<ushort> All => [(ushort)Up, (ushort)Left, (ushort)Down, (ushort)Right];

    /// <summary>Keeps every code inside the virtual-key range.</summary>
    public MovementKeys Normalised() => new(
        Math.Clamp(Up, 0, 0xFF),
        Math.Clamp(Left, 0, 0xFF),
        Math.Clamp(Down, 0, 0xFF),
        Math.Clamp(Right, 0, 0xFF));

    /// <summary>A readable one-liner for the settings page and the status line.</summary>
    public string Describe()
        => $"{FlaskKeyBindings.Describe((ushort)Up)} {FlaskKeyBindings.Describe((ushort)Left)} "
           + $"{FlaskKeyBindings.Describe((ushort)Down)} {FlaskKeyBindings.Describe((ushort)Right)}";
}
