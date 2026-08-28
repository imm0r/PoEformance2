#!/usr/bin/env python3
"""Turn an exported MonsterVarieties table into data/monster-varieties.json.

WHY THIS SCRIPT IS COMMITTED. The table is 124 columns wide and most of them are
dead: Part1_Mods, Part2_Mods, Endgame_Mods and Helmet_ItemVisualIdentity hold
nothing at all across 2734 rows, MonsterArmour holds 16, and several more are
under 1%. Which columns survived is a MEASUREMENT rather than a preference, and
without the script that made the cut the next person has to redo it against a
fresh export to find out whether a missing field was dropped on purpose or lost.

Run it after exporting the table from the game files:

    python3 scripts/monster-varieties.py MonsterVarieties.csv data/monster-varieties.json

WHAT IS DELIBERATELY NOT RESOLVED. Tags, GrantedEffects, Mods, Mods2,
Special_Mods and MonsterType are ROW INDICES into other tables, not names. They
are carried through as numbers because the tables they point at are not in this
repository; a monster's skills read as "[1195]" until GrantedEffects.dat is
exported too. Carrying them costs little and means the join can be added later
without re-exporting anything.
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


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 1

    with open(sys.argv[1], encoding="utf-8", errors="replace") as handle:
        rows = list(csv.DictReader(handle))

    monsters = build(rows)

    payload = {
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": "MonsterVarieties, exported from the game's data tables",
        "monsters": monsters,
    }

    with open(sys.argv[2], "w", encoding="utf-8") as handle:
        json.dump(payload, handle, separators=(",", ":"), sort_keys=True)
        handle.write("\n")

    total = len(monsters)
    print(f"{total} monsters from {len(rows)} rows")
    fields = {}
    for one in monsters.values():
        for key in one:
            fields[key] = fields.get(key, 0) + 1
    for key, count in sorted(fields.items(), key=lambda kv: -kv[1]):
        print(f"  {key:14s} {count:5d}  {100 * count / total:5.1f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
