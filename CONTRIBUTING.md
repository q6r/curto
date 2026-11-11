# Contributing to curto

Thanks for taking a look. The project is small on purpose, so changes that keep it small are the easiest to merge.

## Setup

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (`global.json` pins the 9.0 feature
band) and a terminal. Nothing else: the dashboard has no build step and no Node toolchain.

```bash
git clone https://github.com/q6r/curto.git
cd curto
dotnet restore
```

## Run it

```bash
# Bash / zsh
CURTO_API_KEY="local-development-key-123" ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --project src/Curto.Api
```

```powershell
# PowerShell
$env:CURTO_API_KEY = "local-development-key-123"; $env:ASPNETCORE_URLS = "http://127.0.0.1:5080"
dotnet run --project src/Curto.Api
```

Open `http://127.0.0.1:5080/` for the dashboard (it asks for the key) and `/docs` for the API reference. The
[`examples/`](examples) folder has scripts that create demo links and clicks, and a tool that backdates a demo
database so the charts have a history.

## Build, test, lint

```bash
dotnet build Curto.slnx -c Release -warnaserror    # every warning is an error, analyzers at the recommended level
dotnet test  Curto.slnx -c Release                 # about 680 tests, offline, a few seconds
dotnet format Curto.slnx --verify-no-changes       # formatting follows .editorconfig
```

CI runs the build and the tests on Linux, Windows and macOS and the format check on Linux. A separate job checks the
dashboard assets (`node --check` on the scripts and the static-layer tests), and a third builds the Docker image and
probes it.

## Guidelines

- **Tests come with behaviour.** New or changed behaviour needs a test, including the unhappy paths (malformed
  input, limits, hostile values). Tests must be deterministic: inject the clock (`FakeTimeProvider`) instead of
  sleeping or reading the real time, and never touch the network.
- **Privacy is a feature.** Nothing that identifies a person may be stored, logged or sent to the live stream.
  If a change needs a new field on a click, say why in the pull request. See [docs/privacy.md](docs/privacy.md).
- **Security by default.** Validate input, bound every resource, escape output, compare secrets in constant time.
- **Dashboard.** It must keep working under the strict CSP (no inline script, no inline style, no `style=""`
  attribute, no external origin). `aurora.css`, `aurora.js` and `aurora-charts.js` in `wwwroot` are the project's
  own bundled UI kit: small, dependency-free and part of this repository. Project-specific styling and behaviour
  belong in `app.css` and `app.js`, using the kit's tokens instead of raw colours; `index.html` stays almost empty
  because `Aurora.shell()` builds the page. The kit files may be edited with care, as long as the same rules hold:
  no `innerHTML`, no inline style or script, tokens only, no external requests. `DashboardAssetTests` enforces them.
- **Small, single-purpose pull requests** read faster than big ones. Update `README.md`, `README.pt-BR.md` and
  `CHANGELOG.md` when behaviour changes.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `test:`, `refactor:`,
`perf:`, `build:`, `ci:`, `chore:`. Use the imperative mood and keep the first line under about 72 characters, for
example `fix: reject slugs that collide with static routes`.

## Reporting problems

Bugs and ideas go to the issue templates. Security problems do **not** belong in a public issue: see
[SECURITY.md](SECURITY.md).
