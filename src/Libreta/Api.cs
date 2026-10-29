using System.Text.Json;
using System.Text.Json.Serialization;

namespace Libreta;

public static class PayloadJson
{
  // Nested calculation expansions run as deep as the formula chain, well past the 64-level default.
  public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 512
  };
}

/// <summary>One piece of a written-out calculation. Column addresses a cell; Label and ColumnLabel name it.</summary>
public record CalculationTokenPayload(
  string Text,
  string? Dependency,
  string? Column,
  string? ColumnLabel = null,
  string? SourceView = null,
  CalculationPayload? Expansion = null,
  string? ExpansionId = null,
  string? Label = null,
  string? Missing = null);

public record CalculationPayload(List<CalculationTokenPayload> Tokens);

/// <summary>One written reference: its span in the formula text and the cell it opens.</summary>
public record FormulaReferencePayload(
  int Start,
  int Length,
  string Line,
  string? Column,
  string Label,
  string Kind,
  string? SourceView = null,
  bool Linked = false,
  string? FormulaId = null);

public record FormulaPayload(string Text, List<FormulaReferencePayload> References);

/// <summary>How a unit scales and decorates a number, so the page can show exact values in the same terms.</summary>
public record UnitPayload(double Scale, string Prefix, string Suffix);

public record SourceRegionPayload(int X, int Y, int Width, int Height);

public record SourcePayload(
  string Table,
  string Url,
  string Image,
  SourceRegionPayload? Region,
  Dictionary<string, string> Details,
  string? Note);

/// <summary>A rendered cell. <c>Line</c> and <c>Column</c> identify it in any orientation.</summary>
public record CellPayload(
  string Line,
  string Column,
  string Display,
  string? Exact,
  bool Unresolved,
  string Kind,
  bool Linked,
  string Units,
  FormulaPayload? Formula,
  CalculationPayload? Calculation,
  SourcePayload? Source,
  bool Contra,
  string? SourceView = null,
  string? Note = null)
{
  public List<DependentPayload> Dependents { get; init; } = new List<DependentPayload>();
}

/// <summary>A cell that reads another directly. <c>Hidden</c> means no table shows it; <c>SourceView</c> names one
/// that does.</summary>
public record DependentPayload(
  string Line,
  string Column,
  string Label,
  string ColumnLabel,
  string Value,
  string Kind,
  string? SourceView,
  bool Hidden);

/// <summary>One cell requested alone (<c>--value</c>), carrying the formula expansions its references need.</summary>
public record CellDetailPayload(CellPayload Cell, Dictionary<string, FormulaPayload> Formulas);

/// <summary>A column header. <c>Kind</c> tells the page whether the line runs across the row or down the column;
/// a column that is one line carries the same metadata as a row.</summary>
public record ColumnPayload
{
  public string Label { get; init; } = "";
  public string Kind { get; init; } = "";
  public string Name { get; init; } = "";
  public string Style { get; init; } = "";
  public string? Note { get; init; }
  public string? SourceView { get; init; }
  public FormulaPayload? Formula { get; init; }
  public SourcePayload? Source { get; init; }
}

public record RowPayload
{
  public string Name { get; init; } = "";
  public string Label { get; init; } = "";
  public string Style { get; init; } = "";
  public string Kind { get; init; } = "";
  public string? SourceView { get; init; }
  public int Indent { get; init; }
  public string Units { get; init; } = "";
  public List<CellPayload> Cells { get; init; } = new List<CellPayload>();
  public FormulaPayload? Formula { get; init; }
  public SourcePayload? Source { get; init; }
  public string? Note { get; init; }
}

public record CheckPayload(
  string Name,
  string Formula,
  List<string> Periods,
  List<string> Deltas,
  bool Passed,
  string? Note);

public record SensitivityInputPayload(
  string Label,
  string Line,
  string Column,
  string? SourceView,
  string Units,
  List<double> Values,
  List<string> Displays);

public record SensitivityCellPayload(
  string Display,
  string? Exact,
  bool Valid,
  List<double> Inputs,
  string? UnavailableReason);

public record SensitivityPayload(
  string Name,
  string Title,
  string OutputLabel,
  string OutputLine,
  string OutputColumn,
  string? OutputSourceView,
  string OutputUnits,
  List<SensitivityInputPayload> Inputs,
  List<List<SensitivityCellPayload>> Cells);

public record ViewPayload
{
  public string Title { get; init; } = "";
  public string Subtitle { get; init; } = "";
  public List<ColumnPayload> Columns { get; init; } = new List<ColumnPayload>();
  public List<string> PeriodColumns { get; init; } = new List<string>();
  public List<RowPayload> Rows { get; init; } = new List<RowPayload>();
  public List<CheckPayload> Checks { get; init; } = new List<CheckPayload>();
  public List<SensitivityPayload> Sensitivities { get; init; } = new List<SensitivityPayload>();
  public Dictionary<string, CalculationPayload> Calculations { get; init; } =
    new Dictionary<string, CalculationPayload>();
  public Dictionary<string, FormulaPayload> Formulas { get; init; } = new Dictionary<string, FormulaPayload>();
  public Dictionary<string, UnitPayload> Units { get; init; } = new Dictionary<string, UnitPayload>();
  public string? Error { get; init; }
  public long Version { get; init; }
}

