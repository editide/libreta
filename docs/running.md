# Running Libreta

Libreta provides a local review server for humans and headless commands for agents and scripts.

Book commands accept a book root—the folder containing `book.yaml`—before any options.
`--help` and `--docs` work without a book.

## Read the documentation from the command line

Read the introduction or either complete guide from any folder:

```bash
libreta --docs
libreta --docs format
libreta --docs running
```

## Preview a book

Pass the book folder with no headless option:

```bash
libreta books/spacex
```

The command opens the book in a browser tab, normally at `http://localhost:5173`.
Relative book paths are resolved from the working directory; absolute paths work from anywhere.

Running the command again for a book that is already being served opens the existing server's page rather than starting a second server.
Print that server's address without opening anything:

```bash
libreta books/spacex --url
```

Open the page at one cell, selected with the inspector showing it, by the line's qualified name and the column:

```bash
libreta books/spacex --cell historical.segments.connectivity_revenue 2025
```

The page shows the first view in navigation order that presents the cell, or failing that the line; a tool linking to the book, such as a slide deck tracing a number, runs this.
A line no view presents opens on a page of its own, above the lines its formula reads, as does one listed under **Used by** in the inspector.
When a server already runs the book with a page open, that page selects the cell instead of a new tab opening.
Browsers keep a page from bringing its own tab forward, so a page in a background tab selects the cell there.

A tool that can bring a tab forward, such as a page whose click opened the book's tab, opens the page itself:

```bash
libreta books/spacex --cell historical.segments.connectivity_revenue 2025 --no-open --json
```

`--no-open` opens no tab, starting a server if none runs, and `--json` prints one line with the page's address at the cell and how many open pages selected it:

```json
{"url":"http://localhost:5173/?view=historical%2Fsegments&line=historical.segments.connectivity_revenue&column=2025","pages":1}
```

A server started with `--no-open` stops ten seconds later if no page has connected.
A served page in a frame, such as a book a deck slides in over its slides, selects the cell too, without taking focus from the page around it.

The server stops about ten seconds after its last tab closes, long enough for a reload to reconnect.
It keeps running until a first tab connects, and `Ctrl+C` stops it at any time.
The command always prints the address as well, for sessions with no browser to open, such as over SSH.

## Find the book

Omitting the book root searches the working folder and up to three folders below it.
The search skips `bin`, `obj`, hidden folders, and symbolic links, and stops descending when it finds a `book.yaml`.
Exactly one book must be found; otherwise Libreta reports no books or lists the candidates and asks for an explicit path.

## Watch for changes

Editing a book file while the server runs recalculates its views in place without restarting the command or reloading the page.
A book that temporarily fails to load reports the error in the page rather than stopping the server.

## Run more than one book

Without `--port`, each server starts at port 5173 and probes upward until it finds a free port:

```bash
libreta books/spacex
libreta books/acme
```

Use `--port` when a stable port matters:

```bash
libreta books/spacex --port 5174
```

If that explicit port is occupied, Libreta reports the conflict and exits instead of silently choosing another one.
It also exits if the book is already served on a different port.

## Read a book without a server

List view ids in navigation order:

```bash
libreta books/spacex --list
```

Print one complete view as a Markdown table:

```bash
libreta books/spacex --view dcf/terminal-value
```

Print the engine's exact, unrounded value for one presented cell:

```bash
libreta books/spacex --value dcf value_per_share Value
```

Print several lines by qualified name, each over every period its folder has and then every column formula that applies to it, whether or not a view presents it:

```bash
libreta books/spacex --lines historical.segments.connectivity_revenue historical.segments.connectivity_share_of_revenue
```

Leave the names out to print every line in the book, in order of qualified name.

Add `--json` to any headless command for structured output:

```bash
libreta books/spacex --list --json
libreta books/spacex --view dcf/terminal-value --json
libreta books/spacex --value dcf value_per_share Value --json
libreta books/spacex --lines historical.segments.connectivity_revenue --json
```

