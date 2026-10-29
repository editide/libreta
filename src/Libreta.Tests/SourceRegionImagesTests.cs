using Shouldly;
using SkiaSharp;
using Xunit;

namespace Libreta.Tests;

public class SourceRegionImagesTests
{
  [Fact]
  public void CropsEachRegionWithItsOutlineAndWhatTheCsvRecords()
  {
    // Arrange
    using var fixture = new ImageBookFixture("2024: { x: 40, y: 20, width: 20, height: 10 }");

    // Act
    List<RegionImage> images = SourceRegionImages.Write(fixture.Root, "facts/facts.yaml", fixture.PathTo("regions"));

    // Assert
    RegionImage image = images.ShouldHaveSingleItem();
    image.Line.ShouldBe("revenue");
    image.Period.ShouldBe("2024");
    image.Printed.ShouldBe("1.5");
    image.Problem.ShouldBeNull();
    using SKBitmap crop = SKBitmap.Decode(image.File);
    crop.Width.ShouldBe(40);
    crop.Height.ShouldBe(30);
    crop.GetPixel(9, 15).ShouldBe(SKColors.Red);
    crop.GetPixel(20, 15).ShouldBe(SKColors.White);
  }

  [Fact]
  public void KeepsACropNearTheEdgeInsideTheImage()
  {
    // Arrange
    using var fixture = new ImageBookFixture("2024: { x: 0, y: 0, width: 20, height: 10 }");

    // Act
    RegionImage image = SourceRegionImages.Write(fixture.Root, "facts/facts.yaml", fixture.PathTo("regions")).Single();

    // Assert
    using SKBitmap crop = SKBitmap.Decode(image.File);
    crop.Width.ShouldBe(30);
    crop.Height.ShouldBe(20);
  }

  [Fact]
  public void ReportsARegionOutsideItsImage()
  {
    // Arrange
    using var fixture = new ImageBookFixture("2024: { x: 180, y: 100, width: 40, height: 20 }");

    // Act
    RegionImage image = SourceRegionImages.Write(fixture.Root, "facts/facts.yaml", fixture.PathTo("regions")).Single();

    // Assert
    image.File.ShouldBeNull();
    image.Problem.ShouldBe("reaches past the 100×60 image");
  }

  [Fact]
  public void NamesAFileForEveryPeriodItCanHold()
  {
    // Arrange
    using var fixture = new ImageBookFixture("H1 2025: { x: 40, y: 20, width: 20, height: 10 }", "H1 2025");

    // Act
    RegionImage image = SourceRegionImages.Write(fixture.Root, "facts/facts.yaml", fixture.PathTo("regions")).Single();

    // Assert
    Path.GetFileName(image.File).ShouldBe("revenue.H1_2025.png");
  }

  [Fact]
  public void RejectsAPathThatIsNotAFactTable()
  {
    // Arrange
    using var fixture = new ImageBookFixture("2024: { x: 40, y: 20, width: 20, height: 10 }");

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(() =>
      SourceRegionImages.Write(fixture.Root, "book.yaml", fixture.PathTo("regions")));

    // Assert
    exception.Message.ShouldContain("'book.yaml' is not a fact table");
  }

  private sealed class ImageBookFixture : TempFolder
  {
    public ImageBookFixture(string region, string period = "2024")
    {
      Write("book.yaml", "name: Test\nshort_name: Test\nnavigation: []");
      Write("facts/facts.csv", $"line_item,{period}\nrevenue,1.5\n");
      Write("facts/facts.yaml", $"""
        source:
          image: facts.png
        line_items:
          revenue:
            image_regions:
              {region}
        """);
      using var bitmap = new SKBitmap(100, 60);
      bitmap.Erase(SKColors.White);
      using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
      WriteBytes("facts/facts.png", png.ToArray());
    }


  }


}