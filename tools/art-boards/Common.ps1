# Shared by the board scripts (dot-sourced): colour math, the warning blend, photo lines and writing the outputs.
# The method is written out in docs/design/reference-boards-w1.md, "The exact method". Keep the two in step.
# Windows PowerShell 5.1. Keep this file ASCII: 5.1 reads a script without a BOM as ANSI.
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture

# sRGB hex -> three channels in 0..1
function Rgb([string]$hex) { $h = $hex.TrimStart('#'); 0..2 | ForEach-Object { [Convert]::ToInt32($h.Substring($_ * 2, 2), 16) / 255.0 } }
# sRGB <-> linear light (IEC 61966-2-1)
function ToLin([double[]]$c) { $c | ForEach-Object { if ($_ -le 0.04045) { $_ / 12.92 } else { [Math]::Pow(($_ + 0.055) / 1.055, 2.4) } } }
function ToSrgb([double[]]$c) { $c | ForEach-Object { $v = [Math]::Min(1.0, [Math]::Max(0.0, $_)); if ($v -le 0.0031308) { $v * 12.92 } else { 1.055 * [Math]::Pow($v, 1 / 2.4) - 0.055 } } }
function ToHex([double[]]$c) { '#' + (($c | ForEach-Object { ([int][Math]::Round($_ * 255)).ToString('X2') }) -join '') }
# HSV saturation and value of sRGB channels
function Sat([double[]]$c) { $mx = ($c | Measure-Object -Maximum).Maximum; $mn = ($c | Measure-Object -Minimum).Minimum; if ($mx -eq 0) { 0 } else { ($mx - $mn) / $mx } }
function Val([double[]]$c) { ($c | Measure-Object -Maximum).Maximum }
# linear RGB -> CIE L*a*b* (sRGB primaries, D65 white)
function Lab([double[]]$l) {
  $x = (0.4124564 * $l[0] + 0.3575761 * $l[1] + 0.1804375 * $l[2]) / 0.95047
  $y = (0.2126729 * $l[0] + 0.7151522 * $l[1] + 0.0721750 * $l[2])
  $z = (0.0193339 * $l[0] + 0.1191920 * $l[1] + 0.9503041 * $l[2]) / 1.08883
  $f = @($x, $y, $z) | ForEach-Object { if ($_ -gt 0.008856) { [Math]::Pow($_, 1 / 3.0) } else { 7.787 * $_ + 16 / 116.0 } }
  @((116 * $f[1] - 16), (500 * ($f[0] - $f[1])), (200 * ($f[1] - $f[2])))
}
# CIE76 colour difference between two colours in linear RGB, and between two sRGB hex values
function DeltaE([double[]]$a, [double[]]$b) { $la = Lab $a; $lb = Lab $b; [Math]::Sqrt([Math]::Pow($la[0] - $lb[0], 2) + [Math]::Pow($la[1] - $lb[1], 2) + [Math]::Pow($la[2] - $lb[2], 2)) }
function DeHex([string]$h1, [string]$h2) { DeltaE ([double[]](ToLin (Rgb $h1))) ([double[]](ToLin (Rgb $h2))) }
# alpha blend in linear light
function Blend([double[]]$src, [double]$a, [double[]]$dst) { 0..2 | ForEach-Object { $a * $src[$_] + (1 - $a) * $dst[$_] } }

# Colour-vision simulation: Machado, Oliveira, Fernandes 2009, severity 1.0, applied in linear RGB (rows).
$cvd = [ordered]@{
  normal = @(1, 0, 0, 0, 1, 0, 0, 0, 1)
  protan = @(0.152286, 1.052583, -0.204868, 0.114503, 0.786281, 0.099216, -0.003882, -0.048116, 1.051998)
  deutan = @(0.367322, 0.860646, -0.227968, 0.280085, 0.672501, 0.047413, -0.011820, 0.042940, 0.968881)
  tritan = @(1.255528, -0.076749, -0.178779, -0.078411, 0.930809, 0.147602, 0.004733, 0.691367, 0.303900)
}
function Sim([double[]]$l, $m) { 0..2 | ForEach-Object { [Math]::Min(1.0, [Math]::Max(0.0, $m[$_ * 3] * $l[0] + $m[$_ * 3 + 1] * $l[1] + $m[$_ * 3 + 2] * $l[2])) } }

# A board's inputs: <BoardDir>\palette.json
function Read-Board([string]$boardDir) { Get-Content (Join-Path $boardDir 'palette.json') -Raw -Encoding UTF8 | ConvertFrom-Json }

# The warning colours of palette.json ("telegraphs": hex and alpha byte), in linear light.
function Get-Telegraphs($data) {
  $data.telegraphs | ForEach-Object { [pscustomobject]@{ name = $_.name; lin = [double[]](ToLin (Rgb $_.hex)); a = [Convert]::ToInt32($_.alpha, 16) / 255.0 } }
}
# Each warning blended over a ground. Uses $tele of the calling script.
# lin: the unrounded blend; hex: the blend rounded to sRGB; de: the difference between the bare and the covered ground.
function TeleRow([string]$groundHex) {
  $g = [double[]](ToLin (Rgb $groundHex))
  $tele | ForEach-Object { $b = [double[]](Blend $_.lin $_.a $g); [pscustomobject]@{ name = $_.name; lin = $b; hex = (ToHex (ToSrgb $b)); de = (DeltaE $g $b) } }
}
# A difference as a percentage of the same warning on today's ground. Uses $base of the calling script.
function Pct($de, $name) { $b = ($base | Where-Object name -eq $name).de; [int][Math]::Round(100 * $de / $b) }

# <BoardDir>\photos.json -> one markdown list per section, as template fragments REFS:<section>.
function Get-PhotoFragments([string]$boardDir) {
  $out = @{}
  $file = Join-Path $boardDir 'photos.json'
  if (-not (Test-Path $file)) { return $out }
  $pk = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
  foreach ($p in $pk.PSObject.Properties) {
    $lines = @()
    foreach ($e in $p.Value) { $lines += "- [$($e.title)]($($e.url)): $($e.note). $($e.author), $($e.licence)." }
    $out["REFS:$($p.Name)"] = $lines -join "`n"
  }
  $out
}

# Writes <OutDir>\<name>.svg and fills <BoardDir>\template.md into <OutDir>\<name>.md (UTF-8 without BOM, LF).
function Write-Board([string]$svgText, $fragments, [string]$boardDir, [string]$outDir, [string]$name) {
  $utf8 = New-Object Text.UTF8Encoding($false)
  [IO.File]::WriteAllText((Join-Path $outDir "$name.svg"), $svgText.Replace('><', ">`n<") + "`n", $utf8)
  $md = [IO.File]::ReadAllText((Join-Path $boardDir 'template.md'), $utf8)
  foreach ($k in $fragments.Keys) { $md = $md.Replace("{{$k}}", $fragments[$k]) }
  if ($md -match '\{\{[A-Z:a-z0-9]+\}\}') { throw "unfilled placeholder in $boardDir\template.md: $($Matches[0])" }
  [IO.File]::WriteAllText((Join-Path $outDir "$name.md"), $md.Replace("`r`n", "`n"), $utf8)
}
