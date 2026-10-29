using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Libreta;

public record BookSnapshotPayload(
  ViewCatalog Catalog,
  Dictionary<string, ViewPayload> Views,
  Dictionary<string, string> Images,
  Dictionary<string, PresentedLine> Lines,
  DateTimeOffset Exported);

/// <summary>The first view in navigation order presenting a line, and for each column a different view presents first,
/// that view, or the line's own view when none presents it: what <c>--cell</c> would open, for a page with no server
/// to ask.</summary>
public record PresentedLine(string View, Dictionary<string, string>? Columns);

/// <summary>The whole book as one serverless page: every payload embedded and the page's files inlined.</summary>
public static class HtmlExport
{
  private const string IconTag = "<link rel=\"icon\" href=\"favicon.png\" type=\"image/png\">";
  private const string StyleTag = "<link rel=\"stylesheet\" href=\"app.css\">";
  private const string ScriptTag = "<script src=\"app.js\"></script>";

  /// <summary>Twice the width a page of a filing has on screen, so its text stays sharp zoomed in, at half the bytes of
  /// a capture at twice that.</summary>
  private const int MaxImageWidth = 1400;

  public static string Build(string root, string assets)
  {
    (BookSnapshotPayload snapshot, Dictionary<string, double> scales) = Snapshot(root);
    JsonNode json = JsonSerializer.SerializeToNode(snapshot, PayloadJson.Options)!;
    ScaleRegions(json["views"]!, scales);
    string script = File.ReadAllText(Path.Combine(assets, "app.js"));
    if (script.Contains("</script", StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidDataException("app.js contains '</script' and cannot be inlined.");
    }

    string html = File.ReadAllText(Path.Combine(assets, "index.html"));
    html = Replace(html, IconTag,
      $"<link rel=\"icon\" href=\"{DataUri(Path.Combine(assets, "favicon.png"))}\" type=\"image/png\">");
    string style = InlineFonts(File.ReadAllText(Path.Combine(assets, "app.css")), assets);
    html = Replace(html, StyleTag, $"<style>{style}</style>");
    return Replace(html, ScriptTag,
      // The default encoder escapes <, > and &, so no string in the book can close the script element it sits in.
      "<script type=\"application/json\" id=\"libreta-snapshot\">" +
      json.ToJsonString(PayloadJson.Options) +
      $"</script>\n  <script>{script}</script>");
  }

  /// <summary>The snapshot, and how much each source image was scaled to embed it.</summary>
  private static (BookSnapshotPayload Snapshot, Dictionary<string, double> Scales) Snapshot(string root)
  {
    Book book = Book.Load(root);
    ViewCatalog catalog = CatalogLoader.Load(book, 1);
    var views = new Dictionary<string, ViewPayload>();
    foreach ((string id, View view) in book.Views)
    {
      views[id] = PayloadBuilder.Build(view, new Engine(view), 1);
    }
    Dictionary<string, PresentedLine> lines = PresentedLines(book);
    // A line no view presents gets the page a server builds for it on request.
    foreach (string line in book.Formulas.Keys.Concat(book.Facts.Keys).Where(line => !lines.ContainsKey(line)))
    {
      View view = View.ForLine(book, line);
      views[view.Id] = PayloadBuilder.Build(view, new Engine(view), 1);
      lines[line] = new PresentedLine(view.Id, null);
    }
    // Encoding the source images is the slow part of the export, so they're encoded at the same time.
    Dictionary<string, (string Uri, double Scale)> images = BookStore.SourceImages(book).AsParallel()
      .Select(image => (Image: image, Embedded: SourceImage(Path.Combine(root, image))))
      .ToDictionary(image => image.Image, image => image.Embedded);
    return (new BookSnapshotPayload(catalog, views, images.ToDictionary(image => image.Key, image => image.Value.Uri),
      lines, DateTimeOffset.Now), images.ToDictionary(image => image.Key, image => image.Value.Scale));
  }

  /// <summary>Regions are in the pixels of the image on disk, so each source's follows its image's embedded size.
  /// </summary>
  private static void ScaleRegions(JsonNode? node, Dictionary<string, double> scales)
  {
    if (node is JsonArray array)
    {
      foreach (JsonNode? item in array)
      {
        ScaleRegions(item, scales);
      }
      return;
    }
    if (node is not JsonObject json)
    {
      return;
    }
    if (json["image"] is JsonValue image && json["region"] is JsonObject region
      && scales.TryGetValue(image.GetValue<string>(), out double scale) && scale != 1)
    {
      foreach (string edge in new[] { "x", "y", "width", "height" })
      {
        int value = region[edge]!.GetValue<int>();
        region[edge] = edge is "x" or "y" ? (int)Math.Round(value * scale) : Math.Max(1, (int)Math.Round(value * scale));
      }
    }
    foreach ((string _, JsonNode? child) in json)
    {
      ScaleRegions(child, scales);
    }
  }

  private static Dictionary<string, PresentedLine> PresentedLines(Book book)
  {
    return book.PresentedCoordinates.GroupBy(coordinate => coordinate.Line).ToDictionary(group => group.Key, group =>
    {
      string view = book.PresentingView(group.Key, null, "")!;
      Dictionary<string, string> columns = group
        .Select(coordinate => (coordinate.Column, View: book.PresentingView(coordinate.Line, coordinate.Column, "")!))
        .Where(column => column.View != view)
        .ToDictionary(column => column.Column, column => column.View);
      return new PresentedLine(view, columns.Count == 0 ? null : columns);
    });
  }

  private static string DataUri(string path)
  {
    string contentType = BookStore.ImageContentType(path)
      ?? throw new InvalidDataException($"not an image the page can show: {path}");
    return $"data:{contentType};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
  }

  private static (string Uri, double Scale) SourceImage(string path)
  {
    string contentType = BookStore.ImageContentType(path)
      ?? throw new InvalidDataException($"not an image the page can show: {path}");
    byte[] original = File.ReadAllBytes(path);
    (byte[] Bytes, double Scale)? webp = contentType is "image/png" or "image/jpeg" ? WebP(original) : null;
    return webp is { } smaller && smaller.Bytes.Length < original.Length
      ? ($"data:image/webp;base64,{Convert.ToBase64String(smaller.Bytes)}", smaller.Scale)
      : ($"data:{contentType};base64,{Convert.ToBase64String(original)}", 1);
  }

  /// <summary>Lossless when that costs at most twice the lossy size, as flat colors and hard edges such as a chart's
  /// do; lossy otherwise, as for a photograph or a page of small text. Skia encodes lossless at libwebp's fastest
  /// method, which a detailed picture outgrows.</summary>
  private static (byte[] Bytes, double Scale)? WebP(byte[] picture)
  {
    using SKCodec? codec = SKCodec.Create(new SKMemoryStream(picture));
    // Browsers turn a photo by its EXIF orientation and draw a wide-gamut one in its own colors, and WebP would keep
    // neither.
    if (codec is null || codec.EncodedOrigin != SKEncodedOrigin.TopLeft || codec.Info.ColorSpace is { IsSrgb: false })
    {
      return null;
    }
    // Unpremultiplied, so lossless keeps a translucent pixel's color exactly.
    using SKBitmap? bitmap = SKBitmap.Decode(codec, codec.Info.WithAlphaType(
      codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
    if (bitmap is null)
    {
      return null;
    }
    double scale = Math.Min(1, (double)MaxImageWidth / bitmap.Width);
    using SKBitmap? narrower = scale == 1 ? null : bitmap.Resize(bitmap.Info.WithSize(MaxImageWidth,
      Math.Max(1, (int)Math.Round(bitmap.Height * scale))), new SKSamplingOptions(SKCubicResampler.Mitchell));
    using SKPixmap pixmap = (narrower ?? bitmap).PeekPixels();
    using SKData lossless = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 25));
    using SKData lossy = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80));
    return ((lossless.Size <= 2 * lossy.Size ? lossless : lossy).ToArray(), scale);
  }

  private static string InlineFonts(string css, string assets)
  {
    return Regex.Replace(css, @"url\(""(fonts/[^""]+\.woff2)""\)", match =>
    {
      byte[] font = File.ReadAllBytes(Path.Combine(assets, match.Groups[1].Value));
      return $"url(\"data:font/woff2;base64,{Convert.ToBase64String(font)}\")";
    });
  }

  /// <summary>Fails rather than leaving the page linked to a file the snapshot does not carry.</summary>
  private static string Replace(string html, string tag, string replacement)
  {
    if (!html.Contains(tag, StringComparison.Ordinal))
    {
      throw new InvalidDataException($"index.html no longer contains {tag}; update the export to match.");
    }
    return html.Replace(tag, replacement, StringComparison.Ordinal);
  }


}