using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class SourceRegionsTests
{
  private const string Metadata = """
    table:
      title: Facts

    # Captured from the filing.
    source:
      url: https://example.com/10-k
      note: Selected lines.
    defaults:
      units: millions
    line_items:
      revenue:
        label: Revenue
        image_regions:
          2024: { x: 1, y: 1, width: 1, height: 1 }
        note: Net of returns.
      cost:
        label: Cost of revenue # as printed
        printed: negated
      margin:
        label: Margin
        image_regions:
          2025: { x: 2, y: 2, width: 2, height: 2 }
    """;

  [Fact]
  public void ApplyReplacesEveryRegionAndSetsTheImageWithoutDisturbingTheRest()
  {
    // Arrange
    var regions = new Dictionary<string, Dictionary<string, SourceRegion>>
    {
      ["revenue"] = new Dictionary<string, SourceRegion>
      {
        ["2024"] = Region(10, 20, 30, 40),
        ["H1 2025"] = Region(50, 60, 70, 80)
      },
      ["cost"] = new Dictionary<string, SourceRegion> { ["2024"] = Region(5, 6, 7, 8) }
    };

    // Act
    string written = SourceRegionWriter.Apply(Metadata, "facts.png", regions);

    // Assert
    written.ShouldBe("""
      table:
        title: Facts

      # Captured from the filing.
      source:
        url: https://example.com/10-k
        image: facts.png
        note: Selected lines.
      defaults:
        units: millions
      line_items:
        revenue:
          label: Revenue
          note: Net of returns.
          image_regions:
            2024: { x: 10, y: 20, width: 30, height: 40 }
            H1 2025: { x: 50, y: 60, width: 70, height: 80 }
        cost:
          label: Cost of revenue # as printed
          printed: negated
          image_regions:
            2024: { x: 5, y: 6, width: 7, height: 8 }
        margin:
          label: Margin
      """);
  }

  [Fact]
  public void ApplyReplacesAnExistingImageAndAddsALineTheMetadataDoesNotList()
  {
    // Arrange
    string metadata = """
      source:
        image: old.png
      line_items:
        revenue:
          label: Revenue

      """;
    var regions = new Dictionary<string, Dictionary<string, SourceRegion>>
    {
      ["other_income"] = new Dictionary<string, SourceRegion> { ["2025"] = Region(1, 2, 3, 4) }
    };

    // Act
    string written = SourceRegionWriter.Apply(metadata, "new: shot.png", regions);

    // Assert
    written.ShouldBe("""
      source:
        image: "new: shot.png"
      line_items:
        revenue:
          label: Revenue
        other_income:
          image_regions:
            2025: { x: 1, y: 2, width: 3, height: 4 }

      """);
  }

  [Fact]
  public void WriteRecordsRegionsInPeriodOrderAndReportsWhatIsStillUnboxed()
  {
    // Arrange
    using RegionBookFixture fixture = new();
    fixture.Write("regions.json", """
      {
        "image": "facts.png",
        "regions": {
          "revenue": { "2025": { "x": 1, "y": 2, "width": 3, "height": 4 }, "2024": { "x": 5, "y": 6, "width": 7, "height": 8 } }
        }
      }
      """);

    // Act
    SourceRegionResult result = SourceRegionWriter.Write(
      fixture.Root, fixture.PathTo("facts/facts.yaml"), fixture.PathTo("regions.json"));

    // Assert
    result.Regions.ShouldBe(2);
    result.Unboxed.Select(fact => $"{fact.Line}[{fact.Period}]").ShouldBe(["cost[2024]"]);
    File.ReadAllText(fixture.PathTo("facts/facts.yaml")).ShouldContain("""
          image_regions:
            2024: { x: 5, y: 6, width: 7, height: 8 }
            2025: { x: 1, y: 2, width: 3, height: 4 }
      """);
    Book.Load(fixture.Root).Facts["revenue"].Cells["2025"].SourceRegion.ShouldNotBeNull().X.ShouldBe(1);
  }

  [Fact]
  public void WriteWithoutAnImageUpdatesOnlyTheRegionsItNames()
  {
    // Arrange
    using RegionBookFixture fixture = new();
    fixture.Write("full.json", """
      { "image": "facts.png", "regions": { "revenue": { "2024": { "x": 1, "y": 1, "width": 1, "height": 1 },
        "2025": { "x": 2, "y": 2, "width": 2, "height": 2 } } } }
      """);
    fixture.Write("fix.json", """
      { "regions": { "revenue": { "2025": { "x": 9, "y": 9, "width": 9, "height": 9 } },
        "cost": { "2024": { "x": 3, "y": 3, "width": 3, "height": 3 } } } }
      """);
    SourceRegionWriter.Write(fixture.Root, fixture.PathTo("facts/facts.yaml"), fixture.PathTo("full.json"));

    // Act
    SourceRegionResult result = SourceRegionWriter.Write(
      fixture.Root, fixture.PathTo("facts/facts.yaml"), fixture.PathTo("fix.json"));

    // Assert
    result.Unboxed.ShouldBeEmpty();
    Dictionary<string, FactCell> revenue = Book.Load(fixture.Root).Facts["revenue"].Cells;
    revenue["2024"].SourceRegion.ShouldNotBeNull().X.ShouldBe(1);
    revenue["2025"].SourceRegion.ShouldNotBeNull().X.ShouldBe(9);
  }

  [Theory]
  [InlineData("""{ "regions": { "revenu": { "2025": { "x": 1, "y": 1, "width": 1, "height": 1 } } } }""", "revenu")]
  [InlineData("""{ "regions": { "revenue": { "2026": { "x": 1, "y": 1, "width": 1, "height": 1 } } } }""", "2026")]
  [InlineData("""{ "regions": { "revenue": { "2025": { "x": 1, "y": 1, "width": 0, "height": 1 } } } }""", "width")]
  [InlineData("""{ "image": "missing.png", "regions": {} }""", "missing.png")]
  public void WriteRejectsRegionsTheBookCannotLoadAndLeavesTheTableUntouched(string json, string expected)
  {
    // Arrange
    using RegionBookFixture fixture = new();
    fixture.Write("regions.json", json);
    string before = File.ReadAllText(fixture.PathTo("facts/facts.yaml"));

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => SourceRegionWriter.Write(
      fixture.Root, fixture.PathTo("facts/facts.yaml"), fixture.PathTo("regions.json")));

    // Assert
    exception.Message.ShouldContain(expected);
    File.ReadAllText(fixture.PathTo("facts/facts.yaml")).ShouldBe(before);
  }

  [Fact]
  public void WriteRejectsAFileThatIsNotAFactTableInTheBook()
  {
    // Arrange
    using RegionBookFixture fixture = new();
    fixture.Write("regions.json", """{ "regions": {} }""");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() => SourceRegionWriter.Write(
      fixture.Root, fixture.PathTo("book.yaml"), fixture.PathTo("regions.json")));

    // Assert
    exception.Message.ShouldContain("not a fact table");
  }

  [Fact]
  public void UnboxedListsOnlyValuedFactsOfTablesWithAnImage()
  {
    // Arrange
    using RegionBookFixture fixture = new();
    fixture.Write("facts/plain.csv", "line_item,2024,2025\nshares,10,11");
    fixture.Write("facts/plain.yaml", "line_items:\n  shares:\n    label: Shares");

    // Act
    List<UnboxedFact> unboxed = SourceRegionWriter.Unboxed(Book.Load(fixture.Root));

    // Assert
    unboxed.Select(fact => $"{fact.Table}: {fact.Line}[{fact.Period}]").ShouldBe(
    [
      "facts/facts.yaml: cost[2024]",
      "facts/facts.yaml: revenue[2024]",
      "facts/facts.yaml: revenue[2025]"
    ]);
  }

  private static SourceRegion Region(int x, int y, int width, int height)
  {
    return new SourceRegion { X = x, Y = y, Width = width, Height = height };
  }

  private sealed class RegionBookFixture : TempFolder
  {
    public RegionBookFixture()
    {
      Write("book.yaml", "name: Test\nshort_name: Test\nnavigation: []");
      Write("facts/facts.csv", "line_item,2024,2025\nrevenue,1,2\ncost,3,");
      Write("facts/facts.yaml", """
        source:
          image: facts.png
        line_items:
          revenue:
            label: Revenue
          cost:
            label: Cost
        """);
      WriteBytes("facts/facts.png", [1, 2, 3]);
    }


  }


}