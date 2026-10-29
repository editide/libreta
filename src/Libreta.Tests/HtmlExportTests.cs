using System.Text.Json;
using Shouldly;
using SkiaSharp;
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
  public void EmbedsAFlatImageAsWebpWithEveryPixelKept()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");
    using SKBitmap flat = Flat();
    byte[] png = Encode(flat, SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(fixture.SourceImage, png);

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    byte[] embedded = Embedded(html, "image/webp");
    embedded.Length.ShouldBeLessThan(png.Length);
    using SKBitmap decoded = SKBitmap.Decode(embedded);
    decoded.Pixels.ShouldBe(flat.Pixels);
  }

  [Fact]
  public void EmbedsAPhotographAsLossyWebp()
  {
    // Arrange
    using StoreFixture fixture = Fixture("analysis");
    using SKBitmap photograph = Photograph();
    byte[] png = Encode(photograph, SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(fixture.SourceImage, png);

    // Act
    string html = HtmlExport.Build(fixture.Root, fixture.Assets);

    // Assert
    byte[] embedded = Embedded(html, "image/webp");
    embedded.Length.ShouldBeLessThan(png.Length / 4);
    using SKBitmap decoded = SKBitmap.Decode(embedded);
    (decoded.Width, decoded.Height).ShouldBe((400, 300));
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

  private static byte[] Embedded(string html, string type)
  {
    string image = Snapshot(html).GetProperty("images").GetProperty("analysis/facts/source.png").GetString()!;
    image.ShouldStartWith($"data:{type};base64,");
    return Convert.FromBase64String(image[(image.IndexOf(',') + 1)..]);
  }

  /// <summary>Flat colors and hard edges, which a lossless encoding keeps exactly at less than the lossy size.</summary>
  private static SKBitmap Flat()
  {
    var bitmap = new SKBitmap(400, 300);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var ink = new SKPaint { Color = new SKColor(0x22, 0x22, 0x22) };
    var random = new Random(7);
    for (int line = 0; line < 20; line++)
    {
      for (int x = 10; x < 390; x += random.Next(3, 9))
      {
        canvas.DrawRect(x, 10 + line * 14, random.Next(1, 3), random.Next(4, 9), ink);
      }
    }
    return bitmap;
  }

  /// <summary>Smooth color with grain, which a lossy encoding shrinks far more than a lossless one.</summary>
  private static SKBitmap Photograph()
  {
    var bitmap = new SKBitmap(400, 300);
    var random = new Random(7);
    for (int y = 0; y < bitmap.Height; y++)
    {
      for (int x = 0; x < bitmap.Width; x++)
      {
        int grain = random.Next(-12, 13);
        bitmap.SetPixel(x, y, new SKColor(Channel(x * 255 / 400 + grain), Channel(y * 255 / 300 + grain),
          Channel(160 + grain)));
      }
    }
    return bitmap;
  }

  private static byte Channel(int value)
  {
    return (byte)Math.Clamp(value, 0, 255);
  }

  private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
  {
    using SKData data = bitmap.Encode(format, quality);
    return data.ToArray();
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