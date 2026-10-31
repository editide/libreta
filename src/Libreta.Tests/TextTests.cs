using ClosedXML.Excel;
using Shouldly;
using Xunit;

namespace Libreta.Tests;

[Collection(ConsoleCollection.Name)]
public class TextTests
{
  [Fact]
  public void ResolvesATextFactAsPrinted()
  {
    // Arrange
    using var fixture = new TextFixture();
    View view = fixture.LoadView();

    // Act
    ResolvedCell cell = new Engine(view).Cell("moodys", "2025");
    CellPayload payload = PayloadBuilder.Build(view, new Engine(view), 1).Rows.First().Cells[1];

    // Assert
    cell.Text.ShouldBe("Ba2");
    cell.Value.ShouldBeNull();
    payload.Display.ShouldBe("Ba2");
    payload.Exact.ShouldBe("Ba2");
    payload.Text.ShouldBeTrue();
    payload.Unresolved.ShouldBeFalse();
  }

  [Fact]
  public void KeepsAPrintedDashInATextLine()
  {
    // Arrange
    using var fixture = new TextFixture();
    fixture.Write("ratings/facts/ratings.csv", "line_item,2024,2025\nmoodys,Ba3,Ba2\nfitch,—,BB\n");

    // Act
    ResolvedCell cell = new Engine(fixture.LoadView()).Cell("fitch", "2024");

    // Assert
    cell.Text.ShouldBe("—");
  }

  [Fact]
  public void KeepsANumberInTextUnitsAsPrinted()
  {
    // Arrange
    using var fixture = new TextFixture();
    fixture.Write("ratings/facts/ratings.csv", "line_item,2024,2025\nmoodys,Ba3,2.50\n");

    // Act
    ResolvedCell cell = new Engine(fixture.LoadView()).Cell("moodys", "2025");

    // Assert
    cell.Text.ShouldBe("2.50");
  }

  [Fact]
  public void RejectsTextInANumericLine()
  {
    // Arrange
    using var fixture = new TextFixture();
    fixture.Write("ratings/facts/ratings.yaml", "table:\n  title: Ratings\ndefaults:\n  units: millions\n");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => fixture.LoadView());

    // Assert
    exception.Message.ShouldContain("ratings.csv:2' has invalid value 'Ba3' for moodys[2024]");
  }

  [Fact]
  public void RejectsANumberInTextUnits()
  {
    // Arrange
    using var fixture = new TextFixture("""
      labelled:
        formula: revenue
        units: text
      """);
    var engine = new Engine(fixture.LoadView());

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => engine.Cell("labelled", "2025"));

    // Assert
    exception.Message.ShouldContain("'ratings.labelled[2025]' holds a number but its units 'text' format text");
  }

  [Theory]
  [InlineData("moodys")]
  [InlineData("revenue + moodys")]
  [InlineData("sum(moodys[\"2024\":\"2025\"])")]
  public void RejectsAFormulaReadingText(string formula)
  {
    // Arrange
    using var fixture = new TextFixture($"""
      misused:
        formula: {formula}
      """);
    var engine = new Engine(fixture.LoadView());

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => engine.Cell("misused", "2025"));

    // Assert
    exception.Message.ShouldContain("'ratings.moodys' holds text, which a formula cannot read");
  }

  [Fact]
  public void RejectsACheckReadingText()
  {
    // Arrange
    using var fixture = new TextFixture();
    fixture.Write("ratings/checks.yaml", "checks:\n  rated:\n    formula: moodys\n");
    var engine = new Engine(fixture.LoadView());

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => engine.RunChecks());

    // Assert
    exception.Message.ShouldContain("holds text, which a formula cannot read");
  }

  [Theory]
  [InlineData("    sign: contra\n", "gives text line moodys sign: contra")]
  [InlineData("    printed: negated\n", "gives text line moodys printed: negated")]
  [InlineData("    missing: zero\n", "gives text line moodys missing: zero")]
  public void RejectsANumericConventionOnATextLine(string lineItem, string message)
  {
    // Arrange
    using var fixture = new TextFixture();
    fixture.Write("ratings/facts/ratings.yaml",
      $"table:\n  title: Ratings\ndefaults:\n  units: text\nline_items:\n  moodys:\n{lineItem}");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => fixture.LoadView());

    // Assert
    exception.Message.ShouldContain(message);
  }

  [Fact]
  public void LeavesACalculatedColumnOffATextLine()
  {
    // Arrange
    using var fixture = new TextFixture();
    fixture.Write("ratings/formulas.yaml",
      "formulas: {}\ncolumn_formulas:\n  total:\n    formula: self[\"2024\"] + self[\"2025\"]\n    applies_to: facts\n");
    var engine = new Engine(fixture.LoadView());

    // Act
    ResolvedCell rating = engine.Cell("moodys", "total");
    ResolvedCell revenue = engine.Cell("revenue", "total");

    // Assert
    rating.Kind.ShouldBe("missing");
    rating.Value.ShouldBeNull();
    revenue.Value.ShouldBe(220);
  }

  [Fact]
  public void ListsATextLineWithItsText()
  {
    // Arrange
    using var fixture = new TextFixture();

    // Act
    (int exitCode, string output) = fixture.RunLines("ratings.moodys");

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldBe("ratings.moodys  2024 Ba3, 2025 Ba2\n", StringCompareShould.IgnoreLineEndings);
  }

  [Fact]
  public void ExportsATextLineAsText()
  {
    // Arrange
    using var fixture = new TextFixture();
    var stream = new MemoryStream();

    // Act
    XlsxExport.Write(fixture.Root, stream);
    using var workbook = new XLWorkbook(stream);

    // Assert
    IXLCell cell = workbook.Worksheet("Ratings").Search("Ba2").ShouldHaveSingleItem();
    cell.DataType.ShouldBe(XLDataType.Text);
  }

  private sealed class TextFixture : TempFolder
  {
    public TextFixture(string formulas = "")
    {
      Write("book.yaml", "name: Test\nshort_name: Test\nunits: millions\nnavigation: [ratings]\n");
      Write("formats.yaml", "formats:\n  millions:\n    decimals: 0\n");
      Write("ratings/facts/ratings.csv", "line_item,2024,2025\nmoodys,Ba3,Ba2\n");
      Write("ratings/facts/ratings.yaml", "table:\n  title: Ratings\ndefaults:\n  units: text\n");
      Write("ratings/facts/inputs.csv", "line_item,2024,2025\nrevenue,100,120\n");
      Write("ratings/facts/inputs.yaml", "table:\n  title: Inputs\ndefaults:\n  units: millions\n");
      Write("ratings/formulas.yaml", formulas.Length == 0 ? "formulas: {}" : $"formulas:\n{Indent(formulas)}");
      Write("ratings/view.yaml", "title: Ratings\ncolumns: [2024, 2025]\nrows:\n  - line: moodys\n  - line: revenue\n");
    }

    public View LoadView()
    {
      return View.Load(Book.Load(Root), PathTo("ratings"));
    }

    public (int ExitCode, string Output) RunLines(params string[] lines)
    {
      TextWriter original = Console.Out;
      using var writer = new StringWriter();
      try
      {
        Console.SetOut(writer);
        int exitCode = HeadlessRunner.Run(Root, HeadlessCommand.Lines, null, false, lines.ToList());
        return (exitCode, writer.ToString());
      }
      finally
      {
        Console.SetOut(original);
      }
    }

    private static string Indent(string text)
    {
      return string.Join('\n', text.Split('\n').Select(line => $"  {line}"));
    }


  }


}