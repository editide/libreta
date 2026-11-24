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
  public bool Text { get; init; }

  public bool Rounded { get; init; }

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
  string? Note,
  FormulaPayload? Expression = null);

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

public record ViewTableColumnPayload(string Name, string Label);

public record ViewTableCellPayload(string Line, string Column);

public record ViewTableRowPayload
{
  public string? Name { get; init; }
  public string? Label { get; init; }
  public string Kind { get; init; } = "";
  public string? Style { get; init; }
  public int? Indent { get; init; }
  public string? Units { get; init; }
  public List<string?>? Exact { get; init; }
  public List<string>? Display { get; init; }
  public List<bool>? Unresolved { get; init; }
  /// <summary>Per cell, whether the view shows it negated; <c>Exact</c> stays in natural direction.</summary>
  public List<bool>? Contra { get; init; }
  /// <summary>Per cell, the book cell it shows, given only when one isn't the row's line in its column's period, as
  /// in a comparison, whose rows are books, or a transposed view.</summary>
  public List<ViewTableCellPayload>? Cells { get; init; }
}

public record ViewTablePayload(
  string Id,
  string Title,
  string? Subtitle,
  List<ViewTableColumnPayload> Columns,
  List<ViewTableRowPayload> Rows,
  List<CheckPayload> Checks,
  List<SensitivityPayload> Sensitivities);

/// <summary>A cell another reads or is read by. <c>View</c> names a view presenting it when this one does not; an input
/// this view does not show carries its own calculation, saving a second <c>--value</c>.</summary>
public record ValueCellPayload
{
  public string Line { get; init; } = "";
  public string? Column { get; init; }
  public string? Label { get; init; }
  public string? Display { get; init; }
  public string? View { get; init; }
  public string? Missing { get; init; }
  public string? Formula { get; init; }
  public string? Calculation { get; init; }
  public List<ValueCellPayload>? Inputs { get; init; }
}

public record ValueSourcePayload(
  string Table, string Url, string Image, Dictionary<string, string> Details, string? Note);

public record ValuePayload
{
  public string View { get; init; } = "";
  public string Line { get; init; } = "";
  public string Column { get; init; } = "";
  public string? Label { get; init; }
  public string? ColumnLabel { get; init; }
  public string Kind { get; init; } = "";
  public string? Units { get; init; }
  public string? Value { get; init; }
  public string Display { get; init; } = "";
  public bool? Unresolved { get; init; }
  public bool? Contra { get; init; }
  public string? Formula { get; init; }
  public string? Calculation { get; init; }
  public List<ValueCellPayload>? Inputs { get; init; }
  public string? Note { get; init; }
  public ValueSourcePayload? Source { get; init; }
  public List<ValueCellPayload>? Dependents { get; init; }
}

/// <summary>A line as a caller outside Libreta reads it, over every period its scope has, in natural direction even
/// when <c>Contra</c> has views show it negated.</summary>
public record LinePayload
{
  public string Name { get; init; } = "";
  public string? Error { get; init; }
  public string? Label { get; init; }
  public string? Kind { get; init; }
  public string? Units { get; init; }
  public bool? Contra { get; init; }
  public List<string>? Columns { get; init; }
  public List<string?>? Exact { get; init; }
  public List<string>? Display { get; init; }
  public List<bool>? Unresolved { get; init; }
}