/// <summary>Turns a resolved view into the cell-specific values and provenance the browser needs.</summary>
public static class PayloadBuilder
{
  public static ViewPayload Build(View view, Engine engine, long version)
  {
    ResolvedTable table = ResolvedTable.Build(view);
    var presentation = new Presentation(table.Coordinates(), false);
    Dictionary<CellCoordinate, List<CellCoordinate>> readers = engine.Dependents(view.Book.PresentedCoordinates);
    var rows = new List<RowPayload>();
    var grid = new List<List<CellCoordinate>>();
    var values = new List<List<ResolvedCell>>();

    for (int index = 0; index < table.Rows.Count; index++)
    {
      TableEntry entry = table.Rows[index];
      if (entry.Kind == "spacer")
      {
        rows.Add(new RowPayload { Kind = "spacer", Style = entry.Style });
        continue;
      }
      if (entry.Kind == "header")
      {
        rows.Add(new RowPayload { Kind = "header", Label = entry.Label, Style = "header" });
        continue;
      }

      var cells = new List<CellPayload>();
      var coordinates = new List<CellCoordinate>();
      var resolvedCells = new List<ResolvedCell>();
      for (int column = 0; column < table.Columns.Count; column++)
      {
        CellCoordinate coordinate = table.Coordinate(index, column)!.Value;
        coordinates.Add(coordinate);
        resolvedCells.Add(engine.Cell(coordinate));
        cells.Add(Cell(view, engine, table.LineEntry(index, column), coordinate, resolvedCells[^1], presentation,
          readers));
      }

      grid.Add(coordinates);
      values.Add(resolvedCells);
      rows.Add(Row(view, engine, entry, coordinates, resolvedCells, cells, presentation));
    }

    // A column is a whole line only when every row contributed a cell, which headers and spacers do not.
    bool complete = grid.Count == table.Rows.Count;
    var columns = new List<ColumnPayload>();
    for (int index = 0; index < table.Columns.Count; index++)
    {
      columns.Add(Column(view, engine, table.Columns[index], index, complete ? grid : null, values, presentation));
    }

    return new ViewPayload
    {
      Title = view.Presentation.Title,
      Subtitle = view.Presentation.Subtitle,
      Columns = columns,
      PeriodColumns = table.Coordinates()
        .Select(coordinate => coordinate.Column)
        .Distinct()
        .Where(column => !view.Book.ColumnFormulas.ContainsKey(column))
        .ToList(),
      Rows = rows,
      Checks = BuildChecks(view, engine),
      Sensitivities = BuildSensitivities(view),
      Calculations = presentation.Calculations,
      Formulas = presentation.Formulas,
      Units = view.Formatter.Units(),
      Version = version
    };
  }

  /// <summary>A line reports fact or formula wherever it sits, so the page finds the line axis by kind alone.</summary>
  private static string EntryKind(View view, TableEntry entry)
  {
    if (entry.Kind != "line")
    {
      return entry.Kind;
    }
    if (entry.Column is not null)
    {
      return "metric";
    }
    return LineKind(view, entry.Name);
  }

  private static ColumnPayload Column(
    View view,
    Engine engine,
    TableEntry entry,
    int index,
    List<List<CellCoordinate>>? grid,
    List<List<ResolvedCell>> values,
    Presentation presentation)
  {
    string kind = EntryKind(view, entry);
    var column = new ColumnPayload
    {
      Label = entry.Label,
      Kind = kind,
      Name = entry.Name,
      Style = entry.Style,
      Note = entry.Note
    };
    if (grid is null || kind is not ("fact" or "formula" or "missing"))
    {
      return column;
    }

    LineDetail detail = Line(view, engine, entry, grid.Select(row => row[index]).ToList(), presentation);
    return column with
    {
      Note = detail.Note,
      SourceView = detail.SourceView,
      Formula = detail.Formula,
      Source = detail.Source
    };
  }

