# Reading the quest steps

How this tool goes from a running game to a sentence like *"Find the Red Vale — and it is over
there."* Every offset, every struct, every column, and the check that proves each stage rather
than making it look plausible.

This is a companion to the `### Quests` section of [architecture.md](architecture.md), which
records **why** the design is what it is. This document is the **how**: enough to re-derive it,
port it, or fix it when a patch moves something.

---

## The shape of the answer

The answer is made of two halves that meet on a single number.

| Half | Lives in | Changes | Read by |
| --- | --- | --- | --- |
| What this character has **done** | the game's process memory | constantly | `QuestFlagSet` |
| What the quests **are** | the install's `.datc64` files | only on patch day | `DatFile` + `QuestTables` |

The half in memory is a **sparse bitset over row numbers of `QuestFlags.datc64`**. The half in
the files declares its conditions as **foreign references into the same table**. A foreign
reference stores a row index, and a bit position *is* a row index — so the join is arithmetic.
There is no name matching anywhere in this pipeline and nothing in between to get wrong.

That is the single most important sentence here. Everything else is mechanics.

---

# Part 1 — The flag set, out of the process

## 1.1 The pointer chain

Seven hops from a pattern-scanned static to a `std::vector`.

```
  GameStates                       static, found by pattern scan
      │                            48 39 2D ^ ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? B9 40 01 00 00
      │  *
      ▼
  GameState
      │  + 0x88          ← States (0x48) + InGameStateIndex (4) * StateEntrySize (0x10)
      │  *
      ▼
  InGameState
      │  + 0x290         ← AreaInstanceData
      │  *
      ▼
  AreaInstance
      │  + 0x5A0         ← PlayerInfo. INLINE LocalPlayerStruct, whose ServerDataPtr is +0x00,
      │  *                  so the qword here IS ServerData. Do not add a second dereference.
      ▼
  ServerData
      │  + 0x60          ← QuestFlagOwnerPtr
      │  *
      ▼
  QuestFlagOwner        (unidentified class — five vtables at its head)
      │  + 0x188         ← QuestFlagSetPtr
      │  *
      ▼
  QuestFlagSet
      │  + 0x248         ← Chunks: std::vector { begin, end }
      ▼
  the records
```

**`GameState + 0x88` is not a magic number.** `GameState.States` is at `0x48` and is an *inline*
array of `StateEntrySize = 0x10` entries whose first field is the state object's pointer;
`InGameStateIndex` is `4`. So `0x48 + 4 * 0x10 = 0x88`. Dereferencing `States` as if it were a
pointer to the array is a mistake this project already made — it reads a state object's own
first qword as if it were the array base.

**`AreaInstance + 0x5A0` is the one hop that is not a hop.** `PlayerInfo` is an inline
`LocalPlayerStruct`, not a pointer to one. Its `ServerDataPtr` field is at `+0x00`, so the qword
at `0x5A0` is already the ServerData address. Adding a dereference here validates as a struct
base (it *is* one), so the mistake does not announce itself — it shows up two hops later as the
player chain "breaking".

All of these live in `schema/poe2.offsets.json` and are hot-reloadable with `--watch`. The
`AreaInstance` block carries drift history: `PlayerInfo` has moved twice
(`0x580 → 0x598 → 0x5A0`), and **when one field in that struct drifts, the whole tail moved by
the same delta** — check them all rather than patching one.

## 1.2 The bitset

`QuestFlagSet + 0x248` is a `std::vector` of **9-byte records**:

```
  byte  0        1  2  3  4  5  6  7  8
      ┌──────┬─────────────────────────────┐
      │chunk │  64 bits, little-endian     │
      └──────┴─────────────────────────────┘
        u8              u64
```

- Bit `b` of chunk `c` is **`QuestFlags` row `c * 64 + b`**.
- The vector is **sorted by chunk index, strictly increasing**.
- A `u8` chunk index reaches row `16383`, against the table's `5717` rows — so the index has
  room to spare and a value beyond `89` is already suspicious.
