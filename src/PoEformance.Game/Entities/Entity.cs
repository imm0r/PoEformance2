namespace PoEformance.Game.Entities;

/// <summary>
/// A decoded game entity: its address, id, metadata path, and the components it carries
/// (name -> component address).
/// </summary>
/// <remarks>
/// Everything in Path of Exile is an entity with a bag of components - the player, every
/// monster, ground items, chests, portals. What an entity IS shows in its
/// <see cref="Path"/> (e.g. <c>Metadata/Monsters/...</c>); what it can DO shows in which
/// <see cref="Components"/> it has (Render for a position, Life for health, and so on).
/// This is a plain data snapshot; reading it is <see cref="EntityReader"/>'s job.
/// </remarks>
public sealed class Entity
{
    public Entity(ulong address, uint id, string path, IReadOnlyDictionary<string, ulong> components)
    {
        Address = address;
        Id = id;
        Path = path;
        Components = components;
    }

    /// <summary>Address of the entity struct in the game process.</summary>
    public ulong Address { get; }

    /// <summary>The entity's numeric id (stable while it lives).</summary>
    public uint Id { get; }

    /// <summary>Metadata path, e.g. <c>Metadata/Characters/Int/IntFour</c>. May be empty.</summary>
    public string Path { get; }

    /// <summary>Component name -> component address. Names are case-sensitive as the game stores them.</summary>
    public IReadOnlyDictionary<string, ulong> Components { get; }

    /// <summary>True when the entity has a component with the given name.</summary>
    public bool Has(string component) => Components.ContainsKey(component);

    /// <summary>Address of the named component, or 0 when absent.</summary>
    public ulong Component(string name) => Components.TryGetValue(name, out ulong address) ? address : 0;
}

/// <summary>
/// What an entity is, before the cost of finding out what it can do.
/// </summary>
/// <remarks>
/// Exists so the two halves of reading an entity can be separated. The path arrives from one
/// string read; the components are a hash-bucket walk and several reads more. Since the path
/// is what says whether an entity is worth having at all, reading it first turns the expensive
/// half into something only the entities that matter pay for.
/// </remarks>
/// <param name="Details">The details struct, carried so the component walk need not find it again.</param>
public readonly record struct EntityIdentity(ulong Address, ulong Details, uint Id, string Path);