  private static CellPayload Cell(
    View view,
    Engine engine,
    TableEntry lineEntry,
    CellCoordinate coordinate,
    ResolvedCell cell,
    Presentation presentation,
    Dictionary<CellCoordinate, List<CellCoordinate>> readers)
  {
    view.Facts.TryGetValue(coordinate.Line, out Fact? fact);
    view.Formulas.TryGetValue(coordinate.Line, out Formula? formula);
    // Overrides and calculated columns are judgments of their own, so only the line's own cells carry its note.
    string? lineNote = cell.FactCell?.LineNote
      ?? (formula is not null && ReferenceEquals(cell.Expression, formula.Expression) ? formula.Note : null);
    // A cell in other units than its line, such as a growth rate beside amounts, is a different quantity.
    bool contra = Contra(lineEntry.Sign, fact) && cell.Units == engine.Units(coordinate.Line);
    string scope = cell.Scope ?? view.Scope;
    CellLink link = CellLinkFor(view, cell, coordinate.Line);
    CalculationPayload? calculation = cell.Expression is null
      ? null
      : Calculation(view, engine, cell.Expression, scope, coordinate.Line, coordinate.Column, presentation);
    return new CellPayload(
      coordinate.Line,
      coordinate.Column,
      cell.Value is null ? view.Formatter.Unresolved : Display(view, cell, contra),
      cell.Value is null ? null : view.Formatter.Exact(cell.Units, cell.Value.Value),
      cell.Value is null,
      cell.Kind,
      link.Linked,
      cell.Units ?? view.Formatter.DefaultUnits ?? "",
      Formula(view, engine, cell.Formula, cell.Expression, scope, coordinate.Line, coordinate.Column, presentation,
        new HashSet<string> { Key(coordinate) }),
      calculation,
      Source(cell.FactCell) ?? LinkedSource(engine, link),
      contra,
      link.SourceView,
      Paragraphs(new[] { cell.Note, lineNote }.OfType<string>().ToList()))
    {
      Dependents = Dependents(view, engine, readers.GetValueOrDefault(coordinate), presentation)
    };
  }

  private static List<DependentPayload> Dependents(
    View view,
    Engine engine,
    List<CellCoordinate>? readers,
    Presentation presentation)
  {
    var dependents = new List<DependentPayload>();
    foreach (CellCoordinate reader in readers ?? new List<CellCoordinate>())
    {
      ResolvedCell? cell = SafeCell(engine, reader);
      bool shown = presentation.Shows(reader.Line, reader.Column);
      string? sourceView = shown ? null : view.Book.PresentingView(reader.Line, reader.Column, view.Id);
      dependents.Add(new DependentPayload(
        reader.Line,
        reader.Column,
        view.LabelOf(reader.Line),
        view.ColumnLabel(reader.Column),
        cell?.Value is null ? view.Formatter.Unresolved : CalculationValue(view, cell.Units, cell.Value.Value),
        cell?.Kind ?? "missing",
        sourceView,
        !shown && sourceView is null));
    }
    return dependents;
  }

  /// <summary>Line metadata belongs to a row only while the row is a line; book and period rows keep cells.</summary>
  private static RowPayload Row(
    View view,
    Engine engine,
    TableEntry entry,
    List<CellCoordinate> coordinates,
    List<ResolvedCell> resolvedCells,
    List<CellPayload> cells,
    Presentation presentation)
  {
    string kind = EntryKind(view, entry);
    var row = new RowPayload
    {
      Name = entry.Name,
      Label = entry.Label,
      Style = entry.Style,
      Kind = kind,
      Indent = entry.Indent,
      Note = entry.Note,
      Cells = cells
    };
    if (kind is not ("fact" or "formula" or "missing"))
    {
      return row;
    }

    LineDetail detail = Line(view, engine, entry, coordinates, presentation);
    return row with
    {
      SourceView = detail.SourceView,
      Units = detail.Units,
      Formula = detail.Formula,
      Note = detail.Note,
      Source = detail.Source
    };
  }

  /// <summary>A line's metadata, read along the cells it occupies on whichever axis it is drawn.</summary>
  private static LineDetail Line(
    View view,
    Engine engine,
    TableEntry entry,
    List<CellCoordinate> coordinates,
    Presentation presentation)
  {
    string name = entry.Name;
    view.Facts.TryGetValue(name, out Fact? fact);
    view.Formulas.TryGetValue(name, out Formula? formula);
    return new LineDetail(
      engine.Units(name) ?? view.Formatter.DefaultUnits ?? "",
      SourceView(view, name, coordinates, fact?.Scope ?? formula?.Scope),
      formula is null ? null : Formula(view, engine, formula.Text, formula.Expression, formula.Scope,
        name, null, presentation, new HashSet<string>()),
      Source(fact),
      Paragraphs(fact?.Notes) ?? formula?.Note);
  }

  /// <summary>Paragraphs are separated by a blank line, which the page splits back apart.</summary>
  private static string? Paragraphs(List<string>? notes)
  {
    return notes is null || notes.Count == 0 ? null : string.Join("\n\n", notes);
  }

