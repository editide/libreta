using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class CsvTests
{
  [Theory]
  [InlineData("a,b,c", new[] { "a", "b", "c" })]
  [InlineData("a,,", new[] { "a", "", "" })]
  [InlineData("a,\"Smith, Jones\",c", new[] { "a", "Smith, Jones", "c" })]
  [InlineData("a,\"say \"\"hi\"\"\"", new[] { "a", "say \"hi\"" })]
  [InlineData("a,\"\"", new[] { "a", "" })]
  [InlineData("a, \"x,y\" ,b", new[] { "a", "x,y", "b" })]
  [InlineData("a,5\" screen", new[] { "a", "5\" screen" })]
  public void SplitsALine(string line, string[] expected)
  {
    // Act
    bool split = Csv.TrySplit(line, out string[] cells);

    // Assert
    split.ShouldBeTrue();
    cells.ShouldBe(expected);
  }

  [Theory]
  [InlineData("a,\"open")]
  [InlineData("a,\"closed\"trailing,b")]
  public void RejectsAMalformedQuote(string line)
  {
    // Act
    bool split = Csv.TrySplit(line, out _);

    // Assert
    split.ShouldBeFalse();
  }


}