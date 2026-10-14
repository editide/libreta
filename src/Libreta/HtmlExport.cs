using System.Text.Json;
using System.Text.RegularExpressions;

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

  public static string Build(string root, string assets)
  {
    BookSnapshotPayload snapshot = Snapshot(root);
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
      JsonSerializer.Serialize(snapshot, PayloadJson.Options) +
      $"</script>\n  <script>{script}</script>");
  }

  private static BookSnapshotPayload Snapshot(string root)
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
    Dictionary<string, string> images = BookStore.SourceImages(book)
      .ToDictionary(image => image, image => DataUri(Path.Combine(root, image)));
    return new BookSnapshotPayload(catalog, views, images, lines, DateTimeOffset.Now);
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