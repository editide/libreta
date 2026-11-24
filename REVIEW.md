# Codebase review, 2026-11-10

A review of Libreta at commit `b70465e`, written as a handoff: each item says what is wrong, where, what to do, and how sure the reviewer is.
Nothing in the repository was changed by the review.
Line numbers are as of `b70465e` and will drift; search for the named symbol if a line no longer matches.

Read `AGENTS.md` first.
Its constraints hold for every item here, in particular: no build step for the interface, and the engine knows no accounting.

## Baseline

- `dotnet test src/Libreta.Tests`: 397 passed.
- `node --test src/Libreta.Tests/*.js`: 61 passed.
- All C# under `src/Libreta` was read in full by the reviewer.
- `app.js`, `app.css` and `index.html` were read in full by a second reviewer; its findings marked "verified" below were re-checked against the source, the rest were not.

Confidence labels used below:

- **Verified** — confirmed by reading the code, by grep, or by measurement.
- **Suspected** — reasoned from the code but not reproduced.

## Suggested order

1. ~~Escape text cells in the page (bug F1, two lines).~~ Done (`dcbd7ec`).
2. ~~Skip text cells in the range summary (bug F2, one line).~~ Done (`dcbd7ec`).
3. ~~One engine per book, and view payloads built on request (S1, S2).~~ Done (`fa3af85`, `e7a47aa`).
4. ~~Split `render()` so selection does not rebuild the table (F4, F5).~~ Done.
5. ~~Move types into files named for them (S5).~~ Done.
6. The removals and small duplications, in any order.

## How the server was measured

A scratch console project outside the repository referenced `src/Libreta/Libreta.csproj`, was built in Release with `--artifacts-path` pointing at a temporary folder so the repository's `bin/` and `obj/` were untouched, and ran against `books/spacex`.
It warmed up once, then averaged five rounds of: `Book.Load(root)`, `CatalogLoader.Load(book, 1)`, `PayloadBuilder.Build(view, new Engine(view), 1)` for every view, and `JsonSerializer.Serialize` of every payload.
Finer breakdowns came from a copy of the source with stopwatches wrapped around individual methods.
Run it on an idle machine: the first run, made while another process was busy, read four times too high.

The SpaceX book has 15 views, 27 fact tables, 1,784 table cells and 1,432 distinct presented coordinates.

| Step | Time |
| --- | --- |
| `Book.Load` | ~210 ms |
| Build all 15 view payloads | ~410 ms |
| Serialize all payloads (4.2 MB of JSON) | ~65 ms |
| Evaluate every presented cell once, in one engine | ~6 ms |
| Evaluate each view's own cells, one engine per view | ~14 ms |

Inside the 410 ms of payload building:

| Part | Time |
| --- | --- |
| `Engine.Dependents` over the whole book, once per view (15 times) | ~155 ms |
| `BuildSensitivities` (one view, two grids, 56 trial engines) | ~190 ms |
| Per-cell calculation, formula and dependent detail | ~50 ms |

Inside the 210 ms of `Book.Load`:

| Part | Time |
| --- | --- |
| YAML read and deserialize, 98 file loads | ~150 ms |
| of which `Formatter.Load`, 42 calls re-reading the same chain | ~50 ms |
| CSV fact tables | ~40 ms (overlaps the above) |
| `IndexPresentations` | ~20 ms |

Evaluating the numbers is about 1% of a reload.
The rest is repetition and inspector detail.

## Server

### S1. One engine per book, not per view (verified, measured; main win done in `fa3af85`)

`PayloadBuilder.Build` calls `engine.Dependents(view.Book.PresentedCoordinates)` (`Api.cs:183`), and every caller passes a fresh `new Engine(view)`.
So each of the 15 views walks, and evaluates, the entire book's dependency graph (`Engine.cs:176`).
That is ~155 ms of the 410 ms, and it grows with views × book size.

The engine's memo is already keyed by canonical `(line, column)`, and cell values do not depend on the view.
The view is used only for:

