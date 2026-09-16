# Reading big tables

How a table of thousands of rows becomes something a person can *read* — and how the Monster
Book stops being a list with a search box and becomes the thing this tool can do that a wiki
cannot.

This is a design document, and the only one here for something that does not exist yet. The
**what** it is built on is already written down where it belongs — `MonsterTables` for how the
eight tables are read out of the install, `MonsterVarieties` for what every field means and what
its units are or are not, `MonsterBookWindow` for what the current window deliberately refuses to
do. This records the **why** of the viewer, so that building it is a day's work rather than a
week's argument.

---

## The problem, in the numbers this project actually has

`MonsterBookWindow` today draws six columns and caps the list at 1500 rows. Behind those six
columns sits this:

| What | How much |
| --- | --- |
| Monsters | **2733** |
| Scalar fields per monster | **25** (life, damage, xp, speed, sizes, ranges, poise, crit, …) |
| Joined blocks | type (armour / evasion / ES-from-life / spread / summoned / resistance profile), blood |
| Tags | **185** in use, of 1327 in the table |
| Skills | **5362** in use, of 8347 — one boss carries **67** |
| Modifier rows | **2530**, each with up to **8** stat slots |
| Distinct stats those modifiers set | **122**, of which **67** have a sentence the game words |
| Bosses | **363** · placeholder-named rows **24** · rows with a distinct base **2191** |