  private static List<SensitivityPayload> BuildSensitivities(View view)
  {
    var payloads = new List<SensitivityPayload>();
    foreach (string selected in view.Presentation.Sensitivities)
    {
      string name = view.Book.ResolveSensitivity(selected, view.Scope)
        ?? throw new InvalidDataException($"Unknown sensitivity '{selected}' selected by view '{view.Scope}'");
      Sensitivity sensitivity = view.Book.Sensitivities[name];
      string outputLine = ResolveSensitivityLine(view, sensitivity.Output.Line, sensitivity.Scope, name, "output");
      string outputColumn = ResolveSensitivityColumn(
        view, sensitivity.Output.Column, sensitivity.Scope, outputLine, name, "output");
      var inputs = new List<(SensitivityInputDefinition Definition, string Line, string Column)>();
      foreach (SensitivityInputDefinition input in sensitivity.Inputs)
      {
        string line = ResolveSensitivityLine(view, input.Line, sensitivity.Scope, name, "input");
        string column = ResolveSensitivityColumn(view, input.Column, sensitivity.Scope, line, name, "input");
        inputs.Add((input, line, column));
      }
      if (inputs.Select(input => (input.Line, input.Column)).Distinct().Count() != inputs.Count)
      {
        throw new InvalidDataException($"Sensitivity '{name}' repeats an input coordinate");
      }

      var inputPayloads = new List<SensitivityInputPayload>();
      var baseEngine = new Engine(view);
      foreach ((SensitivityInputDefinition definition, string line, string column) in inputs)
      {
        ResolvedCell baseCell = baseEngine.Cell(line, column);
        if (baseCell.Value is null)
        {
          throw new InvalidDataException($"Sensitivity '{name}' input '{line}[{column}]' is unresolved");
        }
        string label = definition.Label.Length == 0 ? view.LabelOf(line) : definition.Label;
        inputPayloads.Add(new SensitivityInputPayload(
          label,
          line,
          column,
          SensitivitySourceView(view, line, column),
          baseCell.Units ?? view.Formatter.DefaultUnits ?? "",
          definition.Values,
          definition.Values.Select(value => view.Formatter.Format(baseCell.Units, value)).ToList()));
      }

      List<double> rowValues = inputs.Count == 1 ? new List<double> { 0 } : inputs[1].Definition.Values;
      var cells = new List<List<SensitivityCellPayload>>();
      foreach (double rowValue in rowValues)
      {
        var row = new List<SensitivityCellPayload>();
        foreach (double columnValue in inputs[0].Definition.Values)
        {
          var substitutions = new Dictionary<(string Line, string Column), double>
          {
            [(inputs[0].Line, inputs[0].Column)] = columnValue
          };
          var trialValues = new List<double> { columnValue };
          if (inputs.Count == 2)
          {
            substitutions[(inputs[1].Line, inputs[1].Column)] = rowValue;
            trialValues.Add(rowValue);
          }
          ResolvedCell? output = null;
          string? unavailableReason = null;
          try
          {
            var trial = new Engine(view, substitutions);
            double? valid = sensitivity.Valid is null
              ? 1
              : trial.Evaluate(sensitivity.Valid, outputColumn, sensitivity.Scope, null);
            if (valid == 0)
            {
              unavailableReason = "The trial does not satisfy the sensitivity's valid condition.";
            }
            else if (valid is null)
            {
              unavailableReason = "The sensitivity's valid condition is unresolved for this trial.";
            }
            else
            {
              output = trial.Cell(outputLine, outputColumn);
              if (output.Value is null)
              {
                unavailableReason = "The output is unresolved for this trial.";
              }
            }
          }
          catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
          {
            unavailableReason = $"The trial could not be calculated: {exception.Message}";
          }
          bool isValid = output?.Value is not null;
          row.Add(new SensitivityCellPayload(
            isValid ? view.Formatter.FormatCalculated(output!.Units, output.Value!.Value) : view.Formatter.Unresolved,
            isValid ? Formatter.Exact(output!.Value!.Value) : null,
            isValid,
            trialValues,
            unavailableReason));
        }
        cells.Add(row);
      }
      ResolvedCell outputBase = baseEngine.Cell(outputLine, outputColumn);
      if (outputBase.Value is null)
      {
        throw new InvalidDataException($"Sensitivity '{name}' output '{outputLine}[{outputColumn}]' is unresolved");
      }
      payloads.Add(new SensitivityPayload(
        sensitivity.Name,
        sensitivity.Title,
        sensitivity.Output.Label.Length == 0 ? view.LabelOf(outputLine) : sensitivity.Output.Label,
        outputLine,
        outputColumn,
        SensitivitySourceView(view, outputLine, outputColumn),
        outputBase.Units ?? view.Formatter.DefaultUnits ?? "",
        inputPayloads,
        cells));
    }
    return payloads;
  }

  private static string? SensitivitySourceView(View view, string line, string column)
  {
    return view.Book.PresentingViewIncludingCurrent(line, column, view.Id);
  }

