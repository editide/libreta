using System.Collections.Concurrent;
using System.Globalization;
using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class BookLoadingTests
{
  [Fact]
  public void RequiresAShortName()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,1");
    File.WriteAllText(Path.Combine(fixture.Root, "book.yaml"), "name: Test\nnavigation: []\n");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("short_name");
  }

  [Fact]
  public void LoadsFactValuesUsingInvariantCulture()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,1.5");
    CultureInfo originalCulture = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

    try
    {
      // Act
      Book book = Book.Load(fixture.Root);

      // Assert
      book.Facts["revenue"].Cells["2025"].Value.ShouldBe(1.5);
    }
    finally
    {
      CultureInfo.CurrentCulture = originalCulture;
    }
  }

  [Fact]
  public void LoadsIsoDatesAsDaySerials()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,2026-06-30");

    // Act
    Book book = Book.Load(fixture.Root);

    // Assert
    book.Facts["revenue"].Cells["2025"].Value.ShouldBe(46203);
  }

  [Fact]
  public void RejectsUnknownYamlPropertiesWithTheFileName()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,1", "mystery: value");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("facts.yaml");
    exception.Message.ShouldContain("mystery");
  }

  [Fact]
  public void ReportsTheFirstBrokenFactTableInPathOrder()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,1");
    for (int index = 0; index < 40; index++)
    {
      fixture.WriteFactTable($"table-{index:D2}", $"line_item,2025\nline_{index},1", "line_items: {}\n");
    }
    fixture.WriteFactTable("table-10", "line_item,2025\nbroken", "line_items: {}\n");
    fixture.WriteFactTable("table-30", "line_item,2025\nline_30,1", "line_items: [\n");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("table-10.csv");
  }

  [Theory]
  [InlineData("id: operations", "id")]
  [InlineData("periods: [2024, 2025]", "periods")]
  [InlineData("period_type: fiscal_year", "period_type")]
  public void RejectsRemovedFactTableMetadata(string metadataEntry, string property)
  {
    // Arrange
    string metadata = $$"""
      table:
        title: Facts
        {{metadataEntry}}
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
      """;
    using BookFixture fixture = new("line_item,2025\nrevenue,1", metadata: metadata);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("facts.yaml");
    exception.Message.ShouldContain(property);
  }

  [Theory]
  [InlineData("printed", "backwards")]
  [InlineData("sign", "negative")]
  public void RejectsInvalidFactConventions(string property, string value)
  {
    // Arrange
    string metadata = $$"""
      table:
        title: Facts
      defaults:
        units: millions
        {{property}}: {{value}}
      line_items:
        revenue:
          label: Revenue
      """;
    using BookFixture fixture = new("line_item,2025\nrevenue,1", metadata: metadata);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("facts.yaml");
    exception.Message.ShouldContain(property);
    exception.Message.ShouldContain(value);
  }

  [Fact]
  public void RejectsDuplicateCsvPeriods()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025,2025\nrevenue,1,2");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("facts.csv:1");
    exception.Message.ShouldContain("Duplicate period '2025'");
  }

  [Theory]
  [InlineData("line_item,2024,2025\nrevenue,1", 2)]
  [InlineData("line_item,2025\nrevenue,1,2", 3)]
  public void RejectsCsvRowsWithTheWrongNumberOfCells(string csv, int actualCells)
  {
    // Arrange
    using BookFixture fixture = new(csv);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("facts.csv:2");
    exception.Message.ShouldContain($"found {actualCells}");
  }

  [Fact]
  public void RejectsMalformedCsvValuesWithCellContext()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,nope");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("facts.csv:2");
    exception.Message.ShouldContain("revenue[2025]");
    exception.Message.ShouldContain("nope");
  }

  [Fact]
  public void ResolvesSourceImagesRelativeToFactMetadata()
  {
    // Arrange
    string metadata = """
      table:
        title: Facts
      source:
        image: evidence.webp
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
          image_regions:
            2025: { x: 10, y: 20, width: 30, height: 40 }
      """;
    using BookFixture fixture = new("line_item,2025\nrevenue,1", metadata: metadata);
    File.WriteAllBytes(Path.Combine(fixture.Facts, "evidence.webp"), new byte[] { 1, 2, 3 });

    // Act
    Book book = Book.Load(fixture.Root);

    // Assert
    FactCell cell = book.Facts["revenue"].Cells["2025"];
    cell.SourceImage.ShouldBe("facts/evidence.webp");
    SourceRegion region = cell.SourceRegion.ShouldNotBeNull();
    region.X.ShouldBe(10);
    region.Y.ShouldBe(20);
    region.Width.ShouldBe(30);
    region.Height.ShouldBe(40);
  }

  [Fact]
  public void LoadsNestedFactTablesInTheOwningFactsScope()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,1");
    string tableFolder = Path.Combine(fixture.Root, "analysis", "facts", "2025-10-k", "operations");
    Directory.CreateDirectory(tableFolder);
    File.WriteAllText(Path.Combine(tableFolder, "statement.csv"), "line_item,2025\noperating_income,42\n");
    File.WriteAllText(Path.Combine(tableFolder, "statement.yaml"), """
      table:
        title: Operations
      source:
        image: statement.png
      defaults:
        units: millions
      line_items:
        operating_income:
          label: Operating income
      """);
    File.WriteAllBytes(Path.Combine(tableFolder, "statement.png"), new byte[] { 1, 2, 3 });

    // Act
    Book book = Book.Load(fixture.Root);

    // Assert
    Fact fact = book.Facts["analysis.operating_income"];
    fact.Scope.ShouldBe("analysis");
    fact.Cells["2025"].Value.ShouldBe(42);
    fact.Cells["2025"].SourceImage.ShouldBe("analysis/facts/2025-10-k/operations/statement.png");
    book.Facts.ShouldNotContainKey("analysis.2025_10_k.operations.operating_income");
  }

  [Fact]
  public void DoesNotDiscoverNestedFactsDirectoriesTwice()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,1");
    string tableFolder = Path.Combine(fixture.Root, "analysis", "facts", "2025-10-k", "facts");
    Directory.CreateDirectory(tableFolder);
    File.WriteAllText(Path.Combine(tableFolder, "statement.csv"), "line_item,2025\noperating_income,42\n");
    File.WriteAllText(Path.Combine(tableFolder, "statement.yaml"), """
      table:
        title: Operations
      defaults:
        units: millions
      line_items:
        operating_income:
          label: Operating income
      """);

    // Act
    Book book = Book.Load(fixture.Root);

    // Assert
    Fact fact = book.Facts["analysis.operating_income"];
    fact.Cells["2025"].Value.ShouldBe(42);
    book.Facts.ShouldNotContainKey("analysis.facts.operating_income");
  }

  [Fact]
  public void RejectsInvalidSourceImageRegions()
  {
    // Arrange
    string metadata = """
      table:
        title: Facts
      source:
        image: evidence.png
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
          image_regions:
            2025: { x: 10, y: 20, width: 0, height: 40 }
      """;
    using BookFixture fixture = new("line_item,2025\nrevenue,1", metadata: metadata);
    File.WriteAllBytes(Path.Combine(fixture.Facts, "evidence.png"), new byte[] { 1, 2, 3 });

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("line_items.revenue.image_regions.2025");
  }

  [Fact]
  public void RejectsSourceImagesOutsideTheBook()
  {
    // Arrange
    string metadata = """
      table:
        title: Facts
      source:
        image: ../../evidence.png
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
      """;
    using BookFixture fixture = new("line_item,2025\nrevenue,1", metadata: metadata);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("source.image");
    exception.Message.ShouldContain("inside the book");
  }

  [Fact]
  public void CombinesNonOverlappingFactTablesWithoutLosingCellSources()
  {
    // Arrange
    string annualMetadata = """
      table:
        title: Annual operations
      source:
        url: https://example.com/annual
        image: annual.png
      line_items:
        revenue:
          label: Revenue
      """;
    using BookFixture fixture = new("line_item,2025\nrevenue,300", metadata: annualMetadata);
    File.WriteAllBytes(Path.Combine(fixture.Facts, "annual.png"), new byte[] { 1, 2, 3 });
    fixture.WriteFactTable("interim", "line_item,H1 2025,H1 2026\nrevenue,-100,-140", """
      table:
        title: Interim operations
      source:
        url: https://example.com/interim
        image: interim.png
      defaults:
        printed: negated
      line_items:
        revenue:
          label: Revenue
      """);
    File.WriteAllBytes(Path.Combine(fixture.Facts, "interim.png"), new byte[] { 4, 5, 6 });
    File.WriteAllText(Path.Combine(fixture.Root, "formulas.yaml"), """
      column_formulas:
        LTM:
          formula: self["2025"] - self["H1 2025"] + self["H1 2026"]
      """);
    File.WriteAllText(Path.Combine(fixture.Root, "view.yaml"), """
      title: Financials
      columns: [H1 2026, H1 2025, 2025, LTM]
      rows:
        - line: revenue
      """);

    // Act
    Book book = Book.Load(fixture.Root);
    View view = View.Load(book, fixture.Root);
    var engine = new Engine(view);
    RowPayload revenue = PayloadBuilder.Build(view, engine, 1).Rows.Single();

    // Assert
    new[] { "H1 2026", "H1 2025", "2025", "LTM" }
      .Select(period => engine.Value("revenue", period)).ShouldBe(new double?[] { 140, 100, 300, 340 });
    revenue.Cells.Take(3).Select(cell => cell.Source!.Url).ShouldBe(new[]
    {
      "https://example.com/interim",
      "https://example.com/interim",
      "https://example.com/annual"
    });
    revenue.Cells.Take(3).Select(cell => cell.Source!.Image).ShouldBe(new[]
    {
      "facts/interim.png",
      "facts/interim.png",
      "facts/annual.png"
    });
    revenue.Cells.Take(3).Select(cell => cell.Source!.Table).ShouldBe(new[]
    {
      "Interim operations",
      "Interim operations",
      "Annual operations"
    });
    revenue.Cells[3].Kind.ShouldBe("formula");
    revenue.Cells[3].Source.ShouldBeNull();
    revenue.Source.ShouldBeNull();
  }

  [Fact]
  public void KeepsEachFragmentsLineNote()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,300", """
          note: Includes $66 from a related party in 2025.
      """);
    fixture.WriteFactTable("interim", "line_item,H1 2026\nrevenue,140", """
      table:
        title: Interim
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
          note: Includes $513 from a related party in H1 2026.
      """);
    fixture.WriteFactTable("restated", "line_item,H1 2025\nrevenue,100", """
      table:
        title: Restated
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
          note: Includes $66 from a related party in 2025.
      """);

    // Act
    Book book = Book.Load(fixture.Root);

    // Assert
    book.Facts["revenue"].Notes.ShouldBe(new[]
    {
      "Includes $66 from a related party in 2025.",
      "Includes $513 from a related party in H1 2026."
    });
  }

  [Fact]
  public void RejectsLabelNote()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,300", """
          label_note: Printed as "Revenue (related party of $66)".
      """);

    // Act
    Exception exception = Should.Throw<Exception>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("label_note");
  }

  [Fact]
  public void RejectsOverlappingFactCoordinates()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,300");
    fixture.WriteFactTable("restatement", "line_item,2025\nrevenue,301", """
      table:
        title: Restatement
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
      """);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("revenue[2025]");
    exception.Message.ShouldContain("facts.csv");
    exception.Message.ShouldContain("restatement.csv");
  }

  [Fact]
  public void RejectsConflictingMetadataAcrossFactFragments()
  {
    // Arrange
    using BookFixture fixture = new("line_item,2025\nrevenue,300");
    fixture.WriteFactTable("interim", "line_item,H1 2026\nrevenue,140", """
      table:
        title: Interim
      defaults:
        units: percent
      line_items:
        revenue:
          label: Revenue
      """);

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain("revenue");
    exception.Message.ShouldContain("Units");
    exception.Message.ShouldContain("facts.csv");
    exception.Message.ShouldContain("interim.csv");
  }


  [Fact]
  public void ViewsInheritChecksOnlyFromAncestorsWithoutAView()
  {
    // Arrange
    using TempFolder folder = new TempFolder();
    folder.Write("book.yaml", "name: Test\nshort_name: Test\nnavigation: []\n");
    folder.Write("formats.yaml", "formats:\n  millions:\n    decimals: 0\n");
    folder.Write("parent/facts/facts.csv", "line_item,2025\nrevenue,10\n");
    folder.Write("parent/facts/facts.yaml", "table:\n  title: Facts\nline_items:\n  revenue: {}\n");
    folder.Write("parent/checks.yaml", "checks:\n  ties:\n    formula: revenue - 10\n");
    folder.Write("parent/view.yaml", "title: Parent\ncolumns: [2025]\nrows:\n  - line: revenue\n");
    folder.Write("parent/child/view.yaml", "title: Child\ncolumns: [2025]\nrows:\n  - line: revenue\n");
    folder.Write("group/checks.yaml", "checks:\n  shared:\n    formula: parent.revenue - 10\n");
    folder.Write("group/leaf/view.yaml", "title: Leaf\ncolumns: [2025]\nrows:\n  - line: parent.revenue\n");
    Book book = Book.Load(folder.Root);

    // Act
    View child = View.Load(book, folder.PathTo("parent/child"));
    View leaf = View.Load(book, folder.PathTo("group/leaf"));

    // Assert
    child.Checks.ShouldBeEmpty();
    leaf.Checks.Select(check => check.Name).ShouldBe(new[] { "shared" });
  }

  [Fact]
  public void ReadsEachFormatsFileOnceForTheWholeBook()
  {
    // Arrange
    using TempFolder folder = new TempFolder();
    folder.Write("book.yaml", "name: Test\nshort_name: Test\nnavigation: []\n");
    folder.Write("formats.yaml", "formats:\n  millions:\n    decimals: 0\n");
    folder.Write("report/facts/first.csv", "line_item,2025\nrevenue,10\n");
    folder.Write("report/facts/first.yaml", "table:\n  title: First\nline_items:\n  revenue: {}\n");
    folder.Write("report/facts/second.csv", "line_item,2025\ncost,4\n");
    folder.Write("report/facts/second.yaml", "table:\n  title: Second\nline_items:\n  cost: {}\n");
    folder.Write("report/view.yaml", "title: Report\ncolumns: [2025]\nrows:\n  - line: revenue\n");
    var files = new CountingFiles();

    // Act
    Book book = Book.Load(folder.Root, files);
    View.Load(book, folder.PathTo("report"));
    View.Load(book, folder.PathTo("report"));

    // Assert
    files.Reads(folder.PathTo("formats.yaml")).ShouldBe(1);
  }

  private sealed class CountingFiles : IBookFiles
  {
    private readonly ConcurrentDictionary<string, int> reads = new ConcurrentDictionary<string, int>();

    public int Reads(string path)
    {
      return reads.GetValueOrDefault(Path.GetFullPath(path));
    }

    public bool FileExists(string path)
    {
      return File.Exists(path);
    }

    public bool DirectoryExists(string path)
    {
      return Directory.Exists(path);
    }

    public byte[] ReadAllBytes(string path)
    {
      string full = Path.GetFullPath(path);
      reads.AddOrUpdate(full, 1, (_, count) => count + 1);
      return File.ReadAllBytes(path);
    }

    public IEnumerable<string> Files(string folder, string pattern)
    {
      return Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
    }

    public IEnumerable<string> Directories(string folder)
    {
      return Directory.GetDirectories(folder);
    }


  }

  private sealed class BookFixture : TempFolder
  {
    public BookFixture(string csv, string extraMetadata = "", string? metadata = null)
    {
      Facts = PathTo("facts");
      Write("book.yaml", "name: Test\nshort_name: Test\nnavigation: []\n");
      Write("facts/facts.csv", csv);
      Write("facts/facts.yaml", metadata ?? $$"""
        table:
          title: Facts
        defaults:
          units: millions
        line_items:
          revenue:
            label: Revenue
        {{extraMetadata}}
        """);
    }

    public string Facts { get; }

    public void WriteFactTable(string name, string csv, string metadata)
    {
      Write($"facts/{name}.csv", csv);
      Write($"facts/{name}.yaml", metadata);
    }


  }


}