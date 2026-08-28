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
        --skills GrantedEffects.csv --mods Mods.csv --stats Stats.csv \
        --tags Tags.csv --types MonsterTypes.csv \
        --blood BloodTypes.csv --resistances MonsterResistances.csv

The reference tables are OPTIONAL and each one is independent. Without them the
numbers still ship and the browser shows them as numbers, which is what it did
before they were available; with them the same numbers also carry a name.

THE INDICES ARE 0-BASED, and that is measured rather than assumed. An off-by-one
resolves every skill to its neighbour, which is the kind of wrong that reads as
right: a zombie's one skill is "MeleeAtAnimationSpeed" at 1195 and
"MeleeAtAnimationSpeed2" at 1196, and nothing about either looks incorrect. What
settles it is that a named boss's skills carry the boss's own name - Yama gets
GSYamaChaosCloud and YamaSoulrend at 0-based, and DTTPaleFishman at 1-based. The
generator checks that on every run and refuses to write a table that fails it.

The row number is kept alongside every name rather than replaced by it: it is
the game's own identity for that skill, tag or type, and a later table keyed on
it can still be joined without another export.

WHAT THE TAG TABLE IS NOT. It carries a tag for most PoE1 league mechanics -
delve_monster, blight_monster, legion_monster, incursion_monster,
breach_monster_fire and more - and ZERO monsters carry any of them. They are
legacy rows GGG never removed, the same trap Data/Balance/PreloadGroups.dat
turned out to be. Only sanctum_monster (77), expedition_monster (30),
azmeri_cultist_monster (23), affliction_daemon (19), precursor_monster (12) and
sanctified_monster (10) are real, so a mechanic cannot be read off this column
in general. What it IS good for is the classification GameHelper2 also takes
from it: humanoid, human, undead, construct, beast and demon, plus movement
speed, melee/caster/ranged and blood type.
"""

import csv
import json
import re
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


def tag_names(monsters, tags):
    """Row number to tag id, for the rows monsters actually use - 185 of 1327."""
    wanted = set()
    for one in monsters.values():
        wanted.update(one.get("tags", []))

    named = {}
    for row in sorted(wanted):
        if 0 <= row < len(tags):
            ident = (tags[row].get("Id") or "").strip()
            if ident:
                named[str(row)] = ident
    return named


def prove_tags(monsters, named):
    """A zombie is tagged a zombie, or the tag table is misaligned.

    Same shape of check as the skills, and it needs to be: shifted by one the
    Risen Farmhand's six tags go from undead/zombie/melee to beast/skeleton/rodent,
    all of which are real tags on real monsters and none of which is obviously
    wrong until you know what a Risen Farmhand is.
    """
    one = monsters.get("Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium")
    if one is None:
        return

    got = {named.get(str(row), "") for row in one.get("tags", [])}
    for expected in ("undead", "zombie"):
        if expected not in got:
            raise SystemExit(
                f"tag table looks misaligned: a Risen Farmhand is not tagged {expected!r}. "
                f"It has {sorted(got)}."
            )


def type_stats(monsters, types):
    """Row number to the defensive block the game hangs on a monster's TYPE.

    This is where Armour, Evasion and EnergyShieldFromLife live - MonsterVarieties
    itself carries a MonsterArmour column that is filled on 16 of 2734 rows and is
    not the answer.
    """
    wanted = {one.get("type", 0) for one in monsters.values()}

    named = {}
    for row in sorted(wanted):
        if not 0 <= row < len(types):
            continue

        entry = types[row]
        ident = (entry.get("Id") or "").strip()
        if not ident:
            continue

        got = {"id": ident}
        for column, short in (
            ("Armour", "armour"),
            ("Evasion", "evasion"),
            ("EnergyShieldFromLife", "energyShield"),
            ("DamageSpread", "spread"),
        ):
            value = number((entry.get(column) or "").strip())
            if value:
                got[short] = value

        if (entry.get("IsSummoned") or "").strip().lower() == "true":
            got["summoned"] = True

        resistances = [number(p) for p in parts(entry.get("MonsterResistances") or "")]
        resistances = [r for r in resistances if r]
        if resistances:
            got["resistances"] = resistances

        named[str(row)] = got

    return named


def prove_types(monsters, named):
    """A monster's type is named after the monster, or the type table is misaligned.

    Measured: the type id shares a word with the monster's own path on 88% of rows,
    and on 55% when the table is shifted by one - the baseline is high because
    neighbouring types belong to the same family, which is exactly why the check is
    a proportion rather than a single lookup.
    """
    hits = seen = 0
    for path, one in monsters.items():
        ident = named.get(str(one.get("type", -1)), {}).get("id", "")
        if not ident:
            continue

        seen += 1
        last = path.rsplit("/", 1)[-1]
        if _words(ident) & _words(last):
            hits += 1

    if seen and hits * 4 < seen * 3:
        raise SystemExit(
            f"type table looks misaligned: only {hits} of {seen} monsters ({100 * hits // seen}%) "
            "have a type named after them. A correct table measured 88%, a shifted one 55%."
        )


def _words(text):
    return set(re.findall(r"[A-Z][a-z]+", text))


def blood_names(monsters, bloods):
    """Row number to blood type id, for the rows monsters actually use - 37 of 56."""
    wanted = {one.get("blood", 0) for one in monsters.values()}

    named = {}
    for row in sorted(wanted):
        if 0 <= row < len(bloods):
            ident = (bloods[row].get("Id") or "").strip()
            if ident:
                named[str(row)] = ident
    return named


# Monsters whose blood type is known exactly, and what it must be. A Risen Farmhand is a
# rotting corpse and bleeds RotBlood; Yama bleeds plain Blood.
BLOOD_ANCHORS = {
    "Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium": "RotBlood",
    "Metadata/Monsters/YamaBoss/YamaBoss": "Blood",
    # The self-verifying one: the game named this blood type after the monster that has it, the
    # same way MonsterTypes names its rows. It is also the only anchor here whose row has a
    # neighbour in every direction, so it is what catches a shift the other two sit through.
    "Metadata/Monsters/Baron/BaronBossCorruptedWolfForm": "GeonorSpecificBlood",
}


def prove_blood(monsters, named):
    """Named monsters resolve to exactly the blood they have, or the table is misaligned.

    WHY EXACT NAMES AND NOT A WORD MATCH, which is what this check was first written as and why
    it had to be replaced. 52 of the 56 rows have "blood" in their name, and the table is ordered
    in near-duplicate runs - Blood, BloodNoDeathBlood, BloodNoCorpseStainEPK, then BugBlood,
    BugBloodNoCorpseStainEPK. A check asking "does the name contain the word blood" is satisfied
    by almost any row: shifted by one it still passed at 85%, and the file was written.

    That is the exact failure this project warns about - a check a wrong value passes is worse
    than no check, because it launders the wrong value as verified. Exact equality against a
    monster whose blood is known cannot do that: shift either way and RotBlood becomes
    InsectBloodNoCorpseStainEPK or RotBloodNoCorpseStainEPK, and both are simply not RotBlood.

    The count check behind it is the other half: row 0 is plain Blood and 1092 monsters carry it,
    more than any other, and no shift leaves the commonest row named Blood.
    """
    for path, expected in BLOOD_ANCHORS.items():
        one = monsters.get(path)
        if one is None or "blood" not in one:
            continue

        got = named.get(str(one["blood"]), "")
        if got != expected:
            raise SystemExit(
                f"blood table looks misaligned: {path} should bleed {expected!r} and resolves "
                f"to {got!r}."
            )

    carried = {}
    for one in monsters.values():
        if "blood" in one:
            name = named.get(str(one["blood"]), "")
            carried[name] = carried.get(name, 0) + 1

    if carried:
        commonest = max(carried, key=lambda name: carried[name])
        if commonest != "Blood":
            raise SystemExit(
                f"blood table looks misaligned: the commonest blood type is {commonest!r} on "
                f"{carried[commonest]} monsters, and it should be 'Blood'."
            )


def resistance_names(types, resistances):
    """Row number to the resistance profile's name.

    ONLY THE NAME. Each row also carries 32 numeric columns - Fire1..Fire5, Cold1..Cold5 and so
    on, some of them arrays - and what the numbered tiers mean is not something this export
    settles. MinorColdResist reads 30, 30, 30 across the first three and MajorColdResist reads
    75, 60, 50, which is consistent with area tiers and with several other things. The name
    already says what a reader wants; a number here under a guessed label would be the same
    mistake as calling AttackSpeed a percentage.
    """
    wanted = set()
    for one in types.values():
        wanted.update(one.get("resistances", []))

    named = {}
    for row in sorted(wanted):
        if 0 <= row < len(resistances):
            ident = (resistances[row].get("Id") or "").strip()
            if ident:
                named[str(row)] = ident
    return named


def prove_resistances(monsters, types, named, tags):
    """A fire-themed monster RESISTS fire rather than being weak to it.

    ELEMENT AND POLARITY TOGETHER, which is what makes this discriminate at all. The table runs
    in fours - MinorColdResist, MajorColdResist, MinorColdVuln, MajorColdVuln, then the same for
    fire - so a shift of one keeps the element three times in four. Asking only "does it mention
    cold" is nearly vacuous here, the same way the first blood check was.

    Asking whether the monster RESISTS its own element separates cleanly, because half the shifts
    turn a resistance into a vulnerability. Measured over the 346 monsters that are element-themed
    and whose type carries a profile:

        correct     77%
        shifted -1  35%
        shifted +1  53%

    The floor is 65%, which no shift in either direction reaches.
    """
    if not tags:
        return

    elements = ("fire", "cold", "lightning", "chaos")
    hits = seen = 0

    for one in monsters.values():
        kind = types.get(str(one.get("type", -1)))
        if kind is None:
            continue

        carried = {tags.get(str(row), "") for row in one.get("tags", [])}
        affinity = [element for element in elements if f"{element}_affinity" in carried]
        if not affinity:
            continue

        profiles = [named.get(str(row), "").lower() for row in kind.get("resistances", [])]
        profiles = [name for name in profiles if name]
        if not profiles:
            continue

        seen += 1
        if any(
            element in name and "resist" in name for name in profiles for element in affinity
        ):
            hits += 1

    if seen and hits * 20 < seen * 13:
        raise SystemExit(
            f"resistance table looks misaligned: only {hits} of {seen} element-themed monsters "
            f"({100 * hits // seen}%) resist their own element. A correct table measured 77%, and "
            "a shift measured 35% one way and 53% the other."
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
    for flag in ("--skills", "--mods", "--stats", "--tags", "--types", "--blood", "--resistances"):
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
    tags = read(extra["tags"]) if "tags" in extra else []
    types = read(extra["types"]) if "types" in extra else []
    bloods = read(extra["blood"]) if "blood" in extra else []
    resistances = read(extra["resistances"]) if "resistances" in extra else []

    named_skills = skill_names(monsters, skills) if skills else {}
    if named_skills:
        prove_alignment(monsters, named_skills)

    named_mods = mod_meanings(monsters, mods, stats) if mods and stats else {}
    drop_filler(monsters, mods)

    named_tags = tag_names(monsters, tags) if tags else {}
    if named_tags:
        prove_tags(monsters, named_tags)

    named_types = type_stats(monsters, types) if types else {}
    if named_types:
        prove_types(monsters, named_types)

    named_blood = blood_names(monsters, bloods) if bloods else {}
    if named_blood:
        prove_blood(monsters, named_blood)

    named_resistances = resistance_names(named_types, resistances) if resistances else {}
    if named_resistances:
        prove_resistances(monsters, named_types, named_resistances, named_tags)

    payload = {
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": "MonsterVarieties, exported from the game's data tables",
        "skills": named_skills,
        "modifiers": named_mods,
        "tags": named_tags,
        "types": named_types,
        "blood": named_blood,
        "resistances": named_resistances,
        "monsters": monsters,
    }

    with open(sys.argv[2], "w", encoding="utf-8") as handle:
        json.dump(payload, handle, separators=(",", ":"), sort_keys=True)
        handle.write("\n")

    total = len(monsters)
    print(
        f"{total} monsters, {len(named_skills)} skills, {len(named_mods)} modifiers, "
        f"{len(named_tags)} tags, {len(named_types)} types, {len(named_blood)} blood types, "
        f"{len(named_resistances)} resistance profiles"
    )
    fields = {}
    for one in monsters.values():
        for key in one:
            fields[key] = fields.get(key, 0) + 1
    for key, count in sorted(fields.items(), key=lambda kv: -kv[1]):
        print(f"  {key:14s} {count:5d}  {100 * count / total:5.1f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
