# Configuration

curto reads one configuration section, `Curto`, and validates it when the host starts. A bad value stops the
process before it listens, and the error names every problem at once; nothing is silently clamped.

## Where values come from

From lowest to highest precedence:

1. `appsettings.json` (and `appsettings.<Environment>.json`)
2. environment variables in the generic form `Curto__RateLimits__ApiPerMinute=600`
3. the friendly `CURTO_*` aliases from the table below
4. command-line arguments, for example `--Curto:RetentionDays=90`

The `CURTO_*` aliases exist so that a `docker run -e` or a systemd unit reads naturally. If a value is set both
as an alias and in the generic form, the alias wins. Blank aliases are ignored.

## Options

| Option (`Curto:` section) | Environment alias | Default | Valid range | What it does |
|---|---|---|---|---|
| `ApiKey` | `CURTO_API_KEY` | none | 16-256 printable ASCII characters, no spaces | Secret expected in the `X-API-Key` header of every `/api` call. **Required outside Development.** |
| `BaseUrl` | `CURTO_BASE_URL` | empty | absolute `http(s)` origin, no path, query or credentials | Public origin used to build short URLs, for example `https://s.example.com`. Empty derives it from the request's `Host` header, so set it on any public deployment. |
| `DbPath` | `CURTO_DB_PATH` | `data/curto.db` | a file path | SQLite database. Relative paths resolve against the content root. The directory is created if missing and must be writable (WAL mode also writes `-wal` and `-shm` files next to it). |
| `BlockPrivateTargets` | `CURTO_BLOCK_PRIVATE_TARGETS` | `false` | `true` / `false` | Reject target URLs that point at loopback, private, link-local addresses or local-only names (`localhost`, `*.local`, `*.internal`, dotless hosts). Literal addresses and names only: DNS is never resolved. |
| `RetentionDays` | `CURTO_RETENTION_DAYS` | `180` | 1-3650 | Click rows older than this are deleted shortly after start-up and every night at 03:00 UTC. Lifetime counters on links stay. |
| `RateLimits:RedirectPerMinute` | `CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE` | `120` | 1-1 000 000 | Redirects and QR codes, per client address, fixed one-minute window. |
| `RateLimits:CreatePerMinute` | `CURTO_RATE_LIMIT_CREATE_PER_MINUTE` | `20` | 1-1 000 000 | `POST /api/links`, per client address. |
| `RateLimits:ApiPerMinute` | `CURTO_RATE_LIMIT_API_PER_MINUTE` | `300` | 1-1 000 000 | Every other `/api` call, including the live-stream handshake. |
| `MaxRequestBodyBytes` | (generic form only) | `16384` | 1 024-1 048 576 | Largest accepted request body. Larger bodies get `413`. |
| `SseHeartbeatSeconds` | (generic form only) | `15` | 1-300 | Interval of the `: heartbeat` comment on the live stream, which keeps idle connections open through proxies. |
| `SseMaxSubscribers` | (generic form only) | `100` | 1-10 000 | Simultaneous live-stream connections. More get `503` with `Retry-After`. |
| `Clicks:QueueCapacity` | (generic form only) | `10000` | 16-1 000 000 | Click events buffered in memory between the redirect and the database writer. When full, the oldest event is dropped and counted. |
| `Clicks:BatchSize` | (generic form only) | `500` | 1-10 000 | Most events written in one transaction. |
| `Clicks:FlushIntervalMs` | (generic form only) | `250` | 1-10 000 | Longest time an event waits for company before its batch is written. |

The generic form of a nested option uses double underscores, for example
`Curto__Clicks__BatchSize=1000` or `Curto__RateLimits__CreatePerMinute=60`.

## The API key

- Outside the `Development` environment the process refuses to start without a key.
- In `Development`, if no key is configured, curto generates one (`dev_` plus 32 random characters) and prints it
  **once** on the console. It is written to the console only, never to the logging pipeline.
- Generate a strong key with `openssl rand -base64 32`, or in PowerShell:

  ```powershell
  $bytes = New-Object byte[] 32
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  [Convert]::ToBase64String($bytes)
  ```

- The key is compared in constant time: both sides are hashed with SHA-256 first, so the comparison leaks
  neither the content nor the length. It is accepted from the `X-API-Key` header only, never from a query string,
  because URLs end up in logs and referrers.

## ASP.NET Core and runtime settings

These are standard .NET variables, not curto options.

| Variable | Typical value | Notes |
|---|---|---|
| `ASPNETCORE_URLS` | `http://127.0.0.1:5000` | Where Kestrel listens. Bind to loopback unless a reverse proxy or the container network needs more. |
| `ASPNETCORE_HTTP_PORTS` | `8080` | The container image listens on 8080 by default; `--healthcheck` reads this variable. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | The default. `Development` lets curto generate an API key when none is configured. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` | Only behind a reverse proxy you control. See [reverse-proxy.md](reverse-proxy.md). |
| `AllowedHosts` | `s.example.com` | Standard host filtering. The shipped default is `*`; restrict it on a public deployment. |
| `Logging__LogLevel__Default` | `Information` | Standard logging configuration. |

## Health probe flag

```text
Curto.Api --healthcheck
```

Runs no web host. It asks the locally running server for `/healthz` (the port comes from `ASPNETCORE_URLS` or
`ASPNETCORE_HTTP_PORTS`, default 8080) and exits `0` on a 2xx answer, `1` otherwise. The container image has
neither `curl` nor `wget`, so its `HEALTHCHECK` uses this flag.

## Start-up validation

Without a key in the default `Production` environment the process exits with a non-zero code:

```text
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException: Curto:ApiKey (CURTO_API_KEY) is required unless ASPNETCORE_ENVIRONMENT is Development. Generate one with: openssl rand -base64 32
```

With several bad values, every problem is listed in the same message, separated by semicolons:

```text
OptionsValidationException: Curto:ApiKey (CURTO_API_KEY) must be 16-256 printable ASCII characters without spaces.; Curto:BaseUrl (CURTO_BASE_URL) must be an absolute http(s) origin such as https://s.example.com (no path, query or credentials).; Curto:RetentionDays (CURTO_RETENTION_DAYS) must be between 1 and 3650, but is 0.
```

(The runtime also prints a stack trace after the first line. The first line is the one to read.)
