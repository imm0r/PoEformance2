using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PoEformance.Core.Memory;

/// <summary>
/// Reads memory from a running process via ReadProcessMemory.
/// </summary>
/// <remarks>
/// Attaching is read-only: we ask for PROCESS_VM_READ and nothing else, and we never
/// write to the target. A failed read is reported as <c>false</c>, never as an exception,
/// because bad pointers are routine when reverse engineering and the reader thread must
/// not unwind on every one of them.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LiveMemoryReader : IMemoryReader, IMemoryRegions
{
    private nint _handle;

    private LiveMemoryReader(nint handle, int processId, ulong moduleBase, uint moduleSize)
    {
        _handle = handle;
        ProcessId = processId;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
    }

    public bool IsAttached => _handle != 0;

    public int ProcessId { get; }

    public ulong ModuleBase { get; }

    public uint ModuleSize { get; }

    /// <summary>
    /// Opens the given process for reading. Returns null when the process is gone or the
    /// handle cannot be opened - typically a missing elevation, which is the single most
    /// common first-run problem, so callers should say so in their error message.
    /// </summary>
    public static LiveMemoryReader? TryAttach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        nint handle = NativeMethods.OpenProcess(NativeMethods.ProcessReadAccess, false, (uint)process.Id);
        if (handle == 0)
        {
            return null;
        }

        ulong moduleBase = 0;
        uint moduleSize = 0;
        try
        {
            ProcessModule? main = process.MainModule;
            if (main is not null)
            {
                moduleBase = (ulong)main.BaseAddress.ToInt64();
                moduleSize = (uint)main.ModuleMemorySize;
            }
        }
        catch (Exception)
        {
            // MainModule throws for a process that exited between the two calls, and for
            // a bitness mismatch. Neither is fatal: an attached reader with a zero module
            // base still serves absolute-address reads, and the pattern scanner reports
            // the missing module itself.
        }

        return new LiveMemoryReader(handle, process.Id, moduleBase, moduleSize);
    }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (_handle == 0 || destination.IsEmpty)
        {
            return false;
        }

        bool ok = NativeMethods.ReadProcessMemory(
            _handle,
            (nuint)address,
            ref MemoryMarshal.GetReference(destination),
            (nuint)destination.Length,
            out nuint bytesRead);

        return ok && bytesRead == (nuint)destination.Length;
    }

    /// <summary>
    /// Walks the target's address space and yields what is committed and readable.
    /// </summary>
    /// <remarks>
    /// Two protections are skipped and only one of them is obvious. NOACCESS has nothing to
    /// read. GUARD pages do, and reading one raises STATUS_GUARD_PAGE_VIOLATION IN THE GAME -
    /// a scanner that walks over a thread's stack guard crashes the process it is inspecting,
    /// which would be a spectacular way to fail at being read-only.
    ///
    /// Image regions are deliberately KEPT. A global living in the module's data segment is
    /// exactly the kind of thing that hangs off none of the pointer roots this project knows,
    /// which is the reason to enumerate at all.
    /// </remarks>
    public IEnumerable<MemoryRegion> Regions()
    {
        if (_handle == 0)
        {
            yield break;
        }

        nuint address = 0;
        while (true)
        {
            nuint got = NativeMethods.VirtualQueryEx(
                _handle,
                address,
                out NativeMethods.MemoryBasicInformation info,
                (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());

            if (got == 0 || info.RegionSize == 0)
            {
                yield break;
            }

            bool readable = info.State == NativeMethods.MemCommit
                && (info.Protect & NativeMethods.PageGuard) == 0
                && (info.Protect & NativeMethods.PageNoAccess) == 0;

            if (readable)
            {
                yield return new MemoryRegion(info.BaseAddress, info.RegionSize);
            }

            ulong next = (ulong)info.BaseAddress + info.RegionSize;
            if (next <= address || next > MaxUserAddress)
            {
                yield break;   // wrapped, or walked off the end of user space
            }

            address = (nuint)next;
        }
    }

    /// <summary>The top of the 64-bit user-mode address space, where the walk stops.</summary>
    private const ulong MaxUserAddress = 0x7FFF_FFFF_FFFF;

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = 0;
        }
    }
}
