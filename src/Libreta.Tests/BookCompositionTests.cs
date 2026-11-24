using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class BookCompositionTests
{
  [Fact]
  public void LoadsAChildStandaloneAndComposedWithoutRebindingItsLocalReferences()
  {
    // Arrange
    using var fixture = new CompositionFixture();
    fixture.WriteBook("", "Parent", "navigation: [summary]\nbooks:\n  - id: alpha\n    path: children/alpha");
    fixture.WriteFormula("", "summary", "child_result", "alpha::analysis.revenue[\"double\"]");
    fixture.WriteView("", "summary", "child_result");
    fixture.WriteBook("children/alpha", "Alpha", "navigation: [analysis]");
    fixture.WriteFacts("children/alpha", "analysis", 7);
    fixture.WriteFormula("children/alpha", "analysis", "doubled", "revenue * 2");
    fixture.WriteColumnFormula("children/alpha", "analysis", "double", "Double", "self[\"2025\"] * 2");
    fixture.WriteCheck("children/alpha", "analysis", "doubled - 14");
    fixture.WriteView("children/alpha", "analysis", "doubled");

    // Act
    Book standalone = Book.Load(fixture.PathTo("children/alpha"));
    View standaloneView = View.Load(standalone, fixture.PathTo("children/alpha/analysis"));
    Book composed = Book.Load(fixture.Root);
    View composedView = View.Load(composed, fixture.PathTo("summary"));
    View composedChildView = View.Load(composed, fixture.PathTo("children/alpha/analysis"));

    // Assert
    new Engine(standaloneView).Value("doubled", "2025").ShouldBe(14);
    new Engine(composedView).Value("child_result", "2025").ShouldBe(14);
    composed.Facts.ShouldContainKey("alpha::analysis.revenue");
    composed.Formulas.ShouldContainKey("alpha::analysis.doubled");
    standalone.Facts["analysis.revenue"].Cells["2025"].SourceImage.ShouldBe("analysis/facts/source.png");
    composed.Facts["alpha::analysis.revenue"].Cells["2025"].SourceImage
      .ShouldBe("children/alpha/analysis/facts/source.png");
    PayloadBuilder.BuildChecks(composedChildView, new Engine(composedChildView)).Single().Passed.ShouldBeTrue();
  }

  [Fact]
  public void KeepsIdenticallyNamedChildLinesDistinct()
  {
    // Arrange
    using var fixture = new CompositionFixture();
    fixture.WriteBook("", "Parent",
      "navigation: [summary]\nbooks:\n  - id: alpha\n    path: children/alpha\n" +
      "  - id: beta\n    path: children/beta");
    fixture.WriteView("", "summary", "alpha::analysis.revenue\nbeta::analysis.revenue");
    fixture.WriteBook("children/alpha", "Alpha", "navigation: [analysis]");
    fixture.WriteBook("children/beta", "Beta", "navigation: [analysis]");
    fixture.WriteFacts("children/alpha", "analysis", 7);
    fixture.WriteFacts("children/beta", "analysis", 11);
    fixture.WriteView("children/alpha", "analysis", "revenue");
    fixture.WriteView("children/beta", "analysis", "revenue");

    // Act
    Book book = Book.Load(fixture.Root);

    // Assert
    book.Facts["alpha::analysis.revenue"].Cells["2025"].Value.ShouldBe(7);
    book.Facts["beta::analysis.revenue"].Cells["2025"].Value.ShouldBe(11);
  }

  [Fact]
  public void ExcludesUndeclaredDescendantBooksFromLoadingAndCatalogDiscovery()
  {
    // Arrange
    using var fixture = new CompositionFixture();
    fixture.WriteBook("", "Parent", "navigation: [summary]");
    fixture.WriteView("", "summary", "total");
    fixture.WriteFormula("", "summary", "total", "1");
    fixture.WriteBook("children/broken", "Broken", "navigation: [missing]");
    fixture.WriteFormula("children/broken", "bad", "broken", "(");

    // Act
    Book book = Book.Load(fixture.Root);
    ViewCatalog catalog = CatalogLoader.Load(Book.Load(fixture.Root), 1);

    // Assert
    book.Formulas.ShouldContainKey("summary.total");
    book.Formulas.Keys.ShouldNotContain(key => key.Contains("broken", StringComparison.Ordinal));
    catalog.Views.Select(view => view.Id).ShouldBe(new[] { "summary" });
  }

  [Fact]
  public void PlacesAnIncludedChildNavigationSubtreeOnlyWhenRequested()
  {
    // Arrange
    using var fixture = new CompositionFixture();
    fixture.WriteBook("", "Collection",
      "navigation:\n  - overview\n  - book: alpha\nbooks:\n  - id: alpha\n    path: children/alpha");
    fixture.WriteView("", "overview", "total");
    fixture.WriteFormula("", "overview", "total", "1");
    fixture.WriteBook("children/alpha", "Alpha", "navigation: [analysis]");
    fixture.WriteFacts("children/alpha", "analysis", 7);
    fixture.WriteView("children/alpha", "analysis", "revenue");

    // Act
    ViewCatalog catalog = CatalogLoader.Load(Book.Load(fixture.Root), 1);

    // Assert
    catalog.Name.ShouldBe("Collection");
    catalog.ShortName.ShouldBe("Collection");
    catalog.Views.Select(view => view.Id).ShouldBe(new[] { "overview", "alpha/analysis" });
  }

  [Fact]
  public void InheritsMetadataDefaultsWithinEachOwningBook()
  {
    // Arrange
    using var fixture = new CompositionFixture();
    fixture.WriteBook("", "Parent",
      "units: millions\n" +
      "identifiers:\n  project: parent-1\nnavigation: [summary]\nbooks:\n  - id: alpha\n    path: children/alpha");
    fixture.WriteFormula("", "summary", "total", "1");
    fixture.WriteView("", "summary", "total");
    fixture.WriteBook("children/alpha", "Alpha",
      "units: percent\nnavigation: [analysis]");
    fixture.WriteFacts("children/alpha", "analysis", 7);
    fixture.WriteFormula("children/alpha", "analysis", "doubled", "revenue * 2");
    fixture.WriteView("children/alpha", "analysis", "doubled");

    // Act
    Book book = Book.Load(fixture.Root);
    BookFile parent = book.Structure.Definition.File;
    Fact childFact = book.Facts["alpha::analysis.revenue"];

    // Assert
    parent.ShortName.ShouldBe("Parent");
    parent.Identifiers["project"].ShouldBe("parent-1");
    book.Formulas["summary.total"].Units.ShouldBe("millions");
    childFact.Units.ShouldBe("percent");
    book.Formulas["alpha::analysis.doubled"].Units.ShouldBe("percent");
  }

  [Fact]
  public void KeepsCheckPeriodsInsideTheBookThatOwnsTheCheck()
  {
    // Arrange
    using var fixture = new CompositionFixture();
    fixture.WriteBook("", "Parent",
      "navigation: [summary]\nbooks:\n  - id: alpha\n    path: children/alpha");
    fixture.WriteFormula("", "summary", "total", "1");
    fixture.WriteView("", "summary", "total", "2025");
    fixture.WriteBook("children/alpha", "Alpha", "navigation: [analysis]");
    fixture.WriteFacts("children/alpha", "analysis", 7, "2024");
    fixture.WriteView("children/alpha", "analysis", "revenue", "2024");

    // Act
    Book book = Book.Load(fixture.Root);

    // Assert
    book.CheckPeriods("").ShouldBe(new[] { "2025" });
    book.CheckPeriods("alpha").ShouldBe(new[] { "2024" });
  }

  [Theory]
  [InlineData("books:\n  - id: missing\n    path: children/missing", "does not contain book.yaml")]
  [InlineData(
    "books:\n  - id: same-name\n    path: children/alpha\n  - id: same_name\n    path: children/beta",
    "normalized inclusion id")]
  [InlineData("books:\n  - id: parent\n    path: .", "recursive inclusion")]
  [InlineData("books:\n  - id: outside\n    path: ../outside", "must stay inside")]
  [InlineData("books:\n  - id: books\n    path: children/alpha", "reserved")]
  public void RejectsInvalidInclusions(string declaration, string expected)
  {
    // Arrange
    using var fixture = new CompositionFixture();
    fixture.WriteBook("", "Parent", $"navigation: []\n{declaration}");
    fixture.WriteBook("children/alpha", "Alpha", "navigation: []");
    fixture.WriteBook("children/beta", "Beta", "navigation: []");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => Book.Load(fixture.Root));

    // Assert
    exception.Message.ShouldContain(expected, Case.Insensitive);
  }


  private sealed class CompositionFixture : TempFolder
  {
    public CompositionFixture()
    {
      Write("formats.yaml", "formats:\n  millions:\n    decimals: 0");
    }

    public void WriteBook(string relative, string name, string remainder)
    {
      Write(Join(relative, "book.yaml"), $"name: {name}\nshort_name: {name}\n{remainder}\n");
    }

    public void WriteView(string book, string view, string lines, string period = "2025")
    {
      string rows = string.Join('\n', lines.Split('\n').Select(line => $"  - line: {line}"));
      Write(Join(book, $"{view}/view.yaml"), $"title: {view}\ncolumns: [{period}]\nrows:\n{rows}\n");
    }

    public void WriteFacts(string book, string scope, int value, string period = "2025")
    {
      string folder = Join(book, $"{scope}/facts");
      Write($"{folder}/facts.csv", $"line_item,{period}\nrevenue,{value}\n");
      WriteBytes($"{folder}/source.png", new byte[] { 1, 2, 3 });
      Write($"{folder}/facts.yaml",
        "table:\n  title: Facts\nsource:\n  image: source.png\nline_items:\n  revenue:\n    label: Revenue\n");
    }

    public void WriteFormula(string book, string scope, string name, string formula)
    {
      Write(Join(book, $"{scope}/formulas.yaml"), $"formulas:\n  {name}:\n    formula: {formula}\n");
    }

    public void WriteCheck(string book, string scope, string formula)
    {
      Write(Join(book, $"{scope}/checks.yaml"), $"checks:\n  balances:\n    formula: {formula}\n    expect: 0\n");
    }

    public void WriteColumnFormula(string book, string scope, string name, string label, string formula)
    {
      File.AppendAllText(PathTo(Join(book, $"{scope}/formulas.yaml")),
        $"column_formulas:\n  {name}:\n    label: {label}\n    formula: {formula}\n");
    }

    private static string Join(string left, string right)
    {
      return left.Length == 0 ? right : $"{left}/{right}";
    }


  }


}