param([Parameter(Mandatory = $true)][string]$BoardDir, [Parameter(Mandatory = $true)][string]$OutDir)
# The characters and monsters board: sizes on screen from the camera values, an outline and figure tones against
# the main ground of every biome board, the monster families on their home ground, and schematic silhouettes.
# palette.json names the biome boards to read ("biomeBoards"); their palettes give each biome's main ground.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Common.ps1"
$data = Read-Board $BoardDir

# ---------- the camera: what a size in the world is on screen ----------
$cam = $data.camera
$pitch = [Math]::PI * $cam.pitchDegrees / 180
$sinP = [Math]::Sin($pitch); $cosP = [Math]::Cos($pitch)
$camDist = $cam.distanceCm / 100.0
$camY = $cam.lookAtHeightCm / 100.0 + $camDist * $sinP     # height of the camera
$camZ = -$camDist * $cosP                                   # behind the character
$focal = ($cam.screenHeightPx / 2.0) / [Math]::Tan([Math]::PI * $cam.fieldOfViewDegrees / 360)
# A point in metres (x to the right, y up, z away from the camera; the character stands at 0, 0, 0)
# -> screen x, screen y (up) in pixels from the screen centre, and the depth along the view axis.
function Project([double]$x, [double]$y, [double]$z) {
  $vy = $y - $camY; $vz = $z - $camZ
  $depth = -$vy * $sinP + $vz * $cosP
  @(($focal * $x / $depth), ($focal * ($vy * $cosP + $vz * $sinP) / $depth), $depth)
}
function TallPx([double]$fromM, [double]$toM, [double]$z) { (Project 0 $toM $z)[1] - (Project 0 $fromM $z)[1] }
function WidePx([double]$lenM, [double]$atM, [double]$z) { $lenM * $focal / (Project 0 $atM $z)[2] }
$small = $data.smallScreenHeightPx / [double]$cam.screenHeightPx

# palette.json copies the camera values and the smallest and largest monster radius from the game's tables.
# Warn when a table no longer matches, so the copy gets updated; the tables are only read, and a missing one is skipped.
$tables = Join-Path $PSScriptRoot '..\..\data\tables'
$stale = @()
$camFile = Join-Path $tables 'ClientCamera.json'
if (Test-Path $camFile) {
  $text = [IO.File]::ReadAllText($camFile)
  foreach ($key in 'distanceCm', 'lookAtHeightCm', 'fieldOfViewDegrees') {
    if ($text -match ('"' + $key + '"\s*:\s*([0-9]+)') -and [int]$Matches[1] -ne [int]$cam.$key) { $stale += "ClientCamera.$key is $($Matches[1]), palette.json has $($cam.$key)" }
  }
}
$monFile = Join-Path $tables 'MonsterDef.json'
if (Test-Path $monFile) {
  $radii = @([regex]::Matches([IO.File]::ReadAllText($monFile), '"bodyRadiusCm"\s*:\s*([0-9]+)') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object)
  $mine = @($data.monsters | ForEach-Object { [int]$_.radiusCm } | Sort-Object)
  if ($radii.Count -gt 0) {
    if ($radii[0] -ne $mine[0]) { $stale += "the smallest MonsterDef.bodyRadiusCm is $($radii[0]), palette.json has $($mine[0])" }
    if ($radii[-1] -ne $mine[-1]) { $stale += "the largest MonsterDef.bodyRadiusCm is $($radii[-1]), palette.json has $($mine[-1])" }
  }
}
foreach ($s in $stale) { Write-Warning "figures board: $s. Update boards/figures/palette.json (and the sizes it lists) and rebuild." }

