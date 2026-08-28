#!/usr/bin/env python3
"""Turn an exported MonsterVarieties table into data/monster-varieties.json.

WHY THIS SCRIPT IS COMMITTED. The table is 124 columns wide and most of them are
dead: Part1_Mods, Part2_Mods, Endgame_Mods and Helmet_ItemVisualIdentity hold
nothing at all across 2734 rows, MonsterArmour holds 16, and several more are
under 1%. Which columns survived is a MEASUREMENT rather than a preference, and
without the script that made the cut the next person has to redo it against a
fresh export to find out whether a missing field was dropped on purpose or lost.

Run it after exporting the tables from the game files:

    python3 scripts/monster-varieties.py MonsterVarieties.csv data/monster-varieties.json \
        --skills GrantedEffects.csv --mods Mods.csv --stats Stats.csv

The three reference tables are OPTIONAL and each one is independent. Without
them the numbers still ship and the browser shows them as numbers, which is what
it did before they were available; with them the same numbers also carry a name.

THE INDICES ARE 0-BASED, and that is measured rather than assumed. An off-by-one
resolves every skill to its neighbour, which is the kind of wrong that reads as
right: a zombie's one skill is "MeleeAtAnimationSpeed" at 1195 and
"MeleeAtAnimationSpeed2" at 1196, and nothing about either looks incorrect. What
settles it is that a named boss's skills carry the boss's own name - Yama gets
GSYamaChaosCloud and YamaSoulrend at 0-based, and DTTPaleFishman at 1-based. The
generator checks that on every run and refuses to write a table that fails it.

WHAT IS STILL NOT RESOLVED. Tags and MonsterType point at tables that are not
here yet, so they stay as numbers. The row number is kept for the resolved
columns too, rather than replaced by the name: it is the game's own identity for
that skill, and a later table keyed on it can still be joined without another
export.
"""

import csv
import json
import sys
from datetime import datetime, timezone

# Columns kept, and the short name each gets in the output. The measurement that
# chose them is in the module docstring; the fill rate of every one of these is
# in the report this script prints, so a future export that guts a column says so
# rather than silently shipping a field of nothing.
NUMBERS = {
    "MonsterType": "type",
    "MovementSpeed": "speed",
    "ObjectSize": "size",
    "ModelSizeMultiplier": "modelSize",
    "MinimumAttackDistance": "minAttack",
    "MaximumAttackDistance": "maxAttack",
    "MinAgroRange": "minAggro",
    "MaxAgroRange": "maxAggro",
    "ExperienceMultiplier": "xp",
    "DamageMultiplier": "damage",
    "LifeMultiplier": "life",
    "AttackSpeed": "attackSpeed",
    "AttackCrit": "crit",
    "BloodType": "blood",
    "Questflag": "quest",
}

# Kept as text. Stance is the one list-shaped column whose values are already
# words ("stance2"), so it needs no second table to mean anything.
TEXT = {
    "Name": "name",
    "Stance": "stance",
}

# Kept as lists of numbers - indices into tables this repository does not have.
INDEX_LISTS = {
    "Tags": "tags",
    "GrantedEffects": "effects",
    "Mods": "mods",
    "Mods2": "mods2",
    "Special_Mods": "specialMods",
}

# Kept as lists of paths.
PATH_LISTS = {
    "InheritsFrom": "inherits",
}

# A float, and the only one.
FLOATS = {
    "PoiseThreshold": "poise",
}


def parts(value):
    """The entries of a bracketed list.

    The export writes lists as ``[a, b]`` with the entries UNQUOTED, so a path
    list reads ``[Metadata/Monsters/X]`` - which is not JSON and not Python. A
    parser that hands those to ast.literal_eval gets an exception, and one that
    swallows the exception reports an empty list for a column that is 26% full.
    That happened while this table was being read, and the wrong answer looked
    exactly like a column nobody fills.
    """
    value = value.strip()
    if not value or value == "[]":
        return []
    if value.startswith("[") and value.endswith("]"):
        value = value[1:-1]
    return [piece.strip() for piece in value.split(",") if piece.strip()]