- `Cell(string name, string column)`, which resolves a short name through `_view.Scope` (`Engine.cs:42`).
- `RequireUnits` and `ValidateCell`, which read `_view.Formatter` (`Engine.cs:804`, `Engine.cs:224`).
- `RunChecks`, which defaults to `_view.Checks` (`Engine.cs:252`).
- `cell.Scope ?? _view.Scope` fallbacks, which never fire for a cell with an expression, since every such `ResolvedCell` sets `Scope`.

Fix: split a book-scoped evaluator (memo, `Evaluate`, `Dependents`, `References`) from the view-scoped concerns, and share one evaluator and one dependents graph across all views of a rebuild.
Sensitivity trials still need their own engine, since substitutions change values.

Caveat: `RequireUnits` throws inside `CellAt`, before memoizing, and its answer depends on the view's `formats.yaml` chain.
A shared memo must not cache a per-view failure, so move that check to where a view reads the cell.

### S2. Build view payloads on request (done in `e7a47aa`)

`BookStore.Rebuild` builds every view's payload on every file change (`Program.cs:783`), though the page shows one view.
The `dcf` view's two sensitivity grids alone cost ~190 ms on every save, whichever view is open.

Fix: keep the loaded `Book` in the snapshot, build a payload when `/api/view` asks for it, and cache it per version.
`TryGetView` already does this for `line:` views (`Program.cs:536`).

Trade-off: today the console prints an error line per failing view on each reload (`Program.cs:794`).
Lazy building loses that for views nobody has open, unless a cheap validation pass is kept.

Also: `/api/view` serializes the payload on each request (`Program.cs:261`); cache the JSON string with the payload.

### S3. Cache formats per folder within a load (done in `fa3af85` and `933dc11`)

`Formatter.Load` runs for every CSV (`View.cs:757`) and every view (`View.cs:1164`, also `View.cs:1185` and `View.cs:1214`), 42 times on SpaceX.
Each call re-parses the embedded built-in `formats.yaml` and every `formats.yaml` up to the inheritance root, and re-walks to find `.git`.
Cache the resulting `Formatter` by folder on the `Book` being loaded.
About 50 ms of the 210 ms load.

`YamlFile.Deserializer()` is also rebuilt at each of those call sites; build it once per load.

### S4. Name resolution at evaluation time (verified, measured; low priority)

`Book.Lookup` ran 711,000 times per measured round.
Each call normalizes the name with two `Replace` calls, allocates a qualified string per enclosing scope, and calls `DefinitionForScope`, which filters and sorts the book definitions with LINQ (`View.cs:436`, `View.cs:1087`).

Memoizing `DefinitionForScope` and `Lookup` after load was tried on a copy: payload building dropped between 0% and 25% across runs, within noise.
Not worth doing alone.
S1 removes most of the calls anyway.
If it is done, the memo is valid only once loading has finished, since the dictionaries it consults are still filling during `Book.Load`.

### S5. Files do not hold what their names say (done)

These are pure moves with no behavior change.

- `View.cs` (1,259 lines) is `Fact`, `FactCell`, `Formula`, `ColumnFormula`, `CellOverride`, `Check`, `Sensitivity`, then 1,000 lines of `Book`, then 130 lines of `View`.
- `Program.cs` (810 lines) holds `BookStore` (320 lines) below the top-level statements.
- `CommandLine.cs` (976 lines) holds payload records, two enums, `CommandLineOptions`, `BookDiscovery`, `RequestedCell`, `CellLink`, `HeadlessRunner`, `ViewTablePayloadBuilder` and `ValuePayloadBuilder`.
- `Api.cs` (1,125 lines) is 175 lines of payload records and a 950-line `PayloadBuilder`, while `AGENTS.md` describes it as "the JSON payloads the page consumes".
- Two unrelated types are named `CellLink`: a public static class (`CommandLine.cs:427`) and a private record struct nested in `PayloadBuilder` (`Api.cs:1123`). Rename the private one.

