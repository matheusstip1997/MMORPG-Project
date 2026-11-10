param([Parameter(Mandatory = $true)][string]$BoardDir, [Parameter(Mandatory = $true)][string]$OutDir)
# C3, the classic medieval city: the kit palette, colour checks, a section and a plan to scale.
# The swatches of palette.json are looked up by name below, so a renamed swatch has to be renamed here too.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
$data = Read-Board $BoardDir
$tele = Get-Telegraphs $data
$base = TeleRow $data.baselineGround
# every swatch by name: the kit ("swatches") and the biome around the city ("around")
$col = @{}; foreach ($s in $data.swatches) { $col[$s.name] = $s.hex }; foreach ($s in $data.around) { $col[$s.name] = $s.hex }

$frag = @{}
$rows = @('| Swatch | Hex | Role | Where | Saturation | Value |', '|---|---|---|---|---|---|')
foreach ($s in $data.swatches) { $c = [double[]](Rgb $s.hex); $rows += ('| {0} | `{1}` | {2} | {3} | {4:F0}% | {5:F0}% |' -f $s.name, $s.hex, $s.role, $s.use, ((Sat $c) * 100), ((Val $c) * 100)) }
$frag['PALETTE'] = $rows -join "`n"
$roofVs = @('Cobbled street', 'Cobbles in shade', 'Plaza paving', 'Lane earth', 'Grey stone', 'Moat water', 'Marsh wet earth', 'Marsh reed bed (straw)')
$walkable = @('Cobbled street', 'Cobbles in shade', 'Plaza paving', 'Lane earth', 'Marsh wet earth', 'Marsh reed bed (straw)')
$rows = @('| Beside the roof | Warm tile roof | Tile in shade |', '|---|---|---|')
foreach ($n in $roofVs) { $rows += ('| {0} `{1}` | {2:F0} | {3:F0} |' -f $n, $col[$n], (DeHex $col['Warm tile roof'] $col[$n]), (DeHex $col['Tile in shade'] $col[$n])) }
$frag['ROOF'] = $rows -join "`n"
$rows = @('| Edge | Difference |', '|---|---|')
$pairs = @(@('Grey stone', 'Cobbled street'), @('Stone in shade', 'Cobbles in shade'), @('Grey stone', 'Plaza paving'), @('Cobbled street', 'Plaza paving'), @('Cobbled street', 'Lane earth'), @('Cobbled street', 'Marsh causeway road'), @('Bridge planks', 'Cobbled street'), @('Bridge planks', 'Moat water'), @('Moat water', 'Grey stone'), @('Moat water', 'Marsh wet earth'), @('Moat water', 'Marsh dark shallow water'), @('Timber frame', 'Infill plaster'), @('Infill plaster', 'Grey stone'), @('Wall base band', 'Grey stone'), @('Wall base band', 'Cobbled street'), @('Wall base band', 'Cobbles in shade'))
foreach ($p in $pairs) { $rows += ('| {0} against {1} | {2:F0} |' -f $p[0].ToLower(), $p[1].ToLower(), (DeHex $col[$p[0]] $col[$p[1]])) }
$frag['EDGE'] = $rows -join "`n"
function RedOn([string]$groundHex) { (TeleRow $groundHex | Where-Object name -eq 'red').hex }
function RedLin([string]$groundHex) { $gl = [double[]](ToLin (Rgb $groundHex)); $tr = $tele | Where-Object name -eq 'red'; [double[]](Blend $tr.lin $tr.a $gl) }
function DeRed([string]$groundHex, [string]$otherHex) { DeltaE (RedLin $groundHex) ([double[]](ToLin (Rgb $otherHex))) }
$rows = @('| Ground under a red warning | The warning on it | Against the warm tile roof | Against tile in shade | Against a dusty red banner |', '|---|---|---|---|---|')
foreach ($g in ($data.swatches | Where-Object role -eq 'ground')) { $rw = RedOn $g.hex; $rows += ('| {0} | `{1}` | {2:F0} | {3:F0} | {4:F0} |' -f $g.name, $rw, (DeRed $g.hex $col['Warm tile roof']), (DeRed $g.hex $col['Tile in shade']), (DeRed $g.hex $col['Banner (dusty red)'])) }
$frag['RED'] = $rows -join "`n"
$rwc = RedOn $col['Cobbled street']
$frag['REDCMP'] = ('{0:F0} from the water kit''s slate roof `#6E7479` and {1:F0} from the bazaar kit''s white dome `#F0ECE2`' -f (DeRed $col['Cobbled street'] '#6E7479'), (DeRed $col['Cobbled street'] '#F0ECE2'))
$rows = @('| Ground | Hex | Red | Yellow | Blue | Green | Uncoloured |', '|---|---|---|---|---|---|---|')
$rows += "| M1 greybox ground (today) | ``$($data.baselineGround)`` | " + (($base | ForEach-Object { '{0:F0}' -f $_.de }) -join ' | ') + ' |'
foreach ($g in ($data.swatches | Where-Object role -eq 'ground')) { $r = TeleRow $g.hex; $rows += "| $($g.name) | ``$($g.hex)`` | " + (($r | ForEach-Object { '{0:F0} ({1}%)' -f $_.de, (Pct $_.de $_.name) }) -join ' | ') + ' |' }
$frag['CHECK'] = $rows -join "`n"
# the roofs of the plan drawing: x0, y0, x1, y1 in metres
$blocks = @(@(0, 0, 9, 12), @(12, 0, 18, 12), @(18, 0, 27, 6), @(51, 0, 60, 12), @(42, 0, 48, 12), @(33, 0, 42, 6), @(0, 18, 9, 30), @(12, 18, 18, 30), @(18, 24, 27, 30), @(51, 18, 60, 30), @(42, 18, 48, 30), @(33, 24, 42, 30))
$roofArea = 0.0; foreach ($b in $blocks) { $roofArea += ($b[2] - $b[0]) * ($b[3] - $b[1]) }
$frag['COVER'] = ('{0:F0}%' -f (100 * $roofArea / 1800))
$frag['LITSHADE'] = ('{0:F0}' -f (DeHex $col['Warm tile roof'] $col['Tile in shade']))
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
T 30 40 'Reference board: C3, the classic medieval city (proposals)' 22 '#FFFFFF' 'font-weight="600"'
T 30 62 'The medieval kit: palette, the tile roof against what lies beside it, and two drawings to scale. Every colour and size is a proposal for the designer; text and sources in reference-board-c3.md.' 12 '#C8CCD0'
T 30 96 'Kit palette' 14 '#FFFFFF' 'font-weight="600"'
# nine chips per row; ten narrower ones when the kit has more than 18 swatches
$perRow = 9; $chipStep = 132; $chipW = 118; if ($data.swatches.Count -gt 18) { $perRow = 10; $chipStep = 118; $chipW = 106 }
$i = 0
foreach ($s in $data.swatches) {
  $cx = 30 + ($i % $perRow) * $chipStep; $cy = 108 + [Math]::Floor($i / $perRow) * 104
  Box $cx $cy $chipW 50 $s.hex
  T $cx ($cy + 66) $s.name 11
  T $cx ($cy + 81) ('{0}  S {1:F0}%' -f $s.hex, ((Sat ([double[]](Rgb $s.hex))) * 100)) 10 '#C8CCD0'
  $i++
}
$ry = 340
T 30 ($ry - 12) 'A tile roof from above against what lies beside it, and a red warning on the same ground (numbers: colour difference, higher reads better)' 14 '#FFFFFF' 'font-weight="600"'
$i = 0
foreach ($n in $roofVs) {
  $tx = 30 + $i * 148
  Box $tx $ry 138 84 $col[$n]
  Box ($tx + 10) ($ry + 12) 28 60 $col['Warm tile roof'] ''
  Box ($tx + 38) ($ry + 12) 28 60 $col['Tile in shade'] ''
  $shortName = $n -replace '^Marsh wet earth$', 'Marsh earth' -replace '^Marsh reed bed \(straw\)$', 'Marsh reeds'
  T $tx ($ry + 100) ('{0}: roof {1:F0}' -f $shortName, (DeHex $col['Warm tile roof'] $col[$n])) 10 '#C8CCD0'
  if ($walkable -contains $n) {
    $rw = RedOn $col[$n]
    A ('<circle cx="{0}" cy="{1}" r="22" fill="{2}"/>' -f ($tx + 102), ($ry + 42), $rw)
    T $tx ($ry + 114) ('red warning to roof {0:F0}' -f (DeRed $col[$n] $col['Warm tile roof'])) 10 '#C8CCD0'
  }
  $i++
}
# ----- section, 15 px per metre -----
$sc = 15.0; $sx0 = 40.0; $gy = 770.0
function PX($m) { $sx0 + $m * $sc }
function PY($m) { $gy - $m * $sc }
T 30 488 'Section to scale: a main street, the plaza, the wall and the moat (camera on the left, looking down to the right)' 14 '#FFFFFF' 'font-weight="600"'
T 30 506 'Guideline of art-direction.md: an object on the camera side hides the feet when it is taller than its distance from the character. Dashed shapes fade.' 11 '#C8CCD0'
Box (PX 0) $gy (58 * $sc) 10 '#3A3D42' ''
Box (PX 8) $gy (6 * $sc) 10 $col['Cobbled street'] ''
Box (PX 22) $gy (20 * $sc) 10 $col['Plaza paving'] ''
Box (PX 44) $gy (8 * $sc) 10 $col['Moat water'] ''
Box (PX 52) $gy (6 * $sc) 10 $col['Marsh wet earth'] ''
$fadeStyle = 'fill-opacity="0.22" stroke="#E6E6E6" stroke-dasharray="5 4"'
$solidStyle = 'stroke="#111316"'
function GableHouse($m0, $m1, $floors, $faded) {
  $style = $solidStyle; if ($faded) { $style = $fadeStyle }
  $hh = 4.0 * $floors; $mid = ($m0 + $m1) / 2; $ridge = $hh + ($m1 - $m0) / 2
  Box (PX $m0) (PY 4) (($m1 - $m0) * $sc) (4 * $sc) $col['Grey stone'] $style
  if ($floors -gt 1) {
    Box (PX $m0) (PY $hh) (($m1 - $m0) * $sc) (($hh - 4) * $sc) $col['Infill plaster'] $style
    foreach ($q in 1, 2, 3) { $lx = PX ($m0 + $q * ($m1 - $m0) / 4); A ('<line x1="{0:F1}" y1="{1:F1}" x2="{0:F1}" y2="{2:F1}" stroke="{3}" stroke-width="3" {4}/>' -f $lx, (PY $hh), (PY 4), $col['Timber frame'], $(if ($faded) { 'stroke-opacity="0.3"' } else { '' })) }
  }
  A ('<polygon points="{0:F1},{1:F1} {2:F1},{3:F1} {2:F1},{1:F1}" fill="{4}" {5}/>' -f (PX ($m0 - 0.3)), (PY $hh), (PX $mid), (PY $ridge), $col['Warm tile roof'], $style)
  A ('<polygon points="{0:F1},{1:F1} {2:F1},{3:F1} {2:F1},{1:F1}" fill="{4}" {5}/>' -f (PX ($m1 + 0.3)), (PY $hh), (PX $mid), (PY $ridge), $col['Tile in shade'], $style)
}
function Person($m, $label) {
  A ('<rect x="{0:F1}" y="{1:F1}" width="{2:F1}" height="{3:F1}" rx="5" fill="#E0A43A" stroke="#FFFFFF" stroke-width="1.5"/>' -f (PX ($m - 0.4)), (PY 1.8), (0.8 * $sc), (1.8 * $sc))
  T ((PX $m) - 4) ((PY 1.8) - 6) $label 12 '#FFFFFF' 'font-weight="600"'
}
function Sight($m, $len, $blocked) {
  $stroke = '#9AD0A0'; $dash = '8 4'
  if ($blocked) { $stroke = '#FFFFFF'; $dash = '2 3' }
  A ('<line x1="{0:F1}" y1="{1:F1}" x2="{2:F1}" y2="{3:F1}" stroke="{4}" stroke-width="1.5" stroke-dasharray="{5}"/>' -f (PX $m), (PY 0), (PX ($m - $len)), (PY $len), $stroke, $dash)
}
GableHouse 0 8 2 $true
GableHouse 14 22 1 $false
Box (PX 42) (PY 8) (2 * $sc) (8 * $sc) $col['Grey stone'] $solidStyle
foreach ($q in 42.0, 42.75, 43.5) { Box (PX $q) (PY 8.8) (0.5 * $sc) (0.8 * $sc) $col['Grey stone'] $solidStyle }
T (PX 41.2) (PY 9.4) '6-8 m' 10 '#C8CCD0'
Sight 11 3 $true
Sight 34 14 $false
Sight 55 13 $false
Person 11 'A'
Person 34 'B'
Person 55 'C'
$ly = $gy + 28
$labels = @(@(0, 8, 'house, 2 floors (8 m to the eaves)'), @(8, 14, 'main street 6 m'), @(14, 22, 'house, 1 floor (4 m)'), @(22, 42, 'plaza, 20 m'), @(42, 44, 'wall'), @(44, 52, 'moat, 8 m'), @(52, 58, 'Reed marsh'))
$li = 0
foreach ($l in $labels) {
  $mx = ((PX $l[0]) + (PX $l[1])) / 2; $yy = $ly + ($li % 2) * 15
  A ('<line x1="{0:F1}" y1="{1}" x2="{0:F1}" y2="{2}" stroke="#6A6E74"/>' -f (PX $l[0]), ($gy + 10), ($gy + 16))
  A ('<line x1="{0:F1}" y1="{1}" x2="{0:F1}" y2="{2}" stroke="#6A6E74"/>' -f (PX $l[1]), ($gy + 10), ($gy + 16))
  T $mx $yy $l[2] 10 '#C8CCD0' 'text-anchor="middle"'
  $li++
}
T 930 545 'A, in a main street: the 2-floor row on the camera' 11 '#C8CCD0'
T 930 560 'side fades. Its roof slopes away at 45 degrees, so' 11 '#C8CCD0'
T 930 575 'it hides nothing more than the eaves do.' 11 '#C8CCD0'
T 930 600 'B, on the plaza: the 1-floor row is 12 m away and' 11 '#C8CCD0'
T 930 615 '4 m to the eaves, so everything stays solid.' 11 '#C8CCD0'
T 930 640 'C, on the far bank of an 8 m moat: the wall is' 11 '#C8CCD0'
T 930 655 '11 m away and 8 m tall, so it stays solid.' 11 '#C8CCD0'
T 930 680 'Character: 1.8 m tall, 0.8 m wide.' 11 '#C8CCD0'
# ----- plan, 9 px per metre -----
$pc = 9.0; $px0 = 60.0; $py0 = 860.0
function QX($m) { $px0 + $m * $pc }
function QY($m) { $py0 + $m * $pc }
T 30 838 'One screen at the central plaza, to scale (camera at the bottom, looking up the drawing)' 14 '#FFFFFF' 'font-weight="600"'
Box (QX 0) (QY 0) (60 * $pc) (30 * $pc) $col['Cobbled street'] ''
Box (QX 18) (QY 6) (24 * $pc) (18 * $pc) $col['Plaza paving'] ''
function Roof($m0, $n0, $m1, $n1) {
  $rw0 = ($m1 - $m0) * $pc; $rh0 = ($n1 - $n0) * $pc
  if (($m1 - $m0) -ge ($n1 - $n0)) {
    Box (QX $m0) (QY $n0) $rw0 ($rh0 / 2) $col['Tile in shade'] ''
    Box (QX $m0) ((QY $n0) + $rh0 / 2) $rw0 ($rh0 / 2) $col['Warm tile roof'] ''
  } else {
    Box (QX $m0) (QY $n0) ($rw0 / 2) $rh0 $col['Warm tile roof'] ''
    Box ((QX $m0) + $rw0 / 2) (QY $n0) ($rw0 / 2) $rh0 $col['Tile in shade'] ''
  }
  Box (QX $m0) (QY $n0) $rw0 $rh0 'none' 'stroke="#111316"'
}
foreach ($b in $blocks) { Roof $b[0] $b[1] $b[2] $b[3] }
A ('<circle cx="{0:F1}" cy="{1:F1}" r="{2:F1}" fill="{3}"/>' -f (QX 30), (QY 3), (2.5 * $pc), (RedOn $col['Cobbled street']))
A ('<polygon points="{0:F1},{1:F1} {2:F1},{3:F1} {4:F1},{5:F1} {6:F1},{7:F1}" fill="none" stroke="#FFFFFF" stroke-width="2" stroke-dasharray="8 5"/>' -f (QX 20), (QY 28), (QX 40), (QY 28), (QX 51), (QY 5), (QX 9), (QY 5))
A ('<circle cx="{0:F1}" cy="{1:F1}" r="5" fill="#E0A43A" stroke="#FFFFFF" stroke-width="1.5"/>' -f (QX 30), (QY 21))
T 630 880 'Dashed frame: what one screen shows. It is 20 m wide at the near' 11 '#C8CCD0'
T 630 895 'edge, 42 m at the far edge and 23 m deep.' 11 '#C8CCD0'
T 630 920 'Two 6 m main streets cross at the plaza; the lanes are 3 m.' 11 '#C8CCD0'
T 630 945 ('Roofs cover about {0} of this drawing. A pitched roof has a lit' -f $frag['COVER']) 11 '#C8CCD0'
T 630 960 'and a shaded side, which tells it from flat ground.' 11 '#C8CCD0'
T 630 985 'The red disc in the street at the top is a red warning as the game' 11 '#C8CCD0'
T 630 1000 'blends it on cobbles today, between two tile roofs.' 11 '#C8CCD0'
T 630 1025 'A main city is about 170 m across: about 4 to 8 screens.' 11 '#C8CCD0'
A '</svg>'
Write-Board $svg.ToString() $frag $BoardDir $OutDir $data.output
