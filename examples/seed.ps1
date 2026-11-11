<#
.SYNOPSIS
  Creates demo links and simulates varied visitors through the real HTTP API.

.DESCRIPTION
  Every visit is a real GET /{slug} that is not followed, with a browser User-Agent and an optional Referer, so the
  traffic goes through the same pipeline as production clicks. The random generator is seeded: the same parameters
  produce the same traffic mix. Visits go through the redirect rate limit (120 per minute by default); the script
  waits and retries when it is told to slow down. For a fast run start the server with
  CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE=100000. Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER BaseUrl
  Server origin. Defaults to $env:CURTO_URL, then http://127.0.0.1:5080.

.PARAMETER Clicks
  Number of simulated visits (default 600).

.PARAMETER DelaySeconds
  Pause between visits (default 0). Try 0.2 to watch the Live view tick.

.PARAMETER ApiKey
  The key the server runs with. Defaults to $env:CURTO_API_KEY.

.EXAMPLE
  $env:CURTO_API_KEY = 'local-development-key-123'; ./examples/seed.ps1 -BaseUrl http://127.0.0.1:5080 -Clicks 600
#>
[CmdletBinding()]
param(
  [string] $BaseUrl = $(if ($env:CURTO_URL) { $env:CURTO_URL } else { 'http://127.0.0.1:5080' }),
  [int] $Clicks = 600,
  [double] $DelaySeconds = 0,
  [string] $ApiKey = $env:CURTO_API_KEY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ApiKey) { throw 'Set CURTO_API_KEY (or pass -ApiKey) to the key the server runs with.' }
$BaseUrl = $BaseUrl.TrimEnd('/')

# slug|target|title|tags|share of the traffic
$linkTable = @'
launch|https://blog.example.com/curto-launch|Launch announcement|blog,launch|30
guide|https://docs.example.com/getting-started|Getting started guide|docs|14
newsletter|https://news.example.org/issues/42|Newsletter issue 42|email,newsletter|12
podcast|https://audio.example.net/episodes/17|Podcast episode 17|podcast,audio|9
pricing|https://www.example.com/pricing|Pricing page|product|8
github|https://code.example.com/curto|Source code|code,oss|7
changelog|https://docs.example.com/changelog|Changelog|docs,release|6
webinar|https://events.example.org/webinar-q4|Q4 webinar|event|5
q4-report|https://files.example.net/reports/2026-q4.pdf|Q4 report (PDF)|report|4
survey|https://forms.example.com/community-survey|Community survey|research|3
invite|https://app.example.org/invite/early-access|Early access invite|growth|2
'@

# Browser templates and their share of the visitors. {v} and {p} make every simulated visitor's User-Agent unique.
$uaTemplates = @(
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Safari/537.36'
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E{p} Safari/604.1'
  'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Mobile Safari/537.36'
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.{p} Safari/605.1.15'
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Safari/537.36'
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Safari/537.36 Edg/{v}.0.{p}.0'
  'Mozilla/5.0 (X11; Linux x86_64; rv:{v}.{p}) Gecko/20100101 Firefox/{v}.{p}'
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{v}.{p}) Gecko/20100101 Firefox/{v}.{p}'
  'Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/24.{p} Chrome/{v}.0.0.0 Mobile Safari/537.36'
  'Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E{p} Safari/604.1'
  'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)'
)
$uaWeights = @(26, 17, 13, 8, 7, 6, 4, 3, 3, 3, 2)

# Referer values (empty means direct traffic) and their share.
$referrers = @('', 'https://news.ycombinator.com/item?id=41234567', 'https://t.co/aB3dE5', 'https://www.linkedin.com/feed/',
  'https://www.reddit.com/r/selfhosted/', 'https://www.google.com/', 'https://github.com/q6r/curto', 'https://lobste.rs/',
  'https://bsky.app/', 'https://duckduckgo.com/')
$referrerWeights = @(30, 14, 12, 10, 9, 8, 6, 4, 4, 3)

Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$http = [System.Net.Http.HttpClient]::new($handler)
$rng = [System.Random]::new(2026)

function Send-Request {
  param([string] $Method, [string] $Path, [hashtable] $Headers = @{}, [string] $Json)
  $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$BaseUrl$Path")
  foreach ($name in $Headers.Keys) { [void] $request.Headers.TryAddWithoutValidation($name, [string] $Headers[$name]) }
  if ($Json) { $request.Content = [System.Net.Http.StringContent]::new($Json, [System.Text.Encoding]::UTF8, 'application/json') }
  $response = $http.SendAsync($request).GetAwaiter().GetResult()
  $status = [int] $response.StatusCode
  $retry = 0
  if ($response.Headers.RetryAfter -and $response.Headers.RetryAfter.Delta) {
    $retry = [int] [math]::Ceiling($response.Headers.RetryAfter.Delta.TotalSeconds)
  }
  $response.Dispose()
  [pscustomobject] @{ Status = $status; RetryAfter = $retry }
}

