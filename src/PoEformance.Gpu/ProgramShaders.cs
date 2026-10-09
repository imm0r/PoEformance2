using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using PoEformance.Game.Files;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace PoEformance.Gpu;

/// <summary>Which of a program's entry points - see ModelShaders.Programmed.</summary>
internal enum ProgramPass
{
    /// <summary>The solid pass under the picture's lamp.</summary>
    Solid,

    /// <summary>The solid pass under the game's light.</summary>
    SolidScened,

    /// <summary>The mixed pass under the picture's lamp.</summary>
    Mixed,

    /// <summary>The mixed pass under the game's light.</summary>
    MixedScened,
}

/// <summary>
/// Every material's shaders: written from its program, compiled behind the frame, kept on disk, and made on the render thread once ready.
/// </summary>
/// <remarks>
/// COMPILED BEHIND THE FRAME, one at a time on a worker of its own: the compiler takes a good part of a
/// second over a large program, and a room asks for dozens - on the frame that is a stall the length of
/// all of them. Until a material's shader is in, the picture is drawn on the processor, which says so
/// on the card button; the pane redraws when one lands (see <see cref="Landed"/>).
///
/// MADE ON THE RENDER THREAD. The device would take a shader from any thread, but the bytecode is all
/// the worker hands back, and turning it into a shader costs nothing next to compiling it.
///
/// KEPT ON DISK BY WHAT WAS COMPILED - the source, the entry point, the profile and the flags, hashed -
/// so a material seen once is on the card at once the next time the tool starts, and a changed source
/// (a new build, a changed graph) is a new file rather than a stale one. Nothing in the name says
/// which material it was; the folder may be deleted whenever, and is refilled as it is needed.
///
/// IEEE STRICT, because the processor's programs keep not-a-number as a card does and some of the
/// game's nodes depend on it (SmoothStep's own guard): a compiler allowed to assume no number is not
/// one may fold that guard away.
/// </remarks>
internal sealed class ProgramShaders : IDisposable
{
    /// <summary>The profile every program is compiled to - shader model 4, as the fixed shaders are.</summary>
    private const string Profile = "ps_4_0";

    private const ShaderFlags Flags = ShaderFlags.IeeeStrictness | ShaderFlags.OptimizationLevel3;

    private static readonly string[] Entries = ["ProgramSolid", "ProgramSolidScened", "ProgramMixed", "ProgramMixedScened"];

    private readonly ID3D11Device _device;
    private readonly string? _cache;
    private readonly ConditionalWeakTable<ShadeProgram, Written> _written = new();
    private readonly Dictionary<string, Shader> _shaders = new(StringComparer.Ordinal);
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<Done> _done = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    /// <param name="device">The device the shaders are made on.</param>
    /// <param name="cache">The folder compiled shaders are kept in, or null to keep none.</param>
    public ProgramShaders(ID3D11Device device, string? cache)
    {
        _device = device;
        _cache = cache;
        _ = Task.Run(Work);
    }

    /// <summary>How many shaders have landed, made or failed - a pane waiting on one redraws when this moves.</summary>
    public long Landed { get; private set; }

    /// <summary>How many are still being compiled.</summary>
    public int Pending { get; private set; }

    /// <summary>
    /// A program's shader for a pass: made, or null with whether it is still coming or why it never will - asked for the first time, it is queued.
    /// </summary>
    /// <remarks>
    /// ASKED ONCE PER RUN PER FRAME, so the answer is kept on the program's own entry, a pass apiece -
    /// no key built, nothing allocated - and the table by source is gone to only the first time, where
    /// two programs written alike (a material and its copy with other sheets) share their shaders.
    /// </remarks>
    public ID3D11PixelShader? Of(ShadeProgram program, ProgramPass pass, out bool pending, out string why)
    {
        Written written = _written.GetValue(program, Write);
        Shader? shader = written.Passes[(int)pass];
        if (shader is null)
        {
            string key = written.Hash + "." + Entries[(int)pass];
            if (!_shaders.TryGetValue(key, out shader))
            {
                shader = new Shader();
                _shaders[key] = shader;
                Pending++;
                _jobs.Writer.TryWrite(new Job(key, written.Source, Entries[(int)pass]));
            }

            written.Passes[(int)pass] = shader;
        }

        pending = shader.Pending;
        why = shader.Why;
        return shader.Made;
    }

