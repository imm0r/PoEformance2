using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Files;
using PoEformance.Game.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// One key, and every ticked part of the catalogue goes into one folder: the pictures, the rooms, the entities, the area's files, what the diagnostic windows hold, and a recording of memory read once more from nothing.
/// </summary>
/// <remarks>
/// ASKED FOR so that what the tool's work keeps needing from the game arrives together. Until now
/// it came a piece at a time - a screenshot here, a Tile Book dump there, a recording from another
/// session - and pieces from different spots and builds do not line up. One press takes them from
/// one place at one moment; see <see cref="CaptureParts"/> for what each part is and
/// <see cref="CaptureReport"/> for what each file holds.
///
/// THE CATALOGUE DECIDES WHAT IS WRITTEN. Each part has a box on the Keys page, and the key writes
/// the ticked ones - so a question about a room's doodads is a capture of the doodads and the rooms,
/// and no sweep of raw bytes to go with it. The windows' own diagnoses - the routes, the light hunt,
/// the probe, the dissector, the atlas check - are parts too, written as they stood when the key
/// went down: the window offers its text through <see cref="Offer"/>, and the capture asks for it
/// on the frame of the press, where the window's state is safe to read.
///
/// TWO PICTURES, AND THE ORDER IS THE POINT. The one with the overlay is taken the moment the key is
/// seen, while the screen still shows the frame before. Then the overlay draws NOTHING for a few
/// frames - at least <see cref="HiddenFrames"/> and <see cref="HiddenMs"/>, since the compositor
/// shows a frame some time after it was drawn and a fast frame rate would otherwise outrun it - and
/// the game alone is taken. Hiding is what makes the second picture the game's whatever the window
/// manager does with a layered window, and a tenth of a second of no overlay on a key somebody
/// pressed to take a picture is not a cost anybody will notice. With that picture unticked the
/// overlay is not hidden at all.
///
/// BOTH ON THIS THREAD, the one place a frame's hitch is acceptable: the overlay must stay hidden
/// until the copy is done, and the copy is tens of milliseconds. The encoding and the files are
/// written on the pool.
///
/// THE ROOMS NEED THE ROOM SEARCH, the one the large map's outlines use, and it takes seconds the
/// first time in an area; the doodad survey follows it. So the capture waits for them - up to
/// <see cref="RoomWaitMs"/> - with a line saying so, and writes without them if they are not done
/// by then, saying why. It waits only where a ticked part wants them.
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

    /// <summary>How long to wait for the room search and the doodad survey before writing without them.</summary>
    public const int RoomWaitMs = 60_000;

    /// <summary>How long the files wait for the memory half - past the longest a recording runs.</summary>
    private const int MemoryWaitSeconds = 90;

    /// <summary>How long the line saying what happened stays up.</summary>
    private const int SaidMs = 5000;

    private readonly Func<WorldSnapshot> _snapshot;
    private readonly Func<ClientRect> _client;
    private readonly Func<RoomArrangement?> _rooms;
    private readonly Func<bool> _roomsPossible;
    private readonly Func<(int Done, int Of)> _roomsProgress;
    private readonly Func<AreaRooms?> _areaRooms;
    private readonly Func<IReadOnlyList<string>> _loaded;
    private readonly Func<string> _version;

    /// <summary>The parts switched off, by key - what the settings keep. See CaptureParts.</summary>
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);

    /// <summary>The windows' diagnoses, by part key - each asked for its text as the key goes down. See <see cref="Offer"/>.</summary>
    private readonly Dictionary<string, Func<string?>> _offers = new(StringComparer.Ordinal);

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
    /// <param name="areaRooms">The area's rooms as searched and surveyed, for the doodad parts - or null where there is no install to read them from.</param>
    /// <param name="loaded">The files the area loaded.</param>
    /// <param name="version">The version and build, as the title bar has them.</param>
    public CaptureKey(
        Func<WorldSnapshot> snapshot,
        Func<ClientRect> client,
        Func<RoomArrangement?> rooms,
        Func<bool> roomsPossible,
        Func<(int Done, int Of)> roomsProgress,
        Func<AreaRooms?> areaRooms,
        Func<IReadOnlyList<string>> loaded,
        Func<string> version)
    {
        _snapshot = snapshot;
        _client = client;
        _rooms = rooms;
        _roomsPossible = roomsPossible;
        _roomsProgress = roomsProgress;
        _areaRooms = areaRooms;
        _loaded = loaded;
        _version = version;
    }

    /// <summary>The key, as a virtual-key code - zero for the default.</summary>
    public int Key { get; set; }

    /// <summary>The key in force.</summary>
    public int KeyOrDefault => Key == 0 ? DefaultKey : Key;

    /// <summary>The parts switched off, by the catalogue's keys - as the settings keep them. Keys the catalogue no longer has are dropped.</summary>
    public IReadOnlyList<string> Off
    {
        get => CaptureParts.Kept(_off);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _off.Clear();
            _off.UnionWith(value);
        }
    }

    /// <summary>How to read a file out of the install, or null where there is none - for the rooms' files.</summary>
    public Func<string, byte[]?>? Read { get; set; }

    /// <summary>
    /// Starts the memory half of a capture - the recording, and the pass inside it that reads what the ask says - or null where one is already running. Set by whoever owns the reader.
    /// </summary>
    /// <remarks>Completes with what it wrote, in a line, once the recording is closed. See CaptureMemory.</remarks>
    public Func<CaptureMemoryAsk, Task<string>?>? RecordMemory { get; set; }

    /// <summary>Says why there is no memory recording, where <see cref="RecordMemory"/> is not set.</summary>
    public string NoMemory { get; set; } = "this session's reader cannot record on demand";

    /// <summary>Called when the key is rebound or a part ticked, so it is saved.</summary>
    public Action? Changed { get; set; }

    /// <summary>The last capture's folder, or empty.</summary>
    public string Last { get; private set; } = string.Empty;

    /// <summary>
    /// A window's diagnosis as a part of the capture: its text as it stands, or null where there is nothing yet - asked for on the frame the key goes down.
    /// </summary>
    /// <param name="key">The part's key - see CaptureParts.</param>
    /// <param name="report">The window's text, or null for nothing.</param>
    public void Offer(string key, Func<string?> report)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(report);
        _offers[key] = report;
    }

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
                    Last = run.Packed.Length > 0 ? run.Packed : run.Folder;
                    Say(
                        run.LeftOut.Count == 0
                            ? "capture saved and packed: " + Last
                            : $"capture saved: {run.Folder} - packed without {string.Join(", ", run.LeftOut)}",
                        run.LeftOut.Count == 0,
                        now);
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

    private bool On(string key) => !_off.Contains(key);

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

        var run = new Run(folder, local, snapshot, client, now, new HashSet<string>(_off, StringComparer.Ordinal))
        {
            Overlay = On(CaptureParts.OverlayPicture) ? ScreenCapture.Grab(client.X, client.Y, client.Width, client.Height) : null,
            Loaded = _loaded(),

            // NO HIDING where the game alone is not wanted: the frames drawn empty are for that picture.
            Stage = On(CaptureParts.GamePicture) ? Stage.Hiding : Stage.Rooms,
        };

        // THE WINDOWS' DIAGNOSES NOW, on this thread, as they stood when the key went down.
        foreach (CapturePart part in CaptureParts.All)
        {
            if (_offers.TryGetValue(part.Key, out Func<string?>? report) && On(part.Key))
            {
                run.Reports.Add((part, report()));
            }
        }

        if (!On(CaptureParts.Memory))
        {
            run.MemoryNote = "switched off";
        }
        else if (RecordMemory is { } record)
        {
            try
            {
                run.Memory = record(new CaptureMemoryAsk(folder, CaptureParts.Reads(_off)));
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

    /// <summary>Waits for the room search and the doodad survey where a ticked part wants them, then hands everything to the pool.</summary>
    private void Rooms(Run run, long now)
    {
        bool wantsRooms = On(CaptureParts.RoomsNear) || On(CaptureParts.RoomsArranged) || On(CaptureParts.Environments)
            || On(CaptureParts.Doodads) || On(CaptureParts.RoomsPlaced);
        bool wantsSurvey = On(CaptureParts.Doodads) || On(CaptureParts.RoomsPlaced);
        bool sameArea = _snapshot().AreaHash == run.Snapshot.AreaHash;
        bool possible = wantsRooms && sameArea && _roomsPossible();
        RoomArrangement? arranged = possible ? _rooms() : null;
        bool waiting = now - run.Since < RoomWaitMs;
        if (arranged is null && possible && waiting)
        {
            return;
        }

        // THE SURVEY AFTER THE SEARCH, on the same clock: it starts itself once the rooms are read,
        // and is done when the rooms have their places - see AreaRooms.
        AreaRooms? rooms = wantsSurvey && arranged is not null ? _areaRooms() : null;
        if (rooms is { ReadDoodads: not null } && rooms.Placed is null && waiting)
        {
            run.Waiting = rooms.Surveying ? "the doodad survey" : "the doodad survey to start";
            return;
        }

        run.Arranged = arranged;
        run.RoomsNote = arranged is not null ? string.Empty
            : !wantsRooms ? "no ticked part wanted them"
            : !sameArea ? "the area changed before the rooms were found"
            : !_roomsPossible() ? "no install to read the rooms from, or no terrain to find them in"
            : $"the room search was not done after {RoomWaitMs / 1000} s";
        if (rooms is not null)
        {
            run.Searched = rooms.Searched;
            run.Survey = rooms.Doodads;
            run.Placed = rooms.Placed;
        }

        run.SurveyNote = run.Survey is not null ? string.Empty
            : !wantsSurvey ? "no ticked part wanted it"
            : arranged is null ? "the rooms were not found"
            : rooms is null || rooms.ReadDoodads is null ? "nothing can read the area's entities in this session"
            : $"the doodad survey was not done after {RoomWaitMs / 1000} s";
        run.Stage = Stage.Writing;
        Func<string, byte[]?>? read = Read;
        string version = _version();
        run.Writing = Task.Run(() => Write(run, read, version));
    }

    /// <summary>Every ticked part of the capture, each on its own so one failing loses only itself.</summary>
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
        bool On(string key) => !run.Off.Contains(key);
        ClientRect client = run.Client;

        if (On(CaptureParts.GamePicture))
        {
            Part(CaptureReport.GameShot, () => Png(run.Game, client, At(CaptureReport.GameShot), $"the game alone, the overlay hidden for {run.HiddenFor} ms"));
        }

        if (On(CaptureParts.OverlayPicture))
        {
            Part(CaptureReport.OverlayShot, () => Png(run.Overlay, client, At(CaptureReport.OverlayShot), "the game with the overlay, as the key went down"));
        }

        if (On(CaptureParts.Entities))
        {
            Part(CaptureReport.EntitiesFile, () =>
            {
                File.WriteAllText(At(CaptureReport.EntitiesFile), CaptureReport.Entities(run.Snapshot));
                return $"{run.Snapshot.Entities.Count} entities, nearest first";
            });
        }

        if (On(CaptureParts.LoadedFiles))
        {
            Part(CaptureReport.LoadedFile, () =>
            {
                File.WriteAllText(At(CaptureReport.LoadedFile), CaptureReport.Loaded(run.Loaded));
                return $"{run.Loaded.Count} files";
            });
        }

        List<RoomNearby>? near = null;
        if (run.Arranged is { } arranged && run.Snapshot.Player is { } player)
        {
            (_, _, int tileX, int tileY) = CaptureReport.Where(player.WorldX, player.WorldY);
            near = CaptureReport.RoomsNear(arranged, tileX, tileY);
        }

        string noRooms = run.RoomsNote.Length > 0 ? run.RoomsNote : "the player was not read";
        if (On(CaptureParts.Environments))
        {
            if (read is null)
            {
                parts.Add($"{CaptureReport.EnvironmentsFile}  not written: no install to read them from");
            }
            else
            {
                // The environments whether or not the rooms were found: the area's own is known from
                // its row, and it is the one that certainly applies.
                string[] rooms = near is null ? [] : [.. near.Select(one => one.Laid.Room).Distinct(StringComparer.OrdinalIgnoreCase)];
                string area = run.Snapshot.Area.Environment;
                Part(CaptureReport.EnvironmentsFile, () =>
                {
                    File.WriteAllText(At(CaptureReport.EnvironmentsFile), ModelDump.Environments(read, rooms, run.Loaded, area));
                    return $"the area's environment ({(area.Length > 0 ? area : "did not resolve")}) and those of {rooms.Length} rooms";
                });
            }
        }

        if (On(CaptureParts.RoomsNear))
        {
            if (read is null)
            {
                parts.Add($"{CaptureReport.RoomsFile}  not written: no install to read them from");
            }
            else if (near is null)
            {
                parts.Add($"{CaptureReport.RoomsFile}  not written: {noRooms}");
            }
            else
            {
                Part(CaptureReport.RoomsFile, () =>
                {
                    File.WriteAllText(At(CaptureReport.RoomsFile), CaptureReport.Rooms(read, near));
                    return $"{near.Count} rooms within {CaptureReport.RoomReach} tiles, their doodads' lights and the pieces attached to them, and the file of each room the player stands in";
                });
            }
        }

        if (On(CaptureParts.RoomsArranged))
        {
            if (run.Arranged is { } all)
            {
                Part(CaptureReport.ArrangedFile, () =>
                {
                    (string said, string detail) = CaptureReport.Arranged(all);
                    File.WriteAllText(At(CaptureReport.ArrangedFile), CaptureReport.Lined((said, detail)));
                    return said;
                });
            }
            else
            {
                parts.Add($"{CaptureReport.ArrangedFile}  not written: {run.RoomsNote}");
            }
        }

        if (On(CaptureParts.Doodads))
        {
            if (run.Survey is { } survey)
            {
                Part(CaptureReport.DoodadsFile, () =>
                {
                    (string said, string detail) = CaptureReport.Doodads(survey, run.Searched);
                    File.WriteAllText(At(CaptureReport.DoodadsFile), CaptureReport.Lined((said, detail)) + "\n\n" + CaptureReport.Sightings(survey));
                    return said;
                });
            }
            else
            {
                parts.Add($"{CaptureReport.DoodadsFile}  not written: {run.SurveyNote}");
            }
        }

        if (On(CaptureParts.RoomsPlaced))
        {
            if (run.Placed is { } placed)
            {
                Part(CaptureReport.PlacedFile, () =>
                {
                    (string said, string detail) = CaptureReport.Placed(placed);
                    File.WriteAllText(At(CaptureReport.PlacedFile), CaptureReport.Lined((said, detail)));
                    return said;
                });
            }
            else
            {
                parts.Add($"{CaptureReport.PlacedFile}  not written: {run.SurveyNote}");
            }
        }

        // THE WINDOWS' DIAGNOSES, as they stood on the press - see Offer.
        foreach ((CapturePart part, string? text) in run.Reports)
        {
            if (text is null)
            {
                parts.Add($"{part.File}  not written: nothing there when the key went down");
                continue;
            }

            Part(part.File, () =>
            {
                File.WriteAllText(At(part.File), text);
                return $"{text.AsSpan().Count('\n') + 1} lines";
            });
        }

        // Last, so the recording has had the time the rest took. It closes once its ticks are
        // spent and the pass inside it is done - seconds, or a minute at the very most.
        if (run.Memory is { } memory)
        {
            try
            {
                parts.Add(memory.Wait(TimeSpan.FromSeconds(MemoryWaitSeconds))
                    ? $"{CaptureReport.MemoryFile}  {memory.Result}"
                    : $"{CaptureReport.MemoryFile}  still recording after {MemoryWaitSeconds} s when this was written - it closes itself");
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

        string[] off = [.. CaptureParts.All.Where(part => run.Off.Contains(part.Key)).Select(part => part.Key)];
        parts.Add(off.Length == 0 ? "every part of the catalogue was on" : "off: " + string.Join(", ", off));
        parts.Add($"{Path.GetFileName(run.Folder)}.zip  beside this folder: every file here, packed to send");
        File.WriteAllText(
            At(CaptureReport.SummaryFile),
            CaptureReport.Summary(run.Snapshot, run.Local, version, (client.X, client.Y, client.Width, client.Height), near, parts));

        // LAST, so the zip holds the summary and the closed recording. A zip that cannot be made
        // leaves the folder as it is - every file in it was written - and says why.
        try
        {
            (string zip, List<string> leftOut) = CaptureReport.Pack(run.Folder);
            run.Packed = zip;
            run.LeftOut = leftOut;
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            run.LeftOut = [$"everything ({failed.Message})"];
        }
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
            text = run.Stage != Stage.Rooms ? "capturing... writing the files and reading memory"
                : run.Waiting.Length > 0 ? $"capturing... waiting for {run.Waiting}"
                : $"capturing... waiting for the room search ({done} of {of})";
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

    /// <summary>The rows on Appearance → Keys: the key, what the game does with it, and a box for every part of the catalogue.</summary>
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
            "Writes the ticked parts below into one folder beside the tool, and packs them into a zip to send: pictures, "
            + "the rooms and their doodads, every entity, the area's files, what the diagnostic windows hold, and a memory "
            + "recording in which what the ticked parts need is read once more. Tick what the question is about.");

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

        DrawCatalogue();
    }

    /// <summary>A box per part, flowing under its group's heading - see CaptureParts.</summary>
    private void DrawCatalogue()
    {
        bool memory = On(CaptureParts.Memory);
        foreach (string group in CaptureParts.Groups)
        {
            ImGui.TextColored(OverlayInk.Quiet, group);
            float indent = ImGui.GetCursorPosX() + ImGui.GetStyle().FramePadding.X;
            float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            float end = float.NaN;
            foreach (CapturePart part in CaptureParts.In(group))
            {
                if (float.IsNaN(end))
                {
                    ImGui.SetCursorPosX(indent);
                }
                else
                {
                    OverlayLayout.Flow(end, OverlayLayout.CheckboxWidth(part.Label), right, indent);
                }

                // A sweep inside a recording nobody makes is no sweep: the box stays, greyed, so what
                // it would add is still readable.
                bool moot = part.Key == CaptureParts.RawSweep && !memory;
                ImGui.BeginDisabled(moot);
                bool on = On(part.Key);
                if (ImGui.Checkbox(part.Box, ref on))
                {
                    _ = on ? _off.Remove(part.Key) : _off.Add(part.Key);
                    Changed?.Invoke();
                }

                ImGui.EndDisabled();
                end = ImGui.GetItemRectMax().X;
                OverlayLayout.Hint(part.Hover);
            }
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

    private sealed class Run(string folder, DateTime local, WorldSnapshot snapshot, ClientRect client, long since, HashSet<string> off)
    {
        public string Folder { get; } = folder;

        public DateTime Local { get; } = local;

        public WorldSnapshot Snapshot { get; } = snapshot;

        public ClientRect Client { get; } = client;

        /// <summary>The parts off as the key went down - a copy, so a box ticked while the files are written changes nothing of this capture.</summary>
        public HashSet<string> Off { get; } = off;

        public Stage Stage { get; set; } = Stage.Hiding;

        public long Since { get; set; } = since;

        public int Hidden { get; set; }

        public long HiddenFor { get; set; }

        public byte[]? Overlay { get; init; }

        public byte[]? Game { get; set; }

        public IReadOnlyList<string> Loaded { get; init; } = [];

        /// <summary>The windows' diagnoses as they stood on the press, one per ticked part that offers one - null text for nothing there.</summary>
        public List<(CapturePart Part, string? Text)> Reports { get; } = [];

        public Task<string>? Memory { get; set; }

        public string MemoryNote { get; set; } = string.Empty;

        public RoomArrangement? Arranged { get; set; }

        public string RoomsNote { get; set; } = string.Empty;

        /// <summary>What the line at the top says is being waited for, past the room search.</summary>
        public string Waiting { get; set; } = string.Empty;

        public IReadOnlyList<(string Room, RoomLayout Layout, RoomSearch Search)>? Searched { get; set; }

        public DoodadSurvey? Survey { get; set; }

        public IReadOnlyList<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>? Placed { get; set; }

        public string SurveyNote { get; set; } = string.Empty;

        public Task? Writing { get; set; }

        public string Packed { get; set; } = string.Empty;

        public List<string> LeftOut { get; set; } = [];
    }
}