$frag = @{}
$rows = @("| What | Real size | At $($cam.screenHeightPx)p | At $($data.smallScreenHeightPx)p |", '|---|---|---|---|')
foreach ($s in $data.sizes) {
  if ($s.type -eq 'tall') { $px = TallPx ($s.fromCm / 100.0) ($s.toCm / 100.0) 0 } else { $px = WidePx ($s.cm / 100.0) ($s.atCm / 100.0) 0 }
  $rows += ('| {0} | {1} | {2:F0} px | {3:F0} px |' -f $s.name, $s.real, $px, ($px * $small))
}
$frag['SIZES'] = $rows -join "`n"
$charM = $data.character.heightCm / 100.0; $charW = $data.character.widthCm / 100.0
$rows = @('| Where the character stands | Height on screen | Width on screen |', '|---|---|---|')
foreach ($p in $data.positions) {
  $z = $p.zCm / 100.0
  $rows += ('| {0} | {1:F0} px | {2:F0} px |' -f $p.name, (TallPx 0 $charM $z), (WidePx $charW ($charM * 0.78) $z))
}
$frag['DEPTH'] = $rows -join "`n"
$pxPerM = WidePx 1 1 0
$frag['PXPERM'] = '{0:F0}' -f $pxPerM
$frag['CMPERPX'] = '{0:F1}' -f (100 / $pxPerM)
$frag['CHARPX'] = '{0:F0}' -f (TallPx 0 $charM 0)
$frag['CHARWPX'] = '{0:F0}' -f (WidePx $charW ($charM * 0.78) 0)
# the shoulder width the drawing gives each armour tier
$rows = @("| Armour tier | Shoulder width in the drawing | At $($cam.screenHeightPx)p | At $($data.smallScreenHeightPx)p |", '|---|---|---|---|')
$tiers = @{}
foreach ($c in ($data.classes | Sort-Object shoulders)) {
  if ($tiers.ContainsKey($c.armour)) { continue }
  $tiers[$c.armour] = 1
  $px = WidePx $c.shoulders 1.45 0
  $rows += ('| {0} | {1:F2} m | {2:F0} px | {3:F0} px |' -f $c.armour, $c.shoulders, $px, ($px * $small))
}
$frag['SHOULDERS'] = $rows -join "`n"

# ---------- every biome's main ground, from the biome boards ----------
$grounds = @()
foreach ($id in $data.biomeBoards) {
  $set = Read-Board (Join-Path (Split-Path $BoardDir -Parent) $id)
  foreach ($b in $set.biomes) {
    if ($b.settlement) { continue }
    $g = $b.swatches | Where-Object role -eq 'ground' | Sort-Object share -Descending | Select-Object -First 1
    $grounds += [pscustomobject]@{ biome = $b.name; ground = $g.name; hex = $g.hex; L = (Lab ([double[]](ToLin (Rgb $g.hex))))[0] }
  }
}
$grounds = @($grounds | Sort-Object L)
$homes = @{}; foreach ($h in $data.biomes) { $homes[$h.name] = $h }
foreach ($g in $grounds) { if (-not $homes.ContainsKey($g.biome)) { throw "palette.json has no families for the biome '$($g.biome)'" } }

# an outline against each ground
$rows = @(('| Biome | Main ground | L | ' + (($data.outlines | ForEach-Object name) -join ' | ') + ' |'), ('|---|---|---|' + ('---|' * $data.outlines.Count)))
foreach ($g in $grounds) {
  $rows += ('| {0} | {1} `{2}` | {3:F0} | ' -f $g.biome, $g.ground, $g.hex, $g.L) + (($data.outlines | ForEach-Object { '{0:F0}' -f (DeHex $_.hex $g.hex) }) -join ' | ') + ' |'
}
$frag['OUTLINE'] = $rows -join "`n"

# figure tones against each ground
$rows = @(('| Biome | L | ' + (($data.tones | ForEach-Object { "$($_.name) ``$($_.hex)``" }) -join ' | ') + ' |'), ('|---|---|' + ('---|' * $data.tones.Count)))
foreach ($g in $grounds) {
  $rows += ('| {0} | {1:F0} | ' -f $g.biome, $g.L) + (($data.tones | ForEach-Object { '{0:F0}' -f (DeHex $_.hex $g.hex) }) -join ' | ') + ' |'
}
$frag['TONES'] = $rows -join "`n"