def number(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def build(rows):
    out = {}
    for row in rows:
        ident = (row.get("Id") or "").strip()

        # "Any" is a sentinel row rather than a monster - it is the only Id in the
        # table that is not a metadata path, and it would never match an entity.
        if not ident.startswith("Metadata/"):
            continue

        one = {}

        for column, short in TEXT.items():
            text = (row.get(column) or "").strip()
            # A name in [brackets] is a placeholder the game shows to nobody
            # ("[ANY MONSTER]"), so it is dropped rather than displayed.
            if text and not text.startswith("["):
                one[short] = text

        for column, short in NUMBERS.items():
            got = number((row.get(column) or "").strip())
            if got is not None:
                one[short] = got

        for column, short in FLOATS.items():
            try:
                one[short] = float((row.get(column) or "").strip())
            except ValueError:
                pass

        for column, short in INDEX_LISTS.items():
            got = [number(p) for p in parts(row.get(column) or "")]
            got = [g for g in got if g is not None]
            if got:
                one[short] = got

        for column, short in PATH_LISTS.items():
            got = parts(row.get(column) or "")
            if got:
                one[short] = got

        # Only when true: 2371 of 2734 are false, and a field that is absent
        # rather than false is 2371 fewer things to write and read.
        if (row.get("BossHealthBar") or "").strip().lower() == "true":
            one["boss"] = True

        base = (row.get("BaseMonsterTypeIndex") or "").strip()
        if base and base != ident:
            one["base"] = base

        out[ident] = one

    return out


def read(path):
    with open(path, encoding="utf-8", errors="replace") as handle:
        return list(csv.DictReader(handle))


# The one mod row that means "this slot is empty". Mods2 is a fixed-width array
# and 1023 of its 3553 references - 29% - point here, so keeping them would put
# five "Nothing" lines under every monster that has one real modifier.
FILLER = "Nothing"


def skill_names(monsters, skills):
    """Row number to skill id, for the rows monsters actually use.

    Shipping all 8347 would be a third again as much file for rows nothing reads.
    """
    wanted = set()
    for one in monsters.values():
        wanted.update(one.get("effects", []))

    named = {}
    for row in sorted(wanted):
        if 0 <= row < len(skills):
            ident = (skills[row].get("Id") or "").strip()
            if ident:
                named[str(row)] = ident
    return named


def prove_alignment(monsters, named):
    """A named boss's skills carry the boss's own name, or the table is misaligned.

    THE CHECK THE WHOLE RESOLUTION RESTS ON. An index shifted by one still
    produces a plausible skill for every monster, so nothing about a wrong table
    looks wrong. What a shift cannot survive is that Ignagduk's skills are all
    called GTIgnagduk-something: the game itself names them after their owner, and
    that is a reference this script cannot talk itself out of.
    """
    checks = [
        ("Metadata/Monsters/YamaBoss/YamaBoss", "Yama"),
        ("Metadata/Monsters/IgnagdukBogWitch/IgnagdukBogWitch", "Ignagduk"),
        ("Metadata/Monsters/MudBurrower/MudBurrowerHeadBoss", "MudBurrower"),
    ]

    for path, word in checks:
        one = monsters.get(path)
        if one is None:
            continue

        got = [named.get(str(row), "") for row in one.get("effects", [])]
        hits = sum(1 for name in got if word.lower() in name.lower())
        if not got:
            continue
        if hits * 2 < len(got):
            raise SystemExit(
                f"skill table looks misaligned: only {hits} of {len(got)} skills on {path} "
                f"mention {word!r}. An off-by-one in the row numbering does exactly this."
            )


def mod_meanings(monsters, mods, stats):
    """Row number to what the modifier is called and which stats it sets."""
    wanted = set()
    for one in monsters.values():
        for key in ("mods", "mods2", "specialMods"):
            wanted.update(one.get(key, []))

    named = {}
    for row in sorted(wanted):
        if not 0 <= row < len(mods):
            continue

        entry = mods[row]
        ident = (entry.get("Id") or "").strip()
        if not ident or ident == FILLER:
            continue

        carried = []
        for slot in range(1, 5):
            at = (entry.get(f"Stat{slot}") or "").strip()
            if not at.isdigit() or int(at) >= len(stats):
                continue

            low, high = 0, 0
            values = parts(entry.get(f"Stat{slot}Value") or "")
            if values:
                low = number(values[0]) or 0
                high = number(values[-1]) if len(values) > 1 else low
            carried.append(
                {"stat": (stats[int(at)].get("Id") or "").strip(), "min": low, "max": high or low}
            )

        named[str(row)] = {"id": ident, "stats": carried}

    return named


def drop_filler(monsters, mods):
    """Take the empty slots out of every monster's modifier lists."""
    if not mods:
        return

    empty = {row for row, entry in enumerate(mods) if (entry.get("Id") or "").strip() == FILLER}
    for one in monsters.values():
        for key in ("mods", "mods2", "specialMods"):
            if key in one:
                kept = [row for row in one[key] if row not in empty]
                if kept:
                    one[key] = kept
                else:
                    del one[key]


def main():
    argv = sys.argv[1:]
    extra = {}
    for flag in ("--skills", "--mods", "--stats"):
        if flag in argv:
            at = argv.index(flag)
            extra[flag[2:]] = argv[at + 1]
            del argv[at : at + 2]

    if len(argv) != 2:
        print(__doc__)
        return 1

    monsters = build(read(argv[0]))

    skills = read(extra["skills"]) if "skills" in extra else []
    mods = read(extra["mods"]) if "mods" in extra else []
    stats = read(extra["stats"]) if "stats" in extra else []

    named_skills = skill_names(monsters, skills) if skills else {}
    if named_skills:
        prove_alignment(monsters, named_skills)

    named_mods = mod_meanings(monsters, mods, stats) if mods and stats else {}
    drop_filler(monsters, mods)

    payload = {
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": "MonsterVarieties, exported from the game's data tables",
        "skills": named_skills,
        "modifiers": named_mods,
        "monsters": monsters,
    }

    with open(sys.argv[2], "w", encoding="utf-8") as handle:
        json.dump(payload, handle, separators=(",", ":"), sort_keys=True)
        handle.write("\n")

    total = len(monsters)
    print(f"{total} monsters, {len(named_skills)} skills named, {len(named_mods)} modifiers named")
    fields = {}
    for one in monsters.values():
        for key in one:
            fields[key] = fields.get(key, 0) + 1
    for key, count in sorted(fields.items(), key=lambda kv: -kv[1]):
        print(f"  {key:14s} {count:5d}  {100 * count / total:5.1f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
