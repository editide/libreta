namespace Libreta;

public class Fact
{
  public string Name { get; set; } = "";
  public string Scope { get; set; } = "";
  public string Label { get; set; } = "";
  public string? Units { get; set; }

  /// <summary>The line runs against its subtotal: an expense or a contra account. Stored in natural direction; only
  /// the rendered cell is negated.</summary>
  public bool Contra { get; set; }

  /// <summary>An empty period cell means the item did not occur, so it resolves to zero rather than unresolved.
  /// Calculated columns still apply, so a bridge sums the zeros rather than being replaced by them.</summary>
  public bool MissingZero { get; set; }
  /// <summary>Its CSV cells are ISO dates, held as day serials.</summary>
  public bool Date { get; set; }
  /// <summary>Its CSV cells are labels such as credit ratings, which no formula reads.</summary>
  public bool Text { get; set; }
  /// <summary>One per fragment that wrote a distinct note, in load order.</summary>
  public List<string> Notes { get; set; } = new List<string>();
  public Dictionary<string, FactCell> Cells { get; set; } = new Dictionary<string, FactCell>();
}

public class FactCell
{
  public double Value { get; set; }
  /// <summary>The CSV cell as written, which is what its source region should show.</summary>
  public string Printed { get; set; } = "";
  /// <summary>The cell as printed when its line holds text, in which case <see cref="Value"/> means nothing.</summary>
  public string? Text { get; set; }
  public bool PrintedDash { get; set; }
  public string? Note { get; set; }
  /// <summary>The note on the line in the fact table this cell came from.</summary>
  public string? LineNote { get; set; }
  public string TableTitle { get; set; } = "";
  public SourceInfo Source { get; set; } = new SourceInfo();
  public string SourceImage { get; set; } = "";
  public SourceRegion? SourceRegion { get; set; }
  public string SourcePath { get; set; } = "";
  /// <summary>The line as its table names it, the key its <c>line_items</c> entry uses.</summary>
  public string SourceLine { get; set; } = "";
}

public class Formula
{
  public string Name { get; set; } = "";
  public string Label { get; set; } = "";
  public string Scope { get; set; } = "";
  public Expr Expression { get; set; } = null!;
  public string Text { get; set; } = "";
  public string? Units { get; set; }
  public string? Note { get; set; }
  /// <summary>The book means a pinned formula to show its one value under every column, as a level line does.</summary>
  public bool Flat { get; set; }
  public Dictionary<string, string> CellNotes { get; set; } = new Dictionary<string, string>();
}

public class ColumnFormula
{
  public string Name { get; set; } = "";
  /// <summary>The name as written in its file, which a reference resolves from any scope it covers.</summary>
  public string Key { get; set; } = "";
  public string Label { get; set; } = "";
  public string Scope { get; set; } = "";
  public Expr Expression { get; set; } = null!;
  public string Text { get; set; } = "";
  public string? Units { get; set; }
  public string? Note { get; set; }
  public bool FactsOnly { get; set; }
}

public class CellOverride
{
  public string Line { get; set; } = "";
  public string Column { get; set; } = "";
  public string Scope { get; set; } = "";
  public double? Value { get; set; }
  public Expr? Expression { get; set; }
  public string? Text { get; set; }
  public string? Units { get; set; }
  public bool Blank { get; set; }
  public string? Note { get; set; }
}

public class Check
{
  public string Name { get; set; } = "";
  public string Scope { get; set; } = "";
  public Expr Expression { get; set; } = null!;
  public string Text { get; set; } = "";
  public double Expect { get; set; }
  public double Tolerance { get; set; }
  public string? Note { get; set; }
}

public class Sensitivity
{
  public string Name { get; set; } = "";
  public string Title { get; set; } = "";
  public string Scope { get; set; } = "";
  public SensitivityCoordinate Output { get; set; } = new SensitivityCoordinate();
  public List<SensitivityInputDefinition> Inputs { get; set; } = new List<SensitivityInputDefinition>();
  public Expr? Valid { get; set; }
}
