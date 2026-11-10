param([Parameter(Mandatory = $true)][string]$BoardDir, [Parameter(Mandatory = $true)][string]$OutDir)
# C1, the water city: the kit palette, colour checks, a street section and a plan to scale.
# The swatches of palette.json are looked up by name below, so a renamed swatch has to be renamed here too.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
$data = Read-Board $BoardDir
$tele = Get-Telegraphs $data
$base = TeleRow $data.baselineGround
# every swatch by name: the kit ("swatches") and the biome around the city ("around")
$col = @{}; foreach ($s in $data.swatches) { $col[$s.name] = $s.hex }; foreach ($s in $data.around) { $col[$s.name] = $s.hex }

$frag = @{}
# palette table
$rows = @('| Swatch | Hex | Role | Where | Saturation | Value |', '|---|---|---|---|---|---|')
foreach ($s in $data.swatches) { $c = [double[]](Rgb $s.hex); $rows += ('| {0} | `{1}` | {2} | {3} | {4:F0}% | {5:F0}% |' -f $s.name, $s.hex, $s.role, $s.use, ((Sat $c) * 100), ((Val $c) * 100)) }
$frag['PALETTE'] = $rows -join "`n"
# roof against what lies beside it
$roofVs = @('Street paving', 'Pale quay stone', 'Dock planks', 'Canal water', 'Sea', 'Vale short grass', 'Vale dirt path')
$rows = @('| Beside the roof | Slate roof | Slate in shade |', '|---|---|---|')
foreach ($n in $roofVs) { $rows += ('| {0} `{1}` | {2:F0} | {3:F0} |' -f $n, $col[$n], (DeHex $col['Slate roof'] $col[$n]), (DeHex $col['Slate in shade'] $col[$n])) }
$frag['ROOF'] = $rows -join "`n"
# water edge
$rows = @('| Edge | Difference |', '|---|---|')
foreach ($p in @(@('Canal water', 'Pale quay stone'), @('Canal water', 'Street paving'), @('Sea', 'Dock planks'), @('Sea', 'Pale quay stone'), @('Street paving', 'Pale quay stone'), @('Street paving', 'Vale dirt path'))) { $rows += ('| {0} against {1} | {2:F0} |' -f $p[0].ToLower(), $p[1].ToLower(), (DeHex $col[$p[0]] $col[$p[1]])) }
$frag['EDGE'] = $rows -join "`n"
# warnings on the walkable grounds
$rows = @('| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |', '|---|---|---|---|---|---|---|')
$rows += "| M1 greybox ground (today) | ``$($data.baselineGround)`` | " + (($base | ForEach-Object { '{0:F0}' -f $_.de }) -join ' | ') + ' |'
foreach ($g in ($data.swatches | Where-Object role -eq 'ground')) { $r = TeleRow $g.hex; $rows += "| $($g.name) | ``$($g.hex)`` | " + (($r | ForEach-Object { '{0:F0} ({1}%)' -f $_.de, (Pct $_.de $_.name) }) -join ' | ') + ' |' }
$frag['CHECK'] = $rows -join "`n"
$photos = Get-PhotoFragments $BoardDir
foreach ($k in $photos.Keys) { $frag[$k] = $photos[$k] }