function Select-Weighted([int[]] $Weights) {
  $r = $rng.Next(($Weights | Measure-Object -Sum).Sum)
  for ($i = 0; $i -lt $Weights.Count; $i++) {
    if ($r -lt $Weights[$i]) { return $i }
    $r -= $Weights[$i]
  }
}

function New-Link([string] $Slug, [string] $Target, [string] $Title, [string[]] $Tags, [hashtable] $Extra = @{}) {
  $body = [ordered] @{ slug = $Slug; url = $Target; title = $Title; tags = @($Tags) }
  foreach ($key in $Extra.Keys) { $body[$key] = $Extra[$key] }
  $result = Send-Request -Method POST -Path '/api/links' -Headers @{ 'X-API-Key' = $ApiKey } -Json ($body | ConvertTo-Json -Compress)
  switch ($result.Status) {
    201 { Write-Host "  created /$Slug" }
    409 { Write-Host "  /$Slug already exists, keeping it" }
    default { throw "Creating /$Slug failed with HTTP $($result.Status)" }
  }
}

function Invoke-Hit([string] $Slug, [string] $UserAgent, [string] $Referer) {
  $headers = @{ 'User-Agent' = $UserAgent }
  if ($Referer) { $headers['Referer'] = $Referer }
  while ($true) {
    $result = Send-Request -Method GET -Path "/$Slug" -Headers $headers
    if ($result.Status -ne 429) { return $result.Status }
    $wait = if ($result.RetryAfter -gt 0) { $result.RetryAfter } else { 5 }
    Write-Host "  rate limited, waiting ${wait}s (raise CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE for faster seeding)"
    Start-Sleep -Seconds $wait
  }
}

try {
  $health = Send-Request -Method GET -Path '/healthz'
} catch {
  throw "No curto server answers at ${BaseUrl}: $($_.Exception.Message)"
}
if ($health.Status -ne 200) { throw "No healthy curto server at $BaseUrl (HTTP $($health.Status))" }

Write-Host "Creating links at $BaseUrl"
$slugs = @(); $weights = @()
foreach ($line in ($linkTable -split "\r?\n" | Where-Object { $_ })) {
  $f = $line -split '\|'
  New-Link -Slug $f[0] -Target $f[1] -Title $f[2] -Tags ($f[3] -split ',')
  $slugs += $f[0]; $weights += [int] $f[4]
}
New-Link -Slug 'flash-sale' -Target 'https://shop.example.com/flash-sale' -Title 'Flash sale (25 clicks)' -Tags @('campaign') -Extra @{ maxClicks = 25 }
$betaExpiry = [DateTime]::UtcNow.AddSeconds(8)
New-Link -Slug 'beta-signup' -Target 'https://beta.example.org/signup' -Title 'Beta signup (expires in seconds)' -Tags @('campaign') `
  -Extra @{ expiresAt = $betaExpiry.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") }

# A pool of simulated visitors: each one keeps the same User-Agent, so some of them come back.
$visitorCount = [math]::Max(1, [int] [math]::Floor($Clicks * 10 / 22))
$visitors = @(foreach ($i in 1..$visitorCount) {
  [pscustomobject] @{ Template = Select-Weighted $uaWeights; Version = 118 + $rng.Next(8); Patch = 100 + $rng.Next(9000) }
})
$script:counts = @{ ok = 0; gone = 0; other = 0 }

function Invoke-Visit([string] $Slug) {
  $visitor = $visitors[$rng.Next($visitors.Count)]
  $ua = $uaTemplates[$visitor.Template].Replace('{v}', [string] $visitor.Version).Replace('{p}', [string] $visitor.Patch)
  $referrer = $referrers[(Select-Weighted $referrerWeights)]
  switch (Invoke-Hit -Slug $Slug -UserAgent $ua -Referer $referrer) {
    302 { $script:counts.ok++ }
    410 { $script:counts.gone++ }
    default { $script:counts.other++ }
  }
}

1..3 | ForEach-Object { Invoke-Visit 'beta-signup' }   # it is still valid for a few seconds

Write-Host "Simulating $Clicks visits by $visitorCount visitors"
for ($n = 1; $n -le $Clicks; $n++) {
  Invoke-Visit $slugs[(Select-Weighted $weights)]
  if ($n % 100 -eq 0) { Write-Host "  $n / $Clicks" }
  if ($DelaySeconds -gt 0) { Start-Sleep -Milliseconds ([int] ($DelaySeconds * 1000)) }
}

Write-Host 'Spending the click budget of /flash-sale (25 clicks, then HTTP 410)'
1..30 | ForEach-Object { Invoke-Visit 'flash-sale' }

Write-Host 'Checking that /beta-signup has expired'
$remaining = ($betaExpiry.AddSeconds(1) - [DateTime]::UtcNow).TotalSeconds
if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int] ($remaining * 1000)) }
Invoke-Visit 'beta-signup'

Write-Host "Done: $($counts.ok) redirects recorded, $($counts.gone) refused with 410, $($counts.other) other."
Write-Host "Open $BaseUrl/ and enter the API key."
$http.Dispose()
