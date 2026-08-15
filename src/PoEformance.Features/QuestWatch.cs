using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Keeps the quest outlook up to date, off the reader tick.
/// </summary>
/// <remarks>
/// The tables are opened ONCE and the flags are re-read; that split is the whole design. The
/// three .dat files are a few megabytes to unpack and never change while the game runs, while
/// the flag set is a few dozen nine-byte records and changes as you play - so the expensive
/// half happens on the first tick that finds an install and the cheap half happens on a timer.
///
/// Recomputing the JOIN on every flag change rather than caching it: a quest's current step is
/// a scan of its own states and there are a few thousand in total, which is nothing next to a
/// single world scan, and a cache here would need invalidating on exactly the event that is
/// hardest to observe.
/// </remarks>
public sealed class QuestWatch
{
    private readonly object _gate = new();
    private QuestOutlook _outlook = QuestOutlook.Nothing("not read yet");
    private LoadedTable? _quests;
    private LoadedTable? _states;
    private LoadedTable? _flagTable;
    private QuestTableLayouts? _layouts;
    private string _opening = string.Empty;
    private bool _tried;
    private int _flags;

    /// <summary>The last reading, safe to take from any thread.</summary>
    public QuestOutlook Outlook
    {
        get
        {
            lock (_gate)
            {
                return _outlook;
            }
        }
    }

    /// <summary>True once the tables are open and only the flags are still needed.</summary>
    public bool Ready
    {
        get
        {
            lock (_gate)
            {
                return _quests is not null && _states is not null && _flagTable is not null;
            }
        }
    }

    /// <summary>
    /// How many flags the last read found, whether or not the tables opened.
    /// </summary>
    /// <remarks>
    /// Kept apart from the outlook because the two fail independently, and a run where the
    /// flags read fine and a table did not is the one that needs telling apart from a run
    /// where nothing resolved at all. It is also what makes a --record session of a failed
    /// load worth anything: the reads happen either way, so the recording carries the set.
    /// </remarks>
    public int Flags
    {
        get
        {
            lock (_gate)
            {
                return _flags;
            }
        }
    }

    /// <summary>What happened when the tables were opened, for the readout.</summary>
    public string Opening
    {
        get
        {
            lock (_gate)
            {
                return _opening;
            }
        }
    }

    /// <summary>
    /// Opens the three tables. Safe to call repeatedly; it only does the work once.
    /// </summary>
    public void Open(GameFiles? files, string layoutsPath)
    {
        lock (_gate)
        {
            if (_tried)
            {
                return;
            }

            _tried = true;

            if (files is null)
            {
                _opening = "no game install found - the quest tables are read from it, not from memory";
                _outlook = QuestOutlook.Nothing(_opening);
                return;
            }

            _layouts = QuestTableLayouts.Load(layoutsPath);
            if (_layouts is null || _layouts.Tables.Count == 0)
            {
                _opening = $"could not read the column layouts at {layoutsPath}";
                _outlook = QuestOutlook.Nothing(_opening);
                return;
            }

            var said = new List<string>();
            // The string columns each table is read for, so a row size that disagrees can be
            // settled by asking the table rather than by rejecting it. QuestStates' Text is
            // legitimately empty on some states, so Message rides along with it.
            (_quests, string why1) = QuestTables.Open(files, _layouts, "Quest", "Id", "Name");
            (_states, string why2) = QuestTables.Open(files, _layouts, "QuestStates", "Text", "Message");
            (_flagTable, string why3) = QuestTables.Open(files, _layouts, "QuestFlags", "Id");
            said.Add(why1);
            said.Add(why2);
            said.Add(why3);
            _opening = string.Join("; ", said);

            if (!Ready)
            {
                _outlook = QuestOutlook.Nothing(said.ToArray());
            }
        }
    }

    /// <summary>Recomputes the outlook against a fresh set of flag rows.</summary>
    public void Update(IReadOnlyCollection<int> setFlags)
    {
        ArgumentNullException.ThrowIfNull(setFlags);

        lock (_gate)
        {
            _flags = setFlags.Count;

            if (_quests is null || _states is null || _flagTable is null || _layouts is null)
            {
                return;
            }

            _outlook = QuestProgress.Read(_quests, _states, _flagTable, _layouts, setFlags);
        }
    }

    /// <summary>The Id of a flag row, for showing a step's conditions by name.</summary>
    public string FlagId(int row)
    {
        lock (_gate)
        {
            return _flagTable is null || _layouts is null
                ? string.Empty
                : QuestProgress.FlagId(_flagTable, _layouts, row);
        }
    }
}