# ---------- drawing ----------
$svg = New-Object Text.StringBuilder
$pageW = 1240; $pageH = 1150
function A([string]$s) { [void]$svg.Append($s) }
function T($x, $y, $text, $size = 12, $fill = '#E6E6E6', $extra = '') { A "<text x=""$x"" y=""$y"" font-size=""$size"" fill=""$fill"" $extra>$([Security.SecurityElement]::Escape($text))</text>" }
function Box($x, $y, $bw, $bh, $hex, $extra = 'stroke="#111316" stroke-width="1"') { A ('<rect x="{0:F1}" y="{1:F1}" width="{2:F1}" height="{3:F1}" fill="{4}" {5}/>' -f $x, $y, $bw, $bh, $hex, $extra) }
A "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 $pageW $pageH"" width=""$pageW"" height=""$pageH"" font-family=""Segoe UI, Helvetica, Arial, sans-serif"">"
A "<rect width=""$pageW"" height=""$pageH"" fill=""#26282B""/>"
T 30 40 'Reference board: C1, the water city (proposals)' 22 '#FFFFFF' 'font-weight="600"'
T 30 62 'The water kit: palette, the slate roof against what lies beside it, and two drawings to scale. Every colour and size is a proposal for the designer; text and sources in reference-board-c1.md.' 12 '#C8CCD0'
# palette, two rows
T 30 96 'Kit palette' 14 '#FFFFFF' 'font-weight="600"'
$i = 0
foreach ($s in $data.swatches) {
  $cx = 30 + ($i % 9) * 132; $cy = 108 + [Math]::Floor($i / 9) * 104
  Box $cx $cy 118 50 $s.hex
  T $cx ($cy + 66) $s.name 11
  T $cx ($cy + 81) ('{0}  S {1:F0}%' -f $s.hex, ((Sat ([double[]](Rgb $s.hex))) * 100)) 10 '#C8CCD0'
  $i++
}
# roof tiles
$ry = 340
T 30 ($ry - 12) 'The slate roof from above, against what lies beside it (number: colour difference, higher reads better)' 14 '#FFFFFF' 'font-weight="600"'
$i = 0
foreach ($n in $roofVs) {
  $tx = 30 + $i * 168
  Box $tx $ry 154 84 $col[$n]
  Box ($tx + 42) ($ry + 17) 70 25 $col['Slate roof'] ''
  Box ($tx + 42) ($ry + 42) 70 25 $col['Slate in shade'] ''
  T $tx ($ry + 100) ('{0}: {1:F0} / {2:F0} in shade' -f $n, (DeHex $col['Slate roof'] $col[$n]), (DeHex $col['Slate in shade'] $col[$n])) 10 '#C8CCD0'
  $i++
}
# ----- section, 15 px per metre -----
$sc = 15.0; $sx0 = 60.0; $gy = 760.0
function PX($m) { $sx0 + $m * $sc }
function PY($m) { $gy - $m * $sc }
T 30 478 'Street section to scale: what the 45-degree camera hides (camera on the left, looking down to the right)' 14 '#FFFFFF' 'font-weight="600"'
T 30 496 'Guideline of art-direction.md: an object on the camera side hides the feet when it is taller than its distance from the character. Dashed shapes fade.' 11 '#C8CCD0'
# ground strips
Box (PX 0) $gy (60 * $sc) 10 '#3A3D42' ''
Box (PX 8) $gy (3 * $sc) 10 $col['Pale quay stone'] ''
Box (PX 17) $gy (3 * $sc) 10 $col['Pale quay stone'] ''
Box (PX 11) (PY (-1.0)) (6 * $sc) 25 $col['Canal water'] ''
Box (PX 28) $gy (6 * $sc) 10 $col['Street paving'] ''
Box (PX 42) $gy (3 * $sc) 10 $col['Street paving'] ''
Box (PX 47) $gy (13 * $sc) 10 $col['Vale short grass'] ''
function House($m0, $m1, $eaves, $ridge, $faded) {
  $mid = ($m0 + $m1) / 2
  $style = if ($faded) { 'fill-opacity="0.22" stroke="#E6E6E6" stroke-dasharray="5 4"' } else { 'stroke="#111316"' }
  $stoneH = [Math]::Min(4.0, $eaves)
  Box (PX $m0) (PY $stoneH) (($m1 - $m0) * $sc) ($stoneH * $sc) $col['Pale quay stone'] $style
  if ($eaves -gt 4) { Box (PX $m0) (PY $eaves) (($m1 - $m0) * $sc) (($eaves - 4) * $sc) $col['Timber frame'] $style }
  A ('<polygon points="{0:F1},{1:F1} {2:F1},{3:F1} {4:F1},{5:F1}" fill="{6}" {7}/>' -f (PX ($m0 - 0.4)), (PY $eaves), (PX $mid), (PY $ridge), (PX ($m1 + 0.4)), (PY $eaves), $col['Slate roof'], $style)
}
function Person($m, $label) {
  A ('<rect x="{0:F1}" y="{1:F1}" width="{2:F1}" height="{3:F1}" rx="5" fill="#E0A43A" stroke="#FFFFFF" stroke-width="1.5"/>' -f (PX ($m - 0.4)), (PY 1.8), (0.8 * $sc), (1.8 * $sc))
  T ((PX $m) - 4) ((PY 1.8) - 6) $label 12 '#FFFFFF' 'font-weight="600"'
}
function Sight($m, $len, $blocked) {
  $stroke = if ($blocked) { '#FFFFFF' } else { '#9AD0A0' }
  A ('<line x1="{0:F1}" y1="{1:F1}" x2="{2:F1}" y2="{3:F1}" stroke="{4}" stroke-width="1.5" stroke-dasharray="{5}"/>' -f (PX $m), (PY 0), (PX ($m - $len)), (PY $len), $stroke, $(if ($blocked) { '2 3' } else { '8 4' }))
}
House 0 8 8 11 $false
House 20 28 8 11 $true
House 34 42 4 7 $false
# wall (fades for C) and tower
Box (PX 45) (PY 7) (2 * $sc) (7 * $sc) $col['Pale quay stone'] 'fill-opacity="0.22" stroke="#E6E6E6" stroke-dasharray="5 4"'
Box (PX 54) (PY 15) (6 * $sc) (15 * $sc) $col['Pale quay stone'] 'stroke="#111316"'
A ('<polygon points="{0:F1},{1:F1} {2:F1},{3:F1} {4:F1},{5:F1}" fill="{6}" stroke="#111316"/>' -f (PX 53.6), (PY 15), (PX 57), (PY 18), (PX 60.4), (PY 15), $col['Slate roof'])
Sight 19 15.5 $false
Sight 31 3 $true
Sight 50 3 $true
Person 19 'A'
Person 31 'B'
Person 50 'C'
$ly = $gy + 28
$labels = @(@(0, 8, 'house, 2 floors'), @(8, 11, 'quay 3 m'), @(11, 17, 'canal 6 m'), @(17, 20, 'quay 3 m'), @(20, 28, 'house, 2 floors (8 m eaves)'), @(28, 34, 'street 6 m'), @(34, 42, 'house, 1 floor (4 m eaves)'), @(42, 45, 'lane 3 m'), @(45, 47, 'wall'), @(54, 60, 'tower, up to 15 m'))
$li = 0
foreach ($l in $labels) {
  $mx = ((PX $l[0]) + (PX $l[1])) / 2; $yy = $ly + ($li % 2) * 15
  A ('<line x1="{0:F1}" y1="{1}" x2="{0:F1}" y2="{2}" stroke="#6A6E74"/>' -f (PX $l[0]), ($gy + 10), ($gy + 16)); A ('<line x1="{0:F1}" y1="{1}" x2="{0:F1}" y2="{2}" stroke="#6A6E74"/>' -f (PX $l[1]), ($gy + 10), ($gy + 16))
  T $mx $yy $l[2] 10 '#C8CCD0' 'text-anchor="middle"'
  $li++
}
T (PX 45.2) (PY 7.6) '6-8 m' 10 '#C8CCD0'
T 980 560 'A, on the far quay: the houses across the' 11 '#C8CCD0'
T 980 575 'canal are 9 m or more away and 8 m tall,' 11 '#C8CCD0'
T 980 590 'so they stay solid.' 11 '#C8CCD0'
T 980 615 'B, in a 6 m street: the 2-floor row on the' 11 '#C8CCD0'
T 980 630 'camera side always fades.' 11 '#C8CCD0'
T 980 655 'C, 3 m outside the wall: the wall fades.' 11 '#C8CCD0'
T 980 680 'Character: 1.8 m tall, 0.8 m wide.' 11 '#C8CCD0'
# ----- plan, 9 px per metre -----
$pc = 9.0; $px0 = 60.0; $py0 = 860.0
function QX($m) { $px0 + $m * $pc }
function QY($m) { $py0 + $m * $pc }
T 30 838 'One screen over a canal crossing, to scale (camera at the bottom, looking up the street)' 14 '#FFFFFF' 'font-weight="600"'
Box (QX 0) (QY 0) (60 * $pc) (30 * $pc) $col['Street paving'] ''
Box (QX 0) (QY 7) (60 * $pc) (3 * $pc) $col['Pale quay stone'] ''
Box (QX 0) (QY 16) (60 * $pc) (3 * $pc) $col['Pale quay stone'] ''
Box (QX 0) (QY 10) (60 * $pc) (6 * $pc) $col['Canal water'] ''
Box (QX 27.5) (QY 10) (5 * $pc) (6 * $pc) $col['Pale quay stone'] 'stroke="#111316"'
function Roof($m0, $n0, $m1, $n1) {
  Box (QX $m0) (QY $n0) (($m1 - $m0) * $pc) (($n1 - $n0) / 2 * $pc) $col['Slate in shade'] ''
  Box (QX $m0) (QY (($n0 + $n1) / 2)) (($m1 - $m0) * $pc) (($n1 - $n0) / 2 * $pc) $col['Slate roof'] ''
  Box (QX $m0) (QY $n0) (($m1 - $m0) * $pc) (($n1 - $n0) * $pc) 'none' 'stroke="#111316"'
}
foreach ($m in 0, 8, 16, 33, 41, 49) { $wd = if ($m -eq 16) { 11 } elseif ($m -eq 49) { 11 } else { 8 }; Roof $m 19 ($m + $wd) 27 }
foreach ($m in 0, 10, 20, 34, 44) { Roof $m 0 ($m + 9) 7 }
A ('<polygon points="{0:F1},{1:F1} {2:F1},{3:F1} {4:F1},{5:F1} {6:F1},{7:F1}" fill="none" stroke="#FFFFFF" stroke-width="2" stroke-dasharray="8 5"/>' -f (QX 20), (QY 28), (QX 40), (QY 28), (QX 51), (QY 5), (QX 9), (QY 5))
A ('<circle cx="{0:F1}" cy="{1:F1}" r="5" fill="#E0A43A" stroke="#FFFFFF" stroke-width="1.5"/>' -f (QX 30), (QY 22))
T 630 880 'Dashed frame: what one screen shows. It is 20 m wide at the near' 11 '#C8CCD0'
T 630 895 'edge, 42 m at the far edge and 23 m deep.' 11 '#C8CCD0'
T 630 920 'Canal 6 m, quays 3 m, bridge 5 m, street 6 m, houses 8 m deep.' 11 '#C8CCD0'
T 630 945 'Looking along a street, no building stands between the camera' 11 '#C8CCD0'
T 630 960 'and the character, so nothing fades. Roofs, paving and water' 11 '#C8CCD0'
T 630 975 'fill the picture.' 11 '#C8CCD0'
T 630 1000 'Everything sits on the 50 cm grid and faces N, E, S or W' 11 '#C8CCD0'
T 630 1015 '(R-11.6-06), so canals run straight and turn at right angles.' 11 '#C8CCD0'
T 630 1040 'A main city is about 170 m across: about 4 to 8 screens.' 11 '#C8CCD0'
A '</svg>'
Write-Board $svg.ToString() $frag $BoardDir $OutDir $data.output
