# AGENTS.md

Libreta keeps spreadsheets as plain text: a local web app that renders tables from plain-text books under `books/`.

## Running it

Libreta needs the .NET 10 SDK.
On WSL, use a Linux SDK inside the distribution, never `dotnet.exe`: it builds on the Windows side, where file locks, processes and ports are out of WSL's reach.

```bash
dotnet watch --project src/Libreta run -- books/spacex
```

Use `dotnet watch`; a plain `dotnet run` never reloads compiled C#, which reads as "my change did nothing".

Before killing processes or deleting `bin/`, check for an instance the user is already running with `pgrep -af Libreta`, and match `pkill` patterns tightly enough to spare it.

Start a server only when the user asks to review something, since starting one opens a tab in their browser.
Launching a book that is already served reuses that server, and `--url` prints its address without opening anything.

[docs/running.md](docs/running.md) covers book discovery, ports, reloading and the headless commands.

## Vocabulary

Use these five terms precisely.

- **book** — a folder under `books/` holding `book.yaml`: historical statements, a projection, a comps set, anything built from figures over periods, and not necessarily a model.
- **view** — a folder below a book containing `view.yaml`, rendered as one table, and `View` in the C#.
- **line** — a named row of values across periods, either a fact or a formula, written `line:` in `view.yaml`.
- **fact** — an entered value rather than a calculated one, usually a line the source printed, extracted into `facts/`, authoritative and never restated anywhere else.
  A cell override that hardcodes a value is also a fact, one with no source behind it.
- **formula** — a line calculated from other lines, defined in `formulas.yaml`.

## Layout

A view folder holds `facts/` (what the source printed), `formulas.yaml` (everything derived), `checks.yaml` (assertions against figures the source prints), and `view.yaml` (presentation only).

[docs/format.md](docs/format.md) is the full reference, including discovery and child books.
Read it before adding or restructuring book files.

## The sign invariant

`printed: negated` describes **the source**; `sign: contra` describes **the line**.
**Facts, formulas and checks all work in natural direction**; only the rendered cell is negated.
Getting this wrong corrupts data, not just display; see [docs/format.md](docs/format.md#the-two-sign-conventions).

## Code

The C# lives in `src/Libreta`; `Api.cs` holds the JSON payloads the page consumes, so change it and `app.js` together.

The interface is `src/Libreta/wwwroot`: `index.html`, `app.js`, `app.css`.
Vanilla JavaScript loaded by one `<script>` tag — no modules, no bundler, no npm, and nothing to build.
Keep it that way; a build step would break the hot reload, which depends on the running app serving these files straight from source.

Tests live in `src/Libreta.Tests`: `dotnet test src/Libreta.Tests` for the C#, and `node --test src/Libreta.Tests/*.js` for `app.js`.
They do not cover the books themselves; verify a book with `--check`.
Take a browser screenshot when rendering is what needs verifying, such as layout or a source-image overlay; skip it for changes the tests or `--check` already cover.
The user reviews the interface either way.

## Conventions

A view names what a row is, such as `style: subtotal`; the interface decides how that looks.

The engine knows no accounting: a request to "support" EBITDA or free cash flow is a formula in a book file, not a branch in `Engine.cs`.

## Style

- Two blank lines before the closing brace of a class with behavior; none for data-only types.
- Markdown gets one sentence per line.
- Comments earn their place or do not appear.
  Write for someone reading the file cold, never narrating the edit — git already records what changed.