The view form returns the table's columns, rows, exact and displayed values, checks and sensitivities; formula graphs and sources are left out, so use `--value` for those.
A row whose cells aren't its line in its columns' periods, as in a comparison or a transposed view, names each cell's line and column in `cells`, which `--cell` opens.
The value form includes the resolved line and column, exact and displayed values, units, formula, note and source, the calculation written out with displayed values, the cells it reads as `inputs`, and the cells that read it as `dependents`.
An input this view does not show carries its own formula, calculation and inputs; one that a `prior()` reaches past the first period carries `missing` instead of a column.
`--value` addresses the underlying line and column even when the view is transposed, and fails if the cell is unresolved or is not presented by that view.
With `--json`, an unresolved cell still prints before the nonzero exit, so its inputs show what is missing.
The lines form loads the book once for all the names, so a tool quoting many figures pays one start rather than one per figure, and one that doesn't know which it needs yet reads every line.
It returns each line's label, kind, units, columns, and exact and displayed values in the order asked, formatted by the `formats.yaml` nearest its folder.
Values keep their natural direction, and a line views show negated carries `contra`.
A cell with no value is marked in `unresolved` without failing; an unknown name carries an `error` in its place, and the command exits nonzero once every line has printed.

These commands write results to standard output, report errors to standard error, and return a nonzero exit code on failure.
Use one headless command per invocation; `--port` applies only to the server.

## Verify a book

Run every check and validate every presented formula graph:

```bash
libreta books/spacex --check
libreta books/spacex --check --json
```

The report lists results by view and ends with totals for passed checks, failed checks, view errors, and missing lines.
Failed checks include their per-period deltas.

A presented row that resolves to neither a fact nor a formula is reported as a missing line.
A formula whose every reference names its column, in a folder of several columns, is reported as a repeated line: it shows the same value in each, and belongs in a [folder of one column](format.md#single-values).
The command exits 1 if a check fails, a view fails to load, a presented calculation is invalid, or a line is missing; a repeated line is only reported.

## Share a book as one page

Write the whole book to a single HTML file that opens in a browser with no server:

```bash
libreta books/spacex --export spacex.html
```

The page carries every view, its calculations and provenance, and the source images, so it works offline and attached to an email.
It is a snapshot: it never reloads, and the status bar shows the date it was exported.
The export refuses a book with a view that fails to load, rather than sharing a broken page.

A link opens the page at a cell in its fragment, as `spacex.html#line=historical.segments.connectivity_revenue&column=2025`, choosing the view as `--cell` does, a line no view presents included.
Changing the fragment of a page already open selects the new cell, so a page embedding the export in a frame can follow each link without reloading the book.
In a frame, the page, exported or served, starts with its navigation hidden, without changing the choice a page outside one remembers, and Escape tells the page around it, as the message `{ libreta: 'escape' }`, unless a dialog or menu is open, so that page can close the book.

The export icon at the foot of the navigation downloads this page or the [workbook](#export-a-workbook) from the running server, built from the book as it stands on disk.
An exported page has no Export button, since it has no server to build one.

## Export a workbook

Write the whole book to an Excel workbook, one sheet per view in navigation order:

```bash
libreta books/spacex --export spacex.xlsx
```

Each sheet is laid out like the page, and each cell holds the value the page shows, contra lines included.
A cell's units become an Excel number format, so the cell keeps its exact value and only the display is scaled and rounded.
Facts are values and formulas stay formulas, reading the cells they depend on wherever the workbook shows them, so changing an input in Excel flows through.
A formula that reads a contra cell reads it negated, as shown, and folds the sign into its arithmetic.
Lines a formula reads that no view shows, such as intermediate steps, go on a final Supporting sheet.
Unresolved cells are left empty.
Rows keep their roles: subtotals and totals are bold and ruled, and supplemental lines are italic.
Numbers are colored as modelers code them: blue for hardcodes, green for links that only read a cell on another sheet, and black for calculations.
The labels and column labels stay frozen in view as the figures scroll, and gridlines are hidden, as on the page.
Sources and notes become Excel notes, a line's on its label and a column's on its column label, while a cell carries only what is its own; source images are left out.
The file extension picks the format, so `--export` accepts only `.html` and `.xlsx`.
