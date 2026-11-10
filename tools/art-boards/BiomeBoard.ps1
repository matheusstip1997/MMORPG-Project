param([Parameter(Mandatory = $true)][string]$BoardDir, [Parameter(Mandatory = $true)][string]$OutDir)
# A set of biome boards (W1, W2, W3): computes the palette metrics (HSV saturation, the warnings over each ground,
# the colour-vision simulation, roads and kit roofs against the ground), draws the board SVG and fills the template.
# Optional parts follow the data in palette.json: "roofs" (several kit roofs), a "road" per biome, "alsoBoards".
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
$data = Read-Board $BoardDir
$tele = Get-Telegraphs $data
$base = TeleRow $data.baselineGround

$frag = @{}
$frag['BASELINE'] = "| M1 greybox ground (today) | ``$($data.baselineGround)`` | " + (($base | ForEach-Object { '{0:F0}' -f $_.de }) -join ' | ') + ' |'
$satRows = @(); $cvdRows = @()
$svg = New-Object Text.StringBuilder
$W = 1240; $blockH = 340; $top = 150
$H = $top + $blockH * $data.biomes.Count + 10
function T($x, $y, $text, $size = 12, $fill = '#E6E6E6', $extra = '') { [void]$svg.Append("<text x=""$x"" y=""$y"" font-size=""$size"" fill=""$fill"" $extra>$([Security.SecurityElement]::Escape($text))</text>") }
function Chip($x, $y, $w, $h, $hex) { [void]$svg.Append("<rect x=""$x"" y=""$y"" width=""$w"" height=""$h"" fill=""$hex"" stroke=""#111316"" stroke-width=""1""/>") }
function Tile($x, $y, $label, $groundHex) {
  Chip $x $y 160 56 $groundHex
  $i = 0
  foreach ($t in (TeleRow $groundHex)) { [void]$svg.Append("<circle cx=""$($x + 20 + $i * 30)"" cy=""$($y + 28)"" r=""12"" fill=""$($t.hex)""/>"); $i++ }
  T $x ($y + 72) $label 11 '#C8CCD0'
}
# The closest pair of the four coloured warnings on a ground, under each colour-vision matrix.
function ClosestPairs($rows) {
  $r = @($rows | Where-Object name -ne 'none'); $cells = @()
  foreach ($m in $cvd.Keys) {
    $best = 1e9; $pair = ''
    for ($i = 0; $i -lt 4; $i++) { for ($j = $i + 1; $j -lt 4; $j++) {
      $d = DeltaE ([double[]](Sim $r[$i].lin $cvd[$m])) ([double[]](Sim $r[$j].lin $cvd[$m]))
      if ($d -lt $best) { $best = $d; $pair = "$($r[$i].name)-$($r[$j].name)" } } }
    $cells += ('{0:F0} ({1})' -f $best, $pair)
  }
  $cells -join ' | '
}
function MainGround($biome) { $biome.swatches | Where-Object role -eq 'ground' | Sort-Object share -Descending | Select-Object -First 1 }

[void]$svg.Append("<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 $W $H"" width=""$W"" height=""$H"" font-family=""Segoe UI, Helvetica, Arial, sans-serif"">")
[void]$svg.Append("<rect width=""$W"" height=""$H"" fill=""#26282B""/>")
T 30 40 $data.title 22 '#FFFFFF' 'font-weight="600"'
T 30 62 $data.subtitle 12 '#C8CCD0'
T 30 84 'Bar: rough share of the screen (the camera shows ground and the tops of things). S = HSV saturation. Discs: the red, yellow, blue, green and uncoloured warnings as the game blends them today.' 12 '#C8CCD0'
Tile 30 96 'Today: warnings on the M1 greybox ground' $data.baselineGround
$kx = 260; $kitStep = 190; if ($data.kitSpacing) { $kitStep = [int]$data.kitSpacing }
foreach ($k in $data.kit) { Chip $kx 96 90 40 $k.hex; T $kx 150 "$($k.name) $($k.hex)" 10 '#C8CCD0'; $kx += $kitStep }
if ($data.kitNote) { T 720 120 $data.kitNote 11 '#C8CCD0' }

