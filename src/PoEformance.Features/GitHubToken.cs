using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// The GitHub token the boss-icon upload writes to the repository with, kept encrypted.
/// </summary>
/// <remarks>
/// THE FIRST SECRET THIS TOOL HAS EVER HELD, which is worth saying because everything else was
/// built to avoid holding one: the trade lookup signs in inside a WebView2 profile so that no
/// cookie ever reaches this code, and the update check asks GitHub anonymously. Uploading
/// exports cannot work that way - writing to a repository takes a credential - so this keeps
/// exactly one, as narrowly as it can.
///
/// ENCRYPTED WITH DPAPI, bound to the Windows user. config/ is plaintext JSON throughout and
/// gets copied around - into backups, onto a second machine, into a zip somebody sends while
/// asking for help - and a token in it would travel with every copy. A DPAPI blob decrypts
/// only for the user who made it, on the machine they made it on; anywhere else it is noise.
/// The protect and unprotect steps are handed in rather than called directly, so the store
/// itself is tested on the Linux CI with a stand-in.
///
/// NEVER SHOWN AGAIN once saved. The overlay asks for it once, saves it, and from then on only
/// knows that there is one - which is also why there is no "show token" anywhere.
/// </remarks>
public static class GitHubTokenStore
{
    /// <summary>Where the encrypted token is kept.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "github.json");

    /// <summary>Whether a token has been saved. Reads nothing but the file's existence.</summary>
    public static bool Has(string? path = null) => File.Exists(path ?? DefaultPath);

    /// <summary>Encrypts and saves a token, and says whether it worked.</summary>
    public static bool Save(string token, Func<byte[], byte[]> protect, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(protect);

        token = token.Trim();
        if (token.Length == 0)
        {
            return false;
        }

        string file = path ?? DefaultPath;
        try
        {
            byte[] sealedToken = protect(Encoding.UTF8.GetBytes(token));
            var stored = new GitHubTokenFile { Token = Convert.ToBase64String(sealedToken) };

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".writing";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, GitHubTokenJson.Default.GitHubTokenFile));
            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The saved token, decrypted, or empty when there is none or it cannot be opened.</summary>
    /// <remarks>
    /// Empty rather than an exception for a blob that will not decrypt: that is a config/ folder
    /// copied from another user or machine, and the answer is to type the token in again here,
    /// which is what an empty token makes the overlay ask for.
    /// </remarks>
    public static string Load(Func<byte[], byte[]> unprotect, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(unprotect);

        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file))
            {
                return string.Empty;
            }

            GitHubTokenFile? stored = JsonSerializer.Deserialize(
                File.ReadAllText(file), GitHubTokenJson.Default.GitHubTokenFile);
            if (stored?.Token is not { Length: > 0 } sealedToken)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(unprotect(Convert.FromBase64String(sealedToken))).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or FormatException or CryptographicException)
        {
            return string.Empty;
        }
    }

    /// <summary>Deletes the saved token. True when there is none afterwards.</summary>
    public static bool Forget(string? path = null)
    {
        try
        {
            File.Delete(path ?? DefaultPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Windows' data protection API: encryption bound to the current user, with no key to keep.
/// </summary>
/// <remarks>
/// CALLED DIRECTLY rather than through System.Security.Cryptography.ProtectedData, which is a
/// package this project does not otherwise need for two calls. LibraryImport, so the
/// marshalling is generated at compile time and survives Native AOT, as NativeMethods does it.
///
/// THE ENTROPY IS A NAME, not a secret: it keeps a blob this tool wrote from being opened by a
/// careless call elsewhere that passes none, which is all a constant can do.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class Dpapi
{
    /// <summary>Never show a prompt - there is nobody to answer one on the render thread.</summary>
    private const int UiForbidden = 0x1;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PoEformance.github-token");

    /// <summary>Encrypts for the current Windows user.</summary>
    public static byte[] Protect(byte[] plain) => Run(plain, protect: true);

    /// <summary>Decrypts what <see cref="Protect"/> made, as the same user.</summary>
    public static byte[] Unprotect(byte[] sealedData) => Run(sealedData, protect: false);

    private static unsafe byte[] Run(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);

        fixed (byte* data = input)
        fixed (byte* salt = Entropy)
        {
            var dataIn = new DataBlob { Size = input.Length, Data = (nint)data };
            var entropy = new DataBlob { Size = Entropy.Length, Data = (nint)salt };
            bool done = protect
                ? CryptProtectData(ref dataIn, 0, ref entropy, 0, 0, UiForbidden, out DataBlob dataOut)
                : CryptUnprotectData(ref dataIn, 0, ref entropy, 0, 0, UiForbidden, out dataOut);

            if (!done)
            {
                throw new CryptographicException(
                    $"{(protect ? "CryptProtectData" : "CryptUnprotectData")} failed: {Marshal.GetLastPInvokeError()}");
            }

            try
            {
                var output = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, output, 0, dataOut.Size);
                return output;
            }
            finally
            {
                LocalFree(dataOut.Data);
            }
        }
    }

    /// <summary>DATA_BLOB.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

/// <summary>The file's shape: the encrypted token, base64.</summary>
internal sealed class GitHubTokenFile
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }
}

/// <summary>Source-generated so the file loads under Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GitHubTokenFile))]
internal sealed partial class GitHubTokenJson : JsonSerializerContext;