Suggested split: `Book.cs`, `Model.cs` (the domain classes), `View.cs`, `BookStore.cs`, `HeadlessRunner.cs`, `Payloads.cs` (every payload record, page and command line), `PayloadBuilder.cs`.
Update the `Api.cs` sentence in `AGENTS.md` to match.

### S6. Headless commands are dispatched in two places (done)

`Program.cs:100`–`179` handles `Export`, `SetRegions`, `ShowRegions` and then everything else, in four blocks that each repeat the same `try`/`catch` printing `exception.Message` and returning 1.
`HeadlessRunner.Run` throws "not a console command" for the three it is not given (`CommandLine.cs:474`).
Route every `HeadlessCommand` through `HeadlessRunner` behind one `try`/`catch`.

The list of headless commands is also spelled out in four error strings (`CommandLine.cs:264`, `271`, `286`, `356`) and the usage text, so adding a command means editing five strings.

### S7. Table and inspector detail are built together (verified; consider only if books grow)

`PayloadBuilder.Build` produces the table (rows, columns, display, contra) and the inspector detail (formula references, calculation tokens, expansions, dependents) in one pass.
The detail is most of the 4.2 MB: about 2.4 KB per cell, for cells that are mostly never clicked.

Two callers build the full payload and discard the detail:

- `XlsxExport.Layout` (`XlsxExport.cs:362`) reads titles, labels, sources and contra, then re-resolves every cell through the engine.
- `--view --json` reduces it through `ViewTablePayloadBuilder` (`CommandLine.cs:856`).

Splitting the two would let those callers skip the detail and let the page fetch a cell's detail when it is selected.
The HTML export still has to embed everything, so its size would not change.

### S8. Small duplications (done)

- **Scope of a line.** `Formulas.TryGetValue(line) ? formula.Scope : Facts.TryGetValue(line) ? fact.Scope : …` appears in `Book.ResolveCoordinateColumn` (`View.cs:510`), `Book.ColumnsInRange` (`View.cs:547`), `View.ForLine` (`View.cs:1172`), `PayloadBuilder.ScopeOf` (`Api.cs:701`), `XlsxExport.LineScope` (`XlsxExport.cs:558`) and `HeadlessRunner.ReadLine` (`CommandLine.cs:530`). Two of them try the fact first, four the formula first. Add one `Book.ScopeOf(line)`.
- **Path is inside root.** The `relative == ".." || StartsWith("../") || IsPathRooted` test appears at `Program.cs:522`, `View.cs:969`, `View.cs:1105` (`IsWithin`) and `BookStructure.cs:168`. Make `IsWithin` shared.
- **Image extensions.** `.png`, `.jpg`, `.jpeg`, `.webp` are listed in `BookStore.ImageContentType` (`Program.cs:576`), `BookStore.IsBookFile` (`Program.cs:734`) and `Book.ResolveSourceImage` (`View.cs:974`).
- **Reference to coordinate.** `ResolveSelf` followed by `ReferenceColumn` for a `RefExpr` is written out in `Engine.Evaluate`, `AppendReferences`, `AppendActiveDependencies`, `Availability`, `DateKind`, and `PayloadBuilder.AppendCalculation`. `ExcelFormula.Coordinate` (`ExcelFormula.cs:187`) is already the shared form; move it to `Book`.
- **Children of an expression.** `Book.Children` (`View.cs:1074`) and `ExcelFormula.Children` (`ExcelFormula.cs:207`) differ only in whether `PriorExpr` is included. One method on `Expr`.
- **Relaxed JSON options.** Three clones of `PayloadJson.Options` with `UnsafeRelaxedJsonEscaping`: `HeadlessRunner.JsonOptions`, `HeadlessRunner.ViewJsonOptions` (`CommandLine.cs:451`, `457`) and one allocated per call in `ShowCell` (`Program.cs:422`).

