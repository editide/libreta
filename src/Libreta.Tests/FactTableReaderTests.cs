using Shouldly;
using Xunit;
using YamlDotNet.Serialization;

namespace Libreta.Tests;

public class FactTableReaderTests
{
  private const string Plain = """
    table:
      title: Net bookings
      fiscal_year_end: 03-31
    source:
      url: https://example.com/10-q.htm
      image: 2021-12-31.png
      note: "Quoted: with a colon"
      details:
        Document: sec-10-q.html
        Page: 12
    defaults:
      units: millions
      currency: USD
      printed: negated
      sign: contra
      missing: zero
    line_items:
      net_bookings:
        label: Net Bookings
        units: ~
        note: |
          Two lines,
          kept apart.
        cell_notes:
          FY2021 Q3: 'It''s restated'
        image_regions:
          FY2021 Q3:
            x: 319
            y: 0
            width: 40
            height: -15
          '2024': {x: 1, y: 2, width: 3, height: 4}
      2024:
        label: 12
        printed: Null
        sign: null
        missing:
    """;

  public static TheoryData<string> Tables => new TheoryData<string>
  {
    Plain,
    "",
    "# only a comment\n",
    "table:\n",
    "table: ~\n",
    "table:\n  title: x\n...\n",
    "%YAML 1.2\n---\ntable:\n  title: x\n",
    "table:\n  title: x\n---\ntable:\n  title: y\n",
    "table: x\n",
    "table: [x]\n",
    "line_items: []\n",
    "line_items: ~\n",
    "line_items:\n  a:\n",
    "line_items:\n  a:\n    label: x\n    label: y\n",
    "line_items:\n  a: {label: x}\n  b: {label: z}\n  a: {label: y}\n",
    "line_items:\n  ~:\n    label: x\n",
    "line_items:\n  a:\n    label: \"~\"\n    note: ''\n    units: nUll\n",
    "line_items:\n  a:\n    label: >\n      ~\n",
    "line_items:\n  a:\n    label: [x]\n",
    "line_items:\n  a:\n    bogus: 1\n",
    "Line_items: {}\n",
    "line_items:\n  ? [a]\n  : {label: x}\n",
    "line_items:\n  a:\n    label: !!str 12\n",
    "line_items:\n  a: &x {label: q}\n  b: *x\n",
    "defaults: &d\n  units: m\ntable: *d\n",
    "<<: {table: {title: x}}\n",
    "source:\n  details: ~\n",
    "source:\n  details:\n    a: 1\n    b: ~\n    c:\n",
    "source:\n  details:\n    a: [1, 2]\n",
    "source:\n  details:\n    a: {b: c}\n",
    "line_items:\n  a:\n    image_regions:\n      '2024':\n",
    "line_items:\n  a:\n    image_regions: ~\n",
    "line_items:\n  a:\n    image_regions:\n      '2024': {x: , y: 1, width: 1, height: 1}\n",
    "line_items:\n  a:\n    image_regions:\n      '2024': {x: 0x10, y: +5, width: 1_000, height: 012}\n",
    "line_items:\n  a:\n    image_regions:\n      '2024': {x: '5', y: ' 6', width: \"7\", height: -0}\n",
    "line_items:\n  a:\n    image_regions:\n      '2024': {x: 1.0, y: 1, width: 1, height: 1}\n",
    "line_items:\n  a:\n    image_regions:\n      '2024': {x: 1e2, y: 1, width: 1, height: 1}\n",
    "line_items:\n  a:\n    image_regions:\n      '2024': {x: 999999999, y: 2147483647, width: 2147483648, height: 1}\n",
    "line_items:\n  a:\n    image_regions:\n      '2024': {x: 1, y: 1, width: 1, height: 1, depth: 1}\n",
    "line_items:\n  a:\n    label: \"unterminated\n",
    "line_items:\n  a:\n   label: x\n  label: y\n",
  };