- The record is nine bytes, so it **straddles the eight-byte grid**. Every record read is its
  own; nothing here may assume alignment.

Constants live in the schema as `QuestFlagSet.consts`: `RecordSize 9`, `BitsPerRecord 64`,
`MaxRecords 4096`.

## 1.3 The read

`src/PoEformance.Game/Diagnostics/QuestFlagSet.cs`, method `Rows`:

```csharp
ulong hop   = reader.ReadPointer(serverData + 0x60);
ulong flags = reader.ReadPointer(hop        + 0x188);
ulong chunks = flags + 0x248;

ulong begin = reader.ReadPointer(chunks);
ulong end   = reader.ReadPointer(chunks + 8);

long count = (long)(end - begin) / 9;

Span<byte> record = stackalloc byte[9];
for (long i = 0; i < count; i++)
{
    reader.TryRead(begin + (ulong)(i * 9), record);
    int chunk = record[0];
    ulong word = BinaryPrimitives.ReadUInt64LittleEndian(record[1..]);
    for (int bit = 0; bit < 64; bit++)
    {
        if ((word >> bit & 1) != 0)
        {
            rows.Add((chunk * 64) + bit);
        }
    }
}
```

## 1.4 The four guards, and what each one catches

Each of these rejects a specific way of being wrong. None is decoration.

| Guard | Catches |
| --- | --- |
| `(end - begin) % 9 != 0` → give up | The offset has drifted onto a different vector whose element is not nine bytes. |
| `count > MaxRecords` → give up | `end` is garbage; without this the loop runs for minutes. |
| `chunk <= previous` → **stop, keep what was read** | A partially-torn read, or an offset landing on some other array. Chunk indices that jump about are the signature. Stopping rather than skipping keeps the prefix that was still coherent. |
| `TryRead` fails → break | The page went away mid-read. Ordinary during an area transition. |

## 1.5 Proving it is really the flag set

**Do not accept "the numbers look plausible."** A bitset of anything produces plausible row
numbers. The check that settles it is naming the rows:

```
row  11 → CompletedSkillGemTutorial
row  22 → CompletedLifeFlaskTutorial
row  46 → EnteredHideout
```

