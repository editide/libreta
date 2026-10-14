using Shouldly;
using System.Reflection;
using Xunit;

namespace Libreta.Tests;

[Collection(ConsoleCollection.Name)]
public class BookStoreTests
{
  [Fact]
  public void ReportsCatalogErrorsAndRecoversAfterTheBookIsFixed()
  {
    // Arrange
    using var fixture = new StoreFixture("missing");
    using var store = new BookStore(fixture.Root, fixture.Assets);

    // Act
    string? initialError = store.Catalog.Error;
    fixture.WriteBook("analysis");
    WaitUntil(() => store.Catalog.Error is null);

    // Assert
    initialError.ShouldNotBeNull();
    initialError.ShouldContain("unknown view");
    store.Catalog.Views.Select(view => view.Id).ShouldBe(new[] { "analysis" });
  }

  [Fact]
  public void KeepsTheLastNavigationWhileTheBookFailsToLoad()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    using var store = new BookStore(fixture.Root, fixture.Assets);

    // Act
    File.WriteAllText(Path.Combine(fixture.Root, "analysis", "formulas.yaml"),
      "formulas:\n  broken:\n    formula: revenue +\n");
    WaitUntil(() => store.Catalog.Error is not null);

    // Assert
    store.Catalog.Error.ShouldContain("ended unexpectedly");
    store.Catalog.Views.Select(view => view.Id).ShouldBe(new[] { "analysis" });
    store.TryGetView("analysis", out _).ShouldBeTrue();
  }

  [Fact]
  public void ServesALineNoTableShowsOnItsOwn()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    fixture.Write("formats.yaml", "formats:\n  millions:\n    decimals: 0\n");
    fixture.Write("analysis/formulas.yaml", "formulas:\n  doubled:\n    formula: revenue * 2\n");
    using var store = new BookStore(fixture.Root, fixture.Assets);

    // Act
    bool found = store.TryGetView("line:analysis.doubled", out ViewPayload? payload);
    bool unknown = store.TryGetView("line:analysis.missing", out _);

    // Assert
    found.ShouldBeTrue();
    payload!.Error.ShouldBeNull();
    payload!.Rows.Select(row => row.Name).ShouldBe(["analysis.doubled", "", "analysis.revenue"]);
    unknown.ShouldBeFalse();
  }

  [Fact]
  public void CoalescesAFileSaveBurstIntoOneReload()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    using var store = new BookStore(fixture.Root, fixture.Assets);
    long initialVersion = store.Version;

    // Act
    for (int index = 0; index < 5; index++)
    {
      fixture.WriteView($"Analysis {index}");
    }
    WaitUntil(() => store.Version > initialVersion);
    Thread.Sleep(300);

    // Assert
    store.Version.ShouldBe(initialVersion + 1);
    store.Catalog.Views.Single().Title.ShouldBe("Analysis 4");
  }

  [Fact]
  public void IgnoresFilesThatCannotAffectTheBook()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    using var store = new BookStore(fixture.Root, fixture.Assets);
    long initialVersion = store.Version;

    // Act
    File.WriteAllText(Path.Combine(fixture.Root, "notes.txt"), "draft");
    Thread.Sleep(300);

    // Assert
    store.Version.ShouldBe(initialVersion);
  }

  [Fact]
  public void ReloadsWhenAViewFileIsRenamedAway()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    using var store = new BookStore(fixture.Root, fixture.Assets);
    string view = Path.Combine(fixture.Root, "analysis", "view.yaml");

    // Act
    File.Move(view, Path.Combine(fixture.Root, "analysis", "view.backup"));
    WaitUntil(() => store.Catalog.Error is not null);

    // Assert
    string? error = store.Catalog.Error;
    error.ShouldNotBeNull();
    error.ShouldContain("unknown view");
  }

  [Fact]
  public void ReloadsWhenAPopulatedViewDirectoryMoves()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    using var store = new BookStore(fixture.Root, fixture.Assets);
    string view = Path.Combine(fixture.Root, "analysis");

    // Act
    Directory.Move(view, Path.Combine(fixture.Root, "moved"));
    WaitUntil(() => store.Catalog.Error is not null);

    // Assert
    string? error = store.Catalog.Error;
    error.ShouldNotBeNull();
    error.ShouldContain("unknown view");
  }

  [Fact]
  public void DoesNotRebuildAfterDisposal()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    var store = new BookStore(fixture.Root, fixture.Assets);
    long version = store.Version;
    MethodInfo rebuild = typeof(BookStore).GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // Act
    store.Dispose();
    rebuild.Invoke(store, null);

    // Assert
    store.Version.ShouldBe(version);
  }

  [Fact]
  public void ServesOnlyImagesRegisteredByTheBook()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    string secondSourceImage = fixture.WriteSourceFragment();
    using var store = new BookStore(fixture.Root, fixture.Assets);

    // Act
    bool found = store.TryGetSourceImage("analysis/facts/source.png", out string? path, out string? contentType);
    bool foundSecond = store.TryGetSourceImage(
      "analysis/facts/second-source.png", out string? secondPath, out string? secondContentType);
    bool unregistered = store.TryGetSourceImage("book.yaml", out _, out _);
    bool traversal = store.TryGetSourceImage("../source.png", out _, out _);

    // Assert
    found.ShouldBeTrue();
    path.ShouldBe(Path.Combine(fixture.Root, "analysis", "facts", "source.png"));
    contentType.ShouldBe("image/png");
    foundSecond.ShouldBeTrue();
    secondPath.ShouldBe(secondSourceImage);
    secondContentType.ShouldBe("image/png");
    unregistered.ShouldBeFalse();
    traversal.ShouldBeFalse();
  }

  [Fact]
  public void ReloadsWhenARegisteredSourceImageChanges()
  {
    // Arrange
    using var fixture = new StoreFixture("analysis");
    using var store = new BookStore(fixture.Root, fixture.Assets);
    long initialVersion = store.Version;

    // Act
    File.WriteAllBytes(fixture.SourceImage, new byte[] { 4, 5, 6 });
    WaitUntil(() => store.Version > initialVersion);

    // Assert
    store.Version.ShouldBe(initialVersion + 1);
  }

  [Fact]
  public void ReloadsIncludedChildrenAndRefreshesChangedInclusions()
  {
    // Arrange
    using var fixture = new ComposedStoreFixture(true);
    using var store = new BookStore(fixture.Root, fixture.Assets);
    long initialVersion = store.Version;

    // Act
    fixture.WriteChildView("Updated child");
    WaitUntil(() => store.Catalog.Views.Single().Title == "Updated child");
    fixture.WriteParent(false);
    WaitUntil(() => store.Version > initialVersion + 1 && store.Catalog.Views.Count == 0);

    // Assert
    store.Catalog.Error.ShouldBeNull();
    store.Catalog.Views.ShouldBeEmpty();
  }

  [Fact]
  public void DoesNotReloadForChangesInsideAnExcludedChildBook()
  {
    // Arrange
    using var fixture = new ComposedStoreFixture(false);
    using var store = new BookStore(fixture.Root, fixture.Assets);
    long initialVersion = store.Version;

    // Act
    fixture.WriteChildView("Ignored update");
    Thread.Sleep(300);

    // Assert
    store.Version.ShouldBe(initialVersion);
  }

  private static void WaitUntil(Func<bool> condition)
  {
    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition() && DateTime.UtcNow < deadline)
    {
      Thread.Sleep(20);
    }
    condition().ShouldBeTrue();
  }


}

