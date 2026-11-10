param([Parameter(Mandatory = $true)][string]$OutDir, [Parameter(Mandatory = $true)][string]$QueryFile)
# Searches Wikimedia Commons (file namespace) for photo references and writes two files into -OutDir:
#   refs.json   one entry per hit: key, title, page, thumb, license, artist (sorted by key, then title)
#   sheet.html  a contact sheet of hot-linked 330 px thumbnails, numbered in the order of refs.json
# -QueryFile is a JSON object of "<section>.<topic>": "<search words>", for example { "marsh.reeds": "reed bed marsh" }.
# Nothing is downloaded into the repository: the boards only link to the Commons pages.
# The script can be run again with the same -OutDir: topics that already have three or more hits are skipped.
# Commons limits anonymous requests, so the script waits 6 s between searches and retries once after 30 s.
# The request carries a generic client name and no personal data. Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$userAgent = 'art-reference-research/1.0'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$queries = [ordered]@{}
(Get-Content $QueryFile -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $queries[$_.Name] = $_.Value }

$all = @(); $old = Join-Path $OutDir 'refs.json'
if (Test-Path $old) { $all = @(Get-Content $old -Raw -Encoding UTF8 | ConvertFrom-Json) }
$have = @{}; foreach ($g in ($all | Group-Object key)) { if ($g.Count -ge 3) { $have[$g.Name] = 1 } }
$all = @($all | Where-Object { $have.ContainsKey($_.key) })

foreach ($k in $queries.Keys) {
  if ($have.ContainsKey($k)) { continue }
  $q = [uri]::EscapeDataString($queries[$k] + ' filetype:bitmap')
  $u = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=6&gsrsearch=$q&prop=imageinfo&iiprop=url|extmetadata&iiurlwidth=330&iiextmetadatafilter=LicenseShortName|Artist|ImageDescription"
  try { $r = Invoke-RestMethod -Uri $u -UserAgent $userAgent -TimeoutSec 30 }
  catch {
    Start-Sleep -Seconds 30
    try { $r = Invoke-RestMethod -Uri $u -UserAgent $userAgent -TimeoutSec 30 } catch { Write-Host "FAIL $k"; continue }
  }
  if (-not $r.query) { Write-Host "EMPTY $k"; continue }
  foreach ($p in $r.query.pages.PSObject.Properties.Value | Sort-Object index) {
    $ii = $p.imageinfo[0]
    $artist = ($ii.extmetadata.Artist.value -replace '<[^>]+>', '').Trim()
    if ($artist.Length -gt 60) { $artist = $artist.Substring(0, 60) }
    $all += [pscustomobject]@{ key = $k; title = $p.title; page = $ii.descriptionurl; thumb = $ii.thumburl; license = $ii.extmetadata.LicenseShortName.value; artist = $artist }
  }
  Start-Sleep -Seconds 6
}

$all = @($all | Sort-Object key, title)
$all | ConvertTo-Json -Depth 4 | Out-File -Encoding utf8 (Join-Path $OutDir 'refs.json')
$sb = New-Object Text.StringBuilder
[void]$sb.Append('<!doctype html><meta charset="utf-8"><title>refs</title><style>body{font:11px sans-serif;margin:8px}h2{margin:6px 0 2px;font-size:13px}.r{display:flex;gap:6px}.c{width:200px}.c img{width:200px;height:130px;object-fit:cover;display:block}</style>')
$n = 0
foreach ($g in $all | Group-Object key) {
  [void]$sb.Append("<h2>$($g.Name)</h2><div class=r>")
  foreach ($i in $g.Group) { $n++; [void]$sb.Append("<div class=c><img src=""$($i.thumb)""><b>$n</b> $([Net.WebUtility]::HtmlEncode($i.license))</div>") }
  [void]$sb.Append('</div>')
}
$sb.ToString() | Out-File -Encoding utf8 (Join-Path $OutDir 'sheet.html')
$n = 0
foreach ($i in $all) { $n++; Write-Host ('{0,3} {1,-20} {2,-14} {3}' -f $n, $i.key, $i.license, ($i.title -replace '^File:', '')) }
