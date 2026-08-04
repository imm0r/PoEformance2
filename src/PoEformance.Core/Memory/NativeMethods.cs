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
}
