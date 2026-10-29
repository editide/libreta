using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class PictureCacheTests
{
  [Fact]
  public void APictureEncodedOnceIsReadBackWithoutEncodingAgain()
  {
    // Arrange
    using var folder = new TempFolder();
    var cache = new PictureCache(folder.PathTo("pictures"));
    int encodings = 0;
    cache.Get([1, 2, 3], "webp", () => { encodings++; return [9, 8]; });

    // Act
    byte[]? again = new PictureCache(folder.PathTo("pictures")).Get([1, 2, 3], "webp",
      () => { encodings++; return [7]; });

    // Assert
    again.ShouldBe([9, 8]);
    encodings.ShouldBe(1);
  }

  [Fact]
  public void APictureWithNoEncodingIsRememberedAsHavingNone()
  {
    // Arrange
    using var folder = new TempFolder();
    var cache = new PictureCache(folder.PathTo("pictures"));
    int encodings = 0;
    cache.Get([1, 2, 3], "webp", () => { encodings++; return null; });

    // Act
    byte[]? again = cache.Get([1, 2, 3], "webp", () => { encodings++; return [7]; });

    // Assert
    again.ShouldBeNull();
    encodings.ShouldBe(1);
  }

  [Fact]
  public void AChangedPictureOrEncodingIsEncodedAfresh()
  {
    // Arrange
    using var folder = new TempFolder();
    var cache = new PictureCache(folder.PathTo("pictures"));
    cache.Get([1, 2, 3], "webp", () => [9]);

    // Act
    byte[]? edited = cache.Get([1, 2, 4], "webp", () => [8]);
    byte[]? narrower = cache.Get([1, 2, 3], "webp width 700", () => [7]);

    // Assert
    edited.ShouldBe([8]);
    narrower.ShouldBe([7]);
  }

  [Fact]
  public void ABuildWithChangedCodeEncodesAfresh()
  {
    // Arrange
    using var folder = new TempFolder();
    new PictureCache(folder.PathTo("pictures"), Guid.NewGuid()).Get([1, 2, 3], "webp", () => [9]);

    // Act
    byte[]? rebuilt = new PictureCache(folder.PathTo("pictures"), Guid.NewGuid()).Get([1, 2, 3], "webp", () => [8]);

    // Assert
    rebuilt.ShouldBe([8]);
  }

  [Fact]
  public void AnEntryUnusedForAMonthIsDeleted()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.WriteBytes("pictures/stale", [1]);
    folder.WriteBytes("pictures/recent", [1]);
    File.SetLastWriteTimeUtc(folder.PathTo("pictures/stale"), DateTime.UtcNow.AddDays(-31));

    // Act
    new PictureCache(folder.PathTo("pictures")).Get([1, 2, 3], "webp", () => [9]);

    // Assert
    File.Exists(folder.PathTo("pictures/stale")).ShouldBeFalse();
    File.Exists(folder.PathTo("pictures/recent")).ShouldBeTrue();
  }


}