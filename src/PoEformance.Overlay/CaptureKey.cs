using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// One key, and everything about the spot the player stands on goes into one folder: two pictures, the rooms there with their lights, the entities, the area's files, and three seconds of memory.
/// </summary>
/// <remarks>
/// ASKED FOR so that what the tool's work keeps needing from the game arrives together. Until now
/// it came a piece at a time - a screenshot here, a Tile Book dump there, a recording from another
/// session - and pieces from different spots and builds do not line up. One press takes them from
/// one place at one moment; see <see cref="CaptureReport"/> for what each file holds.
///
/// TWO PICTURES, AND THE ORDER IS THE POINT. The one with the overlay is taken the moment the key is
/// seen, while the screen still shows the frame before. Then the overlay draws NOTHING for a few
/// frames - at least <see cref="HiddenFrames"/> and <see cref="HiddenMs"/>, since the compositor
/// shows a frame some time after it was drawn and a fast frame rate would otherwise outrun it - and
/// the game alone is taken. Hiding is what makes the second picture the game's whatever the window
/// manager does with a layered window, and a tenth of a second of no overlay on a key somebody
/// pressed to take a picture is not a cost anybody will notice.
///
/// BOTH ON THIS THREAD, the one place a frame's hitch is acceptable: the overlay must stay hidden
/// until the copy is done, and the copy is tens of milliseconds. The encoding and the files are
/// written on the pool.
///
/// THE ROOMS NEED THE ROOM SEARCH, the one the large map's outlines use, and it takes seconds the
/// first time in an area. So the capture waits for it - up to <see cref="RoomWaitMs"/> - with a
/// line saying so, and writes without the rooms if it is not done by then, saying why.
///
/// OBSERVED, NEVER SWALLOWED, like the screenshot key: the game gets the key too, so the row that
/// binds it says what the game itself does with it, read from the player's own config.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class CaptureKey
{
    /// <summary>F9, until somebody binds another.</summary>
    public const int DefaultKey = 0x78;

    /// <summary>Frames drawn empty before the game alone is taken.</summary>
    public const int HiddenFrames = 3;

    /// <summary>And the least time they take, whatever the frame rate.</summary>
    public const int HiddenMs = 120;

    /// <summary>How long to wait for the room search before writing without it.</summary>
    public const int RoomWaitMs = 60_000;

    /// <summary>How long the line saying what happened stays up.</summary>
    private const int SaidMs = 5000;

    private readonly Func<WorldSnapshot> _snapshot;
    private readonly Func<ClientRect> _client;
    private readonly Func<RoomArrangement?> _rooms;
    private readonly Func<bool> _roomsPossible;
    private readonly Func<(int Done, int Of)> _roomsProgress;
    private readonly Func<IReadOnlyList<string>> _loaded;
    private readonly Func<string> _version;

    private bool _wasDown;
    private bool _listening;
    private Run? _run;
    private string _said = string.Empty;
    private bool _saidOk = true;
    private long _saidUntil;
    private int _conflictOf = -1;
    private string _conflict = string.Empty;

    /// <param name="snapshot">The world, this frame.</param>
    /// <param name="client">The game's client area on the screen.</param>
    /// <param name="rooms">The area's rooms arranged, or null while they are being searched for - asking starts the search.</param>
    /// <param name="roomsPossible">Whether there is a search to wait for at all: an install to read and a terrain to search.</param>
    /// <param name="roomsProgress">How far the search is.</param>
    /// <param name="loaded">The files the area loaded.</param>
    /// <param name="version">The version and build, as the title bar has them.</param>
    public CaptureKey(
        Func<WorldSnapshot> snapshot,
        Func<ClientRect> client,
        Func<RoomArrangement?> rooms,
        Func<bool> roomsPossible,
        Func<(int Done, int Of)> roomsProgress,
        Func<IReadOnlyList<string>> loaded,
        Func<string> version)
    {
        _snapshot = snapshot;
        _client = client;
        _rooms = rooms;
        _roomsPossible = roomsPossible;
        _roomsProgress = roomsProgress;
        _loaded = loaded;
        _version = version;
    }

    /// <summary>The key, as a virtual-key code - zero for the default.</summary>
    public int Key { get; set; }

    /// <summary>The key in force.</summary>
    public int KeyOrDefault => Key == 0 ? DefaultKey : Key;

    /// <summary>How to read a file out of the install, or null where there is none - for the rooms' files.</summary>
    public Func<string, byte[]?>? Read { get; set; }

    /// <summary>
    /// Starts a memory recording into a file for <see cref="CaptureReport.MemoryFrames"/> ticks, or null where this session cannot - set by whoever owns the reader.
    /// </summary>
    public Func<string, Task<long>?>? RecordMemory { get; set; }

    /// <summary>Says why there is no memory recording, where <see cref="RecordMemory"/> is not set.</summary>
    public string NoMemory { get; set; } = "this session's reader cannot record on demand";

    /// <summary>Called when the key is rebound, so it is saved.</summary>
    public Action? Changed { get; set; }

    /// <summary>The last capture's folder, or empty.</summary>
    public string Last { get; private set; } = string.Empty;

    /// <summary>
    /// One frame, behind the foreground gate: sees the key, moves a capture along. True while the overlay must draw nothing.
    /// </summary>
    public bool Poll(long now, bool typing)
    {
        bool down = !_listening && ScreenInput.IsDown(KeyOrDefault);
        bool pressed = down && !_wasDown;
        _wasDown = down;

        if (pressed && !typing && _run is null)
        {
            Begin(now);
        }

        if (_run is { } run)
        {
            if (run.Stage == Stage.Hiding)
            {
                run.Hidden++;
                if (run.Hidden <= HiddenFrames || now - run.Since < HiddenMs)
                {
                    return true;
                }

                // The frames drawn empty are on screen now; this one has not been drawn yet.
                run.Game = ScreenCapture.Grab(run.Client.X, run.Client.Y, run.Client.Width, run.Client.Height);
                run.HiddenFor = now - run.Since;
                run.Stage = Stage.Rooms;
                run.Since = now;
            }

            if (run.Stage == Stage.Rooms)
            {
                Rooms(run, now);
            }

            if (run.Stage == Stage.Writing && run.Writing is { IsCompleted: true } written)
            {
                _run = null;
                if (written.IsCompletedSuccessfully)
                {
                    Last = run.Folder;
                    Say("capture saved: " + run.Folder, true, now);
                }
                else
                {
                    Say("capture failed: " + written.Exception?.GetBaseException().Message, false, now);
                }
            }
        }

        DrawLine(now);
        return false;
    }

    private void Begin(long now)
    {
        WorldSnapshot snapshot = _snapshot();
        ClientRect client = _client();
        if (!client.IsValid)
        {
            Say("capture: the game's window was not found", false, now);
            return;
        }

        DateTime local = DateTime.Now;
        string folder = Path.Combine(CaptureReport.Root, CaptureReport.FolderName(local, snapshot.Area));
        try
        {
            // Two presses in one second must not write into one folder.
            string unique = folder;
            for (var again = 2; Directory.Exists(unique); again++)
            {
                unique = $"{folder} ({again})";
            }

            folder = unique;
            Directory.CreateDirectory(folder);
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            Say("capture: " + failed.Message, false, now);
            return;
        }

        var run = new Run(folder, local, snapshot, client, now)
        {
            Overlay = ScreenCapture.Grab(client.X, client.Y, client.Width, client.Height),
            Loaded = _loaded(),
        };

        if (RecordMemory is { } record)
        {
            try
            {
                run.Memory = record(Path.Combine(folder, CaptureReport.MemoryFile));
                run.MemoryNote = run.Memory is null ? "a recording was already running" : string.Empty;
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {
                run.MemoryNote = failed.Message;
            }
        }
        else
        {
            run.MemoryNote = NoMemory;
        }

        _run = run;
    }

    /// <summary>Waits for the room search, then hands everything to the pool.</summary>
    private void Rooms(Run run, long now)
    {
        bool sameArea = _snapshot().AreaHash == run.Snapshot.AreaHash;
        RoomArrangement? arranged = sameArea && _roomsPossible() ? _rooms() : null;
        if (arranged is null && sameArea && _roomsPossible() && now - run.Since < RoomWaitMs)
        {
            return;
        }

        run.Arranged = arranged;
        run.RoomsNote = arranged is not null ? string.Empty
            : !sameArea ? "the area changed before the rooms were found"
            : !_roomsPossible() ? "no install to read the rooms from, or no terrain to find them in"
            : $"the room search was not done after {RoomWaitMs / 1000} s";
        run.Stage = Stage.Writing;
        Func<string, byte[]?>? read = Read;
        string version = _version();
        run.Writing = Task.Run(() => Write(run, read, version));
    }

    /// <summary>Every file of the capture, each on its own so one failing loses only itself.</summary>
    private static void Write(Run run, Func<string, byte[]?>? read, string version)
    {
        var parts = new List<string>();
        void Part(string file, Func<string> write)
        {
            try
            {
                parts.Add($"{file}  {write()}");
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                parts.Add($"{file}  FAILED: {failed.Message}");
            }
        }

        string At(string file) => Path.Combine(run.Folder, file);
        ClientRect client = run.Client;

        Part(CaptureReport.GameShot, () => Png(run.Game, client, At(CaptureReport.GameShot), $"the game alone, the overlay hidden for {run.HiddenFor} ms"));
        Part(CaptureReport.OverlayShot, () => Png(run.Overlay, client, At(CaptureReport.OverlayShot), "the game with the overlay, as the key went down"));

        Part(CaptureReport.EntitiesFile, () =>
        {
            File.WriteAllText(At(CaptureReport.EntitiesFile), CaptureReport.Entities(run.Snapshot));
            return $"{run.Snapshot.Entities.Count} entities, nearest first";
        });

        Part(CaptureReport.LoadedFile, () =>
        {
            File.WriteAllText(At(CaptureReport.LoadedFile), CaptureReport.Loaded(run.Loaded));
            return $"{run.Loaded.Count} files";
        });

        List<RoomNearby>? near = null;
        if (run.Arranged is { } arranged && run.Snapshot.Player is { } player)
        {
            (_, _, int tileX, int tileY) = CaptureReport.Where(player.WorldX, player.WorldY);
            near = CaptureReport.RoomsNear(arranged, tileX, tileY);
        }

        if (near is null || read is null)
        {
            string why = read is null ? "no install to read the rooms from" : run.RoomsNote.Length > 0 ? run.RoomsNote : "the player was not read";
            parts.Add($"{CaptureReport.EnvironmentsFile}, {CaptureReport.RoomsFile}  not written: {why}");
        }
        else
        {
            string[] rooms = [.. near.Select(one => one.Laid.Room).Distinct(StringComparer.OrdinalIgnoreCase)];
            Part(CaptureReport.EnvironmentsFile, () =>
            {
                File.WriteAllText(At(CaptureReport.EnvironmentsFile), ModelDump.Environments(read, rooms, run.Loaded));
                return $"the environments of {rooms.Length} rooms and of the area";
            });
            Part(CaptureReport.RoomsFile, () =>
            {
                File.WriteAllText(At(CaptureReport.RoomsFile), CaptureReport.Rooms(read, near));
                return $"{near.Count} rooms within {CaptureReport.RoomReach} tiles, their doodads' lights, and the file of each room the player stands in";
            });
        }

        // Last, so the recording has had the time the rest took; three seconds is its whole length.
        if (run.Memory is { } memory)
        {
            try
            {
                parts.Add(memory.Wait(TimeSpan.FromSeconds(15))
                    ? $"{CaptureReport.MemoryFile}  {memory.Result / 1024} KB, {CaptureReport.MemoryFrames} reader ticks of the overlay's own reads - replay with --replay"
                    : $"{CaptureReport.MemoryFile}  still recording when this was written - it closes itself when its ticks are done");
            }
            catch (AggregateException failed)
            {
                parts.Add($"{CaptureReport.MemoryFile}  FAILED: {failed.GetBaseException().Message}");
            }
        }
        else
        {
            parts.Add($"{CaptureReport.MemoryFile}  not recorded: {run.MemoryNote}");
        }

        File.WriteAllText(
            At(CaptureReport.SummaryFile),
            CaptureReport.Summary(run.Snapshot, run.Local, version, (client.X, client.Y, client.Width, client.Height), near, parts));
    }

    private static string Png(byte[]? pixels, ClientRect client, string path, string what)
    {
        if (pixels is null)
        {
            throw new InvalidOperationException("the screen could not be copied");
        }

        using Image<Bgr24> image = Image.LoadPixelData<Bgr24>(pixels, client.Width, client.Height);
        image.SaveAsPng(path);
        return $"{client.Width} x {client.Height}, {what}";
    }

    private void Say(string text, bool ok, long now)
    {
        _said = text;
        _saidOk = ok;
        _saidUntil = now + SaidMs;
    }

    /// <summary>A line at the top of the screen while a capture runs, and for a while after.</summary>
    private void DrawLine(long now)
    {
        string text;
        bool ok = true;
        if (_run is { } run)
        {
            (int done, int of) = _roomsProgress();
            text = run.Stage == Stage.Rooms
                ? $"capturing... waiting for the room search ({done} of {of})"
                : "capturing... writing the files";
        }
        else if (now < _saidUntil)
        {
            text = _said;
            ok = _saidOk;
        }
        else
        {
            return;
        }

        ImGui.GetForegroundDrawList().AddText(new Vector2(12, 30), ok ? 0xFFFFFFFFu : 0xFF5050FFu, text);
    }

    /// <summary>The Appearance row that shows and rebinds the key, and says what the game does with it.</summary>
    public void DrawControls()
    {
        OverlayLayout.Group("Capture for Diagnosis");
        if (_listening)
        {
            ImGui.Button("Press a key...  (Esc cancels)##capturekey");
            if (PressedKey() is int key and > 0)
            {
                if (key != 0x1B)
                {
                    Key = key;
                    Changed?.Invoke();
                }

                _listening = false;
            }
        }
        else if (ImGui.Button($"{FlaskKeyBindings.Describe((ushort)KeyOrDefault)}##capturekey"))
        {
            _listening = true;
        }

        OverlayLayout.Hint(
            "Writes everything about the spot you stand on into one folder: a picture with and one without the overlay, "
            + "the rooms around you with their light data, every entity, the area's loaded files and three seconds of memory.");

        if (_conflictOf != KeyOrDefault)
        {
            _conflictOf = KeyOrDefault;
            _conflict = FlaskKeyBindings.SayActionsOn((ushort)KeyOrDefault);
        }

        OverlayLayout.Note(_conflict);
        if (Last.Length > 0)
        {
            PathLink.Line("last: " + Last);
        }
    }

    /// <summary>
    /// The key going down this frame, for a row waiting to be bound - or zero.
    /// </summary>
    /// <remarks>
    /// Mouse buttons and the generic modifiers are not keys somebody binds: the click that opened the
    /// row is still down, and Ctrl is the screenshot key's "whole overlay" modifier.
    /// </remarks>
    public static int PressedKey()
    {
        for (int key = 8; key < 255; key++)
        {
            if (key is 0x01 or 0x02 or 0x04 or 0x05 or 0x06 or (>= 0x10 and <= 0x12) or (>= 0xA0 and <= 0xA5))
            {
                continue;
            }

            if (ScreenInput.IsDown(key))
            {
                return key;
            }
        }

        return 0;
    }

    private enum Stage
    {
        Hiding,
        Rooms,
        Writing,
    }

    private sealed class Run(string folder, DateTime local, WorldSnapshot snapshot, ClientRect client, long since)
    {
        public string Folder { get; } = folder;

        public DateTime Local { get; } = local;

        public WorldSnapshot Snapshot { get; } = snapshot;

        public ClientRect Client { get; } = client;

        public Stage Stage { get; set; } = Stage.Hiding;

        public long Since { get; set; } = since;

        public int Hidden { get; set; }

        public long HiddenFor { get; set; }

        public byte[]? Overlay { get; init; }

        public byte[]? Game { get; set; }

        public IReadOnlyList<string> Loaded { get; init; } = [];

        public Task<long>? Memory { get; set; }

        public string MemoryNote { get; set; } = string.Empty;

        public RoomArrangement? Arranged { get; set; }

        public string RoomsNote { get; set; } = string.Empty;

        public Task? Writing { get; set; }
    }
}
