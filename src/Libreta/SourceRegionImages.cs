using System.Text.RegularExpressions;
using SkiaSharp;

namespace Libreta;

/// <summary>One region cut from its source image, or why it could not be, beside the value the CSV records there.
/// </summary>
public record RegionImage(string Line, string Period, string Printed, string? File, string? Problem);

/// <summary>Cuts each region a fact table records out of its source image, outlined, so whoever measured the regions
/// can read back what each one boxes: an outline on the neighboring row or period passes <c>--check</c>.</summary>
public static partial class SourceRegionImages
{
  private const int Outline = 2;

  public static List<RegionImage> Write(string root, string tablePath, string folder)
  {
    (_, string metadataPath, Book book) = SourceRegionWriter.FactTable(root, tablePath);
    string csvPath = Path.ChangeExtension(metadataPath, ".csv");
    List<(Fact Fact, string Period, FactCell Cell)> boxed = book.Facts.Values
      .SelectMany(fact => fact.Cells.Select(cell => (Fact: fact, Period: cell.Key, Cell: cell.Value)))
      .Where(item => item.Cell.SourceRegion is not null &&
        BookStructure.PathComparer.Equals(Path.GetFullPath(item.Cell.SourcePath), csvPath))
      .OrderBy(item => item.Cell.SourceLine, StringComparer.Ordinal)
      .ThenBy(item => book.PeriodsFor(item.Fact.Scope).IndexOf(item.Period))
      .ToList();
    if (boxed.Count == 0)
    {
      return new List<RegionImage>();
    }

    string imagePath = boxed[0].Cell.SourceImage;
    using SKBitmap image = SKBitmap.Decode(Path.Combine(book.Root, imagePath))
      ?? throw new InvalidDataException($"'{imagePath}' is not an image Libreta can read");
    Directory.CreateDirectory(folder);
    var images = new List<RegionImage>();
    foreach ((_, string period, FactCell cell) in boxed)
    {
      SourceRegion region = cell.SourceRegion!;
      SKRectI box = SKRectI.Create(region.X, region.Y, region.Width, region.Height);
      if (box.Right > image.Width || box.Bottom > image.Height)
      {
        images.Add(new RegionImage(cell.SourceLine, period, cell.Printed, null,
          $"reaches past the {image.Width}×{image.Height} image"));
        continue;
      }
      string file = Path.Combine(folder, $"{FileName(cell.SourceLine)}.{FileName(period)}.png");
      File.WriteAllBytes(file, Crop(image, box));
      images.Add(new RegionImage(cell.SourceLine, period, cell.Printed, Path.GetFullPath(file), null));
    }
    return images;
  }

  /// <summary>The region with a margin of its own height, which shows the rows beside it, and an outline drawn just
  /// outside it so no printed character is covered.</summary>
  private static byte[] Crop(SKBitmap image, SKRectI box)
  {
    SKRectI area = SKRectI.Intersect(
      SKRectI.Inflate(box, box.Height, box.Height), SKRectI.Create(image.Width, image.Height));
    using var crop = new SKBitmap(area.Width, area.Height);
    using (var canvas = new SKCanvas(crop))
    {
      canvas.DrawBitmap(image, area, SKRect.Create(area.Width, area.Height));
      using var paint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = Outline };
      SKRect outline = SKRect.Inflate(box, Outline / 2f, Outline / 2f);
      outline.Offset(-area.Left, -area.Top);
      canvas.DrawRect(outline, paint);
    }
    using SKData png = crop.Encode(SKEncodedImageFormat.Png, 100);
    return png.ToArray();
  }

  private static string FileName(string name)
  {
    return UnsafeCharacters().Replace(name, "_");
  }

  [GeneratedRegex("[^A-Za-z0-9_-]")]
  private static partial Regex UnsafeCharacters();


}