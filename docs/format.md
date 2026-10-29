# Book format

A book is a folder of CSV and YAML files: facts record entered or sourced values, formulas calculate with stable financial names, checks reconcile the model, and views select what people see.

See [running.md](running.md) for installation, previews, headless queries, checks, and application development.

## Project structure

Folders are namespaces; Libreta imposes no hierarchy on them.
One project might look like this:

```text
books/
  formats.yaml
  acme/
    book.yaml

    historical/
      income-statement/
        facts/
          consolidated-statements-of-operations.csv
          consolidated-statements-of-operations.yaml
          consolidated-statements-of-operations.webp
        formulas.yaml
        checks.yaml
        view.yaml

      balance-sheet/
        facts/
        formulas.yaml
        checks.yaml
        view.yaml

    dcf/
      facts/
        assumptions.csv
        assumptions.yaml
      formulas.yaml
      view.yaml
```

`facts/` holds entered values, one CSV and sibling YAML per table.
Tables can be organized at any depth beneath it, commonly by source document, without changing their scope or line names.
`formulas.yaml` holds what is calculated from those facts.
`checks.yaml` holds assertions that the extraction agrees with the source.
`view.yaml` holds what to present and in what order.

Every owned folder containing a `view.yaml` is a view, discovered recursively, its id being the path relative to its book root.
A view can contain nested views that reuse its lines; `book.yaml` controls their sidebar placement.
Folders without a `view.yaml` can also hold facts, formulas, checks, and sensitivities for other views to use.
Every CSV beneath a `facts/` folder is loaded, including files inside another directory named `facts`, exactly once.
A nested book remains a book boundary and is not loaded as part of its parent.

## Inherited context

Context that applies to a whole book belongs in its `book.yaml`.

```yaml
# book.yaml
name: Acme Inc.
short_name: Acme
units: millions
currency: USD
fiscal_year_end: December 31

identifiers:
  cik: "1234567"

navigation:
  - historical/income-statement
  - historical/balance-sheet
  - dcf
```

`name`, `short_name`, and a `navigation` list are required.
`name` is the full human-readable identity, while `short_name` is its compact UI label or abbreviation.

`units`, `currency`, and `fiscal_year_end` are optional defaults.
Facts and formulas within the book inherit those defaults unless a closer definition overrides them.
A fact line resolves units from its line item, then its fact-table defaults, then its book.
A fact table resolves currency from its defaults and fiscal year end from its table metadata before falling back to its book.
A formula resolves units from its own definition before falling back to its book.
An included child starts a new inheritance chain from its own `book.yaml`; parent defaults never flow into it.

`identifiers` and `description` are optional descriptive metadata that do not affect calculations.
Identifier values are strings; quote them so leading zeroes survive.

## Book composition

A book can explicitly include child books that remain valid standalone books.
Each inclusion has a stable identifier and a path relative to the including book.

```yaml
name: Semiconductor comparison
short_name: Semis

books:
  - id: nvidia
    path: companies/nvidia
  - id: amd
    path: companies/amd

navigation:
  - overview
  - book: nvidia
  - book: amd
```

An inclusion path must remain inside the including book.
Inclusion identifiers begin with a letter and contain only letters, digits, underscores, or hyphens.
Identifiers that become equal after hyphens are normalized to underscores are rejected.
Formula references write any hyphen in an inclusion identifier as an underscore, consistent with folder and line names.

Including a child loads its facts, formulas, checks, sensitivities, views, and source metadata.
Its view ids use the inclusion identifier as a prefix, such as `nvidia/historical/income-statement`.
The `book:` navigation entry independently places the child's own navigation subtree at that position.
Omitting that entry keeps the child out of the parent's sidebar without removing its data.

The parent ignores a descendant `book.yaml` unless `books` explicitly includes it, so malformed or incomplete excluded books cannot break the parent.
Parent formulas address a direct child through its inclusion identifier and `::`.