# the monster families, and each biome's families against its ground
$family = @{}; foreach ($f in $data.families) { $family[$f.id] = $f }
function FamilyCell([string]$id, [string]$groundHex) {
  $f = $family[$id]
  if (-not $f) { throw "palette.json has no family '$id'" }
  if ($f.hex) { '{0} {1:F0}' -f $f.id, (DeHex $f.hex $groundHex) } else { "$($f.id) (the biome's own material)" }
}
$rows = @('| Family | Tone | Hex | Saturation | Value | Signature family of |', '|---|---|---|---|---|---|')
foreach ($f in $data.families) {
  $where = @($data.biomes | Where-Object signature -eq $f.id | ForEach-Object name) -join ', '
  if ($f.hex) { $c = [double[]](Rgb $f.hex); $rows += ('| {0} | {1} | `{2}` | {3:F0}% | {4:F0}% | {5} |' -f $f.id, $f.tone, $f.hex, ((Sat $c) * 100), ((Val $c) * 100), $where) }
  else { $rows += ('| {0} | {1} | - | - | - | {2} |' -f $f.id, $f.tone, $where) }
}
$frag['FAMILIES'] = $rows -join "`n"
$rows = @('| Biome | Main ground | Signature family | Shared families |', '|---|---|---|---|')
foreach ($g in $grounds) {
  $h = $homes[$g.biome]
  $shared = @($h.shared | ForEach-Object { FamilyCell $_ $g.hex }) -join '; '
  $rows += ('| {0} | {1} `{2}` | {3} | {4} |' -f $g.biome, $g.ground, $g.hex, (FamilyCell $h.signature $g.hex), $shared)
}
$frag['HOME'] = $rows -join "`n"

# ---------- drawing ----------
$svg = New-Object Text.StringBuilder
$pageW = 1240; $pageH = 1210
function A([string]$s) { [void]$svg.Append($s) }
function T($x, $y, $text, $size = 12, $fill = '#E6E6E6', $extra = '') { A "<text x=""$x"" y=""$y"" font-size=""$size"" fill=""$fill"" $extra>$([Security.SecurityElement]::Escape($text))</text>" }
function Box($x, $y, $bw, $bh, $hex, $extra = '') { A ('<rect x="{0:F1}" y="{1:F1}" width="{2:F1}" height="{3:F1}" fill="{4}" {5}/>' -f $x, $y, $bw, $bh, $hex, $extra) }