The many recursive walks over `Expr` (`Evaluate`, `AppendReferences`, `AppendActiveDependencies`, `Availability`, `DateKind`, `AppendCalculation`, `ExcelFormula.Translate`, `Expr.Pinned`) each mean something different.
Leave them as separate walks.

### S9. `book.yaml` is parsed by hand (verified; optional)

`BookStructure.LoadBookFile`, `ParseNavigation`, `ParseBooks`, `ParseIdentifiers` and `Scalar` (`BookStructure.cs:191`–`333`, `384`) walk a `YamlStream`, about 130 lines, while every other book file goes through `YamlFile.Load`.
The deserializer already rejects unknown properties, since `YamlFile.Configure` never calls `IgnoreUnmatchedProperties`.
Only `navigation` is polymorphic (a view id, a view id mapping to children, or `book: id`), and a converter for `NavigationItem` like `ViewColumnConverter` (`YamlFile.cs:38`) would cover it.

Not checked: which tests assert the hand-written error messages, such as "book.yaml contains unknown property: x".
Check before starting.

### S10. Kinds are strings (verified; optional)

`"fact"`, `"formula"`, `"missing"`, `"blank"`, `"trial"`, `"line"`, `"metric"`, `"column"`, `"book"`, `"scope"`, `"header"` and `"spacer"` pass as string literals between `Table.cs`, `Engine.cs`, `Api.cs`, `XlsxExport.cs` and `app.js`, as do `"additive"`/`"contra"` and `"as_is"`/`"negated"`.
A typo compiles.
Constants, or enums serialized as strings, would let the compiler catch one.
The JSON and the CSS class names must not change, since `app.js` and `app.css` key on them.

### Server bugs and inconsistencies

- **B1. Path case rules disagree on macOS (done in `e7a47aa`).** `BookStructure.PathComparer` (`BookStructure.cs:392`) and `Formatter.PathEquals` (`Formatter.cs:281`) are case-insensitive only on Windows. `InstanceRegistry.PathComparison` (`Instances.cs:69`) is case-insensitive everywhere but Linux. On macOS the registry would treat two spellings of a root as the same book while loading treats them as different. Use one rule.

### Server removals

- ~~`CellDetailPayload.Formulas` is never read; `BuildCell` can return `CellPayload` (`Api.cs:93`, `Api.cs:590`). Verified by grep.~~ Removed.
- ~~`ActiveDependency.Scope` is never read (`Engine.cs:22`). Verified by grep.~~ Removed.
- ~~`Fact.Currency` and `Fact.FiscalYearEnd` (`View.cs:14`) are loaded, inherited from the book, and compared when fragments merge (`View.cs:908`), which can fail a load, but appear in no payload, command output or export.~~ Removed.
- `BookFile.Description` and `BookFile.Identifiers` are parsed and validated but read by no production code. `docs/format.md:85` documents them as descriptive metadata, so leaving them is defensible.
- `Engine.Value` (`Engine.cs:80`) is called only by tests. Harmless.
- ~~`YamlFile.Configure` (`YamlFile.cs:15`) is public but called only by `Deserializer()`.~~ Made private.

## Interface

### Bugs

- **F1. Text cells are injected as unescaped HTML (done in `dcbd7ec`).** `app.js:653` writes `${cell.display}` into `innerHTML`, and `app.js:1823` does the same in the inspector's values table. For a text fact the server returns the CSV cell verbatim (`Api.cs:710`). A text fact containing `<` or `&` renders wrong, and markup in it would execute, including in an exported page someone else opens. Fix: `escapeHtml(cell.display)` in both places. Add a test.
- **F2. Numeric-looking text is summed (done in `dcbd7ec`).** `selectedRangeSummary` (`app.js:1685`) uses `Number(displayedExactValue(cell))`, and a text cell's `exact` is its text, so a text cell reading "2024" is counted and summed. Fix: skip cells where `cell.text` is true.
- **F3. Checks collapse on reload, and may clear a deep-linked selection (done in `c9f88cd`).** Each `<details class="check">` is re-rendered closed on every refresh, so an open failing check collapses each time a file is saved; keep a set of open check names. Separately, at `app.js:861`–`884`: parsing `<details open>` queues an asynchronous `toggle` event, a re-render can replace the element before it fires, and the listener then runs on the detached node, setting `checksExpanded` and calling `clearSelection()`. That would drop the selection from a `?view=…&line=…&column=…` link on a view with a failing check. Reproduce in a browser first; the likely fix is `if (!event.currentTarget.isConnected) return;`.

