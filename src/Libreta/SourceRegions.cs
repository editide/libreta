using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Libreta;

/// <summary>A fact a table's source image shows with no region outlining it.</summary>
public record UnboxedFact(string Table, string Line, string Period);

public record SourceRegionResult(int Regions, List<UnboxedFact> Unboxed);

public class SourceRegionRequest
{
  public string? Image { get; set; }
  public Dictionary<string, Dictionary<string, SourceRegion>> Regions { get; set; } =
    new Dictionary<string, Dictionary<string, SourceRegion>>();
}

/// <summary>Records regions measured by a tool outside Libreta, which knows the page but not the book. The YAML is
/// edited in place rather than reserialized, so comments, order and quoting survive.</summary>
public static partial class SourceRegionWriter
{
  private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
  {
    PropertyNameCaseInsensitive = true
  };

  private static readonly HashSet<string> Reserved =
    new HashSet<string> { "~", "null", "true", "false", "yes", "no", "on", "off", "y", "n" };

  /// <summary>A request naming an image replaces every region in the table, since a new image moves every coordinate;
  /// one without updates only the regions it names. The book is then reloaded, and the table restored if it no longer
  /// loads. A relative table path that is not found from the working folder is read from the book
  /// root, as <c>--check</c> prints it.</summary>
  public static SourceRegionResult Write(string root, string tablePath, string regionsPath)
  {
    string fullRoot = Path.GetFullPath(root);
    string metadataPath = Path.IsPathRooted(tablePath) || File.Exists(tablePath)
      ? Path.GetFullPath(tablePath)
      : Path.GetFullPath(tablePath, fullRoot);
    string csvPath = Path.ChangeExtension(metadataPath, ".csv");
    Book book = Book.Load(fullRoot);
    bool loaded = book.Facts.Values
      .SelectMany(fact => fact.Cells.Values)
      .Any(cell => BookStructure.PathComparer.Equals(Path.GetFullPath(cell.SourcePath), csvPath));
    if (!Path.GetExtension(metadataPath).Equals(".yaml", StringComparison.OrdinalIgnoreCase) || !loaded)
    {
      throw new InvalidDataException($"'{tablePath}' is not a fact table in {fullRoot}");
    }

    SourceRegionRequest request;
    try
    {
      request = JsonSerializer.Deserialize<SourceRegionRequest>(File.ReadAllText(regionsPath), JsonOptions) ??
        new SourceRegionRequest();
    }
    catch (JsonException exception)
    {
      throw new InvalidDataException($"Invalid JSON in '{regionsPath}': {exception.Message}", exception);
    }

    List<string[]> rows = File.ReadAllLines(csvPath)
      .Where(row => row.Trim().Length > 0)
      .Select(row => row.Split(',').Select(cell => cell.Trim()).ToArray())
      .ToList();
    List<string> periods = rows[0].Skip(1).ToList();
    HashSet<string> lines = rows.Skip(1).Select(row => row[0]).ToHashSet();
    List<string> unknownLines = request.Regions.Keys.Where(line => !lines.Contains(line)).ToList();
    if (unknownLines.Count > 0)
    {
      throw new InvalidDataException(
        $"'{regionsPath}' names lines '{csvPath}' does not have: {string.Join(", ", unknownLines)}");
    }
    List<string> unknownPeriods = request.Regions.Values
      .SelectMany(regions => regions.Keys)
      .Where(period => !periods.Contains(period))
      .Distinct()
      .ToList();
    if (unknownPeriods.Count > 0)
    {
      throw new InvalidDataException($"'{regionsPath}' names periods '{csvPath}' does not have: " +
        $"{string.Join(", ", unknownPeriods)}; its periods are {string.Join(", ", periods)}");
    }
    var merged = new Dictionary<string, Dictionary<string, SourceRegion>>();
    if (request.Image is null)
    {
      foreach ((string line, LineItem item) in YamlFile.Load<FactTableFile>(metadataPath).LineItems)
      {
        merged[line] = new Dictionary<string, SourceRegion>(item.ImageRegions);
      }
    }
    foreach ((string line, Dictionary<string, SourceRegion> regions) in request.Regions)
    {
      if (!merged.TryGetValue(line, out Dictionary<string, SourceRegion>? periodRegions))
      {
        periodRegions = new Dictionary<string, SourceRegion>();
        merged[line] = periodRegions;
      }
      foreach ((string period, SourceRegion region) in regions)
      {
        periodRegions[period] = region;
      }
    }
    Dictionary<string, Dictionary<string, SourceRegion>> ordered = merged.ToDictionary(
      entry => entry.Key,
      entry => periods.Where(entry.Value.ContainsKey).ToDictionary(period => period, period => entry.Value[period]));

    string original = File.ReadAllText(metadataPath);
    File.WriteAllText(metadataPath, Apply(original, request.Image, ordered));
    try
    {
      book = Book.Load(fullRoot);
    }
    catch (Exception exception)
    {
      File.WriteAllText(metadataPath, original);
      throw new InvalidDataException($"{exception.Message}; '{tablePath}' is unchanged", exception);
    }
    string table = Relative(book, metadataPath);
    return new SourceRegionResult(
      request.Regions.Values.Sum(regions => regions.Count),
      Unboxed(book).Where(fact => fact.Table == table).ToList());
  }