$bi = 0
foreach ($b in $data.biomes) {
  $y0 = $top + 28 + $bi * $blockH
  [void]$svg.Append("<line x1=""30"" y1=""$($y0 - 8)"" x2=""$($W - 30)"" y2=""$($y0 - 8)"" stroke=""#44484D""/>")
  T 30 ($y0 + 22) $b.name 20 '#FFFFFF' 'font-weight="600"'
  T 230 ($y0 + 22) "$($b.id): $($b.mood)" 13 '#C8CCD0'
  # proportion bar
  $x = 30.0; $barW = $W - 60
  foreach ($s in $b.swatches) { $segW = $barW * $s.share / 100.0; [void]$svg.Append(('<rect x="{0:F1}" y="{1}" width="{2:F1}" height="60" fill="{3}"/>' -f $x, ($y0 + 36), ($segW + 0.5), $s.hex)); $x += $segW }
  # chips and the palette table
  $x = 30; $rows = @("| Swatch | Hex | Role | Share | Saturation | Value |", "|---|---|---|---|---|---|")
  $wsum = 0.0; $maxS = 0.0; $maxName = ''; $maxG = 0.0; $maxGName = ''
  foreach ($s in $b.swatches) {
    $c = [double[]](Rgb $s.hex); $sat = Sat $c; $val = Val $c
    Chip $x ($y0 + 112) 106 50 $s.hex
    T $x ($y0 + 178) $s.name 11
    T $x ($y0 + 193) ('{0}  S {1:F0}%' -f $s.hex, ($sat * 100)) 10 '#C8CCD0'
    $rows += ('| {0} | `{1}` | {2} | {3}% | {4:F0}% | {5:F0}% |' -f $s.name, $s.hex, $s.role, $s.share, ($sat * 100), ($val * 100))
    $wsum += $sat * $s.share
    if ($sat -gt $maxS) { $maxS = $sat; $maxName = $s.name }
    if ($s.role -eq 'ground' -and $sat -gt $maxG) { $maxG = $sat; $maxGName = $s.name }
    $x += 118
  }
  $frag["PALETTE:$($b.key)"] = $rows -join "`n"
  $satRows += ('| {0} | {1:F0}% | {2:F0}% ({3}) | {4:F0}% ({5}) |' -f $b.name, ($wsum / 100 * 100), ($maxG * 100), $maxGName, ($maxS * 100), $maxName)
  # light preset
  $x = 30; T 30 ($y0 + 226) 'Light (one fixed preset)' 12 '#FFFFFF' 'font-weight="600"'
  $lrows = @()
  foreach ($l in $b.light) { Chip $x ($y0 + 236) 110 40 $l.hex; T $x ($y0 + 292) "$($l.name) $($l.hex)" 10 '#C8CCD0'; $lrows += "$($l.name) ``$($l.hex)``"; $x += 150 }
  $frag["LIGHT:$($b.key)"] = $lrows -join ', '
  # warnings on the ground: tiles for the main, the lightest and the darkest ground, and the table of every ground
  $grounds = @($b.swatches | Where-Object role -eq 'ground')
  $main = MainGround $b
  $byL = $grounds | Sort-Object { (Lab ([double[]](ToLin (Rgb $_.hex))))[0] }
  $tiles = @($main); foreach ($g in @($byL[-1], $byL[0])) { if ($tiles.name -notcontains $g.name) { $tiles += $g } }
  T 520 ($y0 + 226) 'Warnings on this ground (main, lightest, darkest)' 12 '#FFFFFF' 'font-weight="600"'
  $x = 520; foreach ($g in $tiles) { Tile $x ($y0 + 236) $g.name $g.hex; $x += 180 }
  $crow = @("| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |", "|---|---|---|---|---|---|---|")
  foreach ($g in $grounds) {
    $r = TeleRow $g.hex
    $crow += "| $($g.name) | ``$($g.hex)`` | " + (($r | ForEach-Object { '{0:F0} ({1}%)' -f $_.de, (Pct $_.de $_.name) }) -join ' | ') + ' |'
  }
  $frag["CHECK:$($b.key)"] = $crow -join "`n"
  $cvdRows += "| $($b.name) | $($main.name) | " + (ClosestPairs (TeleRow $main.hex)) + ' |'
  $bi++
}
[void]$svg.Append('</svg>')
$frag['CVD'] = (@("| M1 greybox ground (today) | ``$($data.baselineGround)`` | " + (ClosestPairs $base) + ' |') + $cvdRows) -join "`n"
$frag['SAT'] = $satRows -join "`n"
$frag['KIT'] = ($data.kit | ForEach-Object { "$($_.name.ToLower()) ``$($_.hex)``" }) -join ', '

# Kit roofs against each biome's ground: the main ground and the closest ground swatch.
$kitRows = @()
foreach ($b in $data.biomes) {
  $mainG = MainGround $b
  if ($data.roofs) {
    $cells = @()
    foreach ($rf in $data.roofs) {
      $roofLin = [double[]](ToLin (Rgb $rf.hex)); $best = 1e9; $bn = ''
      foreach ($g in ($b.swatches | Where-Object role -eq 'ground')) { $d = DeltaE $roofLin ([double[]](ToLin (Rgb $g.hex))); if ($d -lt $best) { $best = $d; $bn = $g.name } }
      if ($b.kitHere -eq 'no settlement') { $cells += '-' } else { $cells += ('{0:F0} main, {1:F0} closest ({2})' -f (DeltaE $roofLin ([double[]](ToLin (Rgb $mainG.hex)))), $best, $bn) }
    }
    $kitRows += "| $($b.name) | $($b.kitHere) | " + ($cells -join ' | ') + ' |'
  } else {
    # one kit: its roof is the first "kit" swatch
    $roofLin = [double[]](ToLin (Rgb $data.kit[0].hex)); $best = 1e9; $bn = ''
    foreach ($g in ($b.swatches | Where-Object role -eq 'ground')) { $d = DeltaE $roofLin ([double[]](ToLin (Rgb $g.hex))); if ($d -lt $best) { $best = $d; $bn = $g.name } }
    $kitRows += ('| {0} | {1:F0} ({2}) | {3:F0} ({4}) |' -f $b.name, (DeltaE $roofLin ([double[]](ToLin (Rgb $mainG.hex)))), $mainG.name, $best, $bn)
  }
}
$frag['KITCHECK'] = $kitRows -join "`n"

