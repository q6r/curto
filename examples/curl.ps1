<#
.SYNOPSIS
  A guided tour of the curto API with curl. It needs a running server and the API key it was started with.

.DESCRIPTION
  Creates one link, uses it, and deletes it again. Nothing else is touched. Works in Windows PowerShell 5.1 and
  PowerShell 7. JSON bodies are piped to curl on stdin, which avoids the quoting rules for native commands.

.EXAMPLE
  $env:CURTO_API_KEY = 'local-development-key-123'; ./examples/curl.ps1 -BaseUrl http://127.0.0.1:5080
#>
[CmdletBinding()]
param(
  [string] $BaseUrl = $(if ($env:CURTO_URL) { $env:CURTO_URL } else { 'http://127.0.0.1:5080' }),
  [string] $ApiKey = $env:CURTO_API_KEY
)

$ErrorActionPreference = 'Stop'
if (-not $ApiKey) { throw 'Set CURTO_API_KEY (or pass -ApiKey) to the key the server runs with.' }
$base = $BaseUrl.TrimEnd('/')

# On Windows "curl" is an alias of Invoke-WebRequest in PowerShell 5.1, so call the real binary explicitly.
$onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
$curl = if ($onWindows) { 'curl.exe' } else { 'curl' }
$sink = if ($onWindows) { 'NUL' } else { '/dev/null' }

function Step([string] $Title) { Write-Host "`n# $Title" }

function Send([string] $Method, [string] $Path, [string] $Json, [switch] $NoKey) {
  $curlArgs = @('-sS', '-X', $Method, "$base$Path")
  if (-not $NoKey) { $curlArgs += @('-H', "X-API-Key: $ApiKey") }
  if ($Json) {
    $curlArgs += @('-H', 'Content-Type: application/json', '--data-binary', '@-')
    $Json | & $curl @curlArgs
  } else {
    & $curl @curlArgs
  }
  Write-Host ''
}

Step 'Liveness needs no key'
Send GET '/healthz' -NoKey

Step 'Create a link with a custom slug, a title and tags'
Send POST '/api/links' '{"url":"https://example.com/blog/hello-world","slug":"tour","title":"Guided tour","tags":["blog","demo"]}'

Step 'The same slug again is a conflict (409)'
Send POST '/api/links' '{"url":"https://example.com/","slug":"tour"}'

Step 'Bad input is reported per field (400)'
Send POST '/api/links' '{"url":"ftp://example.com/file","slug":"a","tags":["ok","not ok!"]}'

Step 'Update with a JSON merge patch: present fields change, null clears'
Send PATCH '/api/links/tour' '{"title":"Guided tour (edited)","maxClicks":50}'

Step 'Follow the short link: 302 and a recorded click (curl does not follow redirects unless asked)'
& $curl -sS -i "$base/tour" -H 'Referer: https://news.ycombinator.com/item?id=1' `
  -H 'User-Agent: Mozilla/5.0 (X11; Linux x86_64; rv:125.0) Gecko/20100101 Firefox/125.0' |
  Select-String -Pattern '^(HTTP/|Location:|Cache-Control:)' | ForEach-Object { $_.Line.TrimEnd() }

Step 'List links: filter, sort and page'
Send GET '/api/links?query=tour&status=active&sort=clicks&dir=desc&limit=5'

Step 'QR code of the short URL (SVG, public): the first 100 characters'
$svg = (& $curl -sS "$base/qr/tour.svg?size=128") -join "`n"
$svg.Substring(0, [math]::Min(100, $svg.Length))

Step 'Statistics of the link over the last 24 hours'
Start-Sleep -Seconds 1   # the click is written asynchronously, within a fraction of a second
(& $curl -sS "$base/api/links/tour/stats?range=24h" -H "X-API-Key: $ApiKey") -replace '"series":\[[^\]]*\],', ''

Step 'Without the key every /api call is refused (401)'
& $curl -sS -o $sink -w 'HTTP %{http_code}\n' "$base/api/links"

Step 'Delete the link and its analytics (204), then it is gone (404)'
& $curl -sS -o $sink -w 'DELETE: HTTP %{http_code}\n' -X DELETE "$base/api/links/tour" -H "X-API-Key: $ApiKey"
& $curl -sS -o $sink -w 'GET:    HTTP %{http_code}\n' "$base/api/links/tour" -H "X-API-Key: $ApiKey"