  /// <summary>The first few unboxed facts by line and period, and a count of the rest, keeping a table that boxes
  /// little to one short line.</summary>
  public static string Summarize(IEnumerable<UnboxedFact> unboxed)
  {
    const int Shown = 5;
    List<string> facts = unboxed.Select(fact => $"{fact.Line}[{fact.Period}]").ToList();
    string listed = string.Join(", ", facts.Take(Shown));
    return facts.Count > Shown ? $"{listed} (+{facts.Count - Shown} more)" : listed;
  }

  /// <summary>Facts in a table with a source image that no region outlines, by table, line, then period order.
  /// </summary>
  public static List<UnboxedFact> Unboxed(Book book)
  {
    return book.Facts.Values
      .SelectMany(fact => fact.Cells.Select(cell => (Fact: fact, Period: cell.Key, Cell: cell.Value)))
      .Where(item => item.Cell.SourceImage.Length > 0 && item.Cell.SourceRegion is null)
      .Select(item => (
        Fact: new UnboxedFact(
          Relative(book, Path.ChangeExtension(item.Cell.SourcePath, ".yaml")), item.Cell.SourceLine, item.Period),
        Order: book.PeriodsFor(item.Fact.Scope).IndexOf(item.Period)))
      .OrderBy(item => item.Fact.Table, StringComparer.Ordinal)
      .ThenBy(item => item.Fact.Line, StringComparer.Ordinal)
      .ThenBy(item => item.Order)
      .Select(item => item.Fact)
      .ToList();
  }

  /// <summary>Sets <c>source.image</c> unless <paramref name="image"/> is null, and replaces every
  /// <c>image_regions</c> in the table with <paramref name="regions"/>, adding a <c>line_items</c> entry for a line
  /// that has none.</summary>
  public static string Apply(
    string yaml, string? image, Dictionary<string, Dictionary<string, SourceRegion>> regions)
  {
    string newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
    bool terminated = yaml.EndsWith('\n');
    string text = terminated ? yaml : yaml + newline;
    Node root = Parse(text);
    List<int> lineStarts = LineStarts(text);
    int After(int line) => line < lineStarts.Count ? lineStarts[line] : text.Length;
    var edits = new List<(int Start, int End, string Text)>();

    if (image is not null)
    {
      Node source = root.Get("source") ?? throw new InvalidDataException("Add a source mapping to record an image.");
      if (!source.Block || source.Pairs.Count == 0)
      {
        throw new InvalidDataException("source must be a block mapping to record an image.");
      }
      Node? existing = source.Get("image");
      if (existing is not null)
      {
        int start = (int)existing.Start.Start.Index;
        int end = (int)existing.Start.End.Index;
        edits.Add((start, end, (start == end ? " " : "") + Scalar(image)));
      }
      else
      {
        string indent = new string(' ', source.Pairs[0].Indent);
        Node? url = source.Get("url");
        int at = url is null ? lineStarts[source.Pairs[0].Line - 1] : After(url.EndLine);
        edits.Add((at, at, $"{indent}image: {Scalar(image)}{newline}"));
      }
    }

    Node? items = root.Get("line_items");
    if (items is null || !items.Block || items.Pairs.Count == 0)
    {
      if (regions.Values.Any(periods => periods.Count > 0))
      {
        throw new InvalidDataException("line_items must be a block mapping to take image_regions.");
      }
    }
    else
    {
      foreach (Pair item in items.Pairs)
      {
        regions.TryGetValue(item.Key, out Dictionary<string, SourceRegion>? periods);
        if (!item.Value.Block)
        {
          if (periods?.Count > 0)
          {
            throw new InvalidDataException($"line_items.{item.Key} must be a block mapping to take image_regions.");
          }
          continue;
        }
        foreach (Pair stale in item.Value.Pairs.Where(pair => pair.Key == "image_regions"))
        {
          edits.Add((lineStarts[stale.Line - 1], After(stale.Value.EndLine), ""));
        }
        if (periods?.Count > 0)
        {
          int last = item.Value.Pairs
            .Where(pair => pair.Key != "image_regions")
            .Select(pair => pair.Value.EndLine)
            .DefaultIfEmpty(item.Line)
            .Max();
          edits.Add((After(last), After(last), Block(item.Value.Pairs[0].Indent, periods, newline)));
        }
      }
      int itemIndent = items.Pairs[0].Indent;
      var added = new StringBuilder();
      foreach ((string line, Dictionary<string, SourceRegion> periods) in regions
        .Where(entry => entry.Value.Count > 0 && items.Get(entry.Key) is null))
      {
        added.Append($"{new string(' ', itemIndent)}{Scalar(line)}:{newline}");
        added.Append(Block(itemIndent + 2, periods, newline));
      }
      if (added.Length > 0)
      {
        edits.Add((After(items.EndLine), After(items.EndLine), added.ToString()));
      }
    }

    // Applied back to front so earlier offsets hold; at one offset, a removal goes before the insertion replacing it.
    var result = new StringBuilder(text);
    foreach ((int start, int end, string replacement) in edits.OrderByDescending(edit => edit.Start)
      .ThenByDescending(edit => edit.End))
    {
      result.Remove(start, end - start).Insert(start, replacement);
    }
    string written = result.ToString();
    return terminated || !written.EndsWith(newline) ? written : written[..^newline.Length];
  }

