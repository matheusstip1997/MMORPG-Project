param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$OutPng, [int]$Width = 1240, [int]$Height = 0, [int]$PageHeight = 0)
# Renders a local SVG or HTML file to a PNG with headless Microsoft Edge, to look at a board or a contact sheet.
#   -Path        the .svg or .html file
#   -OutPng      the picture to write
#   -Height      the page height in pixels; 0 reads it from an SVG's height attribute
#   -PageHeight  when above 0, also writes <OutPng>-0.png, -1.png ... of that height each (for long pages)
# Example: powershell -File tools/art-boards/Render-Page.ps1 -Path docs/design/reference-board-c3.svg -OutPng $env:TEMP\c3.png
# A contact sheet of Find-PhotoRefs.ps1 has one row of about 165 px per topic: -Width 1260 -Height (30 + rows * 165).
# Windows PowerShell 5.1. Needs Microsoft Edge; nothing is written into the repository unless -OutPng points there.
$ErrorActionPreference = 'Stop'
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
if (-not (Test-Path $edge)) { throw 'Microsoft Edge was not found.' }
$full = (Resolve-Path $Path).Path
if ($Height -le 0) {
  $m = [regex]::Match([IO.File]::ReadAllText($full), '<svg[^>]*\sheight="(\d+)"')
  if (-not $m.Success) { throw 'Give -Height: the file has no svg height attribute.' }
  $Height = [int]$m.Groups[1].Value
}
$OutPng = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutPng)
New-Item -ItemType Directory -Force (Split-Path $OutPng -Parent) | Out-Null
$edgeProfile = Join-Path ([IO.Path]::GetTempPath()) 'art-boards-edge-profile'
$url = 'file:///' + ($full -replace '\\', '/')
Start-Process -FilePath $edge -Wait -ArgumentList @('--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=$edgeProfile", "--window-size=$Width,$Height", '--virtual-time-budget=40000', "--screenshot=$OutPng", $url)
if (-not (Test-Path $OutPng)) { throw "Edge wrote no picture to $OutPng." }
if ($PageHeight -gt 0) {
  Add-Type -AssemblyName System.Drawing
  $img = [Drawing.Image]::FromFile($OutPng)
  try {
    $parts = [Math]::Ceiling($img.Height / $PageHeight)
    for ($i = 0; $i -lt $parts; $i++) {
      $ph = [Math]::Min($PageHeight, $img.Height - $i * $PageHeight)
      $bmp = New-Object Drawing.Bitmap $img.Width, $ph
      $g = [Drawing.Graphics]::FromImage($bmp)
      $g.DrawImage($img, (New-Object Drawing.Rectangle 0, 0, $img.Width, $ph), (New-Object Drawing.Rectangle 0, ($i * $PageHeight), $img.Width, $ph), [Drawing.GraphicsUnit]::Pixel)
      $bmp.Save(($OutPng -replace '\.png$', "-$i.png"), [Drawing.Imaging.ImageFormat]::Png)
      $g.Dispose(); $bmp.Dispose()
    }
  } finally { $img.Dispose() }
}
Write-Host "wrote $OutPng ($Width x $Height)"
