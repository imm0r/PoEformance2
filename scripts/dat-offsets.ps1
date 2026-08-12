#Requires -Version 7
<#
.SYNOPSIS
  Work out the memory offsets of a .dat table row from the community dat schema,
  instead of reverse-engineering them.

.DESCRIPTION
  Some of what this tool reads is not a game object at all - it is a row of one of the
  game's static content tables (a .dat file), loaded into memory. WorldAreaDat,
  BuffDefinition, MinimapIconRow and ItemVisualIdentityRow are all rows, not structs.

  Rows keep the column order the .dat file declares, PACKED, with no alignment padding.
  So once the column widths are known, every offset in such a row is arithmetic rather
  than a search. poe-tool-dev/dat-schema publishes that column order for 1275 PoE2
  tables; this script does the arithmetic.

  The width table below was not guessed. It is the only combination out of sixteen that
  reproduces every dat-row offset this project had already found the hard way - run
  -SelfTest to see that check happen. One width is still genuinely unknown, see the
  comment on 'row'.

  This does NOT apply to components. Life, Actor, Render, Positioned, Buffs, StatusEffect,
  Entity and every UiElement are engine objects with no .dat table behind them, and
  nothing here says anything about their layout.

.EXAMPLE
  .\scripts\dat-offsets.ps1 -Table BuffDefinitions
      Every column of the row with its computed offset.

.EXAMPLE
  .\scripts\dat-offsets.ps1 -Table BuffDefinitions -Offset 0x67
      "We read something at 0x67 - what is it?" Answers BuffCategory.

.EXAMPLE
  .\scripts\dat-offsets.ps1 -SelfTest
      Check the computed offsets against the ones in schema\poe2.offsets.json.
      Run this after changing a dat-row offset, or when the schema is refreshed.
