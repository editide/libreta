using System.Text.Json;
using Shouldly;
using Xunit;

namespace Libreta.Tests;

[Collection(ConsoleCollection.Name)]
public class CommandLineTests
{
  public static IEnumerable<object[]> BrokenFormulaCases =>
  [
    [
      """
      formulas:
        result:
          formula: result + 1
      """,
      """
      title: Review
      columns: [2024, 2025]
      rows:
        - line: result
      """,
      "Circular reference",
      "v.result[2024]"
    ],
    [
      """
      formulas:
        result:
          formula: intermediate + 1
        intermediate:
          formula: typo + 1
      """,
      """
      title: Review
      columns: [2024, 2025]
      rows:
        - line: result
      """,
      "typo",
      "v.result[2024]"
    ],
    [
      """
      formulas:
        result:
          formula: 1
      column_formulas:
        ratio:
          formula: typo + 1
      """,
      """
      title: Review
      columns: [2024, ratio]
      rows:
        - line: result
      """,
      "typo",
      "v.result[v.ratio]"
    ],
    [
      """
      formulas:
        result:
          formula: 1
      cell_overrides:
        result:
          2025:
            formula: typo + 1
      """,
      """
      title: Review
      columns: [2024, 2025]
      rows:
        - line: result
      """,
      "typo",
      "v.result[2025]"
    ],
    [
      """
      formulas:
        result:
          formula: if(1, 7, broken)
        broken:
          formula: sum(result["missing":"2025"])
      """,
      """
      title: Review
      columns: [2024, 2025]
      rows:
        - line: result
        - line: broken
      """,
      "unknown column",
      "v.broken[2024]"
    ]
  ];

  [Fact]
  public void ParsesEachHeadlessCommand()
  {
    // Arrange
    string[] list = ["books/spacex", "--list"];
    string[] view = ["books/spacex", "--view", "dcf/wacc", "--json"];
    string[] value = ["books/spacex", "--value", "dcf/wacc", "beta", "2025"];
    string[] check = ["books/spacex", "--check"];

    // Act
    CommandLineOptions listed = CommandLineOptions.Parse(list);
    CommandLineOptions viewed = CommandLineOptions.Parse(view);
    CommandLineOptions valued = CommandLineOptions.Parse(value);
    CommandLineOptions checkOnly = CommandLineOptions.Parse(check);

    // Assert
    listed.Command.ShouldBe(HeadlessCommand.List);
    listed.Query.ShouldBeNull();
    viewed.Command.ShouldBe(HeadlessCommand.View);
    viewed.Query.ShouldBe(new ViewQuery("dcf/wacc"));
    viewed.Json.ShouldBeTrue();
    valued.Command.ShouldBe(HeadlessCommand.Value);
    valued.Query.ShouldBe(new ViewQuery("dcf/wacc", "beta", "2025"));
    checkOnly.Command.ShouldBe(HeadlessCommand.Check);
  }

