param([Parameter(Mandatory = $true)][string]$BoardDir, [Parameter(Mandatory = $true)][string]$OutDir)
# C2, the caravan bazaar city: the kit palette, colour checks, a street section and a plan to scale.
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
$roofVs = @('Packed street', 'Street in shade', 'Plaza paving', 'Yard sand', 'Red sands terracotta sand', 'Clay wall')
$rows = @('| Beside the roof | Pale dome | Dome in shade | Flat roof (clay) |', '|---|---|---|---|')
foreach ($n in $roofVs) { $rows += ('| {0} `{1}` | {2:F0} | {3:F0} | {4:F0} |' -f $n, $col[$n], (DeHex $col['Pale dome'] $col[$n]), (DeHex $col['Dome in shade'] $col[$n]), (DeHex $col['Flat roof (clay)'] $col[$n])) }
$rows += ('| Flat roof (clay) `{0}` | {1:F0} | {2:F0} | |' -f $col['Flat roof (clay)'], (DeHex $col['Pale dome'] $col['Flat roof (clay)']), (DeHex $col['Dome in shade'] $col['Flat roof (clay)']))
$frag['ROOF'] = $rows -join "`n"
$rows = @('| Edge | Difference |', '|---|---|')
$pairs = @(@('Clay wall', 'Packed street'), @('Clay in shade', 'Street in shade'), @('Clay wall', 'Yard sand'), @('Clay wall', 'Red sands tan sand'), @('Packed street', 'Yard sand'), @('Packed street', 'Plaza paving'), @('Packed street', 'Red sands caravan road'), @('Carved wood', 'Clay wall'))
foreach ($p in $pairs) { $rows += ('| {0} against {1} | {2:F0} |' -f $p[0].ToLower(), $p[1].ToLower(), (DeHex $col[$p[0]] $col[$p[1]])) }
$frag['EDGE'] = $rows -join "`n"
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
T 30 40 'Reference board: C2, the caravan bazaar city (proposals)' 22 '#FFFFFF' 'font-weight="600"'
T 30 62 'The bazaar kit: palette, the roofs against what lies beside them, and two drawings to scale. Every colour and size is a proposal for the designer; text and sources in reference-board-c2.md.' 12 '#C8CCD0'
T 30 96 'Kit palette' 14 '#FFFFFF' 'font-weight="600"'
$i = 0
foreach ($s in $data.swatches) {
  $cx = 30 + ($i % 9) * 132; $cy = 108 + [Math]::Floor($i / 9) * 104
  Box $cx $cy 118 50 $s.hex
  T $cx ($cy + 66) $s.name 11
  T $cx ($cy + 81) ('{0}  S {1:F0}%' -f $s.hex, ((Sat ([double[]](Rgb $s.hex))) * 100)) 10 '#C8CCD0'
  $i++
}
$ry = 340
T 30 ($ry - 12) 'A dome on a flat roof from above, against what lies beside it (numbers: colour difference, higher reads better)' 14 '#FFFFFF' 'font-weight="600"'
$i = 0
foreach ($n in $roofVs) {
  $tx = 30 + $i * 196
  Box $tx $ry 182 84 $col[$n]
  Box ($tx + 56) ($ry + 12) 70 60 $col['Flat roof (clay)'] ''
  A ('<circle cx="{0}" cy="{1}" r="20" fill="{2}"/>' -f ($tx + 91), ($ry + 42), $col['Pale dome'])
  A ('<path d="M {0},{1} A 20 20 0 0 0 {0},{2} Z" fill="{3}"/>' -f ($tx + 91), ($ry + 22), ($ry + 62), $col['Dome in shade'])
  $shortName = $n -replace '^Red sands ', ''
  T $tx ($ry + 100) ('{0}: dome {1:F0}, flat roof {2:F0}' -f $shortName, (DeHex $col['Pale dome'] $col[$n]), (DeHex $col['Flat roof (clay)'] $col[$n])) 10 '#C8CCD0'
  $i++
}
# ----- section, 17 px per metre -----
$sc = 17.0; $sx0 = 60.0; $gy = 760.0
function PX($m) { $sx0 + $m * $sc }
function PY($m) { $gy - $m * $sc }
T 30 478 'Street section to scale: the covered bazaar and a caravan yard (camera on the left, looking down to the right)' 14 '#FFFFFF' 'font-weight="600"'
T 30 496 'Guideline of art-direction.md: an object on the camera side hides the feet when it is taller than its distance from the character. Dashed shapes fade or are hidden.' 11 '#C8CCD0'
Box (PX 0) $gy (50 * $sc) 10 '#3A3D42' ''
Box (PX 8) $gy (6 * $sc) 10 $col['Street in shade'] ''
Box (PX 22) $gy (20 * $sc) 10 $col['Yard sand'] ''
Box (PX 44) $gy (6 * $sc) 10 $col['Red sands tan sand'] ''
$fadeStyle = 'fill-opacity="0.22" stroke="#E6E6E6" stroke-dasharray="5 4"'
$solidStyle = 'stroke="#111316"'
function FlatHouse($m0, $m1, $hh, $faded, $domeR) {
  $style = $solidStyle; if ($faded) { $style = $fadeStyle }
  Box (PX $m0) (PY $hh) (($m1 - $m0) * $sc) ($hh * $sc) $col['Clay wall'] $style
  Box (PX ($m0 - 0.2)) (PY ($hh + 0.6)) (($m1 - $m0 + 0.4) * $sc) (0.6 * $sc) $col['Flat roof (clay)'] $style
  $mid = ($m0 + $m1) / 2
  A ('<path d="M {0:F1},{1:F1} A {2:F1} {2:F1} 0 0 1 {3:F1},{1:F1} Z" fill="{4}" {5}/>' -f (PX ($mid - $domeR)), (PY ($hh + 0.6)), ($domeR * $sc), (PX ($mid + $domeR)), $col['Pale dome'], $style)
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
FlatHouse 0 8 8 $true 2
FlatHouse 14 22 4 $false 1.5
Box (PX 8) (PY 5.2) (6 * $sc) (0.3 * $sc) $col['Cloth (dusty red)'] 'fill-opacity="0.3" stroke="#E6E6E6" stroke-dasharray="5 4"'
T (PX 8.3) (PY 5.6) 'cloth roof' 10 '#C8CCD0'
Box (PX 42) (PY 7) (2 * $sc) (7 * $sc) $col['Clay wall'] $fadeStyle
T (PX 41.4) (PY 7.6) '6-8 m' 10 '#C8CCD0'
Sight 11 3 $true
Sight 34 15 $false
Sight 47 3 $true
Person 11 'A'
Person 34 'B'
Person 47 'C'
$ly = $gy + 28
$labels = @(@(0, 8, 'house, 2 floors (8 m)'), @(8, 14, 'bazaar street 6 m'), @(14, 22, 'house, 1 floor (4 m)'), @(22, 42, 'caravan yard, 20 m'), @(42, 44, 'wall'), @(44, 50, 'Red sands'))
$li = 0
foreach ($l in $labels) {
  $mx = ((PX $l[0]) + (PX $l[1])) / 2; $yy = $ly + ($li % 2) * 15
  A ('<line x1="{0:F1}" y1="{1}" x2="{0:F1}" y2="{2}" stroke="#6A6E74"/>' -f (PX $l[0]), ($gy + 10), ($gy + 16))
  A ('<line x1="{0:F1}" y1="{1}" x2="{0:F1}" y2="{2}" stroke="#6A6E74"/>' -f (PX $l[1]), ($gy + 10), ($gy + 16))
  T $mx $yy $l[2] 10 '#C8CCD0' 'text-anchor="middle"'
  $li++
}
T 950 560 'A, in the bazaar street: the 2-floor row on the' 11 '#C8CCD0'
T 950 575 'camera side fades, and the cloth roof is hidden' 11 '#C8CCD0'
T 950 590 'while the player is under it.' 11 '#C8CCD0'
T 950 615 'B, in a caravan yard: the 1-floor row is 12 m' 11 '#C8CCD0'
T 950 630 'away and 4 m tall, so everything stays solid.' 11 '#C8CCD0'
T 950 655 'C, 3 m outside the wall: the wall fades.' 11 '#C8CCD0'
T 950 680 'Character: 1.8 m tall, 0.8 m wide.' 11 '#C8CCD0'
# ----- plan, 9 px per metre -----
$pc = 9.0; $px0 = 60.0; $py0 = 860.0
function QX($m) { $px0 + $m * $pc }
function QY($m) { $py0 + $m * $pc }
T 30 838 'One screen in the covered bazaar street, to scale (camera at the bottom, looking up the street)' 14 '#FFFFFF' 'font-weight="600"'
Box (QX 0) (QY 0) (60 * $pc) (30 * $pc) $col['Packed street'] ''
Box (QX 27) (QY 0) (6 * $pc) (30 * $pc) $col['Street in shade'] ''
function Roof($m0, $n0, $m1, $n1, $dome) {
  Box (QX $m0) (QY $n0) (($m1 - $m0) * $pc) (($n1 - $n0) * $pc) $col['Flat roof (clay)'] 'stroke="#111316"'
  if ($dome -gt 0) {
    $cxm = ($m0 + $m1) / 2; $cym = ($n0 + $n1) / 2
    A ('<circle cx="{0:F1}" cy="{1:F1}" r="{2:F1}" fill="{3}" stroke="#111316"/>' -f (QX $cxm), (QY $cym), ($dome * $pc), $col['Pale dome'])
    A ('<path d="M {0:F1},{1:F1} A {2:F1} {2:F1} 0 0 0 {0:F1},{3:F1} Z" fill="{4}"/>' -f (QX $cxm), (QY ($cym - $dome)), ($dome * $pc), (QY ($cym + $dome)), $col['Dome in shade'])
  }
}
$blocks = @(@(0, 0, 11, 9, 2.5), @(14, 0, 27, 9, 0), @(33, 0, 46, 9, 3), @(49, 0, 60, 9, 0), @(0, 12, 11, 21, 0), @(14, 12, 27, 21, 3), @(33, 12, 46, 21, 0), @(49, 12, 60, 21, 2.5), @(0, 24, 11, 30, 0), @(14, 24, 27, 30, 2), @(33, 24, 46, 30, 2), @(49, 24, 60, 30, 0))
foreach ($b in $blocks) { Roof $b[0] $b[1] $b[2] $b[3] $b[4] }
$cloths = @('Cloth (cream)', 'Cloth (dusty red)', 'Cloth (ochre)', 'Cloth (indigo)')
for ($k = 0; $k -lt 6; $k++) {
  $cn = $cloths[$k % 4]
  if ($k -eq 0) { Box (QX 27) (QY 0) (6 * $pc) (5 * $pc) $col[$cn] 'stroke="#111316"' }
  else { Box (QX 27) (QY ($k * 5)) (6 * $pc) (5 * $pc) $col[$cn] 'fill-opacity="0.18" stroke="#E6E6E6" stroke-dasharray="5 4"' }
}
A ('<polygon points="{0:F1},{1:F1} {2:F1},{3:F1} {4:F1},{5:F1} {6:F1},{7:F1}" fill="none" stroke="#FFFFFF" stroke-width="2" stroke-dasharray="8 5"/>' -f (QX 20), (QY 28), (QX 40), (QY 28), (QX 51), (QY 5), (QX 9), (QY 5))
A ('<circle cx="{0:F1}" cy="{1:F1}" r="5" fill="#E0A43A" stroke="#FFFFFF" stroke-width="1.5"/>' -f (QX 30), (QY 22))
T 630 880 'Dashed frame: what one screen shows. It is 20 m wide at the near' 11 '#C8CCD0'
T 630 895 'edge, 42 m at the far edge and 23 m deep.' 11 '#C8CCD0'
T 630 920 'Bazaar street 6 m, lanes 3 m, flat roofs with domes.' 11 '#C8CCD0'
T 630 945 'Looking along the covered street, the cloth roof lies between the' 11 '#C8CCD0'
T 630 960 'camera and the character. It is hidden while the player is under' 11 '#C8CCD0'
T 630 975 'it (dashed panels); how much of it hides is part of the open fade.' 11 '#C8CCD0'
T 630 1000 'The roofs are most of the picture. Flat clay roofs are close to' 11 '#C8CCD0'
T 630 1015 'the street in colour; the pale domes are what stands out.' 11 '#C8CCD0'
T 630 1040 'A main city is about 170 m across: about 4 to 8 screens.' 11 '#C8CCD0'
A '</svg>'
Write-Board $svg.ToString() $frag $BoardDir $OutDir $data.output