### Structure and performance

- **F4. Every selection change rebuilds the page (done in `f894c0b`; event delegation optional).** `selectRow`, `selectColumn`, `inspectCell`, range extension and `moveSelection` all call `render()` (`app.js:968`, `974`, `980`, `1149`, `1328`). `render()` (`app.js:599`) rebuilds the head, body, checks, sensitivities and panel, and attaches three listeners per cell (`app.js:615`–`628`, `657`–`676`). One arrow key on a large table re-creates every cell and destroys then restores the focused node. The incremental path already exists where a pointer drag ends (`app.js:1232`): `syncGridSelection(); renderPanel(); highlightTableCalculation()`. Fix: split `renderTable()`, called only when the payload changes, from `updateSelection()`, and delegate pointer, click and key handling to `.book-table` once at boot, reading coordinates from `data-row-index` and `data-column` as `cellAtPoint` (`app.js:1153`) already does. This also removes the double render in F8.
- **F5. Range highlighting is quadratic (done).** `syncGridSelection` calls `gridCellNode` for every selected coordinate (`app.js:1082`), and each call is a fresh `querySelector` from the top of the body (`app.js:1055`), plus a `rowOrder.indexOf`. Selecting the whole table costs cells squared, on every step of a drag. Fix: build a `gridNodes[row][column]` array during render, beside `cellNodes`, and a map of row positions. About 10 lines.
- **F6. View state is five globals with the field list copied in four places (done).** `selection`, `cellRanges`, `sensitivitySelection`, `checksExpanded` and `calculationTrail` are cleared, remembered and restored at `app.js:501`, `564`, `583`, `959` and `774`. `clearViewState` already omits `calculationTrail`. Fix: one `viewState` object, cleared by assigning defaults, remembered with `structuredClone`. Removes about 20 lines. Leave the tooltip, zoom and shortcut variables as they are.
- **F7. `knownView` scans the catalog per row (verified).** `app.js:459`, called at `app.js:635` and twice per `renderPanel`. Keep a `Set` of view ids, rebuilt when the catalog loads.
- **F8. Double render on the first key after Escape (done in `f894c0b`).** At `app.js:1419`–`1431`, with no selection and focus on a label, an arrow key runs `syncSelection()` and then `movePeriod`, each of which renders. Fixed by F4.

### The page re-derives what the server knows

- **F9. Rounding is guessed by re-parsing the display (done in `bff308e`).** `displayMatchesExact` (`app.js:2166`), used through `exactDiffersFromDisplay` (`app.js:1918`), strips commas, parentheses, prefix and suffix from the server's display string to guess whether the value was rounded. It is wrong for any format it does not know, such as dash or zero text. Fix: add `Rounded` to `CellPayload`, delete the parser (about 12 lines) and its tests in `AppExactNumberTests.js`. Change `Api.cs` and `app.js` together.
- **F10. Check equations are relabelled by regex (done in `bff308e`).** `app.js:892`–`904` rebuilds a label map per check and substitutes with `\b[a-zA-Z_]\w*\b`, which splits dotted names, so a qualified reference such as `historical.revenue` would never match its label. The server already builds `FormulaPayload` with labelled references (`Api.cs:761`); send that on `CheckPayload` and reuse `renderFormula`.
- **F11. The contra sign is re-applied in the page (verified; lower priority).** `displayedExactValue` (`app.js:1615`) negates for copy and sum. Sending the exact value in display direction would keep the sign invariant in one place.

