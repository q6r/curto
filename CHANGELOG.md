# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Dashboard.** Moved to the Aurora UI kit 2.0, an IDE-style layout in a near-black palette with silver text and one
  indigo accent (Prism Classic; Prism Daylight in the light theme): a navigation rail (Overview, Links, Live and the command palette), a top bar with
  breadcrumbs, search, the live state of the click stream and "New link", a sub bar that holds the range switch and
  counts on Overview and the status filter on Links, flat cards on one ground and a status bar with the version, the
  API reference, readiness and the privacy note. `index.html` is now almost empty: `Aurora.shell()` builds the page.
  The Links view starts with a composer (paste a URL, press Enter for a random slug; "More options" opens the full
  form with the URL carried over), and an empty database opens on the same composer under a first-run headline.
  The status bar is hidden on phones, so the command palette also opens the API reference and the readiness probe.
- **Examples.** The seed scripts create their demo links on varied sub-domains of the reserved example domains.
  Screenshots and banner were regenerated for the new look.

## [0.1.0] - 2026-11-10

First release.

### Added

- **Link API.** Create, list, read, update and delete short links under `/api/links`. Random 7-character base62
  slugs or custom ones (3-32 characters), optional title, tags, expiry and click budget. Updates use JSON merge
  patch (`null` clears a field). Lists filter by text, tag and status, sort by creation time or clicks, and page with
  `limit` and `offset`.
- **Redirect and QR codes.** `GET /{slug}` answers `302` with `Cache-Control: no-store`, `404` for unknown slugs
  and `410` for expired or exhausted links. `HEAD` requests are answered the same way but do not count as visits.
  `GET /qr/{slug}.svg` renders a scalable QR code (`size`, `dark` and `light` parameters).
- **Privacy-first analytics.** Clicks are stored with a daily-salted visitor hash, the referrer host and browser, OS
  and device families. No IP address, no raw User-Agent and no cookie is ever stored. Retention defaults to 180
  days.
- **Statistics.** `/api/stats/overview` and `/api/links/{slug}/stats` for the last 24 hours, 7, 30 or 90 days, with a
  zero-filled series, ranked referrers, browsers, operating systems and devices, and a comparison with the window
  before.
- **Live click stream.** `GET /api/events` pushes one Server-Sent Event per redirect, with heartbeats and
  protection against slow consumers.
- **Dashboard.** A responsive Aurora single-page dashboard served from `wwwroot`: Overview, Links (search, sort,
  paginate, create, edit, delete, a drawer with QR code and per-link charts) and Live. It has dark and light themes,
  a command palette (`Ctrl+K`), an API-key dialog, and works offline under a strict Content Security Policy.
- **Security.** API-key authentication with constant-time comparison, per-client rate limits, strict security
  headers, no CORS, request size limits, URL normalisation and an option to refuse private target addresses.
  Every error is an RFC 9457 problem document.
- **Operations.** `/healthz` and `/readyz` (with click-pipeline counters), an OpenAPI document with the Scalar API
  reference at `/docs`, a `--healthcheck` flag for container probes, and a `Dockerfile` and `docker-compose.yml`
  (non-root, read-only root file system, volume for the SQLite file).
- **Documentation and examples.** Architecture, configuration, privacy, API and reverse-proxy guides; an `.http` file,
  curl tours for Bash and PowerShell, seed scripts that create demo links and simulate varied clicks through the real
  API, and a small tool that backdates a demo database with 90 days of history.
- **Tests.** The static layer of the dashboard is covered: content types, revalidating cache headers, the strict
  policy, no inline code, no external origin, and every API path the dashboard calls exists in the OpenAPI document.

[Unreleased]: https://github.com/q6r/curto/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/q6r/curto/releases/tag/v0.1.0
