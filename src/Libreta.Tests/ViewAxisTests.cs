using Shouldly;
using Xunit;

namespace Libreta.Tests;

[Collection(ConsoleCollection.Name)]
public class ViewAxisTests
{
  [Fact]
  public void TransposeExchangesAxesWithoutMovingCellCoordinates()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    ViewPayload upright = fixture.Payload("statement");
    fixture.Append("statement/view.yaml", "transpose: true\n");

    // Act
    ViewPayload transposed = fixture.Payload("statement");

    // Assert
    upright.Rows.Select(row => row.Label).ShouldBe(new[] { "Revenue", "Cost of revenue", "Gross profit" });
    upright.Columns.Select(column => column.Label).ShouldBe(new[] { "2024", "2025" });
    transposed.Rows.Select(row => row.Label).ShouldBe(new[] { "2024", "2025" });
    transposed.Columns.Select(column => column.Label)
      .ShouldBe(new[] { "Revenue", "Cost of revenue", "Gross profit" });
    transposed.Rows.Select(row => row.Kind).ShouldAllBe(kind => kind == "column");
    transposed.Columns.Select(column => column.Kind).ShouldBe(new[] { "fact", "fact", "formula" });
    for (int line = 0; line < 3; line++)
    {
      for (int column = 0; column < 2; column++)
      {
        CellPayload flat = upright.Rows[line].Cells[column];
        CellPayload turned = transposed.Rows[column].Cells[line];
        turned.Line.ShouldBe(flat.Line);
        turned.Column.ShouldBe(flat.Column);
        turned.Display.ShouldBe(flat.Display);
        turned.Contra.ShouldBe(flat.Contra);
      }
    }
  }

  [Fact]
  public void TransposeLeavesPriorAndRangeEvaluationOrderAlone()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    fixture.Append("statement/formulas.yaml", """
        growth:
          formula: revenue - prior(revenue)
        two_year:
          formula: sum(revenue["2024":"2025"])
      """);
    fixture.Write("statement/view.yaml", """
      title: Statement
      transpose: true
      columns: [2024, 2025]
      rows:
        - line: growth
        - line: two_year
      """);

    // Act
    ViewPayload payload = fixture.Payload("statement");

    // Assert
    payload.Rows[0].Cells[0].Unresolved.ShouldBeTrue();
    payload.Rows[1].Cells[0].Exact.ShouldBe("30");
    payload.Rows[0].Cells[1].Exact.ShouldBe("210");
    payload.Rows[1].Cells[1].Exact.ShouldBe("210");
  }

  [Fact]
  public void ViewColumnNoteFollowsTheColumnWhenTheTableIsTransposed()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    fixture.Write("statement/view.yaml", """
      title: Statement
      columns:
        - source: "2024"
          note: The first comparative period.
        - "2025"
      rows:
        - line: revenue
      """);

    // Act
    ViewPayload upright = fixture.Payload("statement");
    fixture.Append("statement/view.yaml", "transpose: true");
    ViewPayload transposed = fixture.Payload("statement");

    // Assert
    upright.Columns[0].Note.ShouldBe("The first comparative period.");
    transposed.Rows[0].Note.ShouldBe("The first comparative period.");
  }

  [Fact]
  public void ComparisonSelectsTheSameLineInsideEachIncludedBook()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison();

    // Act
    ViewPayload payload = fixture.Payload("comps");

    // Assert
    payload.Rows.Select(row => row.Label).ShouldBe(new[] { "Alpha", "BETA" });
    payload.Rows.Select(row => row.Kind).ShouldAllBe(kind => kind == "book");
    payload.Rows.Select(row => row.Name).ShouldBe(new[] { "alpha", "beta" });
    payload.Columns.Select(column => column.Label).ShouldBe(new[] { "Revenue", "Doubled" });
    payload.Rows[0].Cells[0].Line.ShouldBe("alpha::metrics.revenue");
    payload.Rows[1].Cells[0].Line.ShouldBe("beta::metrics.revenue");
    payload.Rows[0].Cells[0].Exact.ShouldBe("100");
    payload.Rows[1].Cells[0].Exact.ShouldBe("300");
    payload.Rows[0].Cells[1].Exact.ShouldBe("200");
    payload.Rows[1].Cells[1].Exact.ShouldBe("600");
    payload.Rows[0].Cells[1].Column.ShouldBe("alpha::metrics.double");
    payload.Rows[1].Cells[1].Column.ShouldBe("beta::metrics.double");
  }

  [Fact]
  public void ComparisonSummarizesItsBooksWithAFolderCarryingTheSameLines()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison();
    fixture.Write("comps/median/metrics/formulas.yaml", """
      formulas:
        revenue:
          formula: median(alpha::metrics.revenue["2025"], beta::metrics.revenue["2025"])
        doubled:
          formula: median(alpha::metrics.doubled["double"], beta::metrics.doubled["double"])
      """);
    fixture.Write("comps/view.yaml", """
      title: Comparison
      rows:
        - book: alpha
          label: Alpha
        - book: beta
        - space: compact
        - scope: median
          style: total
      columns:
        - line: metrics.revenue
          column: "2025"
          label: Revenue
        - line: metrics.doubled
          column: double
          label: Doubled
      """);

    // Act
    ViewPayload payload = fixture.Payload("comps");

    // Assert
    payload.Rows.Select(row => row.Kind).ShouldBe(new[] { "book", "book", "spacer", "scope" });
    payload.Rows[3].Label.ShouldBe("Median");
    payload.Rows[3].Cells[0].Line.ShouldBe("comps.median.metrics.revenue");
    payload.Rows[3].Cells[0].Exact.ShouldBe("200");
    payload.Rows[3].Cells[1].Exact.ShouldBe("400");
  }

  [Fact]
  public void LabelsPreferViewOverridesThenDefinitionsThenHumanizedIdentifiers()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", "name: Labels\nshort_name: Labels\nnavigation: [metrics]\n");
    fixture.Write("metrics/facts/income.csv", "line_item,2025\ngross_sales,100\nnet_sales,90\n");
    fixture.Write("metrics/facts/income.yaml", """
      table:
        title: Income
      defaults:
        units: millions
      line_items:
        net_sales:
          label: Source net sales
      """);
    fixture.Write("metrics/formulas.yaml", """
      formulas:
        operating_income:
          label: Operating profit
          formula: net_sales
        free_cash_flow:
          formula: operating_income
        ebitda:
          formula: operating_income
      column_formulas:
        last_twelve_months:
          formula: self["2025"]
        enterprise_value_to_revenue:
          label: EV / Revenue
          formula: self["2025"]
      """);
    fixture.Write("metrics/view.yaml", """
      title: Metrics
      columns: [2025, last_twelve_months, enterprise_value_to_revenue]
      rows:
        - line: gross_sales
        - line: net_sales
        - line: operating_income
        - line: free_cash_flow
        - line: ebitda
        - line: operating_income
          label: View operating income
      """);

    // Act
    ViewPayload payload = fixture.Payload("metrics");

    // Assert
    payload.Rows.Select(row => row.Label).ShouldBe(new[]
    {
      "Gross sales",
      "Source net sales",
      "Operating profit",
      "Free cash flow",
      "Ebitda",
      "View operating income"
    });
    payload.Columns.Select(column => column.Label)
      .ShouldBe(new[] { "2025", "Last twelve months", "EV / Revenue" });
  }

  [Fact]
  public void QualifiedLinesUseTheLabelOwnedByTheirDefinition()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", "name: Labels\nshort_name: Labels\nnavigation: [summary]\n");
    fixture.Write("metrics/facts/income.csv", "line_item,2025\ngross_sales,100\n");
    fixture.Write("metrics/facts/income.yaml", "table:\n  title: Income\ndefaults:\n  units: millions\n");
    fixture.Write("metrics/formulas.yaml", """
      formulas:
        free_cash_flow:
          label: Cash generation
          formula: gross_sales
        operating_income:
          formula: gross_sales
      """);
    fixture.Write("formulas.yaml", "formulas:\n  root_total:\n    formula: metrics.gross_sales\n");
    fixture.Write("summary/view.yaml", """
      title: Summary
      columns: [2025]
      rows:
        - line: metrics.gross_sales
        - line: metrics.free_cash_flow
        - line: metrics.operating_income
        - line: root_total
      """);

    // Act
    ViewPayload payload = fixture.Payload("summary");

    // Assert
    payload.Rows.Select(row => row.Label)
      .ShouldBe(new[] { "Gross sales", "Cash generation", "Operating income", "Root total" });
  }

  [Fact]
  public void TransposedComparisonKeepsEveryCellCoordinate()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison();
    ViewPayload upright = fixture.Payload("comps");
    fixture.Append("comps/view.yaml", "transpose: true\n");

    // Act
    ViewPayload transposed = fixture.Payload("comps");

    // Assert
    transposed.Rows.Select(row => row.Label).ShouldBe(new[] { "Revenue", "Doubled" });
    transposed.Columns.Select(column => column.Label).ShouldBe(new[] { "Alpha", "BETA" });
    transposed.Columns.Select(column => column.Kind).ShouldAllBe(kind => kind == "book");
    for (int book = 0; book < 2; book++)
    {
      for (int metric = 0; metric < 2; metric++)
      {
        transposed.Rows[metric].Cells[book].Line.ShouldBe(upright.Rows[book].Cells[metric].Line);
        transposed.Rows[metric].Cells[book].Column.ShouldBe(upright.Rows[book].Cells[metric].Column);
        transposed.Rows[metric].Cells[book].Exact.ShouldBe(upright.Rows[book].Cells[metric].Exact);
      }
    }
  }

  [Fact]
  public void ComparisonShowsOneLineAtTwoSourceColumnsWithoutCollapsingThem()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison("""
      columns:
        - line: metrics.revenue
          column: "2024"
          label: Prior revenue
        - line: metrics.revenue
          column: "2025"
          label: Revenue
      """);

    // Act
    ViewPayload payload = fixture.Payload("comps");

    // Assert
    payload.Rows[0].Cells[0].Column.ShouldBe("2024");
    payload.Rows[0].Cells[1].Column.ShouldBe("2025");
    payload.Rows[0].Cells[0].Exact.ShouldBe("90");
    payload.Rows[0].Cells[1].Exact.ShouldBe("100");
    payload.Rows[0].Cells.Select(cell => cell.Line).ShouldAllBe(line => line == "alpha::metrics.revenue");
  }

  [Fact]
  public void ComparisonLeavesALineOneBookLacksUnresolved()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison("""
      columns:
        - line: metrics.headcount
          column: "2025"
          label: Headcount
      """);
    fixture.Write("children/alpha/metrics/formulas.yaml", """
      formulas:
        headcount:
          formula: revenue / 2
      """);

    // Act
    ViewPayload payload = fixture.Payload("comps");

    // Assert
    payload.Rows[0].Cells[0].Exact.ShouldBe("50");
    payload.Rows[1].Cells[0].Unresolved.ShouldBeTrue();
    payload.Rows[1].Cells[0].Kind.ShouldBe("missing");
    payload.Rows[1].Cells[0].Line.ShouldBe("beta::metrics.headcount");
  }

  [Fact]
  public void ContraSignSkipsCellsInOtherUnitsThanTheirLine()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    fixture.Write("formats.yaml", "formats:\n  millions:\n    decimals: 0\n  percent:\n    scale: 100\n    decimals: 1\n");
    fixture.Write("statement/formulas.yaml", """
      column_formulas:
        growth:
          formula: self["2025"] / self["2024"] - 1
          units: percent
        change:
          formula: self["2025"] - self["2024"]
      """);
    fixture.Write("statement/view.yaml", """
      title: Statement
      columns: [2025, growth, change]
      rows:
        - line: cost_of_revenue
      """);

    // Act
    List<CellPayload> cells = fixture.Payload("statement").Rows[0].Cells;

    // Assert
    cells[0].Display.ShouldBe("(50)");
    cells[0].Exact.ShouldBe("50");
    cells[0].DisplayExact.ShouldBe("-50");
    cells[1].Contra.ShouldBeFalse();
    cells[1].Display.ShouldBe("25.0");
    cells[1].DisplayExact.ShouldBeNull();
    cells[2].Contra.ShouldBeTrue();
    cells[2].Display.ShouldBe("(10)");
    cells[2].Exact.ShouldBe("10");
    cells[2].DisplayExact.ShouldBe("-10");
  }

  [Fact]
  public void DisplayExactNegatesNonZeroContraValuesOnly()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", "name: Test\nshort_name: TEST\nnavigation: [statement]\n");
    fixture.Write("statement/facts/numbers.csv", "line_item,2024,2025,2026\nexpense,50,-20,0\nrevenue,100,100,100\n");
    fixture.Write("statement/facts/numbers.yaml", """
      table:
        title: Numbers
      defaults:
        units: millions
      line_items:
        expense:
          label: Expense
          sign: contra
        revenue:
          label: Revenue
      """);
    fixture.Write("statement/view.yaml", """
      title: Statement
      columns: [2024, 2025, 2026]
      rows:
        - line: expense
        - line: revenue
      """);

    // Act
    ViewPayload payload = fixture.Payload("statement");
    List<CellPayload> expense = payload.Rows[0].Cells;
    List<CellPayload> revenue = payload.Rows[1].Cells;

    // Assert
    expense[0].Exact.ShouldBe("50");
    expense[0].DisplayExact.ShouldBe("-50");
    expense[1].Exact.ShouldBe("-20");
    expense[1].DisplayExact.ShouldBe("20");
    expense[2].Exact.ShouldBe("0");
    expense[2].DisplayExact.ShouldBeNull();
    revenue[0].Exact.ShouldBe("100");
    revenue[0].DisplayExact.ShouldBeNull();
  }

  [Fact]
  public void SignOverrideFollowsItsLineWhenTheAxisMoves()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    fixture.Write("statement/view.yaml", """
      title: Statement
      transpose: true
      columns: [2025]
      rows:
        - line: revenue
          sign: contra
        - line: cost_of_revenue
          sign: additive
      """);

    // Act
    ViewPayload payload = fixture.Payload("statement");

    // Assert
    payload.Columns[0].Label.ShouldBe("Revenue");
    payload.Rows[0].Cells[0].Contra.ShouldBeTrue();
    payload.Rows[0].Cells[0].Display.ShouldBe("(120)");
    payload.Rows[0].Cells[1].Contra.ShouldBeFalse();
    payload.Rows[0].Cells[1].Display.ShouldBe("50");
  }

  [Theory]
  [InlineData(
    "title: T\ntranspose: true\ncolumns: [2025]\nrows:\n  - line: revenue\n  - space: compact",
    "spacer")]
  [InlineData(
    "title: T\ncolumns: [2025]\nrows:\n  - book: missing",
    "unknown included book")]
  [InlineData(
    "title: T\ncolumns: [2025]\nrows:\n  - book: alpha\n  - line: revenue",
    "every row must name a book")]
  [InlineData(
    "title: T\ncolumns:\n  - line: metrics.revenue\nrows:\n  - book: alpha",
    "must name a column")]
  [InlineData(
    "title: T\ncolumns:\n  - line: metrics.revenue\n    column: \"2025\"\nrows:\n  - line: revenue",
    "only a view whose rows name books")]
  public void RejectsAxisCombinationsItCannotInterpret(string view, string expected)
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison();
    fixture.Write("comps/view.yaml", view);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => fixture.Payload("comps"));

    // Assert
    exception.Message.ShouldContain(expected, Case.Insensitive);
  }

  [Fact]
  public void ALineDrawnDownAColumnCarriesWhatItsRowWouldHave()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    ViewPayload upright = fixture.Payload("statement");
    fixture.Append("statement/view.yaml", "transpose: true\n");

    // Act
    ViewPayload transposed = fixture.Payload("statement");

    // Assert
    transposed.Columns[2].Formula!.Text.ShouldBe(upright.Rows[2].Formula!.Text);
    transposed.Columns[2].Formula!.References.Select(reference => reference.Line)
      .ShouldBe(upright.Rows[2].Formula!.References.Select(reference => reference.Line));
    transposed.Columns[1].Source!.Table.ShouldBe(upright.Rows[1].Source!.Table);
  }

  [Fact]
  public void AComparisonMetricNamesNoSingleFormulaBecauseEachBookHasItsOwn()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison();

    // Act
    ViewPayload payload = fixture.Payload("comps");

    // Assert
    payload.Columns[1].Kind.ShouldBe("metric");
    payload.Columns[1].Formula.ShouldBeNull();
    payload.Rows[0].Cells[1].Formula!.Text.ShouldBe("self[\"2025\"]");
  }

  [Fact]
  public void ValueAddressingIgnoresDisplayOrientation()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison();
    fixture.Append("comps/view.yaml", "transpose: true\n");

    // Act
    (int exitCode, string output) = fixture.Value("comps", "beta::metrics.revenue", "2025");

    // Assert
    exitCode.ShouldBe(0);
    output.Trim().ShouldBe("300");
  }

  [Fact]
  public void ViewOutputFollowsTheDisplayedOrientation()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison();
    fixture.Append("comps/view.yaml", "transpose: true\n");

    // Act
    (int exitCode, string output) = fixture.Table("comps");

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldContain("| Alpha | BETA |");
    output.ShouldContain("| 100 | 300 |");
  }

  [Fact]
  public void ChecksValidateEveryCoordinateAComparisonDisplays()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteComparison("""
      columns:
        - line: metrics.nowhere
          column: "2025"
          label: Nowhere
      """);

    // Act
    (int exitCode, string output) = fixture.Check();

    // Assert
    exitCode.ShouldBe(1);
    output.ShouldContain("missing line: alpha::metrics.nowhere");
    output.ShouldContain("missing line: beta::metrics.nowhere");
  }


}