  private static string ResolveSensitivityLine(
    View view,
    string requested,
    string scope,
    string sensitivity,
    string role)
  {
    return view.Book.Resolve(requested, scope)
      ?? throw new InvalidDataException($"Sensitivity '{sensitivity}' has unknown {role} line '{requested}'");
  }

  private static string ResolveSensitivityColumn(
    View view,
    string requested,
    string scope,
    string line,
    string sensitivity,
    string role)
  {
    return view.Book.ResolveCoordinateColumn(requested, scope, line)
      ?? throw new InvalidDataException(
        $"Sensitivity '{sensitivity}' has unknown {role} column '{requested}' for line '{line}'");
  }

  public static List<CheckPayload> BuildChecks(View view, Engine engine, IEnumerable<Check>? requestedChecks = null)
  {
    List<Check> checks = (requestedChecks ?? view.Checks).ToList();
    var payloads = new List<CheckPayload>();
    foreach (Check check in checks)
    {
      List<CheckResult> results = engine.RunChecks(new[] { check });
      payloads.Add(new CheckPayload(
        check.Name,
        check.Text,
        results.Select(result => result.Period).ToList(),
        results.Select(result => result.Delta is null ? "unresolved" : Formatter.Exact(result.Delta.Value)).ToList(),
        results.Count > 0 && results.All(result => result.Passed),
        check.Note));
    }
    return payloads;
  }

  public static CellDetailPayload BuildCell(View view, Engine engine, ResolvedTable table, int row, int column)
  {
    CellCoordinate coordinate = table.Coordinate(row, column)!.Value;
    var presentation = new Presentation(table.Coordinates(), true);
    CellPayload cell = Cell(view, engine, table.LineEntry(row, column), coordinate, engine.Cell(coordinate),
      presentation, engine.Dependents(view.Book.PresentedCoordinates));
    return new CellDetailPayload(cell, presentation.Formulas);
  }

  private static bool Contra(string? sign, Fact? fact)
  {
    return sign switch
    {
      null => fact?.Contra ?? false,
      "additive" => false,
      "contra" => true,
      _ => throw new InvalidDataException($"Unknown row sign '{sign}'")
    };
  }

  // A formula that is one cross-scope reference aliases that line. A scope need not present a view, so may have none.
  private static CellLink CellLinkFor(View view, ResolvedCell cell, string line)
  {
    if (cell.Expression is not RefExpr reference)
    {
      return new CellLink(false, null, null, null);
    }
    string scope = cell.Scope ?? view.Scope;
    string? resolved = view.Book.ResolveSelf(reference.Name, scope, line);
    string? resolvedScope = resolved is null ? null : ScopeOf(view, resolved);
    bool linked = resolvedScope is not null && resolvedScope != scope;
    string? column = resolved is null
      ? null
      : view.Book.ReferenceColumn(reference.Column, cell.Column, scope, resolved);
    string? sourceView = resolved is null || !linked
      ? null
      : view.Book.PresentingView(resolved, column, view.Id, LinkedView(view, resolvedScope, scope));
    return new CellLink(linked, sourceView, linked ? resolved : null, linked ? column : null);
  }

  // An alias has no source of its own; it shows the source behind the cell it names.
  private static SourcePayload? LinkedSource(Engine engine, CellLink link)
  {
    return link.Line is null || link.Column is null
      ? null
      : Source(engine.Cell(new CellCoordinate(link.Line, link.Column)).FactCell);
  }

  private static string? SourceView(
    View view,
    string line,
    IEnumerable<CellCoordinate> coordinates,
    string? scope)
  {
    // A line found in an enclosing scope was inherited by ordinary lookup, not linked from another table.
    if (scope is null || Encloses(scope, view.Scope))
    {
      return null;
    }
    view.Book.ViewIdsByScope.TryGetValue(scope, out string? owned);
    string? presented = coordinates
      .Select(coordinate => view.Book.PresentingView(line, coordinate.Column, view.Id, owned))
      .FirstOrDefault(candidate => candidate is not null);
    return presented;
  }

  // Linked means the input lives in another view than the expression naming it. Enclosure cannot decide this: a nested
  // view's own formula reading its parent is a link, while an inherited formula is local wherever it renders.
  private static string? LinkedView(View view, string? scope, string referringScope)
  {
    return scope is not null && scope != referringScope
      && view.Book.ViewIdsByScope.TryGetValue(scope, out string? id) ? id : null;
  }

  private static bool Encloses(string scope, string viewScope)
  {
    return scope.Length == 0 || viewScope == scope || viewScope.StartsWith($"{scope}.", StringComparison.Ordinal);
  }

  // A reference the table does not display must be reachable elsewhere. Scope alone cannot decide: a child's own line
  // is local to its formula yet absent from a parent's table.
  private static string? ReferenceView(
    View view,
    string line,
    string? column,
    string referringScope,
    bool shown)
  {
    string? scope = ScopeOf(view, line);
    if (scope is null)
    {
      return null;
    }
    if (shown)
    {
      return LinkedView(view, scope, referringScope);
    }
    string? owned = scope == view.Scope ? null : view.Book.ViewIdsByScope.GetValueOrDefault(scope);
    return view.Book.PresentingView(line, column, view.Id, owned);
  }

