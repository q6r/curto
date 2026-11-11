# Examples

Runnable material for a curto server. Every script takes the base URL as an argument (or `CURTO_URL`) and the API
key from `CURTO_API_KEY`, and defaults to `http://127.0.0.1:5080`.

| File | What it does |
|---|---|
| [`curto.http`](curto.http) | The API as a request collection for the VS Code REST Client and the JetBrains HTTP client. |
| [`curl.sh`](curl.sh), [`curl.ps1`](curl.ps1) | A guided tour with curl (Bash, and PowerShell 5.1 or 7): create a link, break it on purpose, patch it, follow it, read its statistics, delete it. It cleans up after itself. |
| [`seed.sh`](seed.sh), [`seed.ps1`](seed.ps1) | Create a dozen demo links and simulate a few hundred varied visits through the real HTTP API: different browsers, operating systems, devices and referrers, returning visitors, one link with a click budget that runs out, one that expires. Seeded random generator: the same arguments give the same traffic. |
| [`Curto.DemoHistory`](Curto.DemoHistory) | A small .NET tool that backdates the links of a **demo** database and fills it with 90 days of plausible clicks. |

## Seeding a demo

```bash
# terminal 1: a server with a throw-away database and relaxed redirect limits
CURTO_API_KEY="local-development-key-123" CURTO_DB_PATH=/tmp/curto-demo.db \
CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE=100000 ASPNETCORE_URLS=http://127.0.0.1:5080 \
dotnet run --project src/Curto.Api

# terminal 2: links and live traffic
CURTO_API_KEY="local-development-key-123" ./examples/seed.sh http://127.0.0.1:5080 300
```

```powershell
# the same on Windows PowerShell
$env:CURTO_API_KEY = "local-development-key-123"; $env:CURTO_DB_PATH = "$env:TEMP\curto-demo.db"
$env:CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE = "100000"; $env:ASPNETCORE_URLS = "http://127.0.0.1:5080"
dotnet run --project src/Curto.Api

./examples/seed.ps1 -BaseUrl http://127.0.0.1:5080 -Clicks 300
```

Pass a delay as the third argument of `seed.sh` (or `-DelaySeconds` for `seed.ps1`), for example `0.2`, to watch the
Live view tick while the script runs. Without `CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE` raised, the script waits
whenever the server says `429`.

## Backdated history

Visits made over HTTP are always stamped with the current time, so a freshly seeded database has one busy day and
nothing before it. To give the charts a past, run the history tool against the same database file once the server
has created it (the server may keep running):

```bash
dotnet run --project examples/Curto.DemoHistory -- --db /tmp/curto-demo.db --days 90
```

```text
Added 7,932 clicks over 90 days to 11 of 13 links (seed 2026).
```

It writes straight into the SQLite file, moves the creation dates of the links back in time, and adds clicks with a
weekly rhythm, a daily rhythm, a slow upward trend and a campaign-style spike on some links. Links with a click
budget or an expiry keep the shape the API gave them. It refuses to run on a database that already has history, and
it is meant for demo data only: never point it at a database that matters.

Options: `--days` (7-365, default 90), `--clicks-per-day` (default 160, the average across all links), `--seed`
(default 2026).

## How the README screenshots were made

On a throw-away database: `seed.ps1 -Clicks 110` (few live visits, so that today does not tower over the history),
then the history tool above with its defaults. The Live view was captured with the page open while `seed.ps1` ran a
few more times with different delays, so the last five minutes had some shape.