internal sealed class ComposedStoreFixture : TempFolder
{
  public ComposedStoreFixture(bool includeChild)
  {
    Assets = PathTo("assets");
    Directory.CreateDirectory(Assets);
    Write("formats.yaml", "formats:\n  millions:\n    decimals: 0\n");
    Write("children/alpha/book.yaml", "name: Alpha\nshort_name: Alpha\nunits: millions\nnavigation: [analysis]\n");
    Write("children/alpha/analysis/formulas.yaml", "formulas:\n  result:\n    formula: 1\n");
    WriteChildView("Child");
    WriteParent(includeChild);
  }

  public string Assets { get; }

  public void WriteParent(bool includeChild)
  {
    Write("book.yaml", includeChild
      ? "name: Parent\nshort_name: Parent\nunits: millions\nnavigation:\n  - book: alpha\n" +
        "books:\n  - id: alpha\n    path: children/alpha\n"
      : "name: Parent\nshort_name: Parent\nunits: millions\nnavigation: []\n");
  }

  public void WriteChildView(string title)
  {
    Write("children/alpha/analysis/view.yaml", $"title: {title}\ncolumns: [2025]\nrows:\n  - line: result\n");
  }


}

internal sealed class StoreFixture : TempFolder
{
  public StoreFixture(string navigation)
  {
    Assets = PathTo("assets");
    Directory.CreateDirectory(Assets);
    WriteBook(navigation);
    Write("formats.yaml", "defaults:\n  units: millions\n  unresolved: \"\"\n  dash: \"-\"\nformats: {}\n");
    Write("analysis/facts/source.csv", "line_item,2024\nrevenue,1\n");
    Write("analysis/facts/source.yaml", """
      table:
        title: Source
      source:
        image: source.png
      line_items:
        revenue:
          label: Revenue
      """);
    SourceImage = PathTo("analysis/facts/source.png");
    File.WriteAllBytes(SourceImage, new byte[] { 1, 2, 3 });
    WriteView("Analysis");
  }

  public string Assets { get; }
  public string SourceImage { get; }

  public void WriteBook(string navigation)
  {
    Write("book.yaml", $"name: Test Book\nshort_name: TEST\nunits: millions\nnavigation:\n  - {navigation}\n");
  }

  public void WriteView(string title)
  {
    Write("analysis/view.yaml", $"title: {title}\ncolumns: [2024]\nrows:\n  - line: revenue\n");
  }

  public string WriteSourceFragment()
  {
    Write("analysis/facts/second-source.csv", "line_item,2025\nrevenue,2\n");
    Write("analysis/facts/second-source.yaml", """
      table:
        title: Second source
      source:
        image: second-source.png
      line_items:
        revenue:
          label: Revenue
      """);
    WriteBytes("analysis/facts/second-source.png", new byte[] { 4, 5, 6 });
    return PathTo("analysis/facts/second-source.png");
  }


}