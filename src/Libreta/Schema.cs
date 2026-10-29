namespace Libreta;

public class BookFile
{
  public string Name { get; set; } = "";
  public string ShortName { get; set; } = "";
  public string Description { get; set; } = "";
  public string Units { get; set; } = "";
  public string Currency { get; set; } = "";
  public string FiscalYearEnd { get; set; } = "";
  public Dictionary<string, string> Identifiers { get; set; } = new Dictionary<string, string>();
  public List<NavigationItem> Navigation { get; set; } = new List<NavigationItem>();
  public List<BookInclusion> Books { get; set; } = new List<BookInclusion>();
}

public class NavigationItem
{
  public string View { get; set; } = "";
  public string Book { get; set; } = "";
  public List<NavigationItem> Children { get; set; } = new List<NavigationItem>();
}

public class BookInclusion
{
  public string Id { get; set; } = "";
  public string Path { get; set; } = "";
}

public class FactTableFile
{
  public TableInfo Table { get; set; } = new TableInfo();
  public SourceInfo Source { get; set; } = new SourceInfo();
  public Defaults Defaults { get; set; } = new Defaults();
  public Dictionary<string, LineItem> LineItems { get; set; } = new Dictionary<string, LineItem>();
}

public class SourceInfo
{
  public string Url { get; set; } = "";
  public string Image { get; set; } = "";
  public string Note { get; set; } = "";
  public Dictionary<string, string> Details { get; set; } = new Dictionary<string, string>();
}

public class TableInfo
{
  public string Title { get; set; } = "";
  public string FiscalYearEnd { get; set; } = "";
}

public class Defaults
{
  public string Units { get; set; } = "";
  public string Currency { get; set; } = "";
  public string Printed { get; set; } = "as_is";
  public string Sign { get; set; } = "additive";
  public string Missing { get; set; } = "blank";
}

public class LineItem
{
  public string Label { get; set; } = "";
  public string? Units { get; set; }
  public string? Printed { get; set; }
  public string? Sign { get; set; }
  public string? Missing { get; set; }
  public string? Note { get; set; }
  public Dictionary<string, string> CellNotes { get; set; } = new Dictionary<string, string>();
  public Dictionary<string, SourceRegion> ImageRegions { get; set; } = new Dictionary<string, SourceRegion>();
}

public class SourceRegion
{
  public int X { get; set; }
  public int Y { get; set; }
  public int Width { get; set; }
  public int Height { get; set; }
}

public class FormulasFile
{
  public Dictionary<string, FormulaDefinition> Formulas { get; set; } = new Dictionary<string, FormulaDefinition>();
  public Dictionary<string, ColumnFormulaDefinition> ColumnFormulas { get; set; } =
    new Dictionary<string, ColumnFormulaDefinition>();
  public Dictionary<string, Dictionary<string, CellOverrideDefinition>> CellOverrides { get; set; } =
    new Dictionary<string, Dictionary<string, CellOverrideDefinition>>();
}

public class FormulaDefinition
{
  public string Label { get; set; } = "";
  public string Formula { get; set; } = "";
  public string? Units { get; set; }
  public string? Note { get; set; }
  public Dictionary<string, string> CellNotes { get; set; } = new Dictionary<string, string>();
}

public class ColumnFormulaDefinition
{
  public string Label { get; set; } = "";
  public string Formula { get; set; } = "";
  public string? Units { get; set; }
  public string? Note { get; set; }
  public string? AppliesTo { get; set; }
}

public class CellOverrideDefinition
{
  public double? Value { get; set; }
  public string? Formula { get; set; }
  public string? Units { get; set; }
  public bool Blank { get; set; }
  public string? Note { get; set; }
}

public class ChecksFile
{
  public Dictionary<string, CheckDefinition> Checks { get; set; } = new Dictionary<string, CheckDefinition>();
}

public class CheckDefinition
{
  public string Formula { get; set; } = "";
  public double Expect { get; set; }
  public double Tolerance { get; set; }
  public string? Note { get; set; }
}

public class ViewFile
{
  public string Title { get; set; } = "";
  public string Subtitle { get; set; } = "";
  public bool Transpose { get; set; }
  public List<ViewColumn> Columns { get; set; } = new List<ViewColumn>();
  public List<ViewRow> Rows { get; set; } = new List<ViewRow>();
  public List<string> Sensitivities { get; set; } = new List<string>();
}

public class SensitivitiesFile
{
  public Dictionary<string, SensitivityDefinition> Sensitivities { get; set; } =
    new Dictionary<string, SensitivityDefinition>();
}

public class SensitivityDefinition
{
  public string Title { get; set; } = "";
  public SensitivityCoordinate Output { get; set; } = new SensitivityCoordinate();
  public List<SensitivityInputDefinition> Inputs { get; set; } = new List<SensitivityInputDefinition>();
  public string? Valid { get; set; }
}

public class SensitivityCoordinate
{
  public string Line { get; set; } = "";
  public string Column { get; set; } = "";
  public string Label { get; set; } = "";
}

public class SensitivityInputDefinition : SensitivityCoordinate
{
  public List<double> Values { get; set; } = new List<double>();
}

public class FormatsFile
{
  public string? DefaultUnits { get; set; }
  public FormatSpec Plain { get; set; } = new FormatSpec();
  public string Unresolved { get; set; } = "—";
  public string Dash { get; set; } = "-";
  public string? Zero { get; set; }
  public Dictionary<string, FormatSpec> Formats { get; set; } = new Dictionary<string, FormatSpec>();
}

public class FormatSpec
{
  public double Scale { get; set; } = 1;
  public int Decimals { get; set; }
  public bool Separator { get; set; } = true;
  public string Prefix { get; set; } = "";
  public string Suffix { get; set; } = "";
  public string Negative { get; set; } = "parentheses";
  public string? Date { get; set; }
  public bool Text { get; set; }
}

public class ViewRow
{
  public string? Line { get; set; }
  public string? Book { get; set; }
  public string? Scope { get; set; }
  public string? Label { get; set; }
  public string? Style { get; set; }
  public string? Sign { get; set; }
  public int Indent { get; set; }
  public string? Space { get; set; }
}

/// <summary>A column is written either as a bare source column — a period or a calculated column — or as a
/// mapping naming a line and the source column to read it at, which is what a cross-book comparison selects.</summary>
public class ViewColumn
{
  public string? Source { get; set; }
  public string? Line { get; set; }
  public string? Column { get; set; }
  public string? Label { get; set; }
  public string? Style { get; set; }
  public string? Sign { get; set; }
  public string? Note { get; set; }
}