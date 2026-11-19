using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class NavigationHeadingTests
{
  [Fact]
  public void GroupsViewsUnderHeadingsWithoutChangingTheirIds()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", """
      name: Headings
      short_name: HEAD
      navigation:
        - heading: Statements
          children:
            - operations
            - heading: Detail
              children:
                - balance
        - parent:
            - heading: Breakdown
              children:
                - parent/child
      """);
    WriteView(fixture, "operations");
    WriteView(fixture, "balance");
    WriteView(fixture, "parent");
    WriteView(fixture, "parent/child");

    // Act
    ViewCatalog catalog = CatalogLoader.Load(Book.Load(fixture.Root), 1);

    // Assert
    catalog.Views.Select(view => view.Id).ShouldBe(new[] { "operations", "balance", "parent", "parent/child" });
    catalog.Views[0].Headings.ShouldBe(new[] { new CatalogHeading("Statements", 0) });
    catalog.Views[1].Headings.ShouldBe(new[] { new CatalogHeading("Statements", 0), new CatalogHeading("Detail", 0) });
    catalog.Views[2].Headings.ShouldBeEmpty();
    catalog.Views[2].Book.ShouldBeNull();
    catalog.Views[3].Headings.ShouldBe(new[] { new CatalogHeading("Breakdown", 1) });
  }

  [Fact]
  public void PlacesAnIncludedBookAndItsOwnHeadingsUnderTheParentHeading()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", """
      name: Collection
      short_name: COLL
      navigation:
        - heading: Companies
          children:
            - book: alpha
      books:
        - id: alpha
          path: children/alpha
      """);
    fixture.Write("children/alpha/book.yaml", """
      name: Alpha
      short_name: ALPHA
      navigation:
        - heading: Statements
          children: [analysis]
      """);
    WriteView(fixture, "children/alpha/analysis");

    // Act
    ViewCatalog catalog = CatalogLoader.Load(Book.Load(fixture.Root), 1);

    // Assert
    ViewCatalogEntry entry = catalog.Views.Single();
    entry.Id.ShouldBe("alpha/analysis");
    entry.Book.ShouldBe("alpha");
    entry.Headings.ShouldBe(new[] { new CatalogHeading("Companies", 0), new CatalogHeading("Statements", 1) });
  }

  [Fact]
  public void ReadsAHeadingNamedLikeAViewAsAHeadingAndAViewNamedHeadingAsAView()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", """
      name: Names
      short_name: NAMES
      navigation:
        - heading: operations
          children: [operations]
        - heading:
            - heading/detail
      """);
    WriteView(fixture, "operations");
    WriteView(fixture, "heading");
    WriteView(fixture, "heading/detail");

    // Act
    ViewCatalog catalog = CatalogLoader.Load(Book.Load(fixture.Root), 1);

    // Assert
    catalog.Views.Select(view => view.Id).ShouldBe(new[] { "operations", "heading", "heading/detail" });
    catalog.Views[0].Headings.ShouldBe(new[] { new CatalogHeading("operations", 0) });
    catalog.Views[1].Headings.ShouldBeEmpty();
  }

  [Theory]
  [InlineData("- heading: Group\n  children: []", "Navigation heading 'Group' must list children")]
  [InlineData("- heading: Group", "Navigation heading 'Group' must list children")]
  [InlineData("- heading: ''\n  children: [alpha]", "A navigation heading must have a name")]
  [InlineData("- heading: Group\n  children: [alpha]\n  extra: 1", "Each navigation item must be")]
  [InlineData("- heading: Group\n  children: [alpha]\n- heading: Group\n  children: [beta]",
    "Navigation repeats heading 'Group'")]
  [InlineData("- heading: Group\n  children: [alpha]", "navigation omits: beta")]
  [InlineData("- alpha:\n    - heading: Group\n      children: [beta]", "Navigation view 'beta' is not nested beneath 'alpha'")]
  public void RejectsMalformedHeadings(string navigation, string message)
  {
    // Arrange
    using var fixture = new ViewFixture();
    string indented = string.Join('\n', navigation.Split('\n').Select(line => $"  {line}"));
    fixture.Write("book.yaml", $"name: Bad\nshort_name: BAD\nnavigation:\n{indented}\n");
    WriteView(fixture, "alpha");
    WriteView(fixture, "beta");

    // Act
    Exception error = Should.Throw<Exception>(() => CatalogLoader.Load(Book.Load(fixture.Root), 1));

    // Assert
    error.Message.ShouldContain(message);
  }

  [Fact]
  public void NavigationOrderThroughHeadingsDecidesWhichViewPresentsALine()
  {
    // Arrange
    using var fixture = new ViewFixture();
    fixture.Write("book.yaml", """
      name: Shared inputs
      short_name: SHARED
      navigation:
        - spread
        - heading: Inputs
          children:
            - inputs/secondary
            - inputs/primary
      """);
    fixture.Write("inputs/facts/figures.csv", "line_item,2026\nrevenue,100\n");
    fixture.Write("inputs/facts/figures.yaml", "table:\n  title: Figures\n");
    foreach (string id in new[] { "primary", "secondary" })
    {
      fixture.Write($"inputs/{id}/view.yaml", "title: Inputs\ncolumns: [2026]\nrows:\n  - line: revenue\n");
    }
    fixture.Write("spread/formulas.yaml", "formulas:\n  revenue:\n    formula: inputs.revenue[\"2026\"]\n");
    fixture.Write("spread/view.yaml", "title: Spread\ncolumns: [Spread]\nrows:\n  - line: revenue\n");

    // Act
    FormulaReferencePayload reference = fixture.Payload("spread").Rows.Single().Formula!.References.Single();

    // Assert
    reference.SourceView.ShouldBe("inputs/secondary");
  }

  private static void WriteView(ViewFixture fixture, string folder)
  {
    fixture.Write($"{folder}/formulas.yaml", "formulas:\n  one:\n    formula: \"1\"\n");
    fixture.Write($"{folder}/view.yaml", $"title: {folder}\ncolumns: [2025]\nrows:\n  - line: one\n");
  }


}