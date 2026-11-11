# API quick tour

The complete, always up-to-date reference is generated from the code: open `/docs` on a running server (the Scalar
API reference) or fetch `/openapi/v1.json`. This page is the short version, with the conventions that apply
everywhere and real responses. The examples use `$BASE` for the server origin and `$KEY` for the API key; the guided
scripts in [`examples/`](../examples) run all of them.

## Conventions

- **Auth.** Everything under `/api` needs the key in an `X-API-Key` header. The redirect, QR codes, `/healthz`,
  `/readyz`, `/docs` and the dashboard are public. A missing or wrong key is a `401` with
  `WWW-Authenticate: ApiKey realm="curto"`.
- **JSON.** Request and response bodies are JSON with camelCase names; send `Content-Type: application/json` (a
  merge patch may also use `application/merge-patch+json`). Unknown properties in a request are rejected. Enums are
  lower-case strings (`"status": "active"`).
- **Time.** Every timestamp is ISO 8601 in UTC with a `Z` suffix. Timestamps you send must carry an explicit offset
  (`2030-01-31T12:00:00Z`) and are stored with one-second precision.
- **Errors.** Every error is an `application/problem+json` document (RFC 9457) with `type`, `title`, `status`,
  `detail`, `instance`, `traceId` and `correlationId`. Validation errors add `errors`, keyed by field name. The same
  `correlationId` is returned in the `X-Correlation-Id` header, and you may supply your own (letters, digits, `.`,
  `_`, `-`, up to 64 characters).
- **Caching.** API responses and redirects are `Cache-Control: no-store`.

## Links

| Call | Body or query | Success |
|---|---|---|
| `POST /api/links` | `url` (required), `slug`, `title`, `tags`, `expiresAt`, `maxClicks` | `201` with the link and `Location: /api/links/{slug}` |
| `GET /api/links` | `query`, `tag`, `status`, `sort`, `dir`, `limit`, `offset` | `200` `{ "items": [...], "total": n }` |
| `GET /api/links/{slug}` | | `200` |
| `PATCH /api/links/{slug}` | any of `url`, `title`, `tags`, `expiresAt`, `maxClicks` | `200` with the updated link |
| `DELETE /api/links/{slug}` | | `204`; the link and its click rows are gone |

```json
{"slug":"tour","shortUrl":"http://127.0.0.1:5080/tour","url":"https://example.com/blog/hello-world","title":"Guided tour","tags":["blog","demo"],"createdAt":"2026-11-11T01:14:15Z","expiresAt":null,"maxClicks":null,"clicks":0,"status":"active"}
```

- **Slugs** are 3-32 characters from `A-Z a-z 0-9 _ -`, case-sensitive. Leave `slug` out for a random 7-character one.
  Words that collide with routes (`api`, `docs`, `qr`, `healthz`, ...) are reserved, and a taken slug is a `409`
  with `"code": "slug_taken"`.
- **Targets** must be absolute `http` or `https` URLs of at most 2048 characters, without credentials. They are
  normalised to ASCII (the host in punycode, other characters percent-encoded). With
  `CURTO_BLOCK_PRIVATE_TARGETS=true`, loopback, private and local-only hosts are refused.
- **Titles** are at most 200 characters without control characters. **Tags** are lower-cased, de-duplicated, at most
  10, each 1-32 characters from letters, digits and `- _ . :`. `expiresAt` must be in the future. `maxClicks` is at
  least 1.
- **Status** is `active`, `expired` (past `expiresAt`) or `exhausted` (`clicks` reached `maxClicks`). Expiry wins
  when both apply. Both end states answer the redirect with `410`.
- **Lists** match `query` case-insensitively against slug, title and URL (at most 200 characters), `tag` exactly,
  and `status` as above. `sort` is `createdAt` (default) or `clicks`, `dir` is `desc` (default) or `asc`, `limit` is
  1-200 (default 50). `total` ignores paging.