```yaml
formulas:
  combined_revenue:
    formula: nvidia::historical.income_statement.revenue + amd::historical.income_statement.revenue
```

The portion after `::` is resolved from the child book's root.
A child uses its ordinary local references internally, so the same formulas produce the same results standalone and when composed.
Nested child books add another `::`-separated inclusion identifier.
`books` is reserved as the inclusion identifier that means every direct child at once.

```yaml
formulas:
  median_operating_margin:
    formula: median(books::historical.income_statement.operating_margin["2025"])
    units: percent
```

It expands to one reference per included book, in inclusion order, so a statistic stays right when the set grows.
Only `sum`, `min`, `max`, `average`, and `median` accept it, because every other position needs one value rather than a set.

## Facts

A fact is an entered value, either sourced or assumed.
CSV files preserve the conventional financial-statement layout: line items in rows and periods in columns.

```csv
line_item,2023,2024,2025
revenue,10387,14015,18674
cost_of_revenue,6110,7996,9451
total_costs_and_expenses,13892,13549,21263
operating_income,-3505,466,-2589
```

The files contain unformatted numbers rather than display strings such as `$125.0m`.
Numeric literals and CSV values use a dot as the decimal separator regardless of the machine’s locale.
A CSV value may instead be an ISO date such as `2025-12-31`, which makes the whole row a [date line](#dates); a row mixing dates and numbers is an error.
A line whose units format [text](#text) holds each cell as printed, such as a credit rating.

`-`, `–`, and `—` count as zero while retaining their distinction from a printed `0` in source provenance.
`formats.yaml` controls the displayed dash.
An empty cell supplies no fact for that period, allowing a formula or override to fill it; otherwise it remains unresolved.
Each row must contain one cell per period, including empty trailing cells, and period headers must be unique.
The first header must be `line_item`; quoted CSV fields and thousands separators are not supported.

Several fact tables in one scope may contain the same line when the populated periods do not overlap.
Libreta combines those fragments into one line while each cell keeps its own table's provenance, so annual and interim filings can contribute columns to one view.
A repeated populated coordinate is an error naming both CSV files, even when the two values agree.
Combined fragments must agree on the line's units, currency, fiscal year end, and additive or contra sign.

Subtotals a source prints are stored as facts rather than recalculated, because a source rounds and a recalculation does not.
For example, store printed earnings per share and compare it with the underlying division in `checks.yaml`.
Assumptions that naturally form a period-based grid can use the same representation.

## Fact metadata

Each CSV has a sibling YAML describing where the table came from and how to read it.
The sibling YAML is the authoritative provenance for every populated cell in that table.

```yaml
# consolidated-statements-of-operations.yaml
table:
  title: Consolidated Statements of Operations

source:
  url: https://www.sec.gov/Archives/edgar/data/...#id286866c4c474ba490d6531a57db9e93_1632
  image: consolidated-statements-of-operations.png
  details:
    Table: Consolidated Statements of Operations
    Page: F-6
    Document: SpaceX 2026 S-1/A
    Extracted: 2026-08-08

defaults:
  units: millions
  printed: as_is

line_items:
  revenue:
    label: Revenue
  interest_expense:
    label: Interest expense
    printed: negated
  earnings_per_share_basic:
    label: Basic
    units: dollars_per_share
```

`details` is free-form provenance: each key is a label and the inspector lists the pairs in the order written.
Only `url`, `image`, and `note` carry behavior; everything else a reader needs goes in `details`.

`source.note` provides context that applies to the complete source table and appears in the inspector's Source section.

`label` is the source's own wording, kept so a row can be found again in the original document.
Presentation wording lives in `view.yaml`, so the two can differ without either one drifting.
`units` tells any view how to render a value, so a view never restates that a margin is a percentage.

CSV headers are the sole authority for periods.
Unknown table metadata is rejected rather than ignored.
`line_items` entries are optional; a row without one uses the [label fallback](#formulas) and inherits the table defaults.

`note` explains the line to a reader, whether a qualification the source prints beside the label, such as a related-party amount, or the modeler's rationale for using the fact.
It appears when inspecting the line and each of its cells; when several tables contribute to a line, each cell shows only its own table's note.
`cell_notes` maps periods to rationale specific to individual fact cells, shown above the line's note:

```yaml
line_items:
  revenue_growth:
    label: Revenue growth
    note: Based on management projections.
    cell_notes:
      2035E: Last year of the explicit forecast.
```

Use `cell_notes` only for what holds in one period; a line with a single value, such as an assumption, uses `note`.

`missing` is `blank` by default, so an empty cell is unresolved.
Set it to `zero` for an item that occurs in only some periods, such as a one-off charge, so every other period resolves to zero rather than needing an override per cell:

```yaml
line_items:
  legal_settlement:
    label: Legal settlement
    missing: zero
    note: Recognized only in the third quarter of 2025.
```

Like `printed` and `sign`, `missing` can be set in fact-table `defaults` and overridden per `line_items` entry.
A zero from `missing` has no source document, displays as `defaults.dash`, and cell overrides and calculated columns still take precedence over it.
Every table that carries the line must agree on `missing`.

### Source images

`image` optionally names a PNG, JPEG, or WebP image relative to the fact metadata file.
The file must exist and remain inside the loaded book root.

Prefer a lossless PNG for text-heavy tables because small digits and punctuation must remain crisp.
Capture the source table with enough title, units, headers, and neighboring rows to review the extraction in context.
Do not bake highlights or other annotations into the image.

The inspector shows the image, and clicking it opens the complete image in a modal.
`image_regions` can map periods to pixel rectangles within that image, which the inspector crops around and outlines:

```yaml
line_items:
  revenue:
    label: Revenue
    image_regions:
      2025: { x: 100, y: 200, width: 80, height: 24 }
```

Regions are optional independently for every line and period.
An unmapped fact shows the complete image without a rectangle.

Coordinates are original-image pixels: nonnegative `x` and `y` locate the top-left corner, and `width` and `height` are positive.
The period must belong to the fact table and `source.image` must be set.
Do not resize or replace the image after recording regions because its coordinate system may change.

Map the complete printed value, including commas, decimal points, minus signs, or parentheses, rather than a nearby cell or the whole row.
Obtain coordinates from browser, PDF, OCR, or image-selection tooling rather than estimating them by eye.
Measure from the image's top-left corner at its scale: a page captured at 2× device scale or cropped from a larger page needs its coordinates multiplied and offset to match, or every outline lands in the wrong place without any error.
Have the measuring tool read back the text inside each region and compare it with the fact, because an outline on the neighboring row or period is just as valid to `--check`.
After adding a region, verify in the modal that its outline identifies both the intended row and period, especially when the same value appears more than once.
Map every fact taken from one captured table, so the table behaves consistently.
`--check` reports any fact left unmapped, and `--set-regions` records regions a tool measured; see [running.md](running.md#record-source-regions).

## The two sign conventions

`printed` accepts `as_is` (the default) or `negated`; `sign` accepts `additive` (the default) or `contra`.
Both can be set in fact-table `defaults` and overridden per `line_items` entry.

`printed: negated` describes **the source**: the page shows the negation of what the name means, so the loader flips it.
Filings switch conventions between sections — cost of revenue prints positive because its whole block is subtracted, while interest expense prints in parentheses because it sits in a list that is summed.
`sign: contra` describes **the line**: it runs against the subtotal it rolls into, so views render it negated.

Between them sits the invariant: **facts, formulas and checks all work in natural direction** — an expense is a positive amount of expense, and a formula says `- cost_of_revenue` and means it.
Only the rendered cell is negated, so formulas read correctly out of context:

```text
pretax_income = operating_income - interest_expense + interest_income + other_income_expense_net
```

A column therefore adds downward, and a tax *benefit* is a `contra` row that lands positive because that year it added to net income.
The as-printed figure survives in the source drill-down, which is where reconciliation belongs.

## Formulas

```yaml
# formulas.yaml
formulas:
  gross_profit:
    label: Gross profit
    formula: revenue - cost_of_revenue
  gross_margin:
    formula: gross_profit / revenue
    units: percent
  revenue_growth:
    formula: revenue / prior(revenue) - 1
    units: percent
```

Each key below `formulas` defines one line in the namespace of the folder containing the file.
Nothing in `formulas.yaml` restates a value the source printed.
`formula` is required.

`label` optionally gives a calculated line its canonical display name.
Labels resolve in presentation order: a `view.yaml` row override wins, then the explicit fact or formula label, then Libreta turns the local snake-case identifier into sentence case.
The fallback is deliberately mechanical, so `free_cash_flow` becomes `Free cash flow` and `ebitda` becomes `Ebitda`; write an explicit label for `EBITDA` and other acronyms or specialized wording.

`units` is optional and falls back to the owning book's default units.
`note` is optional and records the modeling judgment or methodology, shown when inspecting the calculated line or any of its cells.
`cell_notes` optionally maps columns to notes shown above it when inspecting those individual cells.
Cell overrides and calculated columns show only their own note.

### References

Formulas can use short local names within their folder.
References across folders can use qualified names such as `historical.cash_flow.depreciation_and_amortization`.
Write folder separators as dots and hyphens as underscores in formula references.

A bare reference such as `revenue` uses the current column; a quoted column such as `revenue["2023"]` always uses that column:

```yaml
formulas:
  change:
    formula: revenue["2025"] - revenue["2023"]
```

`prior(x)` is `x` in the preceding period, following the formula scope's period order, not calendar arithmetic or the current display order.

`self` means the line at the current row and is useful in [calculated columns](#calculated-columns-and-cell-overrides).
A scope prefix such as `interim.self` means the same line in another folder, which is how a column bridges figures a sub-folder holds under matching line names.

### Single values

A figure that holds once rather than per period, such as an assumption, a valuation, or `change` above, lives in a folder of one column, conventionally `Value`, as the DCF's inputs do.
Facts get there with a `Value` header in their CSV; a folder of formulas alone sets its one column with `columns: [Value]` in its `view.yaml`, since a folder with no periods of its own inherits its parent's.

```yaml
# historical/summary/view.yaml
title: Summary
columns: [Value]

# historical/summary/formulas.yaml
formulas:
  revenue_change:
    formula: historical.revenue["2025"] - historical.revenue["2023"]
```

A formula whose every reference names its column has the same value in every column of its folder, so in a folder of periods it repeats one figure under each, and `--check` reports it as a repeated line unless cell overrides take all but one of the columns.

### Operators and functions

The formula language supports numeric literals, parentheses, `+`, `-`, `*`, `/`, and `^`.
Exponentiation is right-associative and binds more tightly than unary minus.
When a formula starts with unary minus, omit the space after it because YAML reserves `-` for a sequence entry:

```yaml
formulas:
  net_investment_purchases:
    formula: -purchases_of_short_term_investments + maturities
```

Comparisons use `<`, `<=`, `>`, `>=`, `==`, and `!=`.
`if(condition, when_true, when_false)` evaluates only the selected branch, so an unresolved unused branch does not make the result unresolved.

`sum`, `min`, `max`, `average`, and `median` accept comma-separated expressions and inclusive ranges in the referenced line's scope period order:

```yaml
formulas:
  present_value_of_forecast:
    formula: sum(present_value["2026E":"2030E"])
  average_revenue:
    formula: average(revenue["2023":"2025"])
  median_multiple:
    formula: median(alpha_multiple, beta_multiple, gamma_multiple)
```

An even number of values makes `median` return the average of the two middle values.

### Dates

A date is held as a count of days, so date arithmetic is ordinary arithmetic with a few operations ruled out:

```yaml
formulas:
  discount_period:
    formula: (period_end - dcf.valuation_date["Value"]) / 365
    units: ratio
```

- `date - date` is a number of days.
- `date + days` and `date - days` are dates.
- `min` and `max` of dates are dates, and comparing two dates is allowed.
- `eomonth(date, months)` is the last day of the month `months` away, truncated to whole months as in Excel, so a year-end steps by `eomonth(prior(period_end), 12)` without drifting off month ends.
- Anything else a date takes part in is an error naming the cell: adding two dates, subtracting a date from a number, negating, multiplying, dividing or raising a date, comparing a date with a number, mixing dates and numbers in a function, `sum`, `average` or `median` of dates, and `eomonth` of a number or by a date.

A line holding dates needs units whose format sets [`date`](#formats), and a line holding numbers must not have one, so a formula yielding a date declares `units: date`.
A cell override's `value` is always a number, so the first period of a stepped date line is an override `formula`, such as `eomonth(valuation_date, 12)`.
Misuse surfaces once the cell's inputs resolve, as `--check` and the page report it.

### Text

A label the source prints, such as a credit rating, is a fact like any figure, with its source and its region on the image:

```csv
line_item,FY2024,FY2025
moodys,Ba3,Ba2
```

```yaml
# ratings.yaml
defaults:
  units: text
```

Units whose format sets [`text`](#formats), such as the built-in `text`, make a line hold text, and they are the only way to: a line in other units fails at the first cell that is not a number or a date.
Every cell of a text line is kept as printed, a dash or a number included, and shows as written.
The CSV splits on commas, so a cell cannot contain one.

No formula or check reads a text line, and one that does is an error naming the line.
A calculated column leaves a text line empty rather than reading it.
`sign: contra`, `printed: negated` and `missing: zero` describe numbers, so any of them on a text line is an error, as is a number in text units, such as a cell override's `value`.

## Calculated columns and cell overrides

A calculated column supplies a default formula for every line at that column.
It belongs in `formulas.yaml`, while `view.yaml` selects and orders it alongside period columns.

```yaml
# formulas.yaml
column_formulas:
  cagr:
    label: CAGR
    formula: (self["2025"] / self["2023"]) ^ (1 / 2) - 1
    units: percent
```

```yaml
# view.yaml
columns: [2023, 2024, 2025, cagr]
```

`formula` is required, and `units` and `note` are optional.
With `units`, every cell in the column uses them, as a growth rate would; without, each cell keeps its line's units.
`label` is optional and uses the same fallback as a calculated line.
A calculated-column `note` describes the column method and appears when inspecting the complete column.

`applies_to` is `all` by default, so the column formula lands on every line, which suits a growth rate or a change.
Set it to `facts` for a column that is itself a period, such as a last-twelve-months bridge.
Fact lines are then bridged by the column formula, and calculated lines evaluate their own formula in that column, so a margin divides bridged profit by bridged revenue rather than bridging the margins.
Another folder reading such a line in a column of the same name gets the bridged value, so `results.revenue` fills an `LTM` column elsewhere without naming `results.revenue["LTM"]`.

```yaml
column_formulas:
  LTM:
    formula: self["FY2025"] - self["H1 2025"] + self["H1 2026"]
    applies_to: facts
```

A column formula may share its name with a fact table header: sourced cells keep their values and the formula fills only the lines the source left empty, so a `Monthly` and an `Annual` column can each derive from the other.

A scoped `self` lets one column carry a whole sub-folder's figures onto a statement, such as a last-twelve-months folder that bridges each annual line with its interim stubs.

```yaml
column_formulas:
  LTM:
    label: LTM 2026
    formula: ltm.self["LTM_2026"]
```

A line the other scope does not carry resolves as unresolved rather than as an error, so a growth line the bridge cannot produce simply stays blank.
Name such a column for the folder it reads rather than for the period the folder itself presents, because a sub-folder inherits its parent's column formulas and a shared name would apply the parent's formula to the sub-folder's own rows.

### Cell overrides

An override can supply a hardcoded value, replace a calculated cell's formula, change its units, or intentionally leave it blank.

```yaml
cell_overrides:
  forecast_year:
    2026E:
      value: 1

  gross_margin:
    cagr:
      blank: true

  revenue:
    cagr:
      formula: revenue["2025"] / revenue["2023"] - 1
      units: percent
```

An override may use only one of `value`, `formula`, or `blank: true`.
`units` can accompany a value or formula, or appear alone to change only the resolved cell's units.
`note` can accompany an override and describes the judgment specific to that cell.
A hardcoded value resolves as a fact, since it was entered rather than calculated, and the review surface says it carries no source document.
Metadata resolves at the cell level, so a factual row can contain a calculated cell with different units and no source attribution or contra sign.

### Resolution order

Each cell resolves from the first of these that applies: a sourced fact, an explicit cell override, a column formula unless it applies only to facts, a row formula, then unresolved.
Facts remain authoritative and cannot be overridden at a coordinate where the source supplied a value.

## Checks

A check asserts that the book agrees with its source.

```yaml
# checks.yaml
checks:
  operating_income:
    formula: revenue - total_costs_and_expenses - operating_income
    expect: 0
  earnings_per_share_basic:
    formula: net_income_attributable_to_shareholders_basic / weighted_average_shares_basic - earnings_per_share_basic
    expect: 0
    tolerance: 0.005
```

They report rather than block, because a broken number is more useful on the page than withheld.
Each key below `checks` names one assertion in the namespace of the folder containing the file.

`formula` is evaluated in that folder's scope for every period presented by its view, or by descendant views when the check's folder has no view.
`expect` is the expected result and defaults to `0`.
`tolerance` is an inclusive absolute tolerance and defaults to `0`, so a check passes when `abs(actual - expect) <= tolerance`.
`note` is optional and records why the check is shaped as it is, such as the rounding a tolerance absorbs.

A period that is structurally unavailable, such as the first period of `prior(x)`, is omitted from the check.
A period in which every fact the check reads is empty is also omitted, so a balance-sheet tie runs only on the dates the balance sheet covers.
A period whose required input exists but cannot be resolved is reported as a failure with no numeric delta, so a period with some facts entered and others missing fails.
A check omitted from every period fails, because it verified nothing.

## Presentation

`view.yaml` decides which rows appear, in what order, and what they are called.

```yaml
# view.yaml
columns: [2023, 2024, 2025]

rows:
  - line: revenue
  - line: revenue_growth
    label: Revenue growth
    style: supplemental
  - line: operating_income
    style: subtotal
```

`title` names the view and `subtitle` supplies optional supporting text.
`columns` selects periods or calculated columns in display order.
A column can use a mapping when it needs presentation metadata:

```yaml
columns:
  - source: 2024
    note: The first comparative period.
  - 2025
```

`source` names the period or calculated column.
`note` appears when inspecting the complete column and remains attached to it when the view is transposed.
`label` and `style` are optional presentation overrides.

If `columns` is omitted, the view uses all periods available in its scope.
Scope periods collect CSV headers first, then additional non-calculated columns from `view.yaml`, preserving first occurrence.
A scope with no periods of its own inherits the nearest ancestor's periods within the same book.

A row without a label uses the definition's label, then the [label fallback](#formulas).
A row with `label` but no `line` is a section header.
`space` inserts a named spacing role such as `compact`, and `indent` sets a line's indentation level.
`style` names a role, never an appearance, and the interface decides how each role looks:

| Role | Use it for |
| --- | --- |
| `subtotal` | A line that sums the lines above it, such as gross profit or operating income. |
| `total` | A final figure, such as net income, total assets, or value per share. |
| `supplemental` | A measure read alongside the line above it, such as a margin or a growth rate. |

A column takes the same roles through its mapping form; a transposed view applies each row's role to its column instead.
Any other `style` renders as an ordinary line.

`sign` can override how a line contributes in one presentation without changing its stored value or any calculation.
This matters when the same natural amount has different roles, such as depreciation being subtractive above EBIT and additive in a cash-flow bridge.

```yaml
rows:
  - line: depreciation_and_amortization
    sign: contra
  - line: depreciation_and_amortization_addback
    sign: additive
```

`supplemental` does not mean that a line is calculated; standalone analytical sections use ordinary rows even when every line is a formula.

`transpose: true` exchanges the displayed row and column axes without changing cell coordinates, formulas, sources, or CLI value addressing.
A transposed view cannot contain section-header or spacer rows because those entries cannot become columns.

### Cross-book comparisons

A view can compare explicitly included books by naming a book on every row and a metric on every column.

```yaml
title: Comparison

rows:
  - book: nvidia
    label: NVIDIA
  - book: amd
    label: AMD

columns:
  - line: historical.income_statement.revenue
    column: "2025"
    label: Revenue
  - line: historical.income_statement.operating_margin
    column: "2025"
    label: Operating margin
    style: supplemental
```

`book` must name an inclusion from `book.yaml`.
A row can instead name a `scope`: a folder in the comparing book whose lines carry the same names as the books beside it, which is how a summary of the set — a median, a mean, a high and a low — takes its place among them.

```yaml
rows:
  - book: nvidia
  - book: amd
  - space: compact
  - scope: median
    style: total
```

The folder mirrors the structure the columns name: a column selecting `historical.income_statement.revenue` reads `median/historical/income-statement/formulas.yaml`, which defines `revenue` like any other line.

```yaml
formulas:
  revenue:
    formula: median(books::historical.income_statement.revenue["2025"])
```

The folder is found from the view outward, the same way a short line reference is, and its name becomes the row label unless the row overrides it.
The summary is an ordinary line: inspectable, checkable and addressable by `--value`.

Each comparison column requires both `line` and `column`; periods are never inferred.
`label`, `style`, `sign`, and `note` are optional presentation overrides on a comparison column.
`label`, `style`, and `indent` are optional on a comparison book row.
A metric absent from one included book remains unresolved for that book rather than removing the metric from the comparison.

### Navigation and scope

`book.yaml` defines the sidebar as one ordered tree.

```yaml
navigation:
  - historical/income-statement
  - historical/segments:
      - historical/segments/revenue
      - historical/segments/profitability
```

List order is display order, and nesting defines sidebar nesting.
Every discovered view must appear exactly once.
An explicitly included child keeps its own navigation rule, while `book: child_id` inserts that complete subtree into its parent.

Short line references resolve first in the view folder and then in each ancestor folder, so a nested view presents its parent's facts and formulas without copying them.
Qualified references resolve from the book root, so calculation dependencies remain independent of navigation nesting.

## Sensitivities

A sensitivity table recalculates one output while temporarily replacing one or two exact input coordinates.
Definitions live in `sensitivities.yaml`, separate from the views that select them.

```yaml
sensitivities:
  equity_value:
    title: Equity value sensitivity
    output:
      line: dcf.equity_value
      column: Value
      label: Equity value
    inputs:
      - line: dcf.terminal_value.perpetual_growth_rate
        column: Value
        label: Perpetual growth rate
        values: [0.02, 0.03, 0.04]
      - line: dcf.wacc.wacc
        column: Value
        label: WACC
        values: [0.08, 0.09, 0.10]
    valid: dcf.terminal_value.perpetual_growth_rate["Value"] < dcf.wacc.wacc["Value"]
```

Each input names a line and column, whether its ordinary value comes from a fact or a formula.
The column must be a known period for that line's scope or a calculated column resolvable from the sensitivity's scope.

Two inputs cannot resolve to the same coordinate, even when their written names differ.
The first input runs across the columns, and the optional second input runs down the rows.
Trial values never modify the book or its base result.

`valid` is an optional formula evaluated under the trial inputs.
A zero `valid` result marks that combination unavailable, while any other resolved number permits it.
An unresolved condition, unresolved output, or calculation error makes only that trial unavailable and reports the reason in its details.
A valid output of zero remains a result.

A view selects sensitivity definitions by name after its columns.

```yaml
columns: [Value]

sensitivities: [equity_value]
```

## Formats

`formats.yaml` decides how a number is spelled.
A fact or formula declares its `units`, and the matching format decides scaling, decimals, separators, symbols, and how a negative is shown.
Every unit used for display must have a matching format; an unknown unit reports an error.

Libreta has built-in formats for `percent`, `times`, `ratio`, `dollars`, `date` and `text`; see [src/Libreta/formats.yaml](../src/Libreta/formats.yaml).
A book needs no `formats.yaml` of its own until it uses another unit or wants to differ:

```yaml
defaults:
  units: millions
  negative: minus

formats:
  millions:
    decimals: 0
```

Each key below `formats` names a unit and accepts these properties:

- `scale` multiplies the exact value before display, so `100` renders a ratio of `0.4118` as `41.18` before rounding.
- `decimals` selects the number of digits after the decimal point.
- `separator` enables invariant-culture thousands separators.
- `prefix` and `suffix` surround the magnitude with text such as `$` or `%`.
- `negative` is `parentheses` to render `(125)` or `minus` to render `-125`.
- `date` renders the value as a date with a .NET pattern such as `yyyy-MM-dd`, ignoring every other property; the inspector's exact value is the ISO date rather than its day count.
- `text: true` makes a line in these units hold [text](#text), shown as printed, ignoring every other property.

`defaults` accepts the same properties and supplies them to every unit that does not set them.
Properties nothing sets use `scale: 1`, `decimals: 0`, `separator: true`, empty `prefix` and `suffix`, and `negative: parentheses`.
`defaults` also accepts:

- `units`, supplied when a fact, formula, or override does not declare them; without it, such a value is a plain number formatted by `defaults`.
- `dash`, the display text for a fact whose CSV cell contains a dash, which remains a real zero with distinct source provenance, and for a zero from `missing: zero`.
- `zero`, the display text for a calculated value or hardcoded override that is exactly zero; sourced facts are unaffected, because a printed `0` can mean an amount that rounds to zero at the source's scale.
- `unresolved`, the display text for a value that cannot be calculated, which is not zero.

Built in, `dash` and `zero` are `"-"` and `unresolved` is `""`.

Lookup walks up from the view folder and merges every `formats.yaml` through the enclosing Git worktree root, so one file at the root covers every book while any folder can override individual settings for itself.
Without a Git worktree, the nearest `formats.yaml` at or above the book root becomes the boundary.
A comparison view formats every cell from its own folder, including cells read from included books, so a comps folder can set `millions` to `scale: 0.001` to show billions without changing how those books render.

Files merge from farthest to nearest, and each property merges independently, so a book can change only the precision of `millions` while inheriting everything else:

```yaml
formats:
  millions:
    decimals: 1
```

A named-unit property beats `defaults` from any folder, however near.
Built-in units do not merge this way: a unit any file defines replaces the built-in entirely, so `percent` defined without a `suffix` shows none.
A file's `defaults` apply to the built-in units it leaves alone.

Prefer adding a new unit at the root over redefining an existing one locally, so two tables never spell the same unit differently.

## Evaluation

Missing inputs produce unresolved values rather than zero; invalid references and circular dependencies report errors.
Calculations use finite double-precision numbers, and display rounding does not change their results.
Financial definitions belong in book formulas; the engine supplies no built-in accounting definitions.