# A schematic figure as the camera sees it: widths at full size, heights and depths shortened by the pitch.
# $k is pixels per metre across; $style is the SVG paint of every part.
function FigureParts([double]$cx, [double]$baseY, [double]$k, $shape, [string]$style) {
  $vk = $k * $cosP          # pixels per metre of height
  $dk = $k * $sinP          # pixels per metre of depth
  $X = { param($m) $cx + $m * $k }; $Y = { param($m) $baseY - $m * $vk }
  $sh = 1.45; $headY = 1.68; $headR = 0.15
  $bb = $shape.bodyBottom; $bt = $shape.bodyTop
  # lines (weapons) take the paint without a fill, and their own width plus the outline's
  $extra = 0.0; if ($style -match 'stroke-width="([0-9.]+)"') { $extra = [double]$Matches[1] }
  $lineStyle = ($style -replace 'fill="[^"]*"', 'fill="none"') -replace ' stroke-width="[^"]*"', ''
  # weapon parts that lie behind the body
  if ($shape.weapon -eq 'bow') {
    A ('<line x1="{0:F1}" y1="{1:F1}" x2="{2:F1}" y2="{3:F1}" stroke-linecap="round" stroke-width="{4:F1}" {5}/>' -f (& $X (-0.10)), (& $Y 1.20), (& $X (-0.34)), (& $Y 1.82), (0.11 * $k + $extra), $lineStyle)
  }
  # body: two legs up to the hips, a torso from the hips to the shoulders, two arms, the shoulder ellipse, the head
  $hip = 0.90; $legW = $bb * 0.44; $armW = 0.11; $armX = $shape.shoulders / 2 - 0.04
  foreach ($side in -1, 1) {
    A ('<line x1="{0:F1}" y1="{1:F1}" x2="{0:F1}" y2="{2:F1}" stroke-linecap="round" stroke-width="{3:F1}" {4}/>' -f (& $X ($side * ($bb - $legW) / 2)), (& $Y 0.06), (& $Y $hip), ($legW * $k + $extra), $lineStyle)
    A ('<line x1="{0:F1}" y1="{1:F1}" x2="{2:F1}" y2="{3:F1}" stroke-linecap="round" stroke-width="{4:F1}" {5}/>' -f (& $X ($side * $armX)), (& $Y ($sh - 0.05)), (& $X ($side * ($armX + 0.03))), (& $Y 0.98), ($armW * $k + $extra), $lineStyle)
  }
  A ('<polygon points="{0:F1},{1:F1} {2:F1},{1:F1} {3:F1},{4:F1} {5:F1},{4:F1}" {6}/>' -f (& $X (-$bb / 2)), (& $Y ($hip - 0.08)), (& $X ($bb / 2)), (& $X ($bt / 2)), (& $Y $sh), (& $X (-$bt / 2)), $style)
  A ('<ellipse cx="{0:F1}" cy="{1:F1}" rx="{2:F1}" ry="{3:F1}" {4}/>' -f $cx, (& $Y $sh), ($shape.shoulders / 2 * $k), ($shape.shoulderDepth / 2 * $dk), $style)
  A ('<circle cx="{0:F1}" cy="{1:F1}" r="{2:F1}" {3}/>' -f $cx, (& $Y $headY), ($headR * $k), $style)
  switch ($shape.weapon) {
    'swordShield' {
      # a sword in the right hand, point up, and a round shield on the left arm
      A ('<line x1="{0:F1}" y1="{1:F1}" x2="{2:F1}" y2="{3:F1}" stroke-linecap="round" stroke-width="{4:F1}" {5}/>' -f (& $X 0.46), (& $Y 0.90), (& $X 0.60), (& $Y 1.95), (0.09 * $k + $extra), $lineStyle)
      A ('<line x1="{0:F1}" y1="{1:F1}" x2="{2:F1}" y2="{3:F1}" stroke-linecap="round" stroke-width="{4:F1}" {5}/>' -f (& $X 0.36), (& $Y 1.08), (& $X 0.60), (& $Y 1.02), (0.06 * $k + $extra), $lineStyle)
      A ('<circle cx="{0:F1}" cy="{1:F1}" r="{2:F1}" {3}/>' -f (& $X (-0.46)), (& $Y 1.00), (0.30 * $k), $style)
    }
    'bow' {
      A ('<path d="M {0:F1},{1:F1} Q {2:F1},{3:F1} {0:F1},{4:F1}" stroke-linecap="round" stroke-width="{5:F1}" {6}/>' -f (& $X 0.38), (& $Y 0.30), (& $X 0.82), (& $Y 1.10), (& $Y 1.90), (0.06 * $k + $extra), $lineStyle)
    }
    'staff' {
      A ('<line x1="{0:F1}" y1="{1:F1}" x2="{0:F1}" y2="{2:F1}" stroke-linecap="round" stroke-width="{3:F1}" {4}/>' -f (& $X 0.48), $baseY, (& $Y 2.10), (0.06 * $k + $extra), $lineStyle)
      A ('<circle cx="{0:F1}" cy="{1:F1}" r="{2:F1}" {3}/>' -f (& $X 0.48), (& $Y 2.16), (0.11 * $k), $style)
    }
    'gloves' {
      foreach ($side in -1, 1) { A ('<circle cx="{0:F1}" cy="{1:F1}" r="{2:F1}" {3}/>' -f (& $X ($side * 0.42)), (& $Y 0.95), (0.14 * $k), $style) }
    }
  }
}
# A figure in one colour, with an optional outline of $outlinePx around the whole shape.
function Figure([double]$cx, [double]$baseY, [double]$k, $shape, [string]$fill, [string]$outline = '', [double]$outlinePx = 0) {
  if ($outline) { FigureParts $cx $baseY $k $shape ('fill="{0}" stroke="{0}" stroke-width="{1:F1}" stroke-linejoin="round"' -f $outline, ($outlinePx * 2)) }
  FigureParts $cx $baseY $k $shape ('fill="{0}" stroke="{0}" stroke-width="0.6"' -f $fill)
}
$shapes = @{}; foreach ($c in $data.classes) { $shapes[$c.id] = $c }

A "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 $pageW $pageH"" width=""$pageW"" height=""$pageH"" font-family=""Segoe UI, Helvetica, Arial, sans-serif"">"
A "<rect width=""$pageW"" height=""$pageH"" fill=""#26282B""/>"
T 30 40 $data.title 22 '#FFFFFF' 'font-weight="600"'
T 30 62 $data.subtitle 12 '#C8CCD0'