#>
param(
    # Table name as the community schema spells it, e.g. WorldAreas. Case-insensitive.
    [string]$Table,

    # Only report the column covering this offset, e.g. 0x67. Needs -Table.
    [string]$Offset,

    # Check the computed layout against schema\poe2.offsets.json and exit non-zero on drift.
    [switch]$SelfTest,

    # Use a local schema.min.json instead of the cached download.
    [string]$SchemaPath,

    # Download the schema again even if a cached copy exists.
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'

$SchemaUrl = 'https://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema.min.json'
$RepoRoot = Split-Path $PSScriptRoot -Parent

# Bitmask from the schema's own types.ts: 0x01 PoE1, 0x02 PoE2. Both games ship tables
# under the same name with DIFFERENT columns - BuffDefinitions is one of them - so
# filtering on this is not optional.
$ValidForPoE2 = 0x02

# Byte width per column type, as the row sits in memory. Everything here is packed:
# a bool really does cost one byte and the next column starts on the next byte, which
# is why so many of these offsets are odd numbers.
#
# This is the .datc64 FILE width table from the AHK tool (tools/poe_tools.py,
# TYPE_SIZES_DATC64), which has been extracting these tables from the game's own data
# for months. That it also describes the layout IN MEMORY is the whole point: the game
# does not repack a row when it loads it, it keeps the file layout and fills the
# references in place. Derived independently here and then found to agree type for type -
# including the two widths that were guessed wrong first, see 'row' and 'foreignrow'.
$Widths = @{
    'bool'   = 1
    'i8'     = 1
    'u8'     = 1
    'i16'    = 2
    'u16'    = 2
    'i32'    = 4
    'u32'    = 4
    'f32'    = 4
    # A reference to a table that has no columns of its own. Behaves like an i32 index.
    'enumrow' = 4
    # The schema's placeholder for "a row reference whose table we have not identified yet".
    'rid'    = 4
    # In the FILE this is an offset into the variable-length section; in MEMORY it is a
    # pointer to a raw UTF-16 string. Same width either way.
    'string' = 8
    # 64-bit TABLE reference followed by 64-bit ROW reference. So the pointer to the
    # referenced row sits at +8, not at the column start - the offset this script reports
    # is where the COLUMN begins. GameHelper2 corroborates it from the other side: its
    # GrantedEffects.ActiveSkillDatPtr is 0x57, and the ActiveSkill column starts at 0x4F.
    'foreignrow' = 16
    # A reference to a row of the SAME table, and only 8 bytes - there is no table
    # reference to store. This was 16 here at first, which nothing in this repo could
    # disprove because no offset we read sits behind one; 41 PoE2 tables do have such a
    # column, and in WorldAreas it is column 13 of 82, so everything after ParentTown was
    # coming out 8 bytes too far.
    'row'    = 8
}

# Count plus offset in the file, two pointers in memory. Applies to an array of anything,
# including arrays of scalars.
$ArrayWidth = 16

function Get-Schema {
    if ($SchemaPath) {
        if (-not (Test-Path $SchemaPath)) {
            Write-Host "No schema at $SchemaPath" -ForegroundColor Red
            exit 1
        }
        return Get-Content $SchemaPath -Raw | ConvertFrom-Json
    }

    $cache = Join-Path ([System.IO.Path]::GetTempPath()) 'poeformance-dat-schema.min.json'
    if ($Refresh -or -not (Test-Path $cache)) {
        Write-Host "Downloading dat schema -> $cache" -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $SchemaUrl -OutFile $cache -UseBasicParsing
    }
    return Get-Content $cache -Raw | ConvertFrom-Json
}

function Get-ColumnWidth($column) {
    if ($column.array -or $column.type -eq 'array') { return $ArrayWidth }
    if (-not $Widths.ContainsKey($column.type)) {
        throw "Unknown column type '$($column.type)' - the schema gained a type this script does not price."
    }
    return $Widths[$column.type]
}

# Returns the columns of one PoE2 table, each with the offset it lands on in memory.
function Get-RowLayout($schema, [string]$name) {
    $table = $schema.tables | Where-Object {
        $_.name -eq $name -and ($_.validFor -band $ValidForPoE2)
    } | Select-Object -First 1

    if (-not $table) {
        Write-Host "No PoE2 table named '$name' in the schema." -ForegroundColor Red
        exit 1
    }

    $offset = 0
    $out = foreach ($c in $table.columns) {
        $width = Get-ColumnWidth $c
        [pscustomobject]@{
            Offset = $offset
            Width  = $width
            Name   = if ($c.name) { $c.name } else { '_' }
            Type   = if ($c.array) { "$($c.type)[]" } else { $c.type }
            Ref    = if ($c.references) { $c.references.table } else { '' }
        }
        $offset += $width
    }
    return , @($out)
}

function Get-RowSize($layout) { return $layout[-1].Offset + $layout[-1].Width }

# Aligned by hand rather than with Format-Table. Format-Table asks the host how wide the
# console is, and anywhere that answer is -1 - redirected output, a CI log, a pipe - it
# renders the whole table as blank lines instead of failing. This prints the same either way.
function Format-Layout($layout) {
    $nameWidth = ($layout | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
    $typeWidth = ($layout | ForEach-Object { $_.Type.Length } | Measure-Object -Maximum).Maximum

    foreach ($c in $layout) {
        $line = '  0x{0:X2}  {1}  {2}' -f $c.Offset, $c.Name.PadRight($nameWidth), $c.Type.PadRight($typeWidth)
        if ($c.Ref) { $line += "  -> $($c.Ref)" }
        Write-Host $line
    }
}

# --- self test ---------------------------------------------------------------------
#
# Every dat row this project reads, and which column each of our field names really is.
# The offsets are NOT repeated here - they are read from schema\poe2.offsets.json, so
# this fails if either side drifts. Where our name differs from the column name it is
# because we named it before we knew; the column name is the truth.
#
# 'Column+8' means the field is not the start of that column but the row reference
# inside it - see the note on foreignrow above.
$KnownRows = @(
    @{ Struct = 'WorldAreaDat';          Table = 'WorldAreas'
       Fields = [ordered]@{ IdPtr = 'Id'; NamePtr = 'Name'; Act = 'Act'
                            IsTown = 'IsTown'; HasWaypoint = 'HasWaypoint' } }
    @{ Struct = 'BuffDefinition';        Table = 'BuffDefinitions'
       Fields = [ordered]@{ Name = 'Id'; BuffVisualsKey = 'BuffVisual+8'; BuffType = 'BuffCategory' } }
    @{ Struct = 'MinimapIconRow';        Table = 'MinimapIcons'
       Fields = [ordered]@{ NamePtr = 'Id' } }
    @{ Struct = 'ItemVisualIdentityRow'; Table = 'ItemVisualIdentity'
       Fields = [ordered]@{ IdPtr = 'Id' } }
)

function Invoke-SelfTest($schema) {
    $ours = Get-Content (Join-Path $RepoRoot 'schema/poe2.offsets.json') -Raw | ConvertFrom-Json
    $failed = 0

    foreach ($row in $KnownRows) {
        $layout = Get-RowLayout $schema $row.Table
        $struct = $ours.structs.($row.Struct)
        if (-not $struct) {
            Write-Host "FAIL $($row.Struct) - no such struct in poe2.offsets.json" -ForegroundColor Red
            $failed++
            continue
        }

        foreach ($field in $row.Fields.Keys) {
            $expected = $row.Fields[$field]
            $inset = 0
            if ($expected -match '^(.+)\+(\d+)$') {
                $expected = $Matches[1]
                $inset = [int]$Matches[2]
            }

            $declared = $struct.fields.$field
            if (-not $declared) {
                Write-Host "FAIL $($row.Struct).$field - not declared" -ForegroundColor Red
                $failed++
                continue
            }

            $offset = [Convert]::ToInt32($declared.offset.Replace('0x', ''), 16)
            $column = $layout | Where-Object { $_.Offset -eq ($offset - $inset) } | Select-Object -First 1
            $label = '{0}.{1} @ 0x{2:X2}' -f $row.Struct, $field, $offset
            $where = if ($inset) { " (+$inset into it)" } else { '' }

            if (-not $column) {
                $at = if ($inset) { "0x{0:X2}" -f ($offset - $inset) } else { 'here' }
                Write-Host "FAIL $label - no column starts at $at in $($row.Table)" -ForegroundColor Red
                $failed++
            }
            elseif ($column.Name -ne $expected) {
                Write-Host "FAIL $label - is $($row.Table).$($column.Name), expected $expected" -ForegroundColor Red
                $failed++
            }
            else {
                Write-Host "ok   $label -> $($row.Table).$($column.Name) ($($column.Type))$where" -ForegroundColor DarkGray
            }
        }
    }

    if ($failed) {
        Write-Host "`n$failed offset(s) no longer agree with the dat schema." -ForegroundColor Red
        exit 1
    }
    Write-Host "`nAll dat-row offsets agree with the dat schema." -ForegroundColor Green
}

# --- entry point -------------------------------------------------------------------
$schema = Get-Schema

if ($SelfTest) {
    Invoke-SelfTest $schema
    return
}

if (-not $Table) {
    Write-Host 'Give it -Table <name>, or -SelfTest. See -? for examples.'
    return
}

$layout = Get-RowLayout $schema $Table

if ($Offset) {
    $wanted = [Convert]::ToInt32($Offset.Replace('0x', ''), 16)
    $size = Get-RowSize $layout
    if ($wanted -ge $size) {
        Write-Host ("0x{0:X2} is past the end of a {1} row (size 0x{2:X})." -f $wanted, $Table, $size) -ForegroundColor Red
        exit 1
    }

    # The last column starting at or before the offset is the one covering it.
    $hit = $layout | Where-Object { $_.Offset -le $wanted } | Select-Object -Last 1
    $exact = if ($hit.Offset -eq $wanted) { '' } else { " (starts at 0x{0:X2}, +{1} into it)" -f $hit.Offset, ($wanted - $hit.Offset) }
    Write-Host ("0x{0:X2} -> {1}.{2} : {3}{4}{5}" -f $wanted, $Table, $hit.Name, $hit.Type,
        $(if ($hit.Ref) { " -> $($hit.Ref)" } else { '' }), $exact)
    return
}

Write-Host ("{0} - {1} columns, row size 0x{2:X}" -f $Table, $layout.Count, (Get-RowSize $layout)) -ForegroundColor Cyan
Format-Layout $layout