    /// <summary>Makes the shaders the worker has compiled since the last call - on the render thread.</summary>
    public void Pump()
    {
        while (_done.TryDequeue(out Done? done))
        {
            if (_disposed || !_shaders.TryGetValue(done.Key, out Shader? shader))
            {
                continue;
            }

            shader.Pending = false;
            Pending--;
            Landed++;
            if (done.Bytes is null)
            {
                shader.Why = done.Why;
                continue;
            }

            try
            {
                shader.Made = Made(done.Bytes);
            }
            catch (SharpGenException exception)
            {
                shader.Why = $"the card would not take a material's shader: {exception.Message}";
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _jobs.Writer.TryComplete();
        _stop.Cancel();
        foreach (Shader shader in _shaders.Values)
        {
            shader.Made?.Dispose();
        }

        _shaders.Clear();
        _stop.Dispose();
    }

    private unsafe ID3D11PixelShader Made(byte[] bytes)
    {
        fixed (byte* code = bytes)
        {
            return _device.CreatePixelShader(code, new PointerSize(bytes.Length), null);
        }
    }

    /// <summary>A program's source and its hash, written once per program.</summary>
    private static Written Write(ShadeProgram program)
    {
        string source = ModelShaders.Program(program);
        return new Written(source, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
    }

    /// <summary>The worker: each job from the disk where it was kept, else compiled and kept.</summary>
    private async Task Work()
    {
        try
        {
            await foreach (Job job in _jobs.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _done.Enqueue(Compiled(job));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // LET GO WITH THE DRAWING: what was still queued is not wanted by anyone.
        }
    }

    private Done Compiled(Job job)
    {
        string? file = _cache is null ? null : Path.Combine(_cache, Key(job) + ".cso");
        if (file is not null)
        {
            try
            {
                if (File.Exists(file))
                {
                    return new Done(job.Key, File.ReadAllBytes(file), string.Empty);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A FILE THAT WILL NOT READ IS COMPILED AGAIN - the folder is only a shortcut.
            }
        }

        byte[]? bytes;
        string why;
        try
        {
            Result result = Compiler.Compile(job.Source, null!, null!, job.Entry, job.Entry, Profile, Flags, out Blob blob, out Blob errors);
            try
            {
                if (result.Failure || blob is null)
                {
                    string said = errors is null ? result.ToString() : Marshal.PtrToStringAnsi(errors.BufferPointer) ?? result.ToString();
                    return new Done(job.Key, null, $"a material's shader would not compile: {said}");
                }

                bytes = new byte[blob.BufferSize];
                Marshal.Copy(blob.BufferPointer, bytes, 0, bytes.Length);
                why = string.Empty;
            }
            finally
            {
                blob?.Dispose();
                errors?.Dispose();
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new Done(job.Key, null, $"a material's shader would not compile: {exception.Message}");
        }

        if (file is not null)
        {
            try
            {
                // WHOLE OR NOT AT ALL: written aside and moved in, so a tool closed mid-write leaves no half a shader to read next time.
                Directory.CreateDirectory(_cache!);
                string aside = file + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".part";
                File.WriteAllBytes(aside, bytes);
                File.Move(aside, file, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // NOT KEPT, AND STILL DRAWN - the next start compiles it again.
            }
        }

        return new Done(job.Key, bytes, why);
    }

    /// <summary>The file a compile is kept under: everything that went into it, hashed.</summary>
    private static string Key(Job job)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{job.Source}\n{job.Entry}\n{Profile}\n{(int)Flags}")));

    /// <summary>A program's source, its hash, and its shader for each pass once asked for.</summary>
    private sealed class Written(string source, string hash)
    {
        public string Source { get; } = source;

        public string Hash { get; } = hash;

        public Shader?[] Passes { get; } = new Shader?[Entries.Length];
    }

    private sealed record Job(string Key, string Source, string Entry);

    private sealed record Done(string Key, byte[]? Bytes, string Why);

    /// <summary>One program's shader for one pass: coming, made, or why not.</summary>
    private sealed class Shader
    {
        public bool Pending { get; set; } = true;

        public ID3D11PixelShader? Made { get; set; }

        public string Why { get; set; } = string.Empty;
    }
}