# 1. sizes, one drawn pixel = one screen pixel
T 30 98 ("Size on screen at $($cam.screenHeightPx)p, to pixel scale (one pixel here is one screen pixel), with the placeholder camera values") 14 '#FFFFFF' 'font-weight="600"'
$y1 = 300
Box 30 110 1180 230 $data.sizeGround ''
A ('<line x1="50" y1="322" x2="150" y2="322" stroke="#FFFFFF" stroke-width="2"/><line x1="50" y1="316" x2="50" y2="328" stroke="#FFFFFF" stroke-width="2"/><line x1="150" y1="316" x2="150" y2="328" stroke="#FFFFFF" stroke-width="2"/>')
T 78 314 '100 px' 11 '#FFFFFF'
Figure 230 $y1 $pxPerM $shapes[$data.sizeFigure] $data.tones[1].hex $data.outlines[0].hex 1.5
T 196 328 ('a character: {0} x {1} px' -f $frag['CHARPX'], $frag['CHARWPX']) 11 '#FFFFFF'
$mx = 370.0
foreach ($m in $data.monsters) {
  $wpx = WidePx ($m.radiusCm * 2 / 100.0) 0 0
  A ('<ellipse cx="{0:F1}" cy="{1}" rx="{2:F1}" ry="{3:F1}" fill="#000000" fill-opacity="0.28" stroke="#E6E6E6" stroke-dasharray="4 3"/>' -f ($mx + $wpx / 2), 232, ($wpx / 2), ($wpx / 2 * $sinP))
  T $mx 328 ('{0}: {1:F0} px' -f $m.name, $wpx) 11 '#FFFFFF'
  $mx += $wpx + 34
}
T 370 134 'Dashed: the footprint of a monster (its body radius). Monster heights are not in the data.' 11 '#E6E6E6'

# 2. the root classes as black shapes: at screen size, then enlarged
T 30 378 'The root classes as black shapes, as the camera sees them: at screen size (top row) and enlarged (bottom row)' 14 '#FFFFFF' 'font-weight="600"'
Box 30 390 1180 520 '#B9B9B1' ''
$bx = 172.0
foreach ($c in $data.classes) { Figure $bx 536 $pxPerM $c '#111111'; $bx += 227 }
$big = $pxPerM * 2.2; $bx = 172.0
foreach ($c in $data.classes) {
  Figure $bx 868 $big $c '#111111'
  T ($bx - 96) 896 ("$($c.name): $($c.armour) armour, $($c.carries)") 11 '#26282B'
  $bx += 227
}
T 950 412 'Schematic shapes, not character designs.' 11 '#26282B'

# 3. a dark and a light outline on three grounds
T 30 948 'One figure with a dark and with a light outline, on the lightest, a middle and the darkest main ground' 14 '#FFFFFF' 'font-weight="600"'
$picks = @($grounds[-1], ($grounds | Where-Object biome -eq $data.middleGround), $grounds[0])
$gx = 30.0
foreach ($g in $picks) {
  Box $gx 960 380 190 $g.hex 'stroke="#111316"'
  $fx = $gx + 100
  foreach ($o in $data.outlines) {
    Figure $fx 1120 ($pxPerM * 1.3) $shapes[$data.sizeFigure] $data.tones[1].hex $o.hex 2
    T ($fx - 62) 1145 ('{0}: {1:F0}' -f $o.name, (DeHex $o.hex $g.hex)) 11 $(if ($g.L -gt 60) { '#26282B' } else { '#FFFFFF' })
    $fx += 190
  }
  T $gx 1168 ("$($g.biome): $($g.ground) $($g.hex)") 11 '#C8CCD0'
  $gx += 400
}
T 30 1190 'The number is the colour difference between the outline and the ground (higher reads better).' 11 '#C8CCD0'
A '</svg>'

$photos = Get-PhotoFragments $BoardDir
foreach ($k in $photos.Keys) { $frag[$k] = $photos[$k] }
Write-Board $svg.ToString() $frag $BoardDir $OutDir $data.output