  [Fact]
  public void ParsesLineNamesUpToTheNextOption()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--lines", "historical.segments.revenue", "dcf.wacc", "--json"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Command.ShouldBe(HeadlessCommand.Lines);
    options.Lines.ShouldBe(["historical.segments.revenue", "dcf.wacc"]);
    options.Json.ShouldBeTrue();
  }

  [Fact]
  public void ParsesTheCellALaunchOpensAt()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--cell", "historical.segments.ai_revenue", "H1 2026"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Command.ShouldBeNull();
    options.Cell.ShouldBe(("historical.segments.ai_revenue", "H1 2026"));
  }

  [Fact]
  public void ParsesACellForAToolThatOpensThePageItself()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--cell", "historical.segments.ai_revenue", "2025", "--no-open", "--json"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Cell.ShouldBe(("historical.segments.ai_revenue", "2025"));
    options.NoOpen.ShouldBeTrue();
    options.Json.ShouldBeTrue();
  }

  [Fact]
  public void NoOpenCannotCombineWithAHeadlessCommand()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--check", "--no-open"];

    // Act
    ArgumentException exception = Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(arguments));

    // Assert
    exception.Message.ShouldBe("--no-open leaves the server's page to the caller, so it cannot be combined with a " +
      "headless command.");
  }

  [Fact]
  public void CellCannotCombineWithAHeadlessCommand()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--cell", "historical.segments.ai_revenue", "2025", "--list"];

    // Act
    ArgumentException exception = Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(arguments));

    // Assert
    exception.Message.ShouldBe("--cell opens the page, so it cannot be combined with a headless command.");
  }

  [Fact]
  public void ACellLinkOpensTheViewPresentingItAndSelectsIt()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();

    // Act
    string query = CellLink.Query(fixture.Root, "statement.gross_profit", "2025");

    // Assert
    query.ShouldBe("/?view=statement&line=statement.gross_profit&column=2025");
  }

  [Fact]
  public void ACellLinkResolvesInABookAlreadyLoaded()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    Book book = Book.Load(fixture.Root);

    // Act
    RequestedCell cell = CellLink.Resolve(book, "statement.gross_profit", "2025");

    // Assert
    cell.ShouldBe(new RequestedCell("statement", "statement.gross_profit", "2025"));
  }

  [Fact]
  public void ACellLinkToALineNoViewPresentsOpensTheLineOnItsOwn()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    fixture.Write("statement/formulas.yaml", """
      formulas:
        gross_profit:
          formula: revenue - cost_of_revenue
        gross_margin:
          label: Gross margin
          formula: gross_profit / revenue
      """);
    Book book = Book.Load(fixture.Root);

    // Act
    RequestedCell cell = CellLink.Resolve(book, "statement.gross_margin", "2025");

    // Assert
    cell.ShouldBe(new RequestedCell("line:statement.gross_margin", "statement.gross_margin", "2025"));
  }

  [Fact]
  public void ALineOnItsOwnShowsItAboveTheLinesItReads()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();
    fixture.Write("statement/formulas.yaml", """
      formulas:
        gross_profit:
          formula: revenue - cost_of_revenue
        gross_margin:
          label: Gross margin
          formula: gross_profit / revenue
      """);
    Book book = Book.Load(fixture.Root);

    // Act
    Libreta.View view = Libreta.View.ForLine(book, "statement.gross_margin");
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);

    // Assert
    view.Id.ShouldBe("line:statement.gross_margin");
    payload.Title.ShouldBe("Gross margin");
    payload.Columns.Select(column => column.Name).ShouldBe(["2024", "2025"]);
    payload.Rows.Select(row => row.Name)
      .ShouldBe(["statement.gross_margin", "", "statement.gross_profit", "statement.revenue"]);
    payload.Rows[1].Label.ShouldBe("Reads");
  }

  [Fact]
  public void ACellLinkToAnUnknownLineFails()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.WriteStatement();

    // Act
    ArgumentException exception = Should.Throw<ArgumentException>(
      () => CellLink.Query(fixture.Root, "statement.net_income", "2025"));

    // Assert
    exception.Message.ShouldBe("Unknown line 'statement.net_income'.");
  }

  [Fact]
  public void ParsesLinesWithNoNames()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--lines", "--json"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Command.ShouldBe(HeadlessCommand.Lines);
    options.Lines.ShouldBeEmpty();
    options.Json.ShouldBeTrue();
  }

  [Theory]
  [InlineData("--docs", DocumentationTopic.Index)]
  [InlineData("--docs format", DocumentationTopic.Format)]
  [InlineData("--docs running", DocumentationTopic.Running)]
  public void ParsesDocumentationCommands(string commandLine, DocumentationTopic topic)
  {
    // Arrange
    string[] arguments = commandLine.Split(' ');

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Documentation.ShouldBe(topic);
    options.Root.ShouldBeNull();
  }

  [Theory]
  [InlineData("--docs unknown")]
  [InlineData("books/spacex --docs")]
  [InlineData("--docs --check")]
  [InlineData("--docs --json")]
  public void RejectsInvalidDocumentationCommands(string commandLine)
  {
    // Arrange
    string[] arguments = commandLine.Split(' ');

    // Act, Assert
    Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(arguments));
  }

  [Theory]
  [InlineData(DocumentationTopic.Format, "# Book format", "## Project structure")]
  [InlineData(DocumentationTopic.Running, "# Running Libreta", "## Preview a book")]
  public void PrintsPackagedDocumentation(
    DocumentationTopic topic,
    string expectedHeading,
    string expectedSection)
  {
    // Arrange
    using var writer = new StringWriter();

    // Act
    Documentation.Write(topic, writer);

    // Assert
    string output = writer.ToString();
    output.ShouldContain(expectedHeading);
    output.ShouldContain(expectedSection);
  }

  [Fact]
  public void ServesWhenNoHeadlessCommandIsGiven()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--port", "5200"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Command.ShouldBeNull();
    options.Root.ShouldBe("books/spacex");
    options.Port.ShouldBe(5200);
  }

  [Fact]
  public void ParsesAnExport()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--export", "spacex.html"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Command.ShouldBe(HeadlessCommand.Export);
    options.Output.ShouldBe("spacex.html");
    options.Root.ShouldBe("books/spacex");
  }

  [Fact]
  public void ParsesAWorkbookExport()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--export", "spacex.XLSX"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Command.ShouldBe(HeadlessCommand.Export);
    options.Output.ShouldBe("spacex.XLSX");
  }

  [Theory]
  [InlineData("books/spacex --export")]
  [InlineData("books/spacex --export spacex.csv")]
  [InlineData("books/spacex --export spacex")]
  [InlineData("books/spacex --export spacex.html --json")]
  [InlineData("books/spacex --export spacex.html --check")]
  [InlineData("books/spacex --export spacex.html --port 5200")]
  public void RejectsIncoherentExports(string commandLine)
  {
    // Arrange
    string[] arguments = commandLine.Split(' ');

    // Act, Assert
    Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(arguments));
  }

  [Fact]
  public void ParsesAUrlLookup()
  {
    // Arrange
    string[] arguments = ["books/spacex", "--url"];

    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments);

    // Assert
    options.Command.ShouldBe(HeadlessCommand.Url);
    options.Root.ShouldBe("books/spacex");
  }

  [Theory]
  [InlineData("books/spacex --url --json")]
  [InlineData("books/spacex --url --port 5200")]
  [InlineData("books/spacex --url --check")]
  public void RejectsIncoherentUrlLookups(string commandLine)
  {
    // Arrange
    string[] arguments = commandLine.Split(' ');

    // Act, Assert
    Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(arguments));
  }

  [Theory]
  [InlineData("books/spacex --check --list")]
  [InlineData("books/spacex --check --port 5200")]
  [InlineData("books/spacex --json")]
  [InlineData("books/spacex --value dcf/wacc beta")]
  public void RejectsIncoherentArguments(string commandLine)
  {
    // Arrange
    string[] arguments = commandLine.Split(' ');

    // Act, Assert
    Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(arguments));
  }

  [Fact]
  public void LeavesTheRootUnsetWhenNoneIsGiven()
  {
    // Arrange
    string[] bare = [];
    string[] checkOnly = ["--check"];

    // Act
    CommandLineOptions served = CommandLineOptions.Parse(bare);
    CommandLineOptions checked_ = CommandLineOptions.Parse(checkOnly);

    // Assert
    served.Root.ShouldBeNull();
    served.Command.ShouldBeNull();
    checked_.Root.ShouldBeNull();
    checked_.Command.ShouldBe(HeadlessCommand.Check);
  }

  [Theory]
  [InlineData(new[] { "books/spacex" }, new[] { "books/spacex" })]
  [InlineData(new[] { "" }, new[] { "" })]
  [InlineData(new[] { "books/spacex", "books/spacex/historical" }, new[] { "books/spacex" })]
  [InlineData(new[] { "books/spacex", "books/acme" }, new[] { "books/acme", "books/spacex" })]
  [InlineData(new[] { "one/two/three" }, new[] { "one/two/three" })]
  [InlineData(new[] { "one/two/three/four" }, new string[] { })]
  [InlineData(new[] { "bin/spacex", "obj/spacex", ".git/spacex" }, new string[] { })]
  public void FindsEveryBookWithinThreeFoldersWithoutDescendingIntoOne(string[] books, string[] expected)
  {
    // Arrange
    using TreeFixture tree = new(books);

    // Act
    List<string> found = BookDiscovery.Find(tree.Root);

    // Assert
    found.ShouldBe(expected.Select(tree.Folder).ToList());
  }

  [Theory]
  [MemberData(nameof(BrokenFormulaCases))]
  public void CheckRejectsBrokenPresentedFormulaGraphs(
    string formulas,
    string view,
    string expectedDefect,
    string expectedCoordinate)
  {
    // Arrange
    using CheckBookFixture fixture = new(formulas, view);

    // Act
    (int exitCode, string output) = RunCheck(fixture.Root, false);

    // Assert
    exitCode.ShouldBe(1);
    output.ShouldContain(expectedDefect);
    output.ShouldContain(expectedCoordinate);
  }

  [Fact]
  public void CheckJsonReportsPresentedFormulaDefects()
  {
    // Arrange
    using CheckBookFixture fixture = new("""
      formulas:
        result:
          formula: typo + 1
      """);

    // Act
    (int exitCode, string output) = RunCheck(fixture.Root, true);

    // Assert
    exitCode.ShouldBe(1);
    output.ShouldContain("\"ok\": false");
    output.ShouldContain("typo");
    output.ShouldContain("v.result[2024]");
  }

  [Fact]
  public void CheckRejectsUnitsWithNoFormat()
  {
    // Arrange
    using CheckBookFixture fixture = new("""
      formulas:
        result:
          formula: 1
          units: percnt
      """);

    // Act
    (int exitCode, string output) = RunCheck(fixture.Root, false);

    // Assert
    exitCode.ShouldBe(1);
    output.ShouldContain("percnt");
  }

  [Fact]
  public void CheckAllowsMissingDataAndUnusedConditionalDependencies()
  {
    // Arrange
    using CheckBookFixture fixture = new(
      """
      formulas:
        from_empty:
          formula: input
        first_prior:
          formula: prior(input)
        blanked:
          formula: input
        counterpart:
          formula: missing.self
        selected:
          formula: if(1, 7, broken)
        broken:
          formula: sum(selected["missing":"2025"])
      cell_overrides:
        blanked:
          2024:
            blank: true
      """,
      """
      title: Review
      columns: [2024, 2025]
      rows:
        - line: from_empty
        - line: first_prior
        - line: blanked
        - line: counterpart
        - line: selected
      """,
      "line_item,2024,2025\ninput,,2");

    // Act
    (int exitCode, string output) = RunCheck(fixture.Root, false);

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldContain("0 views in error");
  }

  [Fact]
  public void CheckWarnsOfAFormulaPinnedToOneColumnInAScopeOfSeveral()
  {
    // Arrange
    using CheckBookFixture fixture = new(
      """
      formulas:
        result:
          formula: input["2025"] / input["2024"] - 1
        growth:
          formula: input / prior(input) - 1
        total:
          formula: sum(input["2024":"2025"])
      """,
      """
      title: Review
      columns: [2024, 2025]
      rows:
        - line: result
        - line: growth
        - line: total
      """,
      "line_item,2024,2025\ninput,1,2");

    // Act
    (int exitCode, string output) = RunCheck(fixture.Root, false);

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldContain("repeated line: v.result, the same in every column");
    output.ShouldContain("repeated line: v.total, the same in every column");
    output.ShouldNotContain("repeated line: v.growth");
    output.ShouldContain("2 repeated lines");
  }

  [Fact]
  public void CheckRunsEachExplicitCheckOnce()
  {
    // Arrange
    using CheckBookFixture fixture = new(
      """
      formulas:
        result:
          formula: 1
      """,
      checks: """
        checks:
          result:
            formula: result
            expect: 1
        """);

    // Act
    (int exitCode, string output) = RunCheck(fixture.Root, false);

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldContain("1 checks in 1 views: 1 passed, 0 failed");
  }

  [Fact]
  public void CheckRejectsAnInvalidFormulaWhenAnExplicitCheckSelectsIt()
  {
    // Arrange
    using CheckBookFixture fixture = new(
      """
      formulas:
        result:
          formula: if(1, 7, broken)
        broken:
          formula: sum(result["missing":"2025"])
      """,
      checks: """
        checks:
          broken:
            formula: broken
            expect: 0
        """);

    // Act
    (int exitCode, string output) = RunCheck(fixture.Root, false);

    // Assert
    exitCode.ShouldBe(1);
    output.ShouldContain("unknown column");
  }

  [Fact]
  public void ViewJsonReturnsACompactRenderedTableAndSkipsAnInvalidUnusedConditionalInput()
  {
    // Arrange
    using CheckBookFixture fixture = new("""
      formulas:
        result:
          formula: if(1, 7, broken)
        broken:
          formula: sum(result["missing":"2025"])
      """);

    // Act
    (int exitCode, string output) = Run(
      fixture.Root, HeadlessCommand.View, new ViewQuery("v"), true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement payload = document.RootElement;
    payload.GetProperty("id").GetString().ShouldBe("v");
    payload.GetProperty("title").GetString().ShouldBe("Review");
    payload.GetProperty("columns").EnumerateArray()
      .Select(column => column.GetProperty("name").GetString())
      .ShouldBe(new[] { "2024", "2025" });
    JsonElement row = payload.GetProperty("rows")[0];
    row.GetProperty("name").GetString().ShouldBe("v.result");
    row.GetProperty("kind").GetString().ShouldBe("formula");
    row.GetProperty("exact").EnumerateArray()
      .Select(value => value.GetString()).ShouldBe(new[] { "7", "7" });
    row.GetProperty("display").EnumerateArray()
      .Select(value => value.GetString()).ShouldBe(new[] { "7.00", "7.00" });
    row.TryGetProperty("cells", out _).ShouldBeFalse();
    row.TryGetProperty("formula", out _).ShouldBeFalse();
    row.TryGetProperty("dependencies", out _).ShouldBeFalse();
    payload.TryGetProperty("calculations", out _).ShouldBeFalse();
    payload.TryGetProperty("formulas", out _).ShouldBeFalse();
    payload.TryGetProperty("version", out _).ShouldBeFalse();
    output.ShouldNotContain("unknown column");
  }

  [Fact]
  public void ViewJsonMarksContraCellsWhoseExactValuesKeepTheirNaturalDirection()
  {
    // Arrange
    using CheckBookFixture fixture = new("""
      formulas:
        result:
          formula: 7
          units: millions
      """, """
      title: Review
      columns: [2024, 2025]
      rows:
        - line: result
          sign: contra
      """);

    // Act
    (int exitCode, string output) = Run(
      fixture.Root, HeadlessCommand.View, new ViewQuery("v"), true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement row = document.RootElement.GetProperty("rows")[0];
    row.GetProperty("exact").EnumerateArray()
      .Select(value => value.GetString()).ShouldBe(new[] { "7", "7" });
    row.GetProperty("display").EnumerateArray()
      .Select(value => value.GetString()).ShouldBe(new[] { "(7.00)", "(7.00)" });
    row.GetProperty("contra").EnumerateArray()
      .Select(value => value.GetBoolean()).ShouldBe(new[] { true, true });
    row.TryGetProperty("cells", out _).ShouldBeFalse();
  }

  [Fact]
  public void ViewJsonNamesEachCellWhereItsRowAndColumnDoNot()
  {
    // Arrange
    using var fixture = new ViewFixture();
    WriteComparisonWithMedian(fixture);

    // Act
    (int exitCode, string output) = Run(fixture.Root, HeadlessCommand.View, new ViewQuery("comps"), true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement rows = document.RootElement.GetProperty("rows");
    Cells(rows[0]).ShouldBe(new[]
    {
      ("alpha::metrics.revenue", "2025"), ("alpha::metrics.doubled", "alpha::metrics.double")
    });
    Cells(rows[3]).ShouldBe(new[]
    {
      ("comps.median.metrics.revenue", "2025"), ("comps.median.metrics.doubled", "double")
    });
  }

  [Fact]
  public void ACellLinkOpensACellOfAComparedBook()
  {
    // Arrange
    using var fixture = new ViewFixture();
    WriteComparisonWithMedian(fixture);

    // Act
    string query = CellLink.Query(fixture.Root, "beta::metrics.doubled", "beta::metrics.double");

    // Assert
    query.ShouldBe("/?view=comps&line=beta%3A%3Ametrics.doubled&column=beta%3A%3Ametrics.double");
  }

  [Fact]
  public void ACellLinkOpensACellOfAComparisonSummary()
  {
    // Arrange
    using var fixture = new ViewFixture();
    WriteComparisonWithMedian(fixture);

    // Act
    string query = CellLink.Query(fixture.Root, "comps.median.metrics.revenue", "2025");

    // Assert
    query.ShouldBe("/?view=comps&line=comps.median.metrics.revenue&column=2025");
  }

  [Fact]
  public void ValueJsonSerializesADeepCalculation()
  {
    // Arrange
    var formulas = new List<string> { "formulas:" };
    for (int index = 0; index < 40; index++)
    {
      string line = index == 0 ? "result" : $"step_{index}";
      formulas.Add($"  {line}:");
      formulas.Add($"    formula: step_{index + 1} + 1");
    }
    formulas.Add("  step_40:");
    formulas.Add("    formula: 1");
    using CheckBookFixture fixture = new(string.Join('\n', formulas));

    // Act
    (int exitCode, string output) = Run(
      fixture.Root, HeadlessCommand.Value, new ViewQuery("v", "result", "2024"), true);

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldContain("\"value\": \"41\"");
  }

  [Fact]
  public void ValueErrorsNameTheViewIdAndListThePresentedColumns()
  {
    // Arrange
    using ReviewBookFixture fixture = new();

    // Act
    InvalidDataException column = Should.Throw<InvalidDataException>(() => Run(
      fixture.Root, HeadlessCommand.Value, new ViewQuery("cost-review", "result", "Growth YoY"), false));
    InvalidDataException line = Should.Throw<InvalidDataException>(() => Run(
      fixture.Root, HeadlessCommand.Value, new ViewQuery("cost-review", "missing", "2024"), false));

    // Assert
    column.Message.ShouldBe(
      "'result[Growth YoY]' is not presented by view 'cost-review'. Columns for this line: 2024, 2025, growth");
    line.Message.ShouldBe("Unknown line 'missing' in view 'cost-review'.");
  }

  [Theory]
  [InlineData(HeadlessCommand.View)]
  [InlineData(HeadlessCommand.Value)]
  [InlineData(HeadlessCommand.Check)]
  public void JsonLeavesFormulaPunctuationUnescaped(HeadlessCommand command)
  {
    // Arrange
    using ReviewBookFixture fixture = new();
    ViewQuery query = command == HeadlessCommand.Value
      ? new ViewQuery("cost-review", "result", "2024")
      : new ViewQuery("cost-review");

    // Act
    (int exitCode, string output) = Run(fixture.Root, command, query, true);

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldNotContain("\\u00");
    output.ShouldContain(command == HeadlessCommand.View ? "Growth YoY's" : "1 + 2");
  }

  [Fact]
  public void ValueJsonIsACompactCellWithItsCalculationInputsAndDependents()
  {
    // Arrange
    using ReviewBookFixture fixture = new();

    // Act
    (int exitCode, string output) = Run(
      fixture.Root, HeadlessCommand.Value, new ViewQuery("cost-review", "change", "2025"), true);

    // Assert
    exitCode.ShouldBe(0);
    output.ShouldNotContain("null");
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement cell = document.RootElement;
    cell.GetProperty("view").GetString().ShouldBe("cost-review");
    cell.GetProperty("line").GetString().ShouldBe("cost_review.change");
    cell.GetProperty("column").GetString().ShouldBe("2025");
    cell.GetProperty("value").GetString().ShouldBe("0");
    cell.GetProperty("formula").GetString().ShouldBe("result - prior(result)");
    cell.GetProperty("calculation").GetString().ShouldBe("3 − 3");
    cell.GetProperty("inputs").EnumerateArray()
      .Select(input => $"{input.GetProperty("line").GetString()}[{input.GetProperty("column").GetString()}]")
      .ShouldBe(new[] { "cost_review.result[2025]", "cost_review.result[2024]" });
    cell.GetProperty("dependents").EnumerateArray()
      .Select(dependent => dependent.GetProperty("column").GetString())
      .ShouldBe(new[] { "cost_review.growth" });
    foreach (string pageOnly in new[] { "cell", "formulas", "tokens", "references", "linked", "start", "region" })
    {
      output.ShouldNotContain($"\"{pageOnly}\"");
    }
  }

  [Fact]
  public void ValueJsonWritesOutAnInputNoViewPresents()
  {
    // Arrange
    using ReviewBookFixture fixture = new();

    // Act
    (int exitCode, string output) = Run(
      fixture.Root, HeadlessCommand.Value, new ViewQuery("cost-review", "shifted", "2024"), true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement doubled = document.RootElement.GetProperty("inputs")[0];
    doubled.GetProperty("line").GetString().ShouldBe("cost_review.doubled");
    doubled.GetProperty("formula").GetString().ShouldBe("result * 2");
    doubled.GetProperty("calculation").GetString().ShouldBe("3 × 2");
    doubled.GetProperty("inputs")[0].GetProperty("line").GetString().ShouldBe("cost_review.result");
  }

  [Fact]
  public void ValueJsonPrintsAnUnresolvedCellAndNamesTheMissingPriorPeriod()
  {
    // Arrange
    using ReviewBookFixture fixture = new();

    // Act
    (int exitCode, string output) = Run(
      fixture.Root, HeadlessCommand.Value, new ViewQuery("cost-review", "change", "2024"), true);

    // Assert
    exitCode.ShouldBe(1);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement cell = document.RootElement;
    cell.GetProperty("unresolved").GetBoolean().ShouldBeTrue();
    cell.TryGetProperty("value", out _).ShouldBeFalse();
    cell.GetProperty("calculation").GetString().ShouldBe("3 − ?");
    JsonElement prior = cell.GetProperty("inputs")[1];
    prior.GetProperty("line").GetString().ShouldBe("cost_review.result");
    prior.TryGetProperty("column", out _).ShouldBeFalse();
    prior.GetProperty("missing").GetString().ShouldBe("no period before 2024");
  }

  [Fact]
  public void LinesJsonReadsEachLineByQualifiedNameAcrossItsPeriodsWithoutAView()
  {
    // Arrange
    using LinesBookFixture fixture = new();

    // Act
    (int exitCode, string output) = RunLines(
      fixture.Root, ["historical.segments.connectivity", "historical.segments.total"], true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement[] lines = document.RootElement.EnumerateArray().ToArray();
    lines.Select(line => line.GetProperty("name").GetString())
      .ShouldBe(["historical.segments.connectivity", "historical.segments.total"]);
    JsonElement connectivity = lines[0];
    connectivity.GetProperty("label").GetString().ShouldBe("Connectivity");
    connectivity.GetProperty("kind").GetString().ShouldBe("fact");
    connectivity.GetProperty("units").GetString().ShouldBe("millions");
    connectivity.GetProperty("columns").EnumerateArray().Select(column => column.GetString())
      .ShouldBe(["2024", "2025"]);
    connectivity.GetProperty("exact").EnumerateArray().Select(value => value.GetString())
      .ShouldBe(["7.34", "11.5"]);
    connectivity.GetProperty("display").EnumerateArray().Select(value => value.GetString())
      .ShouldBe(["7.3", "11.5"]);
    JsonElement total = lines[1];
    total.GetProperty("kind").GetString().ShouldBe("formula");
    total.GetProperty("exact").EnumerateArray().Select(value => value.GetString())
      .ShouldBe(["9.34", "14.5"]);
    total.TryGetProperty("unresolved", out _).ShouldBeFalse();
  }

  [Fact]
  public void LinesReadAContraLineInItsNaturalDirection()
  {
    // Arrange
    using LinesBookFixture fixture = new();

    // Act
    (_, string output) = RunLines(fixture.Root, ["historical.segments.cost"], true);

    // Assert
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement cost = document.RootElement[0];
    cost.GetProperty("contra").GetBoolean().ShouldBeTrue();
    cost.GetProperty("exact").EnumerateArray().Select(value => value.GetString()).ShouldBe(["4", "5"]);
    cost.GetProperty("display").EnumerateArray().Select(value => value.GetString()).ShouldBe(["4.0", "5.0"]);
  }

  [Fact]
  public void LinesMarkAnUnresolvedCellWithoutFailing()
  {
    // Arrange
    using LinesBookFixture fixture = new();

    // Act
    (int exitCode, string output) = RunLines(fixture.Root, ["historical.segments.change"], true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement change = document.RootElement[0];
    change.GetProperty("exact")[0].ValueKind.ShouldBe(JsonValueKind.Null);
    change.GetProperty("display").EnumerateArray().Select(value => value.GetString()).ShouldBe(["—", "5.2"]);
    change.GetProperty("unresolved").EnumerateArray().Select(value => value.GetBoolean()).ShouldBe([true, false]);
  }

  [Fact]
  public void LinesReportAnUnknownNameInPlaceAndExitNonZero()
  {
    // Arrange
    using LinesBookFixture fixture = new();

    // Act
    (int exitCode, string output) = RunLines(
      fixture.Root, ["historical.segments.missing", "historical.segments.total"], true);

    // Assert
    exitCode.ShouldBe(1);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement missing = document.RootElement[0];
    missing.GetProperty("name").GetString().ShouldBe("historical.segments.missing");
    missing.GetProperty("error").GetString().ShouldBe("Unknown line 'historical.segments.missing'.");
    missing.TryGetProperty("exact", out _).ShouldBeFalse();
    document.RootElement[1].GetProperty("exact").GetArrayLength().ShouldBe(2);
  }

  [Fact]
  public void LinesReadTheCalculatedColumnsThatApplyToThemAfterTheirPeriods()
  {
    // Arrange
    using LinesBookFixture fixture = new();
    fixture.Write("historical/formulas.yaml", """
      column_formulas:
        growth:
          label: Growth
          formula: self["2025"] - self["2024"]
          applies_to: facts
      """);

    // Act
    (int exitCode, string output) = RunLines(
      fixture.Root, ["historical.segments.connectivity", "historical.segments.total"], true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    JsonElement connectivity = document.RootElement[0];
    connectivity.GetProperty("columns").EnumerateArray().Select(column => column.GetString())
      .ShouldBe(["2024", "2025", "growth"]);
    connectivity.GetProperty("exact").EnumerateArray().Select(value => value.GetString())
      .ShouldBe(["7.34", "11.5", "4.16"]);
    document.RootElement[1].GetProperty("columns").EnumerateArray().Select(column => column.GetString())
      .ShouldBe(["2024", "2025"]);
  }

  [Fact]
  public void LinesWithNoNamesReadEveryLineInOrderOfName()
  {
    // Arrange
    using LinesBookFixture fixture = new();

    // Act
    (int exitCode, string output) = RunLines(fixture.Root, [], true);

    // Assert
    exitCode.ShouldBe(0);
    using JsonDocument document = JsonDocument.Parse(output);
    document.RootElement.EnumerateArray().Select(line => line.GetProperty("name").GetString()).ShouldBe(
      ["historical.segments.change", "historical.segments.connectivity", "historical.segments.cost",
        "historical.segments.space", "historical.segments.total"]);
  }

  [Fact]
  public void LinesPrintEachLineWithItsDisplayedValues()
  {
    // Arrange
    using LinesBookFixture fixture = new();

    // Act
    (int exitCode, string output) = RunLines(
      fixture.Root, ["historical.segments.total", "historical.segments.missing"], false);

    // Assert
    exitCode.ShouldBe(1);
    output.ShouldBe("""
      historical.segments.total    2024 9.3, 2025 14.5
      historical.segments.missing  error: Unknown line 'historical.segments.missing'.

      """.ReplaceLineEndings());
  }

  private static (int ExitCode, string Output) RunCheck(string root, bool json)
  {
    return Run(root, HeadlessCommand.Check, null, json);
  }

  private static (int ExitCode, string Output) RunLines(string root, List<string> lines, bool json)
  {
    return Run(root, HeadlessCommand.Lines, null, json, lines);
  }

  private static (int ExitCode, string Output) Run(
    string root,
    HeadlessCommand command,
    ViewQuery? query,
    bool json,
    List<string>? lines = null)
  {
    TextWriter original = Console.Out;
    using var writer = new StringWriter();
    try
    {
      Console.SetOut(writer);
      int exitCode = HeadlessRunner.Run(root, command, query, json, lines);
      return (exitCode, writer.ToString());
    }
    finally
    {
      Console.SetOut(original);
    }
  }

  private static void WriteComparisonWithMedian(ViewFixture fixture)
  {
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
        - book: beta
        - space: compact
        - scope: median
      columns:
        - line: metrics.revenue
          column: "2025"
        - line: metrics.doubled
          column: double
      """);
  }

  private static (string Line, string Column)[] Cells(JsonElement row)
  {
    return row.GetProperty("cells").EnumerateArray()
      .Select(cell => (cell.GetProperty("line").GetString()!, cell.GetProperty("column").GetString()!))
      .ToArray();
  }


  private sealed class TreeFixture : TempFolder
  {
    public TreeFixture(params string[] books)
    {
      foreach (string book in books)
      {
        Write(book.Length == 0 ? "book.yaml" : $"{book}/book.yaml", "name: Book");
      }
    }

    public string Folder(string book)
    {
      return book.Length == 0 ? Root : PathTo(book);
    }


  }


  private sealed class ReviewBookFixture : TempFolder
  {
    public ReviewBookFixture()
    {
      Write("book.yaml", "name: Review\nshort_name: Review\nnavigation: [cost-review]");
      Write("cost-review/view.yaml", """
        title: Growth YoY's review
        columns: [2024, 2025, growth]
        rows:
          - line: result
          - line: change
          - line: shifted
        """);
      Write("cost-review/formulas.yaml", """
        formulas:
          result:
            formula: 1 + 2
          change:
            formula: result - prior(result)
          doubled:
            formula: result * 2
          shifted:
            formula: doubled + 1
        column_formulas:
          growth:
            label: Growth YoY
            formula: self["2025"] - self["2024"]
        """);
      Write("cost-review/checks.yaml", """
        checks:
          sum:
            formula: 1 + 2 - result
        """);
    }


  }

  /// <summary>Facts and formulas in a folder no view presents, so its lines are reached only by name.</summary>
  private sealed class LinesBookFixture : TempFolder
  {
    public LinesBookFixture()
    {
      Write("book.yaml", "name: Lines\nshort_name: Lines\nnavigation: []");
      Write("formats.yaml", "defaults:\n  unresolved: \"—\"\nformats:\n  millions:\n    decimals: 1");
      Write("historical/segments/facts/segments.csv", """
        line_item,2024,2025
        space,2,3
        connectivity,7.34,11.5
        cost,4,5
        """);
      Write("historical/segments/facts/segments.yaml", """
        table:
          title: Segments
        defaults:
          units: millions
        line_items:
          space:
            label: Space
          connectivity:
            label: Connectivity
          cost:
            label: Cost
            sign: contra
        """);
      Write("historical/segments/formulas.yaml", """
        formulas:
          total:
            formula: space + connectivity
            units: millions
          change:
            formula: total - prior(total)
            units: millions
        """);
    }


  }

  private sealed class CheckBookFixture : TempFolder
  {
    public CheckBookFixture(
      string formulas,
      string view = """
        title: Review
        columns: [2024, 2025]
        rows:
          - line: result
        """,
      string? factsCsv = null,
      string? checks = null)
    {
      Write("book.yaml", "name: Review\nshort_name: Review\nnavigation: [v]");
      Write("formats.yaml", "defaults:\n  decimals: 2\nformats:\n  millions: {}");
      Write("v/view.yaml", view);
      Write("v/formulas.yaml", formulas);
      if (checks is not null)
      {
        Write("v/checks.yaml", checks);
      }
      if (factsCsv is not null)
      {
        Write("v/facts/inputs.csv", factsCsv);
        Write("v/facts/inputs.yaml", """
          table:
            title: Inputs
          defaults:
            units: millions
          line_items:
            input:
              label: Input
          """);
      }
    }


  }


}