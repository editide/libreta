using System.Text.Json;
using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class HtmlExportTests
{
  [Fact]
  public void EmbedsTheCatalogAndEveryView()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    JsonElement snapshot = Snapshot(html);
    snapshot.GetProperty("catalog").GetProperty("views")[0].GetProperty("id").GetString().ShouldBe("analysis");
    snapshot.GetProperty("views").GetProperty("analysis").GetProperty("title").GetString().ShouldBe("Analysis");
  }

  [Fact]
  public void EmbedsSourceImagesAsDataUris()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    JsonElement images = Snapshot(html).GetProperty("images");
    images.GetProperty("analysis/facts/source.png").GetString().ShouldBe("data:image/png;base64,AQID");
  }

  [Fact]
  public void InlinesThePageInsteadOfLinkingIt()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    html.ShouldNotContain("href=\"app.css\"");
    html.ShouldNotContain("src=\"app.js\"");
    html.ShouldNotContain("href=\"favicon.png\"");
    html.ShouldContain("<style>body { color: black; }</style>");
    html.ShouldContain("<script>refresh();</script>");
    html.ShouldContain("href=\"data:image/png;base64,Bwg=\"");
    html.IndexOf("libreta-snapshot").ShouldBeLessThan(html.IndexOf("refresh();"));
  }

  [Fact]
  public void KeepsMarkupInBookTextFromEndingTheSnapshot()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");
    fixture.WriteView("\"</script><b>Analysis</b>\"");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    Snapshot(html).GetProperty("views").GetProperty("analysis").GetProperty("title").GetString()
      .ShouldBe("</script><b>Analysis</b>");
  }

  [Fact]
  public void LeavesAbsentFieldsOutRatherThanWritingNull()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    NullPaths(Snapshot(html), "").ShouldBeEmpty();
  }

  [Fact]
  public void NamesTheViewPresentingEachLine()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    JsonElement revenue = Snapshot(html).GetProperty("lines").GetProperty("analysis.revenue");
    revenue.GetProperty("view").GetString().ShouldBe("analysis");
    revenue.TryGetProperty("columns", out _).ShouldBeFalse();
  }

  [Fact]
  public void NamesTheViewPresentingAColumnTheLinesFirstViewLeavesOut()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");
    fixture.WriteSourceFragment();
    fixture.Write("book.yaml",
      "name: Test Book\nshort_name: TEST\nunits: millions\nnavigation:\n  - analysis\n  - later\n");
    fixture.Write("later/view.yaml", "title: Later\ncolumns: [2025]\nrows:\n  - line: analysis.revenue\n");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    JsonElement revenue = Snapshot(html).GetProperty("lines").GetProperty("analysis.revenue");
    revenue.GetProperty("view").GetString().ShouldBe("analysis");
    revenue.GetProperty("columns").GetProperty("2025").GetString().ShouldBe("later");
    revenue.GetProperty("columns").TryGetProperty("2024", out _).ShouldBeFalse();
  }

  [Fact]
  public void CarriesALineNoViewPresentsOnAViewOfItsOwn()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");
    fixture.Write("analysis/formulas.yaml", "formulas:\n  doubled:\n    formula: revenue * 2\n");

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    JsonElement snapshot = Snapshot(html);
    snapshot.GetProperty("lines").GetProperty("analysis.doubled").GetProperty("view").GetString()
      .ShouldBe("line:analysis.doubled");
    snapshot.GetProperty("views").GetProperty("line:analysis.doubled").GetProperty("rows")
      .EnumerateArray().Select(row => row.GetProperty("name").GetString())
      .ShouldBe(["analysis.doubled", "", "analysis.revenue"]);
  }

  [Fact]
  public void RefusesABookThatFailsToLoad()
  {
    // Arrange
    using StoreFixture fixture = Fixture("missing");

    // Act, Assert
    Should.Throw<InvalidDataException>(() => HtmlExport.Build(fixture.Root, fixture.Assets));
  }

  [Fact]
  public void InlinesTheRealInterface()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");
    string assets = Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory, "..", "..", "..", "..", "Libreta", "wwwroot"));

    // Act
    string html = HtmlExport.Build(fixture.Root, assets);

    // Assert
    html.ShouldNotContain("src=\"app.js\"");
    html.ShouldNotContain("href=\"app.css\"");
    html.ShouldContain("function refresh()");
    html.ShouldNotContain("url(\"fonts/");
    html.ShouldContain("url(\"data:font/woff2;base64,");
  }

  private static JsonElement Snapshot(string html)
  {
    const string opening = "<script type=\"application/json\" id=\"libreta-snapshot\">";
    int start = html.IndexOf(opening, StringComparison.Ordinal);
    start.ShouldBeGreaterThanOrEqualTo(0);
    start += opening.Length;
    int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
    return JsonDocument.Parse(html[start..end]).RootElement;
  }

  private static IEnumerable<string> NullPaths(JsonElement element, string path)
  {
    return element.ValueKind switch
    {
      JsonValueKind.Null => new[] { path },
      JsonValueKind.Object => element.EnumerateObject()
        .SelectMany(property => NullPaths(property.Value, $"{path}.{property.Name}")),
      JsonValueKind.Array => element.EnumerateArray().SelectMany((item, index) => NullPaths(item, $"{path}[{index}]")),
      _ => Enumerable.Empty<string>()
    };
  }

  private static StoreFixture Fixture(string navigation)
  {
    var fixture = new StoreFixture(navigation);
    File.WriteAllText(Path.Combine(fixture.Root, "formats.yaml"),
      "defaults:\n  units: millions\n  unresolved: \"\"\n  dash: \"-\"\nformats:\n  millions:\n    decimals: 0\n");
    WriteAssets(fixture.Assets);
    return fixture;
  }

  private static void WriteAssets(string assets)
  {
    File.WriteAllText(Path.Combine(assets, "index.html"), """
      <html>
      <head>
        <link rel="icon" href="favicon.png" type="image/png">
        <link rel="stylesheet" href="app.css">
      </head>
      <body>
        <script src="app.js"></script>
      </body>
      </html>
      """);
    File.WriteAllText(Path.Combine(assets, "app.css"), "body { color: black; }");
    File.WriteAllText(Path.Combine(assets, "app.js"), "refresh();");
    File.WriteAllBytes(Path.Combine(assets, "favicon.png"), new byte[] { 7, 8 });
  }


}