  private static string LineKind(View view, string name)
  {
    return view.Facts.ContainsKey(name) ? "fact" : view.Formulas.ContainsKey(name) ? "formula" : "missing";
  }

  private static string Key(CellCoordinate coordinate)
  {
    return $"{coordinate.Line}\0{coordinate.Column}";
  }

  private static string? ScopeOf(View view, string name)
  {
    return view.Facts.TryGetValue(name, out Fact? fact)
      ? fact.Scope
      : view.Formulas.TryGetValue(name, out Formula? formula) ? formula.Scope : null;
  }

  public static string Display(View view, ResolvedCell cell, bool contra)
  {
    double value = contra ? -cell.Value!.Value : cell.Value!.Value;
    return value == 0 && ZeroDisplay(view, cell) is string zero ? zero : view.Formatter.Format(cell.Units, value);
  }

  /// <summary>The text a zero shows as, or null when it shows as a formatted number.</summary>
  public static string? ZeroDisplay(View view, ResolvedCell cell)
  {
    if (cell.FactCell?.PrintedDash == true || cell.Omitted)
    {
      return view.Formatter.Dash;
    }
    // A sourced fact keeps its printed zero; a hardcoded override has no source scale to round at.
    return cell.Kind == "formula" || cell.FactCell is null ? view.Formatter.Zero : null;
  }

  private static SourcePayload? Source(Fact? fact)
  {
    if (fact is null)
    {
      return null;
    }
    FactCell? first = fact.Cells.Values.FirstOrDefault();
    return first is not null && fact.Cells.Values.All(cell => cell.SourcePath == first.SourcePath)
      ? Source(first, false)
      : null;
  }

  private static SourcePayload? Source(FactCell? factCell, bool includeRegion = true)
  {
    if (factCell is null)
    {
      return null;
    }
    SourceRegion? source = includeRegion ? factCell.SourceRegion : null;
    SourceRegionPayload? region = source is null
      ? null
      : new SourceRegionPayload(source.X, source.Y, source.Width, source.Height);
    return new SourcePayload(
      factCell.TableTitle,
      factCell.Source.Url,
      factCell.SourceImage,
      region,
      factCell.Source.Details,
      string.IsNullOrWhiteSpace(factCell.Source.Note) ? null : factCell.Source.Note.Trim());
  }

  /// <summary>The formula text with each written reference resolved, so the page links every occurrence.</summary>
  private static FormulaPayload? Formula(
    View view,
    Engine engine,
    string? text,
    Expr? expression,
    string scope,
    string line,
    string? column,
    Presentation presentation,
    HashSet<string> path)
  {
    if (text is null || expression is null)
    {
      return null;
    }

    var references = new List<FormulaReferencePayload>();
    foreach (FormulaReference reference in engine.References(expression, column, scope, line))
    {
      // A line formula's reference names no column, so it is shown wherever this view draws the line.
      bool shown = reference.Column is null
        ? presentation.ShowsLine(reference.Line)
        : presentation.Shows(reference.Line, reference.Column);
      string? sourceView = ReferenceView(view, reference.Line, reference.Column, scope, shown);
      references.Add(new FormulaReferencePayload(
        reference.Start,
        reference.Length,
        reference.Line,
        reference.Column,
        view.LabelOf(reference.Line),
        LineKind(view, reference.Line),
        sourceView,
        !shown && (sourceView is not null || ScopeOf(view, reference.Line) != scope),
        shown ? null : Expansion(view, engine, reference, presentation, path)));
    }
    return new FormulaPayload(text, references);
  }

  /// <summary>Writes out a reference the table does not show, once per coordinate.</summary>
  private static string? Expansion(
    View view,
    Engine engine,
    FormulaReference reference,
    Presentation presentation,
    HashSet<string> path)
  {
    if (reference.Column is null)
    {
      return null;
    }

    var coordinate = new CellCoordinate(reference.Line, reference.Column);
    string key = Key(coordinate);
    if (presentation.Formulas.ContainsKey(key))
    {
      return key;
    }
    if (!path.Add(key))
    {
      return null;
    }

    try
    {
      ResolvedCell? cell = SafeCell(engine, coordinate);
      FormulaPayload? expansion = cell is null ? null
        : Formula(view, engine, cell.Formula, cell.Expression, cell.Scope ?? view.Scope, coordinate.Line,
          coordinate.Column, presentation, path);
      if (expansion is null)
      {
        return null;
      }
      presentation.Formulas[key] = expansion;
      return key;
    }
    finally
    {
      path.Remove(key);
    }
  }