Three things the character had demonstrably done, resolved through `QuestFlags`' own
`ByIdIndex` (`QuestFlagSet.Named`, entry size `0x18`, first field is the Id's characters). A
bitset off by one bit names the neighbour instead; a wrong chain names nothing at all.

**The regression fixture covers both ways a flag can arrive**, which is what makes it a test
rather than a snapshot — see `tests/fixtures/session-2026-08-questflags-set.rec` and
`QuestFlagSetTests`:

- Row `1771` was set **in place**, in chunk `0x1B`, which the set already held.
- Row `644` arrived as a **brand-new chunk** `0x0A` — the case that reallocates the whole
  buffer, and the reason six sessions of page-diffing found nothing. When a chunk is inserted
  the vector is exact-sized, so every byte moves to a fresh address and the old copy is left
  behind unchanged. A differential watching for "a bit turning on" sees an untouched old buffer
  and a whole new one somewhere else.

Across every sample in that recording, **not one bit ever cleared**. A progress set only gains,
which is a cheap live sanity check on any new read.

## 1.6 Looking at it yourself

```bash
# The chain, hop by hop, with the neighbouring slots — and re-read on a timer so a
# quest step completed in game prints as a moved slot.
PoEformance.App --peek "GameStates,88,290,5A0,60,188,248" --peekwatch --record qf.rec
```

`--peek` takes a Cheat Engine pointer path. Anchoring on the schema static (`GameStates`) rather
than on a module RVA is what survives a patch. The `--record` makes the session replayable, so
the next question does not need the game.

---

# Part 2 — The tables, out of the install

## 2.1 Why the files and not memory

The resident copy of a `.dat` table carries the **fixed-size rows only**. The variable-length
section — every string, and every array's contents — is not in memory. A quest state's
conditions *are* arrays of foreign references, so the file is the only place they can be read
whole.

## 2.2 Finding them

`GameFiles` opens the install: either a loose `Bundles2` folder or one `Content.ggpk`, then the
bundle index, then Oodle. `GameInstall.Find` asks the **running game** where its own executable
is, and only falls back to the usual folders when there is none — a folder counts because the
files are in it, never because of its name. Four paths are tried per table, observed order
first:

```
data/balance/<table>.datc64      ← observed on a real install
data/balance/<table>.dat
data/<table>.datc64
data/<table>.dat
```

Four tables are opened, once, at startup — they cannot change while the game runs:

| Table | Rows | Row size | Notes |
| --- | --- | --- | --- |
| `Quest` | 127 | **119** (column list computes 103) | accepted on a read-based check, see 2.6 |
| `QuestStates` | 1477 | 208 | agrees |
| `QuestFlags` | 5717 | 12 | agrees |
| `MapPins` | — | — | **optional**: without it steps still read, they just name no place |

## 2.3 The `.datc64` layout

```
  offset 0   ┌────────────────┐
             │ u32 rowCount   │
  offset 4   ├────────────────┤
             │ row 0          │
             │ row 1          │  packed, NO alignment padding
             │ ...            │
             ├────────────────┤
   VariableAt│ BB BB BB BB    │  8 bytes of 0xBB — the separator
             │ BB BB BB BB    │
             ├────────────────┤
             │ strings and    │  offsets from rows count from VariableAt,
             │ array bodies   │  i.e. from the FIRST BYTE OF THE SEPARATOR
             └────────────────┘
```

**The row size is derived, not declared** — and that is the check the whole reader rests on:

```
rowSize = (separatorOffset - 4) / rowCount
```

No schema is involved in producing it, so comparing it against what the column list computes is
a genuine test of the column list rather than a tautology.

## 2.4 Three traps in the format

### Trap 1 — offset 0 means "no string", not "the first string"

Offsets count **from the separator**, so the first eight bytes of the variable section are the
`0xBB` magic itself and nothing real can start there. An unset string column simply holds zero.
Reading it anyway returns a run of `U+BBBB`, which is **not empty** — so a caller choosing
between two text columns picks the garbage over the real one.

Found exactly that way: a quest step that had only a long form started reporting its empty short
form as the objective. The guard is `if (into < 8) return string.Empty;`.

### Trap 2 — the separator scan must be byte-granular

Rows are packed, so the separator lands wherever the rows end and is under no obligation to be
aligned. `Quest`'s rows are **119 bytes** and its separator sits at an odd offset. A scan
stepping four bytes at a time walked straight past it and reported *"in the install but did not
parse as a .dat"* — a wrong answer that looks exactly like a missing file.

And because a row could legitimately contain those bytes as data, a match is only accepted when
the implied row size **divides the fixed region evenly**; otherwise the search carries on from
the next byte.

### Trap 3 — a row size that disagrees is not a reason to reject the table

`Quest` measures 119 bytes where the community schema's columns compute 103: there is a column
the public schema does not know about. But **both** of that schema's variants agree on the first
four columns, and `Id`, `Act` and `Name` are three of them — so rejecting the table would lose
every quest's name over a disagreement about its tail.

So a mismatch asks the table itself. `DatFile.ReadsAsText(offsets)` reads the columns this
feature actually needs, across the first 64 rows, and accepts the layout when **8 in 10** reads
come back printable (length ≥ 2, all characters `' '..'~'`). Eight in ten rather than all of
them, because a real string column has empty rows in it — not every quest has an icon.

When that check *fails*, `TextOffsets()` scans every offset in the row and reports where the
strings actually went, so the readout says *"the layout moved, and here is where to"* rather
than only *"it failed"*. That scan is only paid for on failure.

**Never "fix" a disagreement by padding the column list until the numbers match.** Padding moves
every offset after the padding, which turns a working front into a broken one.

## 2.5 Column offsets

Rows are packed, so an offset is nothing but the sum of the widths in front of it. Widths (the
combination `scripts/dat-offsets.ps1` verified against every dat offset this project had already
found the hard way):

| Type | Width |
| --- | --- |
| `bool`, `i8`, `u8` | 1 |
| `i16`, `u16` | 2 |
| `i32`, `u32`, `f32`, `enumrow`, `rid` | 4 |
| `string` | 8 (offset in the file; a pointer in memory) |
| `row` (same table) | 8 |
| `foreignrow` (another table) | 16 |
| **any array** | 16 (a count and an offset) |

A type the table cannot price makes every offset after it a guess, so `DatColumns.Layout`
**abandons the layout** rather than continuing with a hole in it.

The layouts are vendored in `data/quest-tables.json`, and the offsets are **recomputed at load**
rather than stored — storing the answers would let the widths and the offsets drift apart
silently. What matters for this feature:

**`QuestStates`** — 208 bytes:

```
  +0    foreignrow    Quest            which quest this state belongs to
  +16   i32           Order            COUNTS DOWN — last state of a quest is 0
  +20   foreignrow[]  FlagsPresent     flags that must be set
  +36   foreignrow[]  FlagsMissing     flags that must NOT be set
  +52   string        Text             the long form
  +61   string        Message          the short form — what the game's panel renders
  +69   foreignrow[]  MapPinsKeys      where it points (array)
  +89   string        MapPinsText
  +97   foreignrow    MapPinsKey       where it points (single)
```

**`Quest`** — `Id` +0, `Act` +8, `Name` +12.
**`QuestFlags`** — `Id` +0, `HASH32` +8. Twelve bytes and no state anywhere in it: this table is
identical for every character and is the game's list of flag **names**.

## 2.6 Two things that are measured rather than assumed

### Which way round an array's two words go

An array column is a count and an offset, and **no source this project trusts says which comes
first.** The reader was written count-first; the AHK tool's layout table comments the other way
round — and that tool never decodes an array, so its comment is a belief, not a test. Two
beliefs pointing opposite ways is precisely the situation that produces a confident wrong
answer.

So `DatFile.DetectArrays(columnOffset)` asks the file. A count and an offset are told apart by
what each has to satisfy:

- the **count** must be small (≤ 4096),
- the **offset** must land inside the variable-length section,
- and `offset + count * elementWidth` must still be inside it.

One reading of a row satisfies all three and the other usually satisfies none. Rows where
**both** work say nothing — an empty array is two zeros either way — and are counted for
neither. The majority across the table decides it, and the result is printed in the readout
rather than buried in a comment.

### Which half of a foreign reference is the row

A `foreignrow` is two 64-bit words. In memory the layout is known — table reference then row
reference, so the row is at +8 — but **the file is not the same thing.**
`DatReference.RowIn(rows)` resolves it by asking which half is a plausible row index of the
table being pointed at:

```csharp
public int RowIn(long rows)
{
    if (First < (ulong)rows) return (int)First;
    return Second < (ulong)rows ? (int)Second : -1;
}
```

A null reference carries `ulong.MaxValue` (or `0xFFFFFFFF`) in both halves and resolves to `-1`,
which callers drop. An observation beats a convention that might be the other way round.

---

# Part 3 — The join

`src/PoEformance.Features/QuestProgress.cs`, method `Read`.

## 3.1 Building the steps

For each of the 1477 `QuestStates` rows:

1. `Reference(row, +0).RowIn(Quest.Rows)` → which quest. Unresolved rows are **counted and
   reported**, never swallowed: a quest reference that resolves to no row is the first symptom
   of the two halves of a foreign reference being the other way round in the file than in
   memory.
2. `References(row, +20)` and `References(row, +36)` → `FlagsPresent` / `FlagsMissing`, each
   resolved through `RowIn(QuestFlags.Rows)`.
3. `Text(row, +52)` and `Text(row, +61)` → the two wordings.
4. `MapPinsKeys` (+69, array) **and** `MapPinsKey` (+97, single) → the places. Both, because
   which one a state fills in is not something to assume; names are de-duplicated.

Capped at `MostSteps = 256` per quest as a guard on a layout gone wrong.

## 3.2 A step holds when

```csharp
Present.All(set.Contains) && !Missing.Any(set.Contains)
```

## 3.3 Order counts DOWN

The last state of a quest is `Order 0`. Read off the game with the flags shown — *"Finding the
Forge"* runs:

```
  order 4   Speak to Renly in Clearfell Encampment
  order 3   Travel to Ogham Village and find Renly's tools
  order 2   Find Renly's tools
  order 1   Bring the tools back to Renly
  order 0   Quest Complete
```

So progression order is **descending**, and the steps are sorted that way.

**What sorting it the other way did**, because it is the sharpest lesson in this pipeline: a
*finished* quest reported its completion state as the current objective with an earlier step as
"then", and a quest genuinely *in progress* reported itself finished and was therefore hidden —
which is why two quests the game's own tracker was listing were missing from the window
entirely. **Every synthetic test passed throughout**, because the fixture modelled the direction
backwards too. It took a screenshot of the game to catch.

The sort is `OrderByDescending`, which is **stable**, and that is load-bearing rather than
stylistic: branch states share an `Order`, and an introsort puts ties in an arbitrary relative
order. Two things depend on ties coming out the same on every read — which of several holding
steps is picked as current, and which states end up adjacent for the route to fold.

## 3.4 Which step is current

```csharp
List<QuestStep> holding = [.. steps.Where(s => s.Holds(set))];
QuestStep? now = holding.Count > 0 ? holding[^1] : null;
```

**The LAST one that holds, in progression order.** Steps are cumulative — an early one asks for
flags a later one also has — so the earliest match is where the character has already *been* and
the furthest along is where they *are*. Taking the first match reports every quest as being on
step one forever.

`Next` is found by **position** (`steps[at + 1]`), not by comparing `Order`, so the direction
lives in the sort and in one place only.

**Several steps holding at once is ordinary.** Most states declare only the flags that must be
present and none that must be absent, so every state already passed goes on holding — The
Runeseeker had three at once. It is still counted and surfaced, because a sudden jump in that
number is what a mis-read condition column would look like.

## 3.5 Message, not Text

The two columns are just a string each and the schema does not say which is which. Shown side by
side against the game's own quest panel, **`Message` matched word for word** — *"Find the Red
Vale"*, *"Search for the meaning of the Runes etched into the Tree of Souls"* — while `Text` was
the longer sentence every time.

So `Message` is the objective and `Text` is the detail beneath it, which is usually the half
that says where to go: *"Slay the Devourer"* against *"The Devourer lives underground in a Mud
Burrow. Find it."*

`Line` falls back to `Text` when `Message` is empty; `Detail` is `Text` only when it differs
from the line above it.

## 3.6 The route, and folding its branches

`QuestStates` is a state **machine**, so a quest with branches carries a state per branch — The
Runeseeker has 87, most of them the same sentence for the different regions it can be done in.

`QuestState.Fold` collapses **consecutive** states wording an objective identically into one
`QuestLeg` carrying the count, and gathers every place those states named. The sentence is what
they had in common; the region is what they differed in, so the fold is both shorter than the
wall of identical lines **and** carries the only part the wall was varying.

Two rules keep it honest:

- **Only consecutive states fold.** The route is never rearranged to make the folding look
  tidier — a quest that really does come back to something still says so.
- **A wordless state is dropped rather than folded.** Left in, it splits a run in two and the
  route says the same sentence twice in a row, which is the thing the fold exists to stop.

## 3.7 Cadence

`QuestWatch.Update` is called about **once a second** (`QuestFlagIntervalMs = 1000`). Reading
the flag set is a handful of reads; the join behind it walks a few thousand states, and a quest
step does not change between two frames of anything.

It is **not** gated on the tables having opened. The flags are the half that comes out of the
process, so a `--record` session of a run where a table failed still carries them — which is
exactly the session somebody wants to look at afterwards.

---

# Part 4 — Doing it yourself

## 4.1 Commands

```bash
# The chain live, with the neighbouring slots, re-read on a timer.
PoEformance.App --peek "GameStates,88,290,5A0,60,188,248" --peekwatch --record qf.rec

# The overlay, with the Quests and Map pins tabs.
PoEformance.App --overlay --record session.rec

# Rerun any recording offline. No game, any OS.
PoEformance.App --replay session.rec
```

A recording can only contain reads the running build actually performed, so **a new diagnostic
needs a fresh recording**.

## 4.2 Where each stage lives

| Stage | File |
| --- | --- |
| The chain and the bitset | `src/PoEformance.Game/Diagnostics/QuestFlagSet.cs` |
| The offsets | `schema/poe2.offsets.json` (`ServerData`, `QuestFlagOwner`, `QuestFlagSet`) |
| The `.datc64` reader | `src/PoEformance.Game/Files/DatFile.cs` |
| Finding and verifying tables | `src/PoEformance.Features/QuestTables.cs` |
| Column layouts | `data/quest-tables.json` |
| The join | `src/PoEformance.Features/QuestProgress.cs` |
| Orchestration | `src/PoEformance.Features/QuestWatch.cs` |
| Map pins against the flags | `src/PoEformance.Features/MapPinProgress.cs` |
| The UI | `src/PoEformance.Overlay/QuestWindow.cs`, `MapPinWindow.cs` |
| Tests | `QuestFlagSetTests`, `QuestFlagChainTests`, `QuestProgressTests`, `MapPinProgressTests` |

## 4.3 Verifying each stage separately

The stages fail independently, so check them independently — this is what the Quests tab's
status lines are for:

1. **Chain** — `--peek` prints every hop. A hop that resolves to 0 or to something implausible
   names itself.
2. **Bitset** — flag count > 0. The tab says *"N flags are set — the tables are what did not
   load"* precisely so this can be told apart from a chain failure.
3. **Tables** — each of the four reports its own line: rows, row size, what the column list
   computed, and on what grounds it is being used.
4. **Join** — turn on "show the flags" in the Quests tab. Each holding step lists its conditions
   **by name**, which is the proof that the line above it is the right line.
5. **Against the game** — open the in-game quest panel and compare. That is the reference this
   whole pipeline is measured against, and it is free.

---

# Part 5 — When it breaks

| Symptom | Almost certainly |
| --- | --- |
| No flags at all | The chain. Check `AreaInstance.PlayerInfo` first — it has drifted twice, and the whole tail of that struct moves with it. |
| Flags read, but the count is wild or jumps | `Chunks` has drifted onto another vector. The `% 9` and sorted-chunk guards should already be rejecting it. |
| A table "is in the install but did not parse" | The separator scan. Check it is byte-granular and that the divisibility test is not rejecting the real separator. |
| A table parses but its strings are `U+BBBB` runs | Offset 0 being read as a position instead of "no string". |
| A table is rejected for a row-size mismatch | Expected for `Quest`. If a *new* table starts doing it, read its needed columns as text rather than padding the column list. |
| Every quest sits on step one | Steps sorted ascending, or `holding[0]` instead of `holding[^1]`. |
| A finished quest shows as current; an active one is hidden | `Order` sorted the wrong way. Synthetic tests will not catch this — compare against the game. |
| Arrays read as empty | `DetectArrays` returned `Unknown`, or the word order flipped in a patch. The readout says which order was measured. |
| A step names no place | `MapPins` did not open. Expected and harmless — it is optional on purpose. |
| Quest references resolve to no row | The two halves of a foreign reference are the other way round than `RowIn` guessed. The count of unresolved states is reported for exactly this. |

---

## The rule this pipeline was built under

Every stage above has a check the **game itself** can settle, and that is not an accident —
it is the project's rule, written down in `CLAUDE.md`, and this feature is where it was learned
the expensive way. Both of the properties that had to be guessed (`Order`'s direction and
`Message` over `Text`) were guessed **wrong first**, and in both cases every synthetic test
passed while the window on screen was wrong.

A check that a wrong value passes is worse than no check. When something here is unclear, hold
the window next to the game rather than reasoning about it.
