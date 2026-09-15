#!/usr/bin/env python3
"""
poe_tools.py - unified PoEformance data-tooling CLI.

Consolidates the former individual tools/*.py build/generator scripts into a
single script with subcommands. Shared helpers (OOZ bundle decompression,
murmur2 path hashing, bundle-index parsing, dat-schema loading, CSV reading)
live once at the top instead of being copy-pasted per script.

Heavy/optional dependencies are imported lazily, so the common CSV pipeline runs
even when they are absent:
  * pyooz (ooz) - only the binary extractors (extract-worldareas /
    extract-inventories) need it.
  * Pillow (PIL) - only make-icon needs it.

Subcommands
  build-all            Run the full CSV->TSV pipeline (the five CSV steps below,
                       in order). Mirrors dump_tables2.bat step 3.

  extract-stats        Stats.csv              -> data/stat_name_map.tsv
  extract-mods         Mods.csv               -> data/mod_name_map.tsv
  extract-monsters     MonsterVarieties.csv   -> data/monster_name_map.tsv
  build-item-names     Words/Mods/BaseItemTypes/... CSVs -> 6 item & skill TSVs
  build-stat-desc      extracted *.csd files  -> data/stat_desc_map.tsv

  extract-worldareas   WorldAreas.datc64   (game bundles) -> data/world_area_name_map.tsv
  extract-inventories  Inventories.datc64  (game bundles) -> data/inventory_type_map.tsv

  gen-anim             ahk/AnimationID.ahk map -> ui/animation_names.js
  gen-ct               poe2_ce_inspector.lua    -> tools/PoE2_Inspector.CT
  make-icon            tools/poeformance_logo.png -> ui/poeformance.ico

Usage
  python poe_tools.py <subcommand> [args...]
  python poe_tools.py build-all
  python poe_tools.py extract-worldareas "H:\\...\\Path of Exile 2"
"""

import argparse
import csv
import json
import os
import re
import struct
import sys
import time
import urllib.request
from pathlib import Path


# ===========================================================================
# Common paths (all derived from this script's location, never the cwd, so the
# subcommands behave the same regardless of where python is invoked from).
# ===========================================================================
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.dirname(SCRIPT_DIR)
DATA_DIR = os.path.join(PROJECT_DIR, "data")
UI_DIR = os.path.join(PROJECT_DIR, "ui")
SCHEMA_PATH = os.path.join(SCRIPT_DIR, "schema.min.json")

# Where poe_data_tools dumps its CSV tables / extracted CSD files.
DEFAULT_CSV_DIR = os.path.join(DATA_DIR, "raw_csv", "data", "balance")
DEFAULT_EXTRACTED_DIR = os.path.join(DATA_DIR, "raw_extracted")

# Default game install (the binary extractors fall back to this when no path is
# given). Override by passing a path argument to those subcommands.
DEFAULT_GAME_DIR = r"H:\SteamLibrary\steamapps\common\Path of Exile 2"


class ToolError(Exception):
    """Raised by a subcommand when it cannot complete; the CLI turns it into a
    printed message + non-zero exit code, and build-all records it and moves on."""


def _now_utc():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