  private static CalculationPayload Calculation(
    View view,
    Engine engine,
    Expr expression,
    string scope,
    string currentLine,
    string column,
    Presentation presentation)
  {
    var tokens = new List<CalculationTokenPayload>();
    var expansionPath = new HashSet<string> { Key(new CellCoordinate(currentLine, column)) };
    AppendCalculation(view, engine, expression, scope, currentLine, column, 0, false, '\0',
      presentation, expansionPath, tokens);
    return new CalculationPayload(tokens);
  }

  private static void AppendCalculation(
    View view,
    Engine engine,
    Expr expr,
    string scope,
    string currentLine,
    string? column,
    int parentPrecedence,
    bool rightOperand,
    char parentOperator,
    Presentation presentation,
    HashSet<string> expansionPath,
    List<CalculationTokenPayload> tokens)
  {
    if (expr is PriorExpr prior)
    {
      string? priorColumn = column is null ? null : view.Book.PriorPeriod(scope, column);
      int first = tokens.Count;
      AppendCalculation(view, engine, prior.Inner, scope, currentLine, priorColumn, parentPrecedence,
        rightOperand, parentOperator, presentation, expansionPath, tokens);
      if (column is not null && priorColumn is null)
      {
        for (int index = first; index < tokens.Count; index++)
        {
          if (tokens[index].Dependency is not null)
          {
            tokens[index] = tokens[index] with { Missing = $"no period before {view.ColumnLabel(column)}" };
          }
        }
      }
      return;
    }

    int precedence = Precedence(expr);
    bool parenthesize = precedence < parentPrecedence
      || rightOperand && precedence == parentPrecedence && parentOperator is '-' or '/'
      || !rightOperand && precedence == parentPrecedence && parentOperator == '^';
    if (parenthesize)
    {
      tokens.Add(new CalculationTokenPayload("(", null, null));
    }

    switch (expr)
    {
      case NumberExpr number:
        tokens.Add(new CalculationTokenPayload(Formatter.Exact(number.Value), null, null));
        break;

      case RefExpr reference:
        string dependency = view.Book.ResolveSelf(reference.Name, scope, currentLine) ?? reference.Name;
        tokens.Add(ReferenceToken(
          view,
          engine,
          dependency,
          view.Book.ReferenceColumn(reference.Column, column, scope, dependency),
          scope,
          presentation,
          expansionPath));
        break;

      case RangeExpr range:
        string rangeDependency = view.Book.ResolveSelf(range.Name, scope, currentLine) ?? range.Name;
        List<string> rangeColumns = view.Book.ColumnsInRange(
          rangeDependency, range.StartColumn, range.EndColumn);
        for (int index = 0; index < rangeColumns.Count; index++)
        {
          tokens.Add(ReferenceToken(view, engine, rangeDependency, rangeColumns[index], scope, presentation,
            expansionPath));
          if (index < rangeColumns.Count - 1)
          {
            tokens.Add(new CalculationTokenPayload(", ", null, null));
          }
        }
        break;

      case UnaryExpr unary:
        tokens.Add(new CalculationTokenPayload("−", null, null));
        AppendCalculation(view, engine, unary.Operand, scope, currentLine, column,
          precedence, false, unary.Op, presentation, expansionPath, tokens);
        break;

      case BinaryExpr binary:
        AppendCalculation(view, engine, binary.Left, scope, currentLine, column,
          precedence, false, binary.Op, presentation, expansionPath, tokens);
        tokens.Add(new CalculationTokenPayload($" {Operator(binary.Op)} ", null, null));
        AppendCalculation(view, engine, binary.Right, scope, currentLine, column,
          precedence, true, binary.Op, presentation, expansionPath, tokens);
        break;

      case ComparisonExpr comparison:
        AppendCalculation(view, engine, comparison.Left, scope, currentLine, column,
          precedence, false, '\0', presentation, expansionPath, tokens);
        tokens.Add(new CalculationTokenPayload($" {comparison.Op} ", null, null));
        AppendCalculation(view, engine, comparison.Right, scope, currentLine, column,
          precedence, true, '\0', presentation, expansionPath, tokens);
        break;

      case FunctionExpr function:
        tokens.Add(new CalculationTokenPayload($"{function.Name}(", null, null));
        if (function.Name == "if")
        {
          AppendCalculation(view, engine, function.Arguments[0], scope, currentLine, column,
            0, false, '\0', presentation, expansionPath, tokens);
          double? condition = column is null
            ? null
            : engine.Evaluate(function.Arguments[0], column, scope, currentLine);
          int selected = condition is null ? -1 : condition == 0 ? 2 : 1;
          for (int index = 1; index < function.Arguments.Count; index++)
          {
            tokens.Add(new CalculationTokenPayload(", ", null, null));
            if (index == selected)
            {
              AppendCalculation(view, engine, function.Arguments[index], scope, currentLine, column,
                0, false, '\0', presentation, expansionPath, tokens);
            }
            else
            {
              tokens.Add(new CalculationTokenPayload("not evaluated", null, null));
            }
          }
          tokens.Add(new CalculationTokenPayload(")", null, null));
          break;
        }
        for (int index = 0; index < function.Arguments.Count; index++)
        {
          AppendCalculation(view, engine, function.Arguments[index], scope, currentLine, column,
            0, false, '\0', presentation, expansionPath, tokens);
          if (index < function.Arguments.Count - 1)
          {
            tokens.Add(new CalculationTokenPayload(", ", null, null));
          }
        }
        tokens.Add(new CalculationTokenPayload(")", null, null));
        break;
    }

    if (parenthesize)
    {
      tokens.Add(new CalculationTokenPayload(")", null, null));
    }
  }