# The road swatch against the two largest grounds beside it (boards whose biomes name a "road").
if ($data.biomes[0].road) {
  $roadRows = @()
  foreach ($b in $data.biomes) {
    $road = $b.swatches | Where-Object name -eq $b.road
    $beside = @($b.swatches | Where-Object { $_.role -eq 'ground' -and $_.name -ne $b.road } | Sort-Object share -Descending)
    $roadLin = [double[]](ToLin (Rgb $road.hex))
    $roadRows += ('| {0} | {1} `{2}` | {3:F0} ({4}) | {5:F0} ({6}) |' -f $b.name, $road.name, $road.hex, (DeltaE $roadLin ([double[]](ToLin (Rgb $beside[0].hex)))), $beside[0].name, (DeltaE $roadLin ([double[]](ToLin (Rgb $beside[1].hex)))), $beside[1].name)
  }
  $frag['ROAD'] = $roadRows -join "`n"
}

# Every biome of this board and of the boards named in "alsoBoards": the main ground, its lightness and the warnings on it.
if ($data.alsoBoards) {
  $allRows = @(); $sets = @()
  foreach ($id in $data.alsoBoards) { $sets += (Read-Board (Join-Path (Split-Path $BoardDir -Parent) $id)) }
  $sets += $data
  foreach ($set in $sets) { foreach ($b in $set.biomes) {
    if ($b.settlement) { continue }
    $mainG = MainGround $b
    $r = TeleRow $mainG.hex
    $L = (Lab ([double[]](ToLin (Rgb $mainG.hex))))[0]
    $allRows += [pscustomobject]@{ L = $L; line = ('| {0} | {1} `{2}` | {3:F0} | ' -f $b.name, $mainG.name, $mainG.hex, $L) + (($r | Where-Object name -ne 'none' | ForEach-Object { '{0}%' -f (Pct $_.de $_.name) }) -join ' | ') + ' |' }
  } }
  $frag['ALLGROUNDS'] = ($allRows | Sort-Object L | ForEach-Object line) -join "`n"
}

# Optional tables between named swatches of this board (any biome's swatches, or the kit).
$byName = @{}
foreach ($b in $data.biomes) { foreach ($s in $b.swatches) { $byName[$s.name] = $s.hex } }
foreach ($k in $data.kit) { $byName[$k.name] = $k.hex }
function HexOf([string]$name) { if (-not $byName.ContainsKey($name)) { throw "palette.json names no swatch '$name'" }; $byName[$name] }
# "pairs": the difference between two swatches that lie side by side.
if ($data.pairs) {
  $edgeRows = @('| Edge | Difference |', '|---|---|')
  foreach ($pair in $data.pairs) { $edgeRows += ('| {0} against {1} | {2:F0} |' -f $pair.a.ToLower(), $pair.b.ToLower(), (DeHex (HexOf $pair.a) (HexOf $pair.b))) }
  $frag['EDGE'] = $edgeRows -join "`n"
}
# "warningAgainst": each coloured warning on a ground (the unrounded blend) against a swatch beside it.
if ($data.warningAgainst) {
  $besideRows = @('| Ground under the warning | Beside it | Red | Yellow | Blue | Green |', '|---|---|---|---|---|---|')
  foreach ($entry in $data.warningAgainst) {
    $other = [double[]](ToLin (Rgb (HexOf $entry.swatch)))
    $cells = @(TeleRow (HexOf $entry.ground) | Where-Object name -ne 'none' | ForEach-Object { '{0:F0}' -f (DeltaE $_.lin $other) })
    $besideRows += "| $($entry.ground) | $($entry.swatch) ``$(HexOf $entry.swatch)`` | " + ($cells -join ' | ') + ' |'
  }
  $frag['WARNVS'] = $besideRows -join "`n"
}

$photos = Get-PhotoFragments $BoardDir
foreach ($k in $photos.Keys) { $frag[$k] = $photos[$k] }
Write-Board $svg.ToString() $frag $BoardDir $OutDir $data.output