# ===========================================================================
# Shared CSV helpers (used by the CSV-based build commands)
# ===========================================================================
def read_csv(csv_dir, table_name, required_cols=None):
    """Read <csv_dir>/<table_name>.csv into a list of row dicts.

    Warns (does not fail) on missing required columns so a renamed column does
    not abort the whole build. Returns [] when the file is absent.
    """
    path = os.path.join(csv_dir, f"{table_name}.csv")
    if not os.path.isfile(path):
        print(f"  WARNING: {path} not found")
        return []

    with open(path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        if required_cols:
            missing = set(required_cols) - set(reader.fieldnames or [])
            if missing:
                print(f"  WARNING: {table_name}.csv missing columns: {missing}")
                print(f"  Available: {(reader.fieldnames or [])[:20]}")
        rows = list(reader)

    print(f"  {table_name}.csv: {len(rows):,} rows")
    return rows


def safe_int(val, default=0):
    """Parse an int from a CSV value, tolerating empty strings and float text."""
    if not val or val == "":
        return default
    try:
        return int(val)
    except ValueError:
        try:
            return int(float(val))
        except ValueError:
            return default


def parse_array_field(val):
    """Parse a poe_data_tools array field like '[1, 2, 3]' into a list of ints."""
    if not val or val in ("", "[]"):
        return []
    val = val.strip("[] ")
    if not val:
        return []
    result = []
    for item in val.split(","):
        item = item.strip()
        if item:
            try:
                result.append(int(item))
            except ValueError:
                try:
                    result.append(int(float(item)))
                except ValueError:
                    pass
    return result


def _missing_csv_hint(csv_path, table, csv_dir):
    """Build the 'CSV not found, run poe_data_tools first' error message."""
    return (
        f"CSV not found: {csv_path}\n"
        f"Generate it first with poe_data_tools:\n"
        f'  poe_data_tools --patch 2 dump-tables "Data/Balance/{table}.datc64" '
        f'--output "{csv_dir}"'
    )


# ===========================================================================
# Shared bundle / binary helpers (only the binary extractors use these)
# OOZ is imported lazily so CSV-only commands work without pyooz installed.
# ===========================================================================
_OOZ_CANDIDATES = [
    r"C:\Users\m0nsu\AppData\Local\Packages\PythonSoftwareFoundation.Python.3.13_qbz5n2kfra8p0\LocalCache\local-packages\Python313\site-packages",
]
_ooz_mod = None


def _get_ooz():
    """Lazily import and cache the OOZ decompressor (pip install pyooz).

    Search order: already importable -> $OOZ_PATH -> known local-packages path.
    Raises ToolError with install hints when none work.
    """
    global _ooz_mod
    if _ooz_mod is not None:
        return _ooz_mod
    try:
        import ooz  # noqa: F401
        _ooz_mod = ooz
        return _ooz_mod
    except ImportError:
        pass

    search = []
    env_path = os.environ.get("OOZ_PATH")
    if env_path:
        search.append(env_path)
    search.extend(_OOZ_CANDIDATES)
    for p in search:
        if p and os.path.isdir(p) and p not in sys.path:
            sys.path.insert(0, p)
    try:
        import ooz
        _ooz_mod = ooz
        return _ooz_mod
    except ImportError:
        raise ToolError(
            'the "ooz" module is required to decompress PoE bundles.\n'
            "  Install it with:  pip install pyooz\n"
            "  (or set OOZ_PATH to the directory containing the ooz module)"
        )


def decompress_bundle(bundle_bytes: bytes) -> bytes:
    """Decompress a full PoE2 bundle (.bundle.bin) file."""
    ooz_mod = _get_ooz()
    uncomp_size = struct.unpack_from("<I", bundle_bytes, 0)[0]
    chunk_count = struct.unpack_from("<I", bundle_bytes, 36)[0]
    chunk_size = struct.unpack_from("<I", bundle_bytes, 40)[0]
    chunk_sizes = struct.unpack_from(f"<{chunk_count}I", bundle_bytes, 60)
    data_start = 60 + chunk_count * 4
    result = bytearray()
    offset = data_start
    remaining = uncomp_size
    for cs in chunk_sizes:
        dc = min(chunk_size, remaining)
        dec = ooz_mod.decompress(bundle_bytes[offset:offset + cs], dc)
        result.extend(dec)
        offset += cs
        remaining -= dc
    return bytes(result)


def decompress_bundle_partial(bundle_bytes: bytes, file_offset: int, file_size: int) -> bytes:
    """Decompress only the portion of a bundle needed for a specific file."""
    ooz_mod = _get_ooz()
    uncomp_size = struct.unpack_from("<I", bundle_bytes, 0)[0]
    chunk_count = struct.unpack_from("<I", bundle_bytes, 36)[0]
    chunk_size = struct.unpack_from("<I", bundle_bytes, 40)[0]
    chunk_sizes = struct.unpack_from(f"<{chunk_count}I", bundle_bytes, 60)
    data_start = 60 + chunk_count * 4

    file_end = file_offset + file_size
    first_chunk = file_offset // chunk_size
    last_chunk = (file_end - 1) // chunk_size
    last_chunk = min(last_chunk, chunk_count - 1)

    comp_offset = data_start
    for i in range(first_chunk):
        comp_offset += chunk_sizes[i]

    result = bytearray()
    remaining = uncomp_size - first_chunk * chunk_size
    for i in range(first_chunk, last_chunk + 1):
        dc = min(chunk_size, remaining)
        dec = ooz_mod.decompress(bundle_bytes[comp_offset:comp_offset + chunk_sizes[i]], dc)
        result.extend(dec)
        comp_offset += chunk_sizes[i]
        remaining -= dc

    chunk_start = first_chunk * chunk_size
    local_start = file_offset - chunk_start
    return bytes(result[local_start:local_start + file_size])


def murmur2_64a(data: bytes, seed: int = 0x1337B33F) -> int:
    """64-bit Murmur2 hash (PoE2 path hashing algorithm)."""
    M = 0xC6A4A7935BD1E995
    R = 47
    mask64 = 0xFFFFFFFFFFFFFFFF
    h = (seed ^ (len(data) * M)) & mask64
    for i in range(0, len(data) - 7, 8):
        k = struct.unpack_from("<Q", data, i)[0]
        k = (k * M) & mask64
        k ^= k >> R
        k = (k * M) & mask64
        h ^= k
        h = (h * M) & mask64
    rem = len(data) & 7
    if rem:
        # XOR each remainder byte at its little-endian bit position
        a = len(data) - rem
        if rem >= 7: h ^= data[a + 6] << 48
        if rem >= 6: h ^= data[a + 5] << 40
        if rem >= 5: h ^= data[a + 4] << 32
        if rem >= 4: h ^= data[a + 3] << 24
        if rem >= 3: h ^= data[a + 2] << 16
        if rem >= 2: h ^= data[a + 1] << 8
        h ^= data[a + 0]
        h = (h * M) & mask64
    h ^= h >> R
    h = (h * M) & mask64
    h ^= h >> R
    return h


def poe_path_hash(path: str) -> int:
    """Compute the PoE2 bundle path hash for a given file path.

    PoE2 bundle index uses murmur64a(path.toLowerCase()) - no ++ suffix,
    no backslash conversion, just lowercase UTF-8 encoding.
    See: poe-dat-viewer/lib/src/bundles/index-bundle.ts getFileInfo()
    """
    return murmur2_64a(path.lower().encode("utf-8"))


def parse_index(idx_data: bytes):
    """Parse decompressed _.index.bin data. Returns (bundles, file_table)."""
    pos = 0
    bundle_count = struct.unpack_from("<I", idx_data, pos)[0]; pos += 4
    bundles = []
    for _ in range(bundle_count):
        name_len = struct.unpack_from("<I", idx_data, pos)[0]; pos += 4
        name = idx_data[pos:pos + name_len].decode("utf-8"); pos += name_len
        unc_size = struct.unpack_from("<I", idx_data, pos)[0]; pos += 4
        bundles.append({"name": name, "uncompressed_size": unc_size})

    file_count = struct.unpack_from("<I", idx_data, pos)[0]; pos += 4
    file_table = {}  # hash -> (bundle_idx, file_offset, file_size)
    for _ in range(file_count):
        path_hash, bundle_idx, file_offset, file_size = struct.unpack_from("<QIII", idx_data, pos)
        pos += 20
        file_table[path_hash] = (bundle_idx, file_offset, file_size)
    return bundles, file_table


# .datc64 column sizes (64-bit row/foreignrow references)
TYPE_SIZES_DATC64 = {
    "string": 8, "bool": 1,
    "i8": 1, "u8": 1, "i16": 2, "u16": 2,
    "i32": 4, "u32": 4, "f32": 4, "enumrow": 4, "rid": 4,
    "row": 8,          # 64-bit row reference in .datc64
    "foreignrow": 16,  # 64-bit table ref + 64-bit row ref in .datc64
    "_": 0,
}
_ARRAY_SIZE_DATC64 = 16  # 8-byte offset + 8-byte count for array columns


def _col_size_datc64(col: dict) -> int:
    if col.get("array", False):
        return _ARRAY_SIZE_DATC64
    return TYPE_SIZES_DATC64.get(col["type"], 0)


def get_dat_column_layout(schema: dict, table_name: str):
    """Return (columns, row_size) from schema for the named PoE2 table.

    Prefers the PoE2 table (validFor & 2), falls back to any table with that
    name. Uses .datc64 sizes (row=8, foreignrow=16, array=16). Each column dict
    carries name/type/offset/size.
    """
    candidates = [t for t in schema["tables"] if t["name"] == table_name]
    if not candidates:
        raise ToolError(f"Table '{table_name}' not found in schema")
    table = next((t for t in candidates if t.get("validFor", 0) & 2), candidates[0])
    offset = 0
    cols = []
    for col in table["columns"]:
        sz = _col_size_datc64(col)
        cols.append({"name": col.get("name") or "", "type": col["type"], "offset": offset, "size": sz})
        offset += sz
    return cols, offset


SCHEMA_URL = "https://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema.min.json"
SCHEMA_MAX_AGE_HOURS = 24


def ensure_schema(schema_path: str) -> dict:
    """Load the dat-schema from disk; download from GitHub if missing or stale."""
    needs_download = True
    if os.path.isfile(schema_path):
        age_hours = (time.time() - os.path.getmtime(schema_path)) / 3600
        if age_hours < SCHEMA_MAX_AGE_HOURS:
            needs_download = False

    if needs_download:
        print("Downloading latest schema from GitHub ...")
        try:
            req = urllib.request.Request(SCHEMA_URL, headers={"User-Agent": "poeformance/1.0"})
            with urllib.request.urlopen(req, timeout=30) as resp:
                data = resp.read()
            with open(schema_path, "wb") as f:
                f.write(data)
            print(f"  Saved {len(data):,} bytes to {schema_path}")
        except Exception as e:
            if os.path.isfile(schema_path):
                print(f"  WARNING: Download failed ({e}), using cached schema.")
            else:
                raise ToolError(f"Schema download failed and no cached schema: {e}")

    with open(schema_path, "r", encoding="utf-8") as f:
        schema = json.load(f)
    print(f'  Schema version: {schema.get("version", "?")} (createdAt={schema.get("createdAt", "?")})')
    return schema


def load_dat_table(game_dir, schema, table_name, path_candidates):
    """Locate, decompress and return a .datc64 file's raw bytes from the bundles.

    Walks _.index.bin to find the first matching path in path_candidates, then
    partial-decompresses just that file's slice. Returns
    (dat_bytes, target_path, bundle_name). Raises ToolError when not found.
    """
    bundles2 = os.path.join(game_dir, "Bundles2")

    index_path = os.path.join(bundles2, "_.index.bin")
    print(f"Loading index: {index_path}")
    with open(index_path, "rb") as f:
        idx_bundle = f.read()
    idx_data = decompress_bundle(idx_bundle)
    bundles, file_table = parse_index(idx_data)
    print(f"  {len(bundles)} bundles, {len(file_table):,} files")

    target_hash = None
    target_path = None
    for c in path_candidates:
        h = poe_path_hash(c)
        print(f"  Trying {c!r} -> hash {h:016x} ... ", end="")
        if h in file_table:
            target_hash, target_path = h, c
            print("FOUND!")
            break
        print("not found")
    if target_hash is None:
        raise ToolError(f"{table_name} table not found in bundle index! Tried: {path_candidates}")

    bundle_idx, file_offset, file_size = file_table[target_hash]
    bundle_name = bundles[bundle_idx]["name"]
    print(f"  Found in bundle #{bundle_idx} ({bundle_name}) at offset {file_offset}, size {file_size:,}")

    bundle_file = os.path.join(bundles2, bundle_name + ".bundle.bin")
    if not os.path.isfile(bundle_file):
        raise ToolError(f"Bundle file not found: {bundle_file}")
    with open(bundle_file, "rb") as f:
        bundle_bytes = f.read()
    dat_bytes = decompress_bundle_partial(bundle_bytes, file_offset, file_size)
    print(f"  Got {len(dat_bytes):,} bytes of dat data")
    return dat_bytes, target_path, bundle_name


# ===========================================================================
# extract-stats : Stats.csv -> data/stat_name_map.tsv
# ===========================================================================
def extract_stats(csv_dir=None, output_tsv=None):
    csv_dir = csv_dir or DEFAULT_CSV_DIR
    output_tsv = output_tsv or os.path.join(DATA_DIR, "stat_name_map.tsv")

    csv_path = os.path.join(csv_dir, "Stats.csv")
    if not os.path.isfile(csv_path):
        raise ToolError(_missing_csv_hint(csv_path, "Stats", csv_dir))

    entries = []
    with open(csv_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        if "Id" not in (reader.fieldnames or []):
            raise ToolError(f"CSV missing 'Id' column. Found: {(reader.fieldnames or [])[:10]}")
        for row_idx, row in enumerate(reader):
            stat_id = row.get("Id", "").strip()
            if stat_id:
                entries.append((row_idx, stat_id))

    if not entries:
        raise ToolError("No stat entries found in CSV")

    os.makedirs(os.path.dirname(output_tsv), exist_ok=True)
    with open(output_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# stat_name_map.tsv - generated by poe_tools.py (extract-stats)\n")
        f.write(f"# source: {csv_path}\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# key=row_index (0-based), matches PoE2 character stat pair keys in memory\n")
        for row_idx, stat_id in entries:
            f.write(f"{row_idx}\t{stat_id}\n")

    print(f"Extracted {len(entries):,} stats -> {output_tsv}")
    return 0


# ===========================================================================
# extract-mods : Mods.csv -> data/mod_name_map.tsv
# (3-column form; build-item-names later rewrites this file with a tier_word
#  4th column, so in build-all the richer version wins.)
# ===========================================================================
def extract_mods(csv_dir=None, output_tsv=None):
    csv_dir = csv_dir or DEFAULT_CSV_DIR
    output_tsv = output_tsv or os.path.join(DATA_DIR, "mod_name_map.tsv")

    csv_path = os.path.join(csv_dir, "Mods.csv")
    if not os.path.isfile(csv_path):
        raise ToolError(_missing_csv_hint(csv_path, "Mods", csv_dir))

    entries = []
    with open(csv_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        required = {"Id", "Name", "GenerationType"}
        missing = required - set(reader.fieldnames or [])
        if missing:
            raise ToolError(f"CSV missing columns: {missing}. Found: {(reader.fieldnames or [])[:15]}")
        for row in reader:
            mod_id = row.get("Id", "").strip()
            if not mod_id:
                continue
            name = row.get("Name", "").strip()
            gen_type = safe_int(row.get("GenerationType", "0"), 0)
            entries.append((mod_id, name, gen_type))

    if not entries:
        raise ToolError("No mod entries found in CSV")

    os.makedirs(os.path.dirname(output_tsv), exist_ok=True)
    with open(output_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# mod_name_map.tsv - generated by poe_tools.py (extract-mods)\n")
        f.write(f"# source: {csv_path}\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# columns: mod_id\tname\tgen_type(1=Prefix,2=Suffix)\n")
        for mod_id, name, gen_type in entries:
            f.write(f"{mod_id}\t{name}\t{gen_type}\n")

    with_name = sum(1 for _, n, _ in entries if n)
    print(f"Extracted {len(entries):,} mods ({with_name:,} with names) -> {output_tsv}")
    return 0


# ===========================================================================
# extract-monsters : MonsterVarieties.csv -> data/monster_name_map.tsv
# ===========================================================================
def _mon_normalize_key(s):
    return (s or "").strip().replace("\\", "/").lower()


def _mon_add_mapping(mapping, key, value):
    k = _mon_normalize_key(key)
    v = (value or "").strip()
    if not k or not v:
        return
    if k not in mapping:
        mapping[k] = v


def extract_monsters(csv_dir=None, output_tsv=None):
    csv_dir = csv_dir or DEFAULT_CSV_DIR
    output_tsv = output_tsv or os.path.join(DATA_DIR, "monster_name_map.tsv")

    csv_path = os.path.join(csv_dir, "MonsterVarieties.csv")
    if not os.path.isfile(csv_path):
        raise ToolError(_missing_csv_hint(csv_path, "MonsterVarieties", csv_dir))

    mapping = {}
    with open(csv_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        fields = reader.fieldnames or []
        if "Id" not in fields or "Name" not in fields:
            raise ToolError(
                f"CSV missing required columns. Found: {fields[:10]}... Expected 'Id' and 'Name'"
            )
        for row in reader:
            monster_id = row.get("Id", "")
            monster_name = row.get("Name", "")
            if not monster_id or not monster_name:
                continue
            # Full path key (e.g. "metadata/monsters/...")
            _mon_add_mapping(mapping, monster_id, monster_name)
            # Short basename key (e.g. "abyssalcrystalwaller")
            short = monster_id.rsplit("/", 1)[-1]
            _mon_add_mapping(mapping, short, monster_name)
            # Trailing underscore variant
            _mon_add_mapping(mapping, short.rstrip("_"), monster_name)

    if not mapping:
        raise ToolError("no monster name mappings extracted")

    os.makedirs(os.path.dirname(output_tsv), exist_ok=True)
    with open(output_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# monster_name_map.tsv - generated by poe_tools.py (extract-monsters)\n")
        f.write(f"# source: {csv_path}\n")
        f.write(f"# generated: {_now_utc()}\n")
        for k in sorted(mapping.keys()):
            f.write(f"{k}\t{mapping[k]}\n")

    print(f"Extracted {len(mapping):,} mappings -> {output_tsv}")
    return 0


# ===========================================================================
# build-item-names : Words/Mods/BaseItemTypes/... CSVs -> 6 item & skill TSVs
#   mod_name_map.tsv          mod_id  name  gen_type  tier_word
#   base_item_name_map.tsv    metadata_path  display_name
#   unique_name_map.tsv       unique_name  gold_price
#   unique_item_name_map.tsv  metadata_path  unique_name
#   unique_ivi_name_map.tsv   ivi_id  unique_name
#   skill_name_map.tsv        granted_effect_id  displayed_name  icon_ddsfile
# ===========================================================================
def _iname_extract_words(csv_dir):
    """Returns {row_index: {'wordlist': id, 'text': str}}"""
    print("\n--- Words ---")
    rows = read_csv(csv_dir, "Words", ["Wordlist", "Text"])
    words = {}
    for i, row in enumerate(rows):
        wordlist_id = safe_int(row.get("Wordlist", ""), -1)
        text = row.get("Text", "").strip()
        words[i] = {"wordlist": wordlist_id, "text": text}

    wl6_count = sum(1 for v in words.values() if v["wordlist"] == 6)
    print(f"  Wordlist 6 (unique names): {wl6_count} entries")
    return words


def _iname_extract_mod_type_words(csv_dir, words_count):
    """Returns {row_index: [word_row_indices]}"""
    print("\n--- ModType ---")
    rows = read_csv(csv_dir, "ModType")
    if not rows:
        return {}

    # Find the unnamed array column that contains word references. In
    # poe_data_tools CSV, unnamed columns may appear as column_2, column_3, etc.
    # We try all array-like columns and pick the first with valid word indices.
    array_col = None
    for col_name in (rows[0].keys() if rows else []):
        sample_vals = [r.get(col_name, "") for r in rows[:50]]
        array_vals = [v for v in sample_vals if v.startswith("[")]
        if len(array_vals) > 5:
            valid = 0
            for v in array_vals[:20]:
                indices = parse_array_field(v)
                if indices and all(0 <= idx < words_count for idx in indices):
                    valid += 1
            if valid > 0 and (array_col is None):
                array_col = col_name
                print(f"  Using column '{col_name}' as tier-word array ({valid} valid samples)")

    result = {}
    for i, row in enumerate(rows):
        if array_col:
            indices = parse_array_field(row.get(array_col, ""))
            result[i] = [idx for idx in indices if idx < words_count]
        else:
            result[i] = []

    with_words = sum(1 for v in result.values() if v)
    print(f"  {len(result)} ModType rows, {with_words} with word references")
    return result


def _iname_extract_mods(csv_dir, words, modtype_words):
    """Returns list of (mod_id, name, gen_type, tier_word)."""
    print("\n--- Mods ---")
    rows = read_csv(csv_dir, "Mods", ["Id", "Name", "GenerationType", "ModType"])
    if not rows:
        return []

    modtype_count = max(modtype_words.keys()) + 1 if modtype_words else 0

    results = []
    for row in rows:
        mod_id = row.get("Id", "").strip()
        if not mod_id:
            continue

        name = row.get("Name", "").strip()
        gen_type = safe_int(row.get("GenerationType", ""), 0)

        # ModType is a foreignrow -> integer index
        modtype_row = safe_int(row.get("ModType", ""), -1)

        tier_word = ""
        if 0 <= modtype_row < modtype_count and modtype_row in modtype_words:
            for widx in modtype_words[modtype_row]:
                entry = words.get(widx, {})
                wl = entry.get("wordlist", -1)
                text = entry.get("text", "")
                if text and wl in (1, 3, 7) and not tier_word:
                    tier_word = text

        results.append((mod_id, name, gen_type, tier_word))

    with_name = sum(1 for _, n, _, _ in results if n)
    with_tier = sum(1 for _, _, _, t in results if t)
    print(f"  {len(results)} mods, {with_name} with name, {with_tier} with tier word")
    return results


def _iname_extract_base_items(csv_dir):
    """Returns list of (metadata_path, display_name)."""
    print("\n--- BaseItemTypes ---")
    rows = read_csv(csv_dir, "BaseItemTypes", ["Id", "Name"])
    if not rows:
        return []

    results = []
    for row in rows:
        item_id = row.get("Id", "").strip()
        if not item_id:
            continue
        name = row.get("Name", "").strip()
        results.append((item_id, name))

    print(f"  {len(results)} base items")
    return results


def _iname_extract_unique_gold_prices(csv_dir, words):
    """Returns list of (unique_name, gold_price)."""
    print("\n--- UniqueGoldPrices ---")
    rows = read_csv(csv_dir, "UniqueGoldPrices")
    if not rows:
        return []

    results = []
    for row in rows:
        # Name is a foreignrow -> Words index
        words_row = safe_int(row.get("Name", ""), -1)
        price = safe_int(row.get("Price", ""), 0)
        entry = words.get(words_row, {})
        name = entry.get("text", "") if isinstance(entry, dict) else ""
        if name:
            results.append((name, price))

    print(f"  {len(results)} unique gold prices")
    return results


def _iname_extract_unique_item_names(csv_dir, words):
    """Returns list of (metadata_path, unique_name).

    EXACT join: a base item maps to a unique ONLY when the base item's
    ItemVisualIdentity row equals the unique's ItemVisualIdentityKey row. (A
    fuzzy IVI-Id match would mislabel generic bases as uniques.)
    """
    print("\n--- Unique item name map (USL + BIT, exact IVI row) ---")

    usl_rows = read_csv(csv_dir, "UniqueStashLayout", ["WordsKey", "ItemVisualIdentityKey"])
    if not usl_rows:
        return []

    ivi_to_unique = {}  # ivi_row -> (name, is_alt); prefer non-alternate art
    for row in usl_rows:
        words_row = safe_int(row.get("WordsKey", ""), -1)
        ivi_row = safe_int(row.get("ItemVisualIdentityKey", ""), -1)
        is_alt = row.get("IsAlternateArt", "").strip().lower() in ("true", "1")

        entry = words.get(words_row, {})
        name = entry.get("text", "") if isinstance(entry, dict) else ""
        if not name or ivi_row < 0:
            continue

        existing = ivi_to_unique.get(ivi_row)
        if existing is None or (not is_alt and existing[1]):
            ivi_to_unique[ivi_row] = (name, is_alt)

    print(f"  {len(ivi_to_unique)} unique IVI rows")

    bit_rows = read_csv(csv_dir, "BaseItemTypes", ["Id", "ItemVisualIdentity"])
    if not bit_rows:
        return []

    results = []
    for row in bit_rows:
        item_id = row.get("Id", "").strip()
        if not item_id:
            continue
        ivi_row = safe_int(row.get("ItemVisualIdentity", ""), -1)
        match = ivi_to_unique.get(ivi_row)
        if match:
            results.append((item_id, match[0]))

    print(f"  Mapped {len(results)} base item paths to unique names (exact IVI)")
    return results


def _iname_extract_unique_ivi_names(csv_dir, words):
    """Returns list of (ivi_id, unique_name).

    The robust runtime key: the game exposes an item's IVI row at Base+0x30,
    whose Id string (column 0) uniquely identifies the unique even when several
    uniques share a base item.
    """
    print("\n--- Unique name by IVI Id (USL + ItemVisualIdentity) ---")

    ivi_rows = read_csv(csv_dir, "ItemVisualIdentity", ["Id"])
    if not ivi_rows:
        return []
    ivi_row_to_id = {}
    for i, row in enumerate(ivi_rows):
        ivi_id = row.get("Id", "").strip()
        if ivi_id:
            ivi_row_to_id[i] = ivi_id

    usl_rows = read_csv(csv_dir, "UniqueStashLayout", ["WordsKey", "ItemVisualIdentityKey"])
    if not usl_rows:
        return []

    by_id = {}  # ivi_id -> (name, is_alt); prefer non-alternate art
    for row in usl_rows:
        words_row = safe_int(row.get("WordsKey", ""), -1)
        ivi_row = safe_int(row.get("ItemVisualIdentityKey", ""), -1)
        is_alt = row.get("IsAlternateArt", "").strip().lower() in ("true", "1")

        entry = words.get(words_row, {})
        name = entry.get("text", "") if isinstance(entry, dict) else ""
        ivi_id = ivi_row_to_id.get(ivi_row, "")
        if not name or not ivi_id:
            continue

        existing = by_id.get(ivi_id)
        if existing is None or (not is_alt and existing[1]):
            by_id[ivi_id] = (name, is_alt)

    print(f"  Mapped {len(by_id)} IVI ids to unique names")
    return [(k, v[0]) for k, v in sorted(by_id.items())]


def _iname_extract_skill_names(csv_dir):
    """Returns list of (granted_effect_id, displayed_name, icon).

    Join: GrantedEffects.Id + GrantedEffects.ActiveSkill (row) -> ActiveSkills
    row -> DisplayedName / Icon_DDSFile. Skills with an empty DisplayedName are
    internal action skills and are skipped.
    """
    print("\n--- Skill name map (GrantedEffects + ActiveSkills) ---")

    as_rows = read_csv(csv_dir, "ActiveSkills", ["Id", "DisplayedName", "Icon_DDSFile"])
    if not as_rows:
        return []
    as_by_row = {}  # ActiveSkills row index -> (DisplayedName, Icon)
    for i, row in enumerate(as_rows):
        as_by_row[i] = (row.get("DisplayedName", "").strip(), row.get("Icon_DDSFile", "").strip())

    ge_rows = read_csv(csv_dir, "GrantedEffects", ["Id", "ActiveSkill"])
    if not ge_rows:
        return []

    results = []
    seen = set()
    for row in ge_rows:
        ge_id = row.get("Id", "").strip()
        as_ref = safe_int(row.get("ActiveSkill", ""), -1)
        if not ge_id or as_ref < 0 or ge_id in seen:
            continue
        match = as_by_row.get(as_ref)
        if not match:
            continue
        disp, icon = match
        if not disp:
            continue
        seen.add(ge_id)
        results.append((ge_id, disp, icon))

    print(f"  Mapped {len(results)} granted effects to skill names")
    return sorted(results)


def build_item_names(csv_dir=None):
    csv_dir = csv_dir or DEFAULT_CSV_DIR

    # Validate user-provided path is within the expected project data root.
    data_dir_real = os.path.realpath(DATA_DIR)
    csv_dir_real = os.path.realpath(csv_dir)
    if os.path.commonpath([data_dir_real, csv_dir_real]) != data_dir_real:
        raise ToolError(f"CSV directory must be inside '{data_dir_real}', got: {csv_dir}")
    csv_dir = csv_dir_real

    test_csv = os.path.join(csv_dir, "Words.csv")
    if not os.path.isfile(test_csv):
        raise ToolError(
            f"CSV directory not populated: {csv_dir}\n"
            "Run dump_tables.bat (or poe_tools.py via poe_data_tools) first to generate CSVs."
        )

    # Step 1-3: Words -> ModType -> Mods
    words = _iname_extract_words(csv_dir)
    modtype_words = _iname_extract_mod_type_words(csv_dir, len(words))
    mods = _iname_extract_mods(csv_dir, words, modtype_words)

    mod_tsv = os.path.join(DATA_DIR, "mod_name_map.tsv")
    with open(mod_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# mod_name_map.tsv - generated by poe_tools.py (build-item-names)\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# columns: mod_id\tname\tgen_type(1=Prefix,2=Suffix,3=Unique)\ttier_word\n")
        for mod_id, name, gen_type, tier_word in mods:
            f.write(f"{mod_id}\t{name}\t{gen_type}\t{tier_word}\n")
    print(f"\nWritten: {mod_tsv} ({len(mods)} entries)")

    # Step 4: Base item types
    base_items = _iname_extract_base_items(csv_dir)
    base_tsv = os.path.join(DATA_DIR, "base_item_name_map.tsv")
    with open(base_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# base_item_name_map.tsv - generated by poe_tools.py (build-item-names)\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# columns: metadata_path\tdisplay_name\n")
        for item_id, name in base_items:
            f.write(f"{item_id}\t{name}\n")
    print(f"Written: {base_tsv} ({len(base_items)} entries)")

    # Step 5: Unique names (all Words Wordlist 6), enriched with gold prices
    unique_names_all = sorted(
        {v["text"] for v in words.values() if v.get("wordlist") == 6 and v.get("text")}
    )
    unique_gold = _iname_extract_unique_gold_prices(csv_dir, words)
    gold_map = {name: price for name, price in unique_gold}

    unique_tsv = os.path.join(DATA_DIR, "unique_name_map.tsv")
    with open(unique_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# unique_name_map.tsv - generated by poe_tools.py (build-item-names)\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# Source: Words.csv Wordlist 6 (all PoE2 unique names)\n")
        f.write("# columns: unique_name\tgold_price\n")
        for name in unique_names_all:
            price = gold_map.get(name, "")
            f.write(f"{name}\t{price}\n")
    print(f"Written: {unique_tsv} ({len(unique_names_all)} unique names)")

    # Step 5b: Unique item names (metadata_path -> unique_name)
    unique_items = _iname_extract_unique_item_names(csv_dir, words)
    unique_item_tsv = os.path.join(DATA_DIR, "unique_item_name_map.tsv")
    with open(unique_item_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# unique_item_name_map.tsv - generated by poe_tools.py (build-item-names)\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# columns: metadata_path\tunique_name\n")
        for item_id, unique_name in unique_items:
            f.write(f"{item_id}\t{unique_name}\n")
    print(f"Written: {unique_item_tsv} ({len(unique_items)} entries)")

    # Step 5c: Unique names by ItemVisualIdentity Id (ivi_id -> unique_name)
    unique_ivi = _iname_extract_unique_ivi_names(csv_dir, words)
    unique_ivi_tsv = os.path.join(DATA_DIR, "unique_ivi_name_map.tsv")
    with open(unique_ivi_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# unique_ivi_name_map.tsv - generated by poe_tools.py (build-item-names)\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# Robust unique key: the game exposes the item's ItemVisualIdentity\n")
        f.write("# row at Base+0x30; its Id string (column 0) is this key.\n")
        f.write("# columns: ivi_id\tunique_name\n")
        for ivi_id, unique_name in unique_ivi:
            f.write(f"{ivi_id}\t{unique_name}\n")
    print(f"Written: {unique_ivi_tsv} ({len(unique_ivi)} entries)")

    # Step 6: Skill names (granted_effect_id -> displayed_name)
    skills = _iname_extract_skill_names(csv_dir)
    skill_tsv = os.path.join(DATA_DIR, "skill_name_map.tsv")
    with open(skill_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# skill_name_map.tsv - generated by poe_tools.py (build-item-names)\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# GrantedEffects.Id (read live from memory) -> ActiveSkills.DisplayedName.\n")
        f.write("# columns: granted_effect_id\tdisplayed_name\ticon_ddsfile\n")
        for ge_id, disp, icon in skills:
            f.write(f"{ge_id}\t{disp}\t{icon}\n")
    print(f"Written: {skill_tsv} ({len(skills)} entries)")

    print("\nDone! Files written:")
    for p in (mod_tsv, base_tsv, unique_tsv, unique_item_tsv, unique_ivi_tsv, skill_tsv):
        print(f"  {p}")
    return 0


# ===========================================================================
# build-stat-desc : extracted *.csd files -> data/stat_desc_map.tsv
# ===========================================================================
_WIKI_LINK_RE1 = re.compile(r"\[(?:[^\]|]+)\|([^\]]+)\]")  # [LinkText|Display] -> Display
_WIKI_LINK_RE2 = re.compile(r"\[([^\]|]+)\]")              # [Text] -> Text


def _csd_strip_wiki_links(text):
    text = _WIKI_LINK_RE1.sub(r"\1", text)
    text = _WIKI_LINK_RE2.sub(r"\1", text)
    return text


def _csd_parse(text):
    """Parse CSD format (both lang-block and non-lang-block variants).

    Returns list of (stat_ids_tuple, template_string).
    """
    results = []
    lines = text.splitlines()
    n = len(lines)
    i = 0

    while i < n:
        if lines[i].strip() != "description":
            i += 1
            continue

        i += 1
        while i < n and not lines[i].strip():
            i += 1
        if i >= n:
            break

        stat_line = lines[i].strip()
        m = re.match(r"^(\d+)\s+(.+)$", stat_line)
        if not m:
            i += 1
            continue

        stat_ids = tuple(m.group(2).split())
        i += 1

        english_text = None
        found_any_lang = False
        in_english = False

        while i < n:
            s = lines[i].strip()

            if s == "description":
                break
            if s in ("no_description",):
                i += 1
                break
            if s.startswith("include "):
                i += 1
                continue

            lm = re.match(r'^lang\s+"([^"]+)"', s)
            if lm:
                found_any_lang = True
                in_english = lm.group(1).lower() == "english"
                i += 1
                continue

            if '"' in s:
                fq = s.index('"')
                lq = s.rindex('"')
                if fq < lq:
                    raw_template = s[fq + 1:lq]
                    if english_text is None:
                        if in_english or not found_any_lang:
                            english_text = _csd_strip_wiki_links(raw_template)
                            if in_english:
                                i += 1
                                break

            i += 1

        if english_text is not None:
            results.append((stat_ids, english_text))

    return results


def _csd_decode_file(file_path):
    """Read a CSD file, handling UTF-16LE with BOM or UTF-8 fallback."""
    with open(file_path, "rb") as f:
        data = f.read()

    if data[:2] == b"\xff\xfe":
        data = data[2:]
    try:
        return data.decode("utf-16-le", errors="replace")
    except Exception:
        return data.decode("utf-8", errors="replace")


def _csd_collect_files(extracted_dir):
    """Find all .csd files under the extracted StatDescriptions directory."""
    csd_dir = Path(extracted_dir)
    candidates = [
        csd_dir / "Data" / "StatDescriptions",
        csd_dir / "data" / "statdescriptions",
        csd_dir,
    ]

    csd_files = []
    for base in candidates:
        if base.is_dir():
            found = list(base.rglob("*.csd"))
            if found:
                csd_files = found
                print(f"  Found {len(csd_files)} CSD files under {base}")
                break

    if not csd_files:
        print(f"  WARNING: No .csd files found under {extracted_dir}")
        print("  Expected structure: <dir>/Data/StatDescriptions/**/*.csd")

    return sorted(csd_files)


def build_stat_desc(extracted_dir=None, output_tsv=None):
    extracted_dir = extracted_dir or DEFAULT_EXTRACTED_DIR
    output_tsv = output_tsv or os.path.join(DATA_DIR, "stat_desc_map.tsv")

    csd_files = _csd_collect_files(extracted_dir)
    if not csd_files:
        raise ToolError(
            f"No CSD files found in {extracted_dir}\n"
            "Extract them first with poe_data_tools:\n"
            '  poe_data_tools --patch 2 extract "Data/StatDescriptions/**/*.csd" '
            f'--output "{extracted_dir}"'
        )

    # Priority: stat_descriptions.csd first (most general). First-found wins.
    priority_first = [f for f in csd_files if f.name == "stat_descriptions.csd"]
    priority_rest = [f for f in csd_files if f not in priority_first]
    ordered = priority_first + priority_rest

    all_entries = {}  # stat_id -> (template, arg_index, group_ids_tuple)
    total_parsed = 0

    print(f"\nParsing {len(ordered)} CSD files...")
    for csd_path in ordered:
        text = _csd_decode_file(csd_path)
        entries = _csd_parse(text)
        new_in_file = 0
        for stat_ids, template in entries:
            for idx, sid in enumerate(stat_ids):
                if sid not in all_entries:
                    all_entries[sid] = (template, idx, stat_ids)
                    new_in_file += 1
        total_parsed += len(entries)
        if new_in_file > 0:
            print(f"  {csd_path.name}: {len(entries)} blocks, {new_in_file} new stat_ids")

    print(f"\nTotal blocks parsed: {total_parsed}")
    print(f"Total unique stat_ids: {len(all_entries)}")

    # Add base_ aliases
    alias_added = 0
    for sid in list(all_entries.keys()):
        if sid.startswith("base_"):
            plain = sid[5:]
            if plain not in all_entries:
                all_entries[plain] = all_entries[sid]
                alias_added += 1
    print(f"Added {alias_added} base_* aliases")

    if not all_entries:
        raise ToolError("No stat descriptions found")

    os.makedirs(os.path.dirname(output_tsv), exist_ok=True)
    with open(output_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# stat_desc_map.tsv - generated by poe_tools.py (build-stat-desc)\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# Format: stat_id TAB template TAB arg_index TAB group_ids\n")
        f.write("# template placeholders: {0} {1} = stat values; wiki links stripped\n")
        f.write("# arg_index: which {N} this stat fills (0=first, 1=second, ...)\n")
        f.write("# group_ids: comma-separated group (multi-stat entries share a template)\n")
        for sid in sorted(all_entries):
            tmpl, idx, grp = all_entries[sid]
            group_str = ",".join(grp)
            f.write(f"{sid}\t{tmpl}\t{idx}\t{group_str}\n")

    print(f"\nWritten: {output_tsv} ({len(all_entries)} entries)")
    return 0


# ===========================================================================
# extract-worldareas : WorldAreas.datc64 (game bundles) -> world_area_name_map.tsv
# ===========================================================================
def _wa_find_fixed_boundary(dat_bytes):
    """Return (row_count, fixed_size, row_len) based on the aligned BB*8 separator."""
    row_count = struct.unpack_from("<I", dat_bytes, 0)[0]
    if row_count <= 0:
        return 0, 0, 0

    data = dat_bytes[4:]
    marker = b"\xbb" * 8
    pos = 0
    while True:
        idx = data.find(marker, pos)
        if idx < 0:
            raise ToolError("could not find variable-data boundary marker")
        if idx % row_count == 0:
            fixed_size = idx
            row_len = fixed_size // row_count
            return row_count, fixed_size, row_len
        pos = idx + 1


def _wa_read_utf16_var_string(data_variable, offset):
    """PoE datc64 string decoding (same strategy as pathofexile-dat)."""
    if offset < 0 or offset >= len(data_variable):
        return ""

    end = data_variable.find(b"\x00\x00\x00\x00", offset)
    while end != -1 and ((end - offset) % 2 != 0):
        end = data_variable.find(b"\x00\x00\x00\x00", end + 1)
    if end < 0:
        return ""

    raw = data_variable[offset:end]
    if not raw:
        return ""
    return raw.decode("utf-16-le", errors="ignore").strip()


def _wa_build_name_map(dat_bytes, id_offset, name_offset, maps_only=False):
    """Build {internal Id -> display Name} from WorldAreas.datc64."""
    row_count, fixed_size, row_len = _wa_find_fixed_boundary(dat_bytes)
    data_variable = dat_bytes[4 + fixed_size:]  # variable section (marker at offset 0)

    out = {}
    for row in range(row_count):
        row_base = 4 + row * row_len
        id_var_off = struct.unpack_from("<I", dat_bytes, row_base + id_offset)[0]
        name_var_off = struct.unpack_from("<I", dat_bytes, row_base + name_offset)[0]

        area_id = _wa_read_utf16_var_string(data_variable, id_var_off)
        area_name = _wa_read_utf16_var_string(data_variable, name_var_off)
        if not area_id or not area_name:
            continue
        if maps_only and not area_id.startswith("Map"):
            continue
        # First write wins (the PoE2 WorldArea Id is unique).
        if area_id not in out:
            out[area_id] = area_name
    return out


def extract_worldareas(game_dir=None, output_tsv=None, maps_only=False):
    game_dir = game_dir or DEFAULT_GAME_DIR
    output_tsv = output_tsv or os.path.join(DATA_DIR, "world_area_name_map.tsv")

    if not os.path.isdir(game_dir):
        raise ToolError(
            f"game directory not found: {game_dir}\n"
            "Usage: python poe_tools.py extract-worldareas [game_dir] [output_tsv] [--maps-only]"
        )

    print(f"Schema path: {SCHEMA_PATH}")
    schema = ensure_schema(SCHEMA_PATH)
    cols, _ = get_dat_column_layout(schema, "WorldAreas")
    id_col = next(c for c in cols if c["name"] == "Id")
    name_col = next(c for c in cols if c["name"] == "Name")
    print(f'WorldAreas: Id@{id_col["offset"]}, Name@{name_col["offset"]}')

    dat_bytes, target_path, bundle_name = load_dat_table(
        game_dir, schema, "WorldAreas",
        ["Data/WorldAreas.datc64", "Data/Balance/WorldAreas.datc64"],
    )

    print(f"Parsing WorldAreas.datc64 (maps_only={maps_only}) ...")
    mapping = _wa_build_name_map(dat_bytes, id_col["offset"], name_col["offset"], maps_only)
    print(f"  Extracted {len(mapping):,} area name mappings")
    if not mapping:
        raise ToolError("no WorldArea name mappings extracted. The dat format may have changed.")

    shown = 0
    for k in sorted(mapping):
        if k.startswith("Map"):
            print(f"  {k} -> {mapping[k]}")
            shown += 1
            if shown >= 5:
                break

    with open(output_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# world_area_name_map.tsv - generated by poe_tools.py (extract-worldareas)\n")
        f.write(f'# schema: {schema.get("version", "?")} / {schema.get("createdAt", "")}\n')
        f.write(f"# source: {target_path} in {bundle_name}\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write('# maps: internal WorldArea Id (e.g. "MapHiddenGrotto") -> in-game name (e.g. "Hidden Grotto")\n')
        f.write('# atlas maps are the rows whose Id starts with "Map".\n')
        f.write("# columns: internal_id\tdisplay_name\n")
        for k in sorted(mapping):
            f.write(f"{k}\t{mapping[k]}\n")

    print(f"Written: {output_tsv}  ({len(mapping)} entries)")
    return 0


# ===========================================================================
# extract-inventories : Inventories.datc64 (game bundles) -> inventory_type_map.tsv
# ===========================================================================
def _inv_read_utf16_string(dat_bytes, var_base, str_offset):
    """Resolve a .datc64 UTF-16LE string given a variable-section offset."""
    str_pos = var_base + str_offset
    if str_pos < 0 or str_pos >= len(dat_bytes):
        return ""
    end = str_pos
    while end + 1 < len(dat_bytes) and not (dat_bytes[end] == 0 and dat_bytes[end + 1] == 0):
        end += 2
    raw = dat_bytes[str_pos:end]
    try:
        return raw.decode("utf-16-le").strip()
    except Exception:
        return ""


_INV_DAT64_MAGIC = b"\xbb" * 8


def _inv_parse(dat_bytes, row_size, id_offset, invidkey_offset):
    """Parse Inventories.datc64. Returns list of dicts with dat_row/game_index/type/inventory_id_key."""
    num_rows = struct.unpack_from("<I", dat_bytes, 0)[0]

    # Locate the variable-data section (string offsets are relative to the magic).
    var_base = 4 + num_rows * row_size
    if var_base + 8 > len(dat_bytes) or dat_bytes[var_base:var_base + 8] != _INV_DAT64_MAGIC:
        magic_pos = dat_bytes.find(_INV_DAT64_MAGIC, 4)
        if magic_pos < 0:
            return []
        var_base = magic_pos

    rows = []
    for row in range(num_rows):
        row_base = 4 + row * row_size
        str_offset = struct.unpack_from("<I", dat_bytes, row_base + id_offset)[0]
        inv_type = _inv_read_utf16_string(dat_bytes, var_base, str_offset)
        inv_id_key = struct.unpack_from("<i", dat_bytes, row_base + invidkey_offset)[0]
        rows.append({
            "dat_row": row,
            "game_index": row + 1,
            "type": inv_type,
            "inventory_id_key": inv_id_key,
        })
    return rows


def extract_inventories(game_dir=None, output_tsv=None):
    game_dir = game_dir or DEFAULT_GAME_DIR
    output_tsv = output_tsv or os.path.join(DATA_DIR, "inventory_type_map.tsv")

    if not os.path.isdir(game_dir):
        raise ToolError(
            f"game directory not found: {game_dir}\n"
            "Usage: python poe_tools.py extract-inventories [game_dir] [output_tsv]"
        )

    print(f"Schema path: {SCHEMA_PATH}")
    schema = ensure_schema(SCHEMA_PATH)
    cols, row_size = get_dat_column_layout(schema, "Inventories")
    id_col = next(c for c in cols if c["name"] == "Id")
    invidkey_col = next((c for c in cols if c["name"] == "InventoryIdKey"), id_col)
    print(f'Inventories row_size={row_size}, Id@{id_col["offset"]}, '
          f'InventoryIdKey@{invidkey_col["offset"]}')

    dat_bytes, target_path, bundle_name = load_dat_table(
        game_dir, schema, "Inventories",
        ["Data/Balance/Inventories.datc64", "Data/Inventories.datc64"],
    )

    print("Parsing Inventories.datc64 ...")
    rows = _inv_parse(dat_bytes, row_size, id_col["offset"], invidkey_col["offset"])
    print(f"  Parsed {len(rows)} inventory rows")
    if not rows:
        raise ToolError("No inventory rows found. The dat format may have changed.")

    for r in rows[:5]:
        print(f'  dat_row={r["dat_row"]:3d}  game_index=0x{r["game_index"]:02X}  type={r["type"]}')

    with open(output_tsv, "w", encoding="utf-8", newline="\n") as f:
        f.write("# inventory_type_map.tsv - generated by poe_tools.py (extract-inventories)\n")
        f.write(f'# schema: {schema.get("version", "?")} / {schema.get("createdAt", "")}\n')
        f.write(f"# source: {target_path} in {bundle_name}\n")
        f.write(f"# generated: {_now_utc()}\n")
        f.write("# NOTE: game inventory index is 1-based (starts at 0x01);\n")
        f.write("#       dat_row is 0-based (starts at 0x00).  game_index = dat_row + 1.\n")
        f.write("# columns: game_index(decimal)\tdat_row(decimal)\ttype(Id)\tinventory_id_key\n")
        for r in rows:
            f.write(f'{r["game_index"]}\t{r["dat_row"]}\t{r["type"]}\t{r["inventory_id_key"]}\n')

    print(f"Written: {output_tsv}  ({len(rows)} entries)")
    return 0


# ===========================================================================
# gen-anim : ahk/AnimationID.ahk map -> ui/animation_names.js
# ===========================================================================
# Source of truth is the local, hand-maintained AnimationID map (name -> id)
# in ahk/AnimationID.ahk. It is more complete/current than GameHelper2's
# Animation.cs, so we parse it locally instead of fetching the upstream enum.
_ANIM_SRC = os.path.join(PROJECT_DIR, "ahk", "AnimationID.ahk")


def gen_anim(output_js=None, source_ahk=None):
    output_js = output_js or os.path.join(UI_DIR, "animation_names.js")
    source_ahk = source_ahk or _ANIM_SRC

    print(f"Reading AnimationID map from {source_ahk}")
    with open(source_ahk, "r", encoding="utf-8") as f:
        content = f.read()

    # Lines look like: "Idle" = 0x0,  →  capture (name, hex)
    entries = re.findall(r'"(\w+)"\s*=\s*(0x[0-9A-Fa-f]+)', content)
    if not entries:
        raise ToolError(f"no enum entries parsed from {source_ahk}")

    js_entries = []
    for name, hexval in entries:
        decimal = int(hexval, 16)
        readable = re.sub(r"(?<!^)(?=[A-Z])", " ", name)
        js_entries.append(f"{decimal}:{json.dumps(readable)}")

    js_obj = "const ANIM_NAMES={" + ",".join(js_entries) + "};"

    os.makedirs(os.path.dirname(output_js), exist_ok=True)
    with open(output_js, "w", encoding="utf-8") as f:
        f.write("// Auto-generated from ahk/AnimationID.ahk (gen-anim)\n")
        f.write("// Maps CastType (int) -> human-readable animation name\n")
        f.write(js_obj + "\n")
        f.write('function animName(id) { return ANIM_NAMES[id] || ("0x" + id.toString(16).toUpperCase()); }\n')

    print(f"Generated {len(js_entries)} entries -> {output_js}")
    name_map = {int(h, 16): n for n, h in entries}
    for k in [765, 766, 0, 1, 29]:
        name = name_map.get(k, "?")
        readable = re.sub(r"(?<!^)(?=[A-Z])", " ", name)
        print(f"  {k} (0x{k:X}) -> {readable}")
    return 0


# ===========================================================================
# gen-ct : poe2_ce_inspector.lua -> tools/PoE2_Inspector.CT
# ===========================================================================
_CT_BUTTONS = [
    (10, "[Btn] Scan + Refresh", "poe2_scan()\npoe2_refresh()"),
    (11, "[Btn] List Entities (20)", "poe2_list_entities(20)"),
    (12, "[Btn] Chain Only", "poe2_refresh()"),
    (13, "[Toggle] Live Refresh (2s)",
         "_liveTimer=createTimer(nil,false)\n_liveTimer.Interval=2000\n"
         "_liveTimer.OnTimer=function() poe2_refresh() end\n_liveTimer.Enabled=true\n"
         'print("[PoE2] Live refresh active")',
         'if _liveTimer then _liveTimer.Enabled=false _liveTimer.destroy() '
         '_liveTimer=nil print("[PoE2] Live refresh stopped") end'),
]


def _ct_btn_xml(btn):
    eid, desc, script = btn[0], btn[1], btn[2]
    drop = btn[3] if len(btn) > 3 else None
    drop_tag = f"""\n      <DropScript>{{$lua}}\n{drop}\n{{$asm}}\n</DropScript>""" if drop else ""
    return (
        f'      <CheatEntry><ID>{eid}</ID><Description>"{desc}"</Description>'
        f'<Script>{{$lua}}\n{script}\n{{$asm}}\n</Script>'
        f'{drop_tag}'
        f'<VariableType>Auto Assembler Script</VariableType></CheatEntry>'
    )


def gen_ct():
    root = Path(SCRIPT_DIR)
    lua_path = root / "poe2_ce_inspector.lua"
    if not lua_path.is_file():
        raise ToolError(f"Lua source not found: {lua_path}")
    lua = lua_path.read_text(encoding="utf-8")
    ct_out = root / "PoE2_Inspector.CT"

    btn_xml_all = "\n".join(_ct_btn_xml(b) for b in _CT_BUTTONS)

    ct = f"""<?xml version="1.0" encoding="utf-8"?>
<CheatTable CheatEngineTableVersion="45">
  <CheatEntries>
    <CheatEntry><ID>1</ID><Description>"=== PoE2 Inspector ==="</Description><GroupHeader>1</GroupHeader><Options moHideChildren="0"/><Entries>
      <CheatEntry><ID>2</ID><Description>"[INFO] Lua Engine -&gt; View -&gt; Output"</Description><GroupHeader>1</GroupHeader><Options moHideChildren="0"/><Entries/></CheatEntry>
{btn_xml_all}
      <CheatEntry><ID>20</ID><Description>"--- Dynamic addresses ---"</Description><GroupHeader>1</GroupHeader><Options moHideChildren="0"/><Entries/></CheatEntry>
    </Entries></CheatEntry>
  </CheatEntries>
  <LuaScript><![CDATA[
{lua}
]]></LuaScript>
</CheatTable>
"""

    ct_out.write_text(ct, encoding="utf-8")
    print(f"Written {ct_out}  ({ct_out.stat().st_size} bytes)")
    return 0


# ===========================================================================
# make-icon : tools/poeformance_logo.png -> ui/poeformance.ico
# (Pillow imported lazily so the rest of the CLI works without it.)
# ===========================================================================
def make_icon():
    try:
        from PIL import Image
    except ImportError:
        raise ToolError("Pillow is required for make-icon.  Install it with:  pip install pillow")

    src_png = os.path.join(SCRIPT_DIR, "poeformance_logo.png")
    out_ico = os.path.normpath(os.path.join(UI_DIR, "poeformance.ico"))

    if not os.path.exists(src_png):
        raise ToolError(
            f"Source PNG not found: {src_png}\n"
            "Place the PoEformance logo PNG at that path and re-run."
        )

    img = Image.open(src_png).convert("RGBA")
    sizes = [256, 128, 64, 48, 32, 24, 16]
    icons = [img.copy().resize((s, s), Image.LANCZOS) for s in sizes]
    icons[0].save(out_ico, format="ICO", icon_sizes=[(s, s) for s in sizes], append_images=icons[1:])
    print(f"Saved: {out_ico}")
    return 0


# ===========================================================================
# build-all : run the full CSV->TSV pipeline (mirrors dump_tables2.bat step 3)
# ===========================================================================
def build_all():
    steps = [
        ("extract-stats", extract_stats),
        ("extract-mods", extract_mods),
        ("extract-monsters", extract_monsters),
        ("build-item-names", build_item_names),
        ("build-stat-desc", build_stat_desc),
    ]
    failures = []
    for name, fn in steps:
        print(f"\n========== {name} ==========")
        try:
            fn()
        except Exception as e:  # keep going so one bad step doesn't abort the rest
            print(f"ERROR in {name}: {e}")
            failures.append(name)

    print()
    if failures:
        print(f"build-all finished with errors in: {', '.join(failures)}")
        return 1
    print("build-all: all steps OK")
    return 0


# ===========================================================================
# CLI dispatch
# ===========================================================================
def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="poe_tools.py",
        description="Unified PoEformance data-tooling CLI (see module docstring for details).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    sub = parser.add_subparsers(dest="command", required=True, metavar="<subcommand>")

    p = sub.add_parser("build-all", help="run the full CSV->TSV pipeline (5 steps)")
    p.set_defaults(func=lambda a: build_all())

    p = sub.add_parser("extract-stats", help="Stats.csv -> stat_name_map.tsv")
    p.add_argument("csv_dir", nargs="?")
    p.add_argument("output_tsv", nargs="?")
    p.set_defaults(func=lambda a: extract_stats(a.csv_dir, a.output_tsv))

    p = sub.add_parser("extract-mods", help="Mods.csv -> mod_name_map.tsv")
    p.add_argument("csv_dir", nargs="?")
    p.add_argument("output_tsv", nargs="?")
    p.set_defaults(func=lambda a: extract_mods(a.csv_dir, a.output_tsv))

    p = sub.add_parser("extract-monsters", help="MonsterVarieties.csv -> monster_name_map.tsv")
    p.add_argument("csv_dir", nargs="?")
    p.add_argument("output_tsv", nargs="?")
    p.set_defaults(func=lambda a: extract_monsters(a.csv_dir, a.output_tsv))

    p = sub.add_parser("build-item-names", help="item/skill CSVs -> 6 item & skill TSVs")
    p.add_argument("csv_dir", nargs="?")
    p.set_defaults(func=lambda a: build_item_names(a.csv_dir))

    p = sub.add_parser("build-stat-desc", help="extracted *.csd -> stat_desc_map.tsv")
    p.add_argument("extracted_dir", nargs="?")
    p.add_argument("output_tsv", nargs="?")
    p.set_defaults(func=lambda a: build_stat_desc(a.extracted_dir, a.output_tsv))

    p = sub.add_parser("extract-worldareas", help="WorldAreas.datc64 -> world_area_name_map.tsv")
    p.add_argument("game_dir", nargs="?")
    p.add_argument("output_tsv", nargs="?")
    p.add_argument("--maps-only", action="store_true", help="keep only atlas-map areas (Id starts with 'Map')")
    p.set_defaults(func=lambda a: extract_worldareas(a.game_dir, a.output_tsv, a.maps_only))

    p = sub.add_parser("extract-inventories", help="Inventories.datc64 -> inventory_type_map.tsv")
    p.add_argument("game_dir", nargs="?")
    p.add_argument("output_tsv", nargs="?")
    p.set_defaults(func=lambda a: extract_inventories(a.game_dir, a.output_tsv))

    p = sub.add_parser("gen-anim", help="ahk/AnimationID.ahk -> ui/animation_names.js")
    p.add_argument("output_js", nargs="?")
    p.set_defaults(func=lambda a: gen_anim(a.output_js))

    p = sub.add_parser("gen-ct", help="poe2_ce_inspector.lua -> tools/PoE2_Inspector.CT")
    p.set_defaults(func=lambda a: gen_ct())

    p = sub.add_parser("make-icon", help="poeformance_logo.png -> ui/poeformance.ico")
    p.set_defaults(func=lambda a: make_icon())

    args = parser.parse_args(argv)
    try:
        rc = args.func(args)
    except ToolError as e:
        print(f"ERROR: {e}")
        return 1
    return rc or 0


if __name__ == "__main__":
    sys.exit(main())