  /// <summary>One value in a written-out calculation; a cell the table does not show carries its own.</summary>
  private static CalculationTokenPayload ReferenceToken(
    View view,
    Engine engine,
    string dependency,
    string? column,
    string scope,
    Presentation presentation,
    HashSet<string> expansionPath)
  {
    ResolvedCell? cell = column is null ? null : SafeCell(engine, new CellCoordinate(dependency, column));
    string text = cell?.Value is null
      ? view.Formatter.Unresolved
      : CalculationValue(view, cell.Units, cell.Value.Value);
    bool shown = presentation.Shows(dependency, column);
    CalculationPayload? expansion = null;
    string? expansionId = null;
    if (!shown && column is not null && cell?.Expression is not null)
    {
      string key = Key(new CellCoordinate(dependency, column));
      if (!presentation.Calculations.TryGetValue(key, out CalculationPayload? built) && expansionPath.Add(key))
      {
        var expansionTokens = new List<CalculationTokenPayload>();
        AppendCalculation(view, engine, cell.Expression, cell.Scope ?? scope, dependency, column, 0, false,
          '\0', presentation, expansionPath, expansionTokens);
        built = new CalculationPayload(expansionTokens);
        presentation.Calculations[key] = built;
        expansionPath.Remove(key);
      }
      expansion = presentation.InlineExpansions ? built : null;
      expansionId = presentation.InlineExpansions || built is null ? null : key;
    }
    return new CalculationTokenPayload(
      text,
      dependency,
      column,
      column is null ? null : view.ColumnLabel(column),
      ReferenceView(view, dependency, column, scope, shown),
      expansion,
      expansionId,
      view.LabelOf(dependency));
  }

  private static int Precedence(Expr expr)
  {
    return expr switch
    {
      ComparisonExpr => 0,
      BinaryExpr { Op: '+' or '-' } => 1,
      BinaryExpr { Op: '*' or '/' } => 2,
      UnaryExpr => 3,
      BinaryExpr { Op: '^' } => 4,
      PriorExpr prior => Precedence(prior.Inner),
      _ => 5
    };
  }

  // An unused condition branch may name a cell that cannot resolve; checks report those and the page reads around them.
  private static ResolvedCell? SafeCell(Engine engine, CellCoordinate coordinate)
  {
    try
    {
      return engine.Cell(coordinate);
    }
    catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
    {
      return null;
    }
  }

  private static char Operator(char op)
  {
    return op switch
    {
      '*' => '×',
      '/' => '÷',
      '-' => '−',
      _ => op
    };
  }

  private static string CalculationValue(View view, string? units, double value)
  {
    string formatted = view.Formatter.Format(units, value);
    return value < 0 && formatted.StartsWith('(') && formatted.EndsWith(')')
      ? $"−{formatted[1..^1]}" : formatted;
  }

  /// <summary>What the table shows and what has been written out; only unreachable dependencies are expanded.</summary>
  private sealed class Presentation
  {
    public Presentation(IEnumerable<CellCoordinate> coordinates, bool inlineExpansions)
    {
      Coordinates = coordinates.ToHashSet();
      InlineExpansions = inlineExpansions;
    }

    public HashSet<CellCoordinate> Coordinates { get; }
    public bool InlineExpansions { get; }
    public Dictionary<string, CalculationPayload> Calculations { get; } =
      new Dictionary<string, CalculationPayload>();
    public Dictionary<string, FormulaPayload> Formulas { get; } = new Dictionary<string, FormulaPayload>();

    public bool Shows(string line, string? column)
    {
      return column is not null && Coordinates.Contains(new CellCoordinate(line, column));
    }

    public bool ShowsLine(string line)
    {
      return Coordinates.Any(coordinate => coordinate.Line == line);
    }


  }

  private readonly record struct LineDetail(
    string Units,
    string? SourceView,
    FormulaPayload? Formula,
    SourcePayload? Source,
    string? Note);

  private readonly record struct CellLink(bool Linked, string? SourceView, string? Line, string? Column);


}