### Merge patch

`PATCH` follows RFC 7396. What you send decides what changes:

| In the body | Effect |
|---|---|
| a field with a value | the field is replaced |
| a field set to `null` | `title`, `tags`, `expiresAt` and `maxClicks` are cleared (`url` cannot be null) |
| a field that is absent | the field stays as it is |
| `slug`, or any unknown field | `400`: the slug cannot be changed |

## Redirect and QR codes

`GET /{slug}` answers `302` with `Location` and `Cache-Control: no-store`, `404` for an unknown slug, and `410` for an
expired or exhausted link (with `"code": "link_expired"` or `"code": "link_exhausted"`). The click is recorded in the
background. `HEAD /{slug}` gives the same answer, records nothing and spends no click budget.

`GET /qr/{slug}.svg` renders the short URL as a QR code. `size` is 64-2048 pixels (default 256), `dark` and `light`
are hex colours such as `1a2b3c`, `#1a2b3c` or `#fff`. It is cached for an hour when `CURTO_BASE_URL` is set and not
cached otherwise, because the encoded URL then depends on the `Host` header.

## Statistics

`GET /api/stats/overview?range=7d` covers all links, `GET /api/links/{slug}/stats?range=7d` one link; both return the
same shape. `range` is `24h` (24 hourly buckets), `7d`, `30d` or `90d` (daily buckets, in UTC). Windows are aligned to
whole buckets and always end with the bucket that contains the current time.

| Member | Meaning |
|---|---|
| `range` | `from` (inclusive), `to` (exclusive) and `bucket` (`hour` or `day`) |
| `links` | `total`, `active`, and `expired` (which includes links that used up their click limit) |
| `clicks`, `visitors` | `inRange` and `previous`: the window asked for and the window of equal length before it |
| `series` | one `{ t, clicks, visitors }` per bucket, zero-filled |
| `topLinks` | up to 10 `{ slug, title, url, clicks }` |
| `referrers`, `devices`, `browsers`, `os` | up to 10 `{ name, clicks }` each; direct traffic is `(direct)` |

`visitors` counts distinct daily identifiers, so a person who returns on another day counts again; see
[privacy.md](privacy.md). The dashboard computes its change indicators from `inRange` against `previous`.

## Live click stream

`GET /api/events` is a Server-Sent Events stream: one `click` event per redirect, as it happens.

```text
retry: 5000
: connected

event: click
data: {"slug":"hello","at":"2026-11-11T01:10:51.629018Z","referrer":"linkedin.com","browser":"Safari","os":"iOS","device":"mobile"}
```

A `: heartbeat` comment arrives every 15 seconds by default. `EventSource` cannot send headers, so read the stream
with `fetch()` and a stream reader (see `app.js`) or `curl -N`. A subscriber that cannot keep up is disconnected, and
at most 100 streams are open at once (`503` with `Retry-After` beyond that).

## Status codes

| Status | When |
|---|---|
| `400` | Validation failed (`errors` names the fields), or the body is not valid JSON for the endpoint |
| `401` | No key, or the wrong one |
| `404` | Unknown slug or route |
| `408` | The request took too long to arrive |
| `409` | The slug is already in use |
| `410` | The link expired or used up its clicks |
| `413` | The request body is larger than `MaxRequestBodyBytes` (16 KiB by default) |
| `415` | The body is not sent as JSON |
| `429` | A rate limit was hit; `Retry-After` says how long to wait |
| `500` | Something unexpected: it is logged on the server and answered without internals |
| `503` | Too many live subscribers, a busy database, or `/readyz` cannot reach the database |

Rate limits are per client address and per minute, with separate budgets for redirects and QR codes (120), link
creation (20) and every other `/api` call (300). See [configuration.md](configuration.md) to change them and
[reverse-proxy.md](reverse-proxy.md) for what the "client address" is behind a proxy.
