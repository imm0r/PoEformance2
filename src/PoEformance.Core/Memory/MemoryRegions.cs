namespace PoEformance.Core.Memory;

/// <summary>One committed, readable stretch of the target's address space.</summary>
/// <param name="Address">Where it starts.</param>
/// <param name="Size">How long it is.</param>
public readonly record struct MemoryRegion(ulong Address, ulong Size);

/// <summary>
/// A reader that can say what memory the target actually has, rather than only answering
/// questions about addresses somebody already knows.
/// </summary>
/// <remarks>
/// THE GAP THIS CLOSES. Everything in this project finds things by following pointers from a
/// static, which means it can only ever see what is reachable from the roots it knows. Six
/// sessions of hunting a character's quest flags swept about 130 MB that way and found only
/// rules; the one explanation that survives is an object hanging off none of those roots.
/// Enumerating regions is how a search stops depending on that assumption.
///
/// Separate from <see cref="IMemoryReader"/> on purpose: a synthetic reader in a test has no
/// regions to speak of, and requiring it to invent some would put a fiction in every fixture.
/// Code that needs this asks for it and copes when it is absent.
/// </remarks>
public interface IMemoryRegions
{
    /// <summary>
    /// Every committed region that can be read, lowest address first.
    /// </summary>
    /// <remarks>
    /// Lazily, because the list is thousands long on a game and a caller that stops early
    /// should not pay for the rest of it.
    /// </remarks>
    IEnumerable<MemoryRegion> Regions();
}
