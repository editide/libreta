using System.Globalization;
using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class FormatterTests
{
  [Fact]
  public void InheritsFormatsAndOverridesIndividualProperties()
  {
    // Arrange
    using var folder = new TempFolder();
    Directory.CreateDirectory(folder.PathTo(".git"));
    folder.Write("formats.yaml", """
      defaults:
        units: percent
        unresolved: "?"
        dash: ""
      formats:
        millions:
          decimals: 0
        percent:
          scale: 100
          decimals: 1
          suffix: "%"
      """);
    folder.Write("book/formats.yaml", """
      formats:
        millions:
          decimals: 1
      """);

    // Act
    Formatter formatter = Load(folder, "book");

    // Assert
    formatter.DefaultUnits.ShouldBe("percent");
    formatter.Unresolved.ShouldBe("?");
    formatter.Dash.ShouldBe("");
    formatter.Format(null, 0.125).ShouldBe("12.5%");
    formatter.Format("millions", -1234).ShouldBe("(1,234.0)");
    formatter.Format("percent", 0.125).ShouldBe("12.5%");
  }

  [Fact]
  public void InheritsDefaultsAndLetsNamedFormatsTakePrecedence()
  {
    // Arrange
    using var folder = new TempFolder();
    Directory.CreateDirectory(folder.PathTo(".git"));
    folder.Write("formats.yaml", """
      defaults:
        decimals: 1
        separator: false
        negative: minus
      formats:
        millions:
          separator: true
        percent:
          scale: 100
          suffix: "%"
      """);
    folder.Write("book/formats.yaml", """
      defaults:
        decimals: 2
        separator: false
      formats:
        millions:
          decimals: 0
      """);

    // Act
    Formatter formatter = Load(folder, "book");

    // Assert
    formatter.Format("millions", -1234).ShouldBe("-1,234");
    formatter.Format("percent", -0.125).ShouldBe("-12.50%");
  }

  [Fact]
  public void StopsAtTheNearestFormatAncestorWithoutAGitRoot()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("formats.yaml", """
      formats:
        percent:
          decimals: 1
      """);
    folder.Write("project/formats.yaml", """
      defaults:
        decimals: 2
      formats:
        percent:
          scale: 100
          suffix: "%"
      """);

    // Act
    Formatter formatter = Load(folder, "project/book");

    // Assert
    formatter.Format("percent", 0.125).ShouldBe("12.50%");
  }

  [Fact]
  public void FormatsWithBuiltInDefaultsWhenNoFormatsFileExists()
  {
    // Arrange
    using var folder = new TempFolder();

    // Act
    Formatter formatter = Load(folder, "book");

    // Assert
    formatter.Format(null, -1234.4).ShouldBe("(1,234)");
    formatter.Format("percent", 0.125).ShouldBe("12.5%");
    formatter.Format("dollars", -1234.4).ShouldBe("($1,234.40)");
    Should.Throw<FormatException>(() => formatter.Format("millions", 1));
  }

  [Fact]
  public void FormatsANumberWithNoUnitsFromTheFileDefaults()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/formats.yaml", """
      defaults:
        decimals: 1
        negative: minus
      """);

    // Act
    Formatter formatter = Load(folder, "book");

    // Assert
    formatter.Format(null, -1234.44).ShouldBe("-1,234.4");
    formatter.IsDate(null).ShouldBe(false);
  }

  [Fact]
  public void LayersFormatsFilesOverTheBuiltInDefaults()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/formats.yaml", """
      formats:
        millions:
          decimals: 1
      """);

    // Act
    Formatter formatter = Load(folder, "book");

    // Assert
    formatter.Format("millions", 1234.44).ShouldBe("1,234.4");
    formatter.Format("percent", 0.125).ShouldBe("12.5%");
  }

  [Fact]
  public void AUnitAFileDefinesReplacesTheBuiltInUnit()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/formats.yaml", """
      formats:
        percent:
          scale: 100
          decimals: 2
      """);

    // Act
    Formatter formatter = Load(folder, "book");

    // Assert
    formatter.Format("percent", 0.125).ShouldBe("12.50");
  }

  [Fact]
  public void FormatsDaySerialsWithADatePatternAndKeepsTheExactValueIso()
  {
    // Arrange
    using var folder = new TempFolder();
    Directory.CreateDirectory(folder.PathTo(".git"));
    folder.Write("formats.yaml", """
      formats:
        millions:
          decimals: 1
        date:
          date: MMM d, yyyy
      """);
    Formatter formatter = Load(folder, "book");

    // Act
    string display = formatter.Format("date", 46203);
    string exact = formatter.Exact("date", 46203);

    // Assert
    display.ShouldBe("Jun 30, 2026");
    exact.ShouldBe("2026-06-30");
    formatter.Exact("millions", 1.25).ShouldBe("1.25");
  }

  [Fact]
  public void ExactPreservesSmallValues()
  {
    // Arrange
    const double amount = 0.0000001;

    // Act
    string exact = Formatter.Exact(amount);

    // Assert
    double.Parse(exact, CultureInfo.InvariantCulture).ShouldBe(amount);
  }

  [Fact]
  public void IsRoundedDetectsPrecisionLossAcrossFormats()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/formats.yaml", """
      formats:
        basis_points:
          scale: 10000
          decimals: 0
          prefix: "~"
          suffix: " bp"
        percent:
          scale: 100
          decimals: 1
          suffix: "%"
        iso_date:
          date: yyyy-MM-dd
        short_date:
          date: MMM d, yyyy
        note:
          text: true
      """);
    Formatter formatter = Load(folder, "book");

    // Assert
    formatter.IsRounded("basis_points", -0.0125).ShouldBeFalse();
    formatter.IsRounded("basis_points", 0.01251).ShouldBeTrue();
    formatter.IsRounded("percent", 0.14).ShouldBeFalse();
    formatter.IsRounded("percent", 0.1415).ShouldBeTrue();
    formatter.IsRounded("percent", 0).ShouldBeFalse();
    formatter.IsRounded("iso_date", 46203).ShouldBeFalse();
    formatter.IsRounded("short_date", 46203).ShouldBeTrue();
    formatter.IsRounded("note", 0).ShouldBeFalse();
  }

  private static Formatter Load(TempFolder folder, string book)
  {
    string view = folder.PathTo($"{book}/view");
    Directory.CreateDirectory(view);
    return new Formatter(Formatter.Load(view, folder.PathTo(book), YamlFile.Deserializer()));
  }


}