### Removals and duplicates

- **F12. Dead code (done).**
  - `td.calculation-result` is added at `app.js:2154` and removed at `app.js:2146`–`2149`. The only rule, `.calculation .calculation-result` (`app.css:1903`), targets the inspector span, not the cell. The `removeAttribute('title')` at `app.js:2148` is a leftover.
  - `#checks.expanded` is toggled at `app.js:853`, `860` and `881`, and no CSS uses it.
  - `class="title-row"` and its inner `<div>` (`index.html:49`–`54`) are unstyled wrappers.
  - The `${entry.kind}` class on `.trend-chart` (`app.js:2305`) is unstyled.
- **F13. Two bar-chart renderers (verified).** `renderSensitivityChart` (`app.js:1549`) and `renderChart` (`app.js:2274`) share `chartDomain` and `chartScale` and differ in the selected index, missing markers and label placement. One function saves about 30 lines and lets `.trend-chart*` and `.sensitivity-chart*` share rules.
- **F14. CSS repeats (done).**
  - Button resets restated over the global `button` rule: `#inspector-back` (`app.css:2502`), `.inspector .dep` (`app.css:2137`), `.source-image` (`app.css:1667`), `#shortcuts-button` (`app.css:2213`). About 12 declarations.
  - `.calc-expand::after` (`app.css:1934`) is identical to `.formula-expand::after` (`app.css:1795`).
  - `.navigation` is declared twice inside the same `@media (max-width: 52rem)` (`app.css:2413`, `app.css:2456`).
  - The resizer's `15px`, `7px` and `-7px` repeat across `app.css:231`–`265`, `1209` and `1217`–`1230`; derive them from one custom property.

Not dead, though no literal reference exists: `.blank`, `.subtotal`, `.total`, `.supplemental`, `.compact` and `.col-*`.
They are built from the cell kind and from the row and column `style` and `space` values (`app.js:636`, `app.js:937`).
No other CSS class is unused, and every custom property is used.

### JS test harness

- **F15. Tests extract functions from `app.js` by regex (done in `9d0d0d7`).** Sixteen files each read `app.js` as text and pull functions out with a pattern like `function name\([\s\S]*?\n\}`, through three different helpers. The pattern depends on the closing brace sitting at column 0 and on the function never becoming a `const` arrow. Collaborators are stubbed by hand and drift: `AppTouchTests.js` stubs 10 names, `AppGridKeyTests.js` 9.

  Fix, with no change to `app.js` and no build step: one shared harness that evaluates the whole of `app.js` once in a `node:vm` context, with a permissive stub for `document`, `matchMedia`, `localStorage` and `history`, and a `fetch` that never resolves. Top-level functions land on the context, so a test overrides a collaborator by assignment, and sets `let` state with `vm.runInContext('payload = …', context)`. About 40 lines of harness replace about 100 lines of boilerplate and stubs.

  Low-value tests to drop or rewrite: the second test in `AppFocusTests.js` copies the selector into its own stub, `AppPhoneInspectorTests.js` tests `phone && !shown`, and `AppTableStyleTests.js` matches CSS text by regex.

## Leave alone

These are small, correct and well-tested; changing them would be churn.

- The expression parser (`Expression.cs`).
- `ExcelFormula`'s sign folding and `ExcelNumberFormat`.
- `GitFiles` and the `IBookFiles` seam.
- `PictureCache`.
- `InstanceRegistry`, `Viewers` and `Pages`.
- `SourceRegionWriter`'s in-place YAML editing.
- `RequestCoordinator` in `app.js`.
- Theming through `light-dark()` and `color-mix`.
- `createSize` and `wireResizer`.
- Document-level tooltip delegation.
- The history-state design.
- The snapshot and live dual path in the fetch helpers.
- The event stream polling every 200 ms (`Program.cs:350`): fine for one local user.
- CSV splitting on commas with no quoting: a documented limit (`docs/format.md:165`, `docs/format.md:449`).
