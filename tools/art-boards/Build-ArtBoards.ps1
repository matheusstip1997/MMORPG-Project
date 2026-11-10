param([string[]]$Board, [switch]$Check)
# Rebuilds the art reference boards in docs/design from tools/art-boards/boards/<id>.
#   powershell -ExecutionPolicy Bypass -File tools/art-boards/Build-ArtBoards.ps1              rebuild all six
#   powershell -ExecutionPolicy Bypass -File tools/art-boards/Build-ArtBoards.ps1 -Board c2,w3 rebuild only these
#   powershell -ExecutionPolicy Bypass -File tools/art-boards/Build-ArtBoards.ps1 -Check       build to a temp folder and compare
# -Check changes nothing in the repository and exits with 1 when a board in docs/design differs from what the inputs give.
# Windows PowerShell 5.1; see README.md.
$ErrorActionPreference = 'Stop'
$scripts = [ordered]@{
  w1 = 'BiomeBoard.ps1'; c1 = 'CityBoard-C1.ps1'
  w2 = 'BiomeBoard.ps1'; c2 = 'CityBoard-C2.ps1'
  w3 = 'BiomeBoard.ps1'; c3 = 'CityBoard-C3.ps1'
}
$design = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'docs\design'
$ids = @($Board | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLower() } | Where-Object { $_ })
if ($ids.Count -eq 0) { $ids = @($scripts.Keys) }
foreach ($id in $ids) { if (-not $scripts.Contains($id)) { throw "unknown board '$id' (known: $($scripts.Keys -join ', '))" } }

$outDir = $design
if ($Check) {
  $outDir = Join-Path ([IO.Path]::GetTempPath()) ('art-boards-check-' + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force $outDir | Out-Null
}
function ReadText([string]$path) { [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8).Replace("`r`n", "`n") }

$culture = [Threading.Thread]::CurrentThread.CurrentCulture
$different = 0
try {
  foreach ($id in $ids) {
    $boardDir = Join-Path $PSScriptRoot "boards\$id"
    & (Join-Path $PSScriptRoot $scripts[$id]) -BoardDir $boardDir -OutDir $outDir
    $name = (Get-Content (Join-Path $boardDir 'palette.json') -Raw -Encoding UTF8 | ConvertFrom-Json).output
    foreach ($ext in 'md', 'svg') {
      $file = "$name.$ext"
      if (-not $Check) { Write-Host "built  docs/design/$file"; continue }
      $committed = Join-Path $design $file
      if (-not (Test-Path $committed)) { Write-Host "MISSING    docs/design/$file"; $different++; continue }
      $a = (ReadText $committed) -split "`n"; $b = (ReadText (Join-Path $outDir $file)) -split "`n"
      $line = 0; $max = [Math]::Max($a.Count, $b.Count)
      for ($i = 0; $i -lt $max; $i++) { if ($a[$i] -cne $b[$i]) { $line = $i + 1; break } }
      if ($line -eq 0) { Write-Host "same       docs/design/$file" }
      else { Write-Host "DIFFERENT  docs/design/$file (first at line $line)"; $different++ }
    }
  }
} finally {
  [Threading.Thread]::CurrentThread.CurrentCulture = $culture
  if ($Check) { Remove-Item $outDir -Recurse -Force }
}
if ($different -gt 0) { Write-Host "$different file(s) differ from the inputs in tools/art-boards/boards."; exit 1 }