  private static string Relative(Book book, string path)
  {
    return Path.GetRelativePath(book.Root, path).Replace(Path.DirectorySeparatorChar, '/');
  }

  private static string Block(int indent, Dictionary<string, SourceRegion> periods, string newline)
  {
    string pad = new string(' ', indent);
    var block = new StringBuilder($"{pad}image_regions:{newline}");
    foreach ((string period, SourceRegion region) in periods)
    {
      block.Append($"{pad}  {Scalar(period)}: {{ x: {region.X}, y: {region.Y}, width: {region.Width}, " +
        $"height: {region.Height} }}{newline}");
    }
    return block.ToString();
  }

  private static string Scalar(string value)
  {
    return PlainScalar().IsMatch(value) && !Reserved.Contains(value.ToLowerInvariant())
      ? value
      : JsonSerializer.Serialize(value);
  }

  [GeneratedRegex(@"^[A-Za-z0-9_./][A-Za-z0-9 _./()-]*(?<! )$")]
  private static partial Regex PlainScalar();

  private static List<int> LineStarts(string text)
  {
    var starts = new List<int> { 0 };
    for (int index = 0; index < text.Length - 1; index++)
    {
      if (text[index] == '\n')
      {
        starts.Add(index + 1);
      }
    }
    return starts;
  }

  private static Node Parse(string text)
  {
    try
    {
      var parser = new Parser(new StringReader(text));
      parser.Consume<StreamStart>();
      if (!parser.TryConsume<DocumentStart>(out _))
      {
        throw new InvalidDataException("A fact table must be a mapping.");
      }
      Node root = Read(parser);
      return root.Block ? root : throw new InvalidDataException("A fact table must be a block mapping.");
    }
    catch (YamlException exception)
    {
      throw new InvalidDataException($"Invalid YAML: {exception.Message}", exception);
    }
  }

  private static Node Read(IParser parser)
  {
    ParsingEvent current = parser.Current!;
    parser.MoveNext();
    var node = new Node { Start = current, EndLine = EndLine(current) };
    if (current is MappingStart mapping)
    {
      node.Block = mapping.Style != MappingStyle.Flow;
      while (parser.Current is not MappingEnd)
      {
        if (parser.Current is not Scalar key)
        {
          throw new InvalidDataException($"Line {parser.Current!.Start.Line} has a key that is not a plain value.");
        }
        Read(parser);
        Node value = Read(parser);
        node.Pairs.Add(new Pair(key.Value, (int)key.Start.Line, (int)key.Start.Column - 1, value));
        node.EndLine = Math.Max(node.EndLine, value.EndLine);
      }
      ParsingEvent end = parser.Current;
      parser.MoveNext();
      // A block mapping's end is marked where the next token begins, past any comments and blank lines.
      if (!node.Block)
      {
        node.EndLine = EndLine(end);
      }
    }
    else if (current is SequenceStart sequence)
    {
      while (parser.Current is not SequenceEnd)
      {
        node.EndLine = Math.Max(node.EndLine, Read(parser).EndLine);
      }
      ParsingEvent end = parser.Current;
      parser.MoveNext();
      if (sequence.Style == SequenceStyle.Flow)
      {
        node.EndLine = EndLine(end);
      }
    }
    return node;
  }

  /// <summary>The last line holding the event's text; a block scalar's end is marked at the start of the next line.
  /// </summary>
  private static int EndLine(ParsingEvent parsingEvent)
  {
    return parsingEvent.End.Column == 1 && parsingEvent.End.Line > parsingEvent.Start.Line
      ? (int)parsingEvent.End.Line - 1
      : (int)parsingEvent.End.Line;
  }

  private sealed class Node
  {
    public ParsingEvent Start { get; init; } = null!;
    public int EndLine { get; set; }
    public bool Block { get; set; }
    public List<Pair> Pairs { get; } = new List<Pair>();

    public Node? Get(string key)
    {
      return Pairs.FirstOrDefault(pair => pair.Key == key)?.Value;
    }


  }

  /// <summary>A key and its value, with the key's 1-based line and 0-based indent.</summary>
  private sealed record Pair(string Key, int Line, int Indent, Node Value);


}