# Privacy

curto counts clicks without keeping anything that identifies a person. This page says exactly what is stored,
what is computed and thrown away, and where the limits of that promise are.

## What is stored

**Per link** (`links` table): the slug, the normalised target URL, an optional title, tags, the creation time,
an optional expiry, an optional click budget and a lifetime click counter.

**Per click** (`clicks` table), one row each:

| Column | Content | Example |
|---|---|---|
| `slug` | the link that was followed | `launch` |
| `at` | time of the redirect, whole seconds, UTC | `2026-11-10 14:03:07` |
| `visitor` | 64-bit daily-salted hash, explained below | `-3961047561123840213` |
| `referrer` | host of the `Referer` header, or `NULL` for direct traffic | `news.ycombinator.com` |
| `browser` | browser family | `Firefox` |
| `os` | operating-system family | `Linux` |
| `device` | `desktop`, `mobile`, `tablet`, `bot` or `unknown` | `desktop` |

## What is never stored

- **The IP address.** It is read in memory while the redirect is processed, mixed into the hash below, and dropped.
- **The raw User-Agent string.** It is reduced to the three family labels above and dropped.
- **The full referrer URL.** Only the lower-case host survives, without a leading `www.`. Path, query string,
  fragment, port and credentials are discarded, and a `Referer` that is not an http(s) URL counts as direct.
- **Cookies, local storage, fingerprints, languages, screen sizes, geolocation.** curto sets no cookies at all.

curto's own log messages contain no client address and no User-Agent either, and with the shipped logging
levels no access log is written.

## How visitors are told apart

```text
visitor = first 64 bits of HMAC-SHA256(dailySalt, ip + NUL + userAgent)
```

- `dailySalt` is 32 random bytes. It lives **in memory only** and is replaced with a fresh random value at the
  first request of every UTC day. It is never written to disk and cannot be recovered after a restart.
- Because the salt changes daily, the same person gets a different identifier tomorrow. Identifiers **cannot be
  linked across days**, and nobody (including the operator) can recompute one from an IP address afterwards.
- The trade-off is deliberate: "unique visitors" means *unique per day*. A returning visitor counts once per day
  they visit, and a restart in the middle of a day starts a new salt, so someone who clicks before and after the
  restart can be counted twice that day.
- Truncation to 64 bits keeps the stored value short. Two different people colliding is possible in principle and
  negligible at the volumes a self-hosted shortener sees.

The same IP address is also used for rate limiting. That bookkeeping lives in the rate limiter's memory and is never
persisted or logged (IPv6 clients are grouped by /64).

## What leaves the server

- The **live stream** (`GET /api/events`) carries `slug`, `at`, `referrer`, `browser`, `os` and `device` per click.
  It does not carry the visitor hash, and there is no field that could hold an address.
- The **dashboard and the API reference** make no third-party requests: no CDN, no web fonts, no analytics, no
  telemetry. The Scalar API reference is configured with telemetry and its agent disabled. QR codes are rendered
  locally.
- **Visitors** only ever see a `302` with `Cache-Control: no-store`. There is no interstitial and no tracking pixel.

## Retention and deletion

- Click rows older than `RetentionDays` (default **180**, range 1-3650) are deleted shortly after start-up and
  then every night at 03:00 UTC. The lifetime counter on a link is not decremented, so totals survive pruning.
- `DELETE /api/links/{slug}` removes the link **and all of its click rows** (`ON DELETE CASCADE`).
- Everything lives in one SQLite file, `CURTO_DB_PATH`. Deleting the file deletes all data.

## Limits of the promise

- Bots are counted like anyone else and flagged `device = bot`; crawlers that send no User-Agent show up as
  `unknown`. `HEAD` requests (link checkers) are not recorded.
- The database does hold link targets, titles and tags in clear text, plus the pseudonymous daily identifiers.
  Treat the file, its `-wal` companion and any backup like any other application data: restrict file permissions
  and encrypt the volume if the targets are sensitive.
- curto does not read `Do Not Track` or `Global Privacy Control` headers; it has nothing per-person to switch off.
- Whether pseudonymous, daily-rotating identifiers count as personal data depends on your jurisdiction and your
  use. This document describes the implementation; it is not legal advice.
