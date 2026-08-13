using System.Runtime.InteropServices;

namespace PoEformance.Core.Memory;

/// <summary>
/// Raw Win32 entry points. Nothing outside this file calls into the OS directly.
/// </summary>
/// <remarks>
/// Declared with <c>LibraryImport</c> rather than <c>DllImport</c> because the marshalling
/// code is then generated at compile time, which is what makes the project Native AOT
/// compatible. These declarations compile on any OS; only calling them needs Windows.
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ - the least we can ask for.</summary>
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessReadAccess = ProcessQueryInformation | ProcessVmRead;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(
        nint process,
        nuint baseAddress,
        ref byte buffer,
        nuint size,
        out nuint bytesRead);

    /// <summary>Region is backed by physical storage - anything else has no bytes to read.</summary>
    internal const uint MemCommit = 0x1000;

    /// <summary>Protections that make a region unreadable, or unsafe to touch.</summary>
    /// <remarks>
    /// NOACCESS speaks for itself. GUARD is the dangerous one: reading a guard page raises
    /// STATUS_GUARD_PAGE_VIOLATION in the TARGET process, which would arm a stack guard and
    /// crash the game rather than this tool. A memory scanner that skips neither is a
    /// scanner that eventually kills the thing it is inspecting.
    /// </remarks>
    internal const uint PageNoAccess = 0x01;
    internal const uint PageGuard = 0x100;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint VirtualQueryEx(
        nint process,
        nuint address,
        out MemoryBasicInformation buffer,
        nuint length);

    /// <summary>MEMORY_BASIC_INFORMATION, as the 64-bit ABI lays it out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        internal nuint BaseAddress;
        internal nuint AllocationBase;
        internal uint AllocationProtect;
        internal uint Alignment;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
        internal uint Alignment2;
    }
}