  [Theory]
  [MemberData(nameof(Tables))]
  public void ReadsEveryTableAsYamlDotNetDoes(string yaml)
  {
    // Arrange
    IDeserializer deserializer = YamlFile.Deserializer();
    string expected = Outcome(() => deserializer.Deserialize<FactTableFile>(yaml));

    // Act
    string actual = Outcome(() => FactTableReader.Read(yaml, deserializer));

    // Assert
    actual.ShouldBe(expected);
  }

  [Fact]
  public void ReadsAPlainTableItself()
  {
    // Act
    bool read = FactTableReader.TryRead(Plain, out _);

    // Assert
    read.ShouldBeTrue();
  }

  [Theory]
  [InlineData("line_items:\n  a: &x {label: q}\n  b: *x\n")]
  [InlineData("line_items:\n  a:\n    label: !!str 12\n")]
  [InlineData("line_items:\n  a:\n    image_regions:\n      '2024': {x: 012, y: 1, width: 1, height: 1}\n")]
  [InlineData("line_items:\n  a:\n    bogus: 1\n")]
  [InlineData("")]
  public void LeavesAnythingUnfamiliarToYamlDotNet(string yaml)
  {
    // Act
    bool read = FactTableReader.TryRead(yaml, out _);

    // Assert
    read.ShouldBeFalse();
  }

  [Fact]
  public void ReadsEveryFactTableInTheSampleBooksItself()
  {
    // Arrange
    string books = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../books"));
    List<string> tables = Directory.GetFiles(books, "*.csv", SearchOption.AllDirectories)
      .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("facts"))
      .Select(path => Path.ChangeExtension(path, ".yaml"))
      .ToList();

    // Act
    List<string> unread = tables.Where(path => !FactTableReader.TryRead(File.ReadAllText(path), out _)).ToList();

    // Assert
    tables.ShouldNotBeEmpty();
    unread.ShouldBeEmpty();
  }

  private static string Outcome(Func<FactTableFile?> read)
  {
    try
    {
      return Dump(read());
    }
    catch (Exception exception)
    {
      return $"throws {exception.GetType().Name}: {exception.Message} <- {exception.InnerException?.Message}";
    }
  }

  private static string Dump(FactTableFile? file)
  {
    if (file is null)
    {
      return "null";
    }
    string table = file.Table is null ? "null" : $"{{{Text(file.Table.Title)} {Text(file.Table.FiscalYearEnd)}}}";
    string source = file.Source is null
      ? "null"
      : $"{{{Text(file.Source.Url)} {Text(file.Source.Image)} {Text(file.Source.Note)} {Map(file.Source.Details)}}}";
    string defaults = file.Defaults is null
      ? "null"
      : $"{{{Text(file.Defaults.Units)} {Text(file.Defaults.Currency)} {Text(file.Defaults.Printed)} " +
        $"{Text(file.Defaults.Sign)} {Text(file.Defaults.Missing)}}}";
    string lines = file.LineItems is null
      ? "null"
      : "{" + string.Join(", ", file.LineItems.Select(entry => $"{Text(entry.Key)}: {Line(entry.Value)}")) + "}";
    return $"table {table} source {source} defaults {defaults} line_items {lines}";
  }

  private static string Line(LineItem? line)
  {
    if (line is null)
    {
      return "null";
    }
    string regions = line.ImageRegions is null
      ? "null"
      : "{" + string.Join(", ", line.ImageRegions.Select(entry => $"{Text(entry.Key)}: " + (entry.Value is null
        ? "null"
        : $"[{entry.Value.X} {entry.Value.Y} {entry.Value.Width} {entry.Value.Height}]"))) + "}";
    return $"{{{Text(line.Label)} {Text(line.Units)} {Text(line.Printed)} {Text(line.Sign)} {Text(line.Missing)} " +
      $"{Text(line.Note)} {Map(line.CellNotes)} {regions}}}";
  }

  private static string Map(Dictionary<string, string>? map)
  {
    return map is null
      ? "null"
      : "{" + string.Join(", ", map.Select(entry => $"{Text(entry.Key)}: {Text(entry.Value)}")) + "}";
  }

  private static string Text(string? text)
  {
    return text is null ? "null" : $"\"{text.Replace("\n", "\\n")}\"";
  }


}