Every number there is the **shipped export's**, because that is what is committed and what the
tests can be run against. None of them is a constant: a live 0.5.5 client read in
[#368](https://github.com/imm0r/PoEformance2/pull/368) holds **2792** monsters and **1339** tag
rows, and the next patch will hold something else again. So nothing below is sized at compile
time — the store measures the table it is handed, and where a figure appears in this document it
is an illustration of the arithmetic rather than a dimension to code against.

Call it **sixty to eighty facts per monster, two hundred thousand facts in the table**. Six
columns show 0.1% of it. Everything else is one click and one scroll away in a detail pane, which
is fine for *"what is this"* and useless for *"which of these"*.

2733 rows is not a large table. **The fact count is the large thing**, and that is the problem
this design is against: not how to page a million rows, but how to put two hundred thousand facts
in front of somebody without either hiding them or drowning them.

---

## The three jobs

1. **Lookup** — *"what is a Vaal Fallen, exactly"*. Works today. Sharpen, don't rebuild.
2. **Comparison** — *"which monsters block"*, *"what do the undead in this act have in common"*,
   *"how does this rare differ from its base"*. **Impossible today.** The search box can find a
   word; it cannot count, group, rank or contrast.
3. **The live tie-in** — *"what is in front of me right now, and what does it do"*. The table and
   the game are in the same process. Nothing else that reads this data can say that.

Job 2 is where the work goes. Job 3 is where the tool wins.

---

## The design in one picture

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ⌕ tag:undead life>120 not boss                            [views: all · area · boss+] │
├──────────────┬───────────────────────────────────────────────┬───────────────────────┤
│ IN THIS AREA │ name          type      life    dmg    skills │ Vaal Fallen           │
│  ▣ present 12│              ▁▃▅▇▅▃▁   ▁▂▅▇▃▁  ▁▇▅▂▁   ▁▅▇▃▁ │ Metadata/Monsters/…   │
│              │ ─────────────────────────────────────────────  │ ───────────────────── │
│ TYPE      ▾  │ Vaal Fallen   Undead   ▊▊▊▊▏148%  ▊▊▏ 110%  ▊4│ life    ▊▊▊▊▏ 148%    │
│  Undead  267 │ Vaal Fallen²  Undead   ▊▊▊▊▏148%  ▊▊▏ 110%  ▊4│ damage  ▊▊▏   110%    │
│  Beast   198 │ Risen Maraud  Undead   ▊▊▊▊▊▏172% ▊▊▊▏125%  ▊6│ armour  ▊▊▊▊▏ 140%    │
│  Demon    84 │ Skeletal War  Undead   ▊▊▏   96%  ▊▏   80%  ▊2│ ───────────────────── │
│              │ …                                              │ Tags (7)  Skills (4)  │
│ TAG       ▾  │                                                │ Modifiers (6)         │
│  undead  267 │                                                │  • MonsterBlock30     │
│  melee   201 │                                                │    30% chance to block│
│  caster   66 │                                                │                       │
│              │                                                │ [pin] [compare 2]     │
│ MODIFIER  ▸  │                                                │                       │
│ SKILL     ▸  │ 267 of 2733 · 4 filters · 0 hidden             │                       │
└──────────────┴───────────────────────────────────────────────┴───────────────────────┘
```

Three panes, left to right: **what exists** (facets, with live counts) → **what matches** (the
grid, every value encoded) → **what one of them is** (the detail pane, or a comparison of the
pinned set).

This is overview → zoom and filter → details on demand, in one screen, with no mode switch
between them.

---

## The five rules

Everything below follows from these. They are short on purpose: a rule you cannot state in a line
is a rule nobody applies under pressure.

### 1. One visual channel carries one meaning

**Length is magnitude. Colour is category.** A bar's length says how large a number is relative
to the column; colour never does. Colour is spent on `boss`, `unresolved`, `present in this
area` — facts that are true or false, where a length would be a lie.

The moment a cell's colour *also* means "high", a person has to learn which of two encodings to
believe, and the table stops being readable at a glance. That is the whole cost of density done
badly.

### 2. A bar only goes on a magnitude

`Life 148` is a magnitude. `Crit 2` is **a kind** — `AttackCrit` holds 0, 1 or 2 across the
whole table (`MonsterVariety`'s own remarks). `Quest 4211` is a row number. `Type 88` is a row
number. Drawing a bar on any of those invents a statistic, which is the exact failure
`MonsterVarieties` spent its remarks warning about.

So the column descriptor declares what kind of number it holds, and the grid refuses to encode
what it is not allowed to.

### 3. Nothing is hidden, ever

- **No row cap.** The 1500-row limit is not a performance concession any more (see
  [Part 4](#part-4--the-grid)); it is a place where the answer can be off-screen while the screen
  looks complete.
- **A row number that resolves to nothing is still a row** — `#4211`, dim, never dropped. Same
  rule the Monster Book already follows, extended to facets: an unnamed value gets a facet entry
  with its count, because "how many monsters point at a modifier we cannot name" is a question
  worth being able to ask.
- **Every filter says what it removed**: `267 of 2733 · 4 filters`. A filter you forgot is
  otherwise indistinguishable from a table that is missing rows.

### 4. Bare words still work

Type `undead` and it searches, exactly as today. The grammar (`tag:undead life>120`) is an
**escalation available to whoever wants it**, never a gate in front of the thing that already
worked. A viewer that must be learned before it answers anything is a viewer that gets opened
once.

### 5. Built once, drawn often

The overlay redraws at 60 Hz. Anything that touches all 2733 rows happens **when the data or the
filter changes**, never in `DrawTab`. Anything per-frame touches only the rows on screen. This is
not an optimisation pass to do later — it decides the data structures, so it is decided first.

---

## The shape of the code

Layering is compiler-enforced, so the split falls out of it:

```
Features/                      (no ImGui — testable on Linux, no game, no window)
  ColumnStore.cs    rows as columns, built once from any source
  ColumnIndex.cs    value → rows, as bitsets; the thing that makes facets free
  ColumnQuery.cs    text → filter tree, hand-written, AOT-safe
  ColumnFacets.cs   facet values and their counts under the current filter
  ColumnSpread.cs   per-column distribution: percentiles and histogram bins
  ColumnView.cs     a saved view: query, chosen columns, sort, pins — serialisable

Overlay/
  DataGrid.cs       draws a ColumnStore: clipping, encoded cells, sort, column chooser
  FacetRail.cs      the left pane
  SpreadPlot.cs     the histogram in a column header, and the drag that filters by it

Overlay/MonsterBookWindow.cs   becomes an adapter: which columns, which facets, and the
                               monster-specific detail pane. Nothing else.
```

`MonsterTables` and `MonsterVarieties` are untouched. The engine reads what they already produce.

---

## Part 1 — the store

The grid needs, for every row it draws, a number to encode and a string to print. Today both are
produced inside the draw loop: `Percent(row.One.Life)` allocates, and
`$"{row.Name}###monster-{row.Path}"` allocates and is then hashed by ImGui — **for all 1500 rows,
every frame**, because ImGui's own clipping happens after the managed work is done. That is
roughly seven thousand allocations per frame, a third of a million per second, for the forty rows
somebody can see.

So the store is columnar and pre-formatted:

```csharp
/// One column of a table, as the grid needs it rather than as the source holds it.
public sealed record ColumnKind
{
    public required string Name { get; init; }          // "life"
    public required ColumnShape Shape { get; init; }    // Magnitude, Kind, Row, Text, Flag, Set
    public string Unit { get; init; } = "";             // "%" — only where the data settles it
}

public sealed class ColumnStore
{
    public int Rows { get; }
    public ColumnKind[] Columns { get; }

    // Per column, indexed by row ordinal. The grid touches these and nothing else.
    public double[][] Numbers { get; }   // Magnitude / Kind / Row columns
    public string[][] Text { get; }      // already formatted: "148%", "#4211", "Vaal Fallen"
    public int[][] Sets { get; }         // flattened value ids per row, with an offsets array
    public ulong[] Flags { get; }        // one bit per flag column per row
}
```

`double[]` rather than `int[]` for one reason: `Poise` is a float and a column type per numeric
width buys nothing at 2733 rows. Memory for the monster table is about **1.4 MB** — a `string[]`
of 2733 × 6 pre-formatted cells plus the numeric arrays. That is a rounding error against the
1.5 MB `monster-varieties.json` already held in memory, and it removes all per-frame formatting.

**Built when the source version changes, not when the source instance changes.** The Monster Book
today rebuilds on `ReferenceEquals` of the table *and* of the stat sentences — correct, and the
store keeps exactly that trigger, because the install's own tables arrive on a background walk
well after start-up.

A live source (the Entity Browser's rows change every frame) declares itself live: the store is
rebuilt per snapshot, and the index and spreads are skipped. Sources say what they support:

```csharp
public enum SourceRhythm { Fixed, PerSnapshot }
```

`Fixed` gets facets, distributions and an index. `PerSnapshot` gets filtering and sorting only,
done linearly. Neither pretends to be the other.

---

## Part 2 — the index

Facet counts are the feature that makes a large table navigable without typing, and they are the
one thing that cannot be computed naively. Under a crossfilter — *every facet value shows how many
rows would match if you clicked it, given every filter except its own* — the naive cost is
`values × rows`. For monsters that is roughly 2000 values × 2733 rows ≈ **5.5 million operations**.
Once, that is nothing. Per frame, it is a stall.

So: **one bitset per facet value**.

```
2733 rows → ceil(2733 / 64) = 43 ulongs = 344 bytes per value
```

A facet count becomes 43 `AND`s and 43 `PopCount`s. All 2000 values cost about **86 000
popcounts ≈ 50 µs**, and that only runs when the filter changes.

**What not to index, and why the measurement matters.** The obvious next step is a text index. It
is not worth building: the per-row search blob averages ~500 characters, so a full scan is 1.4 MB
of `IndexOf` — comfortably under a millisecond, and it only runs on a keystroke. What *is* worth
having is one line of arithmetic: **when the new query starts with the old one, scan only the
previous result set**. Typing narrows monotonically, so the common case gets faster with every
character for free.

High-cardinality set columns get one refinement. 5362 skills at 344 bytes each is 1.8 MB of
mostly-zero bitsets, and most skills sit on a handful of monsters. So each value stores
**whichever is smaller — a sorted `int[]` of row ordinals, or a bitset** — promoted at
`rows / 64`. Roaring's idea, in about fifty lines, and it keeps the whole index near 300 KB.

Sort keys are precomputed too: text columns get an `int[]` rank array from one
`OrdinalIgnoreCase` sort at build time, so sorting the view is `Array.Sort` over ints and a
multi-column sort is a lexicographic compare of ints rather than of strings.

---

## Part 3 — the filter

**The facet rail and the query box are two views of one filter tree.** This is not a new idea in
this codebase: `RuleExpression` already parses a condition to a tree, writes the tree back to
text, and round-trips — which is what makes its graph editor and its text box two views of one
store rather than two stores to keep in step. The same trick, for the same reason, with the same
constraint: **hand-written, because Native AOT has no runtime code generation**, and this project
ships AOT.

Clicking `undead` in the rail appends `tag:undead` to the box. Editing the box re-ticks the rail.
There is one filter.

```
query      := or
or         := and ( ("or" | "||") and )*
and        := term ( ("and" | "&&")? term )*        -- juxtaposition means and
term       := ("not" | "!") term | "(" or ")" | predicate
predicate  := field ":" value                        -- tag:undead, skill:*fire*, type:Undead
            | field compare number                   -- life > 120, skills >= 10
            | field number ".." number               -- life 120..260      (what a drag writes)
            | word                                   -- free text, exactly as today
compare    := ">=" | "<=" | ">" | "<" | "=" | "!="
```

Three things this gets right by construction:

- **`word` is in the grammar**, so rule 4 is not a special case bolted on the side.
- **Errors come back as a position, not an exception** — `ExpressionResult` already carries
  `(Condition, Error, Column)` precisely so a caret can be drawn under the offending character.
  Copy that record's shape and the caret comes free.
- **Field names and values are known**, so `tag:` opens a completion list of the 185 tags with
  their counts. That is how the grammar gets discovered without anybody reading this document.

What it deliberately will not do is arithmetic, for the reason `RuleExpression` gives: an
expression that cannot be drawn in the rail is an expression that can be silently lost when
somebody uses the rail. Both views describe the same set, in both directions.

---

## Part 4 — the grid

### Clipping, so the cap can go

`IconPicker` already solved this in this codebase, and its reasoning transfers exactly: a fixed
row height makes the visible range arithmetic.

```csharp
float step   = ImGui.GetTextLineHeightWithSpacing();
float scroll = ImGui.GetScrollY();
int   first  = Math.Max(0, (int)(scroll / step) - 1);
int   last   = Math.Min(rows, (int)((scroll + ImGui.GetWindowHeight()) / step) + 2);
```

with a spacer row of `first * step` above and `(rows - last) * step` below, so the scrollbar still
measures the whole table. In a table that is one `ImGui.TableNextRow(flags, minRowHeight)` rather
than a `Dummy` — *verify that overload exists in these bindings before relying on it; the fallback
is a `Dummy` in the first cell.*

Not `ImGuiListClipper`: these bindings reach it through a raw pointer with a matching `Destroy`,
which is a lifetime to get right in code that cannot be run on the machine it is written on.
`AtlasLogWindow`, `IconPicker` and `MonsterBookWindow` all made that call already.

The result: **forty rows of managed work per frame instead of fifteen hundred**, and the cap comes
off — the table can be ten times the size and cost the same.

### The encoded cell

```
▊▊▊▊▏148%
```

A bar drawn into the cell rect with `AddRectFilled` behind right-aligned text. Two decisions worth
recording:

**Length is the percentile, not the value.** These distributions are heavy-tailed — `AttackSpeed`
runs 0..7170 around a median of 1500 — and a bar scaled linearly to the maximum makes every
ordinary row a stub and answers nothing. A percentile bar answers *"is this high, for a monster"*,
which is the question somebody scanning a column actually has, and **the number is printed beside
it**, so the magnitude is never lost. Where a linear reading is wanted, the view carries a toggle
for linear-clamped-at-p99; the default is percentile.

**Colour is not part of it.** The bar is one colour (`OverlayInk.Chrome`, under the text), because
of rule 1. Colour in the grid means `boss` (`OverlayInk.Name`, as today), `unresolved`
(`OverlayInk.Warn`), and `present in this area` (`OverlayInk.Reference`).

### The header spread

Each magnitude column's header carries a **24-bin histogram, about 14 px tall**, drawn from
`ColumnSpread`. It costs one row of vertical space for the whole table and it answers, before any
interaction: is this bimodal? is everything 100 with six outliers? where does this monster sit?

**Dragging across it writes a range filter into the query** (`life 120..260`). That is the only
interaction in the design that a person discovers by accident, which is worth a lot — and because
it writes text into the shared filter, it leaves a trace they can read, edit and undo.

Hovering it says `p50 112 · p90 180 · max 604` rather than a tooltip of bin counts, because
percentiles are the thing a person can act on.

### Columns, sorting, views

- **Column chooser** — all 25 scalars plus the joined ones. `ImGuiTableFlags.Hideable` gives the
  right-click menu for free; *verify against these bindings, fallback is our own popup.*
- **Multi-column sort** — `ImGuiTableFlags.SortMulti` and iterate `specs.Specs[i]`; *same caveat.*
  The existing `Sort()` already reads specs correctly, including the null-pointer check that these
  bindings require, so this is an extension of working code rather than a rewrite.
- **Saved views** — query, columns, sort, pins, in `ColumnView`, persisted beside the other
  overlay settings. Ship three: **all**, **in this area**, **bosses**. A saved view is the cheapest
  possible answer to "the tool is complicated": the complexity is opt-in and the useful
  configurations already exist.

### Pin and compare — the comparative payoff

Ctrl-click pins rows. With two or more pinned, the detail pane becomes a column per monster and
**collapses every field they agree on into one line — `27 fields identical` — leaving only what
differs**, each with its encoded bar.

That inversion is the feature. *"What is actually different about this rare versus its base"* is
currently a job of opening two monsters in turn and remembering sixty numbers; here it is the
default rendering of a two-row selection.

---

## Part 5 — the live tie-in

Three connections, all of which have precedent in the codebase already.

**The area facet.** A facet whose values are the paths present in the current `WorldSnapshot`,
with counts — *twelve of these alive right now*. Entity paths need
`MonsterVarieties.Same(path)` to normalise; that method exists. This turns the book into a
pre-fight briefing: everything in the room, ranked by whatever column matters.

**Row → screen.** Selecting a row highlights those entities in the world. The overlay already
draws per-entity decoration (`MonsterLineLayer`, `HealthBarLayer`, `EntityHiding`), so the hook is
one shared set of paths the layer consults — deliberately a *set of paths* rather than a set of
addresses, because the book's subject is a kind of monster, not an instance.

**Screen → row.** `EntityBrowserWindow` already holds a `Func<MonsterVarieties>` and already jumps
to another window: `_dissect` calls `_dissector.Show(address, …)` then `_tools.Show(DissectorTab)`.
Mirror it exactly — `Action<string> openInBook` → `book.Show(path)` then
`_tools.Show("monster-book")`. The pattern, the naming and the reason it brings the tab forward are
all established; this is filling in a second instance of it.

---

## Part 6 — what the Monster Book becomes

An adapter and a detail pane. Roughly:

```csharp
private static readonly ColumnKind[] Shown =
[
    new() { Name = "name",   Shape = ColumnShape.Text },
    new() { Name = "type",   Shape = ColumnShape.Text },
    new() { Name = "life",   Shape = ColumnShape.Magnitude, Unit = "%" },
    new() { Name = "damage", Shape = ColumnShape.Magnitude, Unit = "%" },
    new() { Name = "armour", Shape = ColumnShape.Magnitude, Unit = "%" },   // from the type
    new() { Name = "skills", Shape = ColumnShape.Magnitude },
    new() { Name = "crit",   Shape = ColumnShape.Kind },                    // no bar. ever.
    …
];
```

Everything the current window knows that the engine must not learn stays here: that `Life`,
`Damage`, `Xp` and `ModelSize` are percentages *because 2733 rows say so*; that `AttackSpeed`,
`Speed` and the aggro ranges carry no unit this table can prove; that armour lives on the type and
not on the monster; that resistances are profile **names** rather than percentages; that a stat
line needs `ImGuiText.Mono` because 207 stat ids contain a `%` and ImGui's text calls are printf.

None of that is generic, none of it should leak into the engine, and all of it is already written
down in `MonsterVarieties` and `MonsterBookWindow`. The adapter is where it keeps living.

---

## The performance budget

| When | What runs | Cost |
| --- | --- | --- |
| Table arrives (start-up, and again when the install's tables land) | Build store + index + spreads | one pass over 2733 rows; ~1.4 MB store, ~300 KB index |
| Keystroke | Parse query, filter | bitset intersect + substring over the *previous* result set; sub-ms |
| Filter changes | Recount every facet | ~86 000 popcounts, ~50 µs |
| Header click | Sort | `Array.Sort` over precomputed int keys |
| **Every frame** | Draw ~40 visible rows | ~8 ImGui calls + 2 `AddRectFilled` per row. **Zero allocations** — every string is pre-formatted |

The line that matters is the last one. Today's window does 1500 rows of string formatting and ID
hashing per frame; this does forty rows of neither.

---

## What can be tested without the game

All of it, which is the point of putting the engine in `Features`.

`data/monster-varieties.json` is a committed 2733-row table and `MonsterTablesTests` already walks
up to the repository root to load it. So on Linux, with no game and no window:

- **Store build** — column count, row count, the pre-formatted strings, against known rows.
- **Query round-trip** — `Parse(Write(x))` evaluates as `x`, the check `RuleExpression` already
  makes of itself.
- **Filter correctness against a naive implementation.** Generate queries, run them through the
  index, run them through a straight `foreach` over the records, require the same row set. This is
  the check this project's own rules demand: *a test the data can settle, not one the
  implementation passes trivially.* A bitset bug that a hand-written assertion would miss cannot
  survive it.
- **Facet counts** against the same naive count.
- **Percentiles and bins** against a sorted copy.

What cannot be tested here is the drawing, and that is fine — it is also true of every other
window in the overlay.

---

## Staging

Each stage is useful shipped alone, and each one is a commit somebody can review.

1. **Store + clipped grid + encoded cells.** Removes the 1500 cap, removes the per-frame
   allocations, adds the bars. No new concepts for the user; the window just gets better.
2. **Column chooser, multi-sort, header spreads, saved views.** Density, still no new vocabulary.
3. **Index, facet rail, query grammar with completion.** The comparative jobs arrive here.
4. **Pin and compare.**
5. **The live tie-in** — area facet, row → screen, screen → row.
6. **A second consumer**, to prove the engine is one: the Entity Browser's survey (`PerSnapshot`),
   or a generic viewer over any table `QuestTables` can open — the loader's file table names 153 of
   them and this tool currently reads eight.

Stage 6 is the test of whether this was worth generalising. If the second consumer needs the
engine bent, the engine was wrong, and better to find that out on a table we already understand.

---

## Where this design stops working

Worth writing down now, so nobody rediscovers it as a bug:

- **Above ~100 000 rows**, the full-scan text search stops being sub-millisecond and needs a real
  token index; the pre-formatted string cache stops being a rounding error and needs to be built
  per visible page instead.
- **Above ~50 000 facet values**, the crossfilter recount stops being free and needs to be
  incremental (recount only facets whose own filter did not change).
- **A source that changes every frame** cannot have facets or distributions at a sensible cost.
  That is why `SourceRhythm` exists and why a `PerSnapshot` source is honest about offering less,
  rather than offering counts that flicker.

None of these are near the monster table. All of them are near a table of items or of ground
effects, which is exactly where this engine would be pointed next.

---

## Open questions

1. **Where does the store get built?** Today the Monster Book rebuilds on the draw thread the
   first frame after a new table arrives. One pass over 2733 rows is a visible hitch at 60 Hz if
   it lands mid-frame. The install's tables already arrive on a background walk — building the
   store there and handing over a finished object is probably right, and costs a second reference
   to swap.
2. **Should the facet rail show values with a count of zero** under the current filter? Hiding
   them keeps the rail short; showing them dim keeps *"there are no casters in this area"*
   answerable. Leaning towards showing them dim, per rule 3.
3. **How much of this belongs to the Entity Browser instead?** Its survey pane asks a
   near-identical question about live entities. If stage 6 folds it into this engine, the two
   windows stop drifting apart — but the Entity Browser's rows are addresses, not table rows, and
   that difference may be load-bearing.
