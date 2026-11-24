using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Libreta;

/// <summary>Reads a fact table's metadata from the parser's events, since YamlDotNet's reflection costs more than the
/// parsing itself on a book of hundreds of tables. It reads only the shape tables are written in, and leaves any
/// other file (anchors, tags, numbers like <c>0x10</c>, unknown keys, malformed YAML) to YamlDotNet, so every file
/// reads, or fails, exactly as YamlDotNet would have it.</summary>
public static class FactTableReader
{
  private static readonly object Fallback = new object();

  public static FactTableFile Load(string path, IDeserializer deserializer, IBookFiles source)
  {
    try
    {
      return Read(source.ReadAllText(path), deserializer)!;
    }
    catch (YamlException exception)
    {
      throw new InvalidDataException($"Invalid YAML in '{path}': {exception.Message}", exception);
    }
  }

  public static FactTableFile? Read(string yaml, IDeserializer deserializer)
  {
    if (TryRead(yaml, out FactTableFile? file))
    {
      return file;
    }
    // Tables load on several threads, and YamlDotNet doesn't promise its deserializer is safe to share.
    lock (Fallback)
    {
      return deserializer.Deserialize<FactTableFile>(yaml);
    }
  }

  public static bool TryRead(string yaml, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FactTableFile? file)
  {
    try
    {
      file = new Reader(yaml).Document();
      return true;
    }
    catch (Exception exception) when (exception is UnfamiliarException or YamlException)
    {
      file = null;
      return false;
    }
  }

  private sealed class UnfamiliarException : Exception
  {
  }

  private sealed class Reader
  {
    private readonly Parser parser;

    public Reader(string yaml)
    {
      parser = new Parser(new StringReader(yaml));
      Advance();
    }

    public FactTableFile Document()
    {
      Expect<StreamStart>();
      Expect<DocumentStart>();
      FactTableFile file = Mapping(() => new FactTableFile(), (table, key) =>
      {
        switch (key)
        {
          case "table": table.Table = Table()!; break;
          case "source": table.Source = Source()!; break;
          case "defaults": table.Defaults = Defaults()!; break;
          case "line_items": table.LineItems = Dictionary(LineItem)!; break;
          default: throw new UnfamiliarException();
        }
      }) ?? throw new UnfamiliarException();
      Expect<DocumentEnd>();
      if (parser.Current is not StreamEnd)
      {
        throw new UnfamiliarException();
      }
      return file;
    }

    private TableInfo? Table()
    {
      return Mapping(() => new TableInfo(), (table, key) =>
      {
        switch (key)
        {
          case "title": table.Title = String()!; break;
          default: throw new UnfamiliarException();
        }
      });
    }

    private SourceInfo? Source()
    {
      return Mapping(() => new SourceInfo(), (source, key) =>
      {
        switch (key)
        {
          case "url": source.Url = String()!; break;
          case "image": source.Image = String()!; break;
          case "note": source.Note = String()!; break;
          case "details": source.Details = Dictionary(String)!; break;
          default: throw new UnfamiliarException();
        }
      });
    }

    private Defaults? Defaults()
    {
      return Mapping(() => new Defaults(), (defaults, key) =>
      {
        switch (key)
        {
          case "units": defaults.Units = String()!; break;
          case "printed": defaults.Printed = String()!; break;
          case "sign": defaults.Sign = String()!; break;
          case "missing": defaults.Missing = String()!; break;
          default: throw new UnfamiliarException();
        }
      });
    }

    private LineItem? LineItem()
    {
      return Mapping(() => new LineItem(), (line, key) =>
      {
        switch (key)
        {
          case "label": line.Label = String()!; break;
          case "units": line.Units = String(); break;
          case "printed": line.Printed = String(); break;
          case "sign": line.Sign = String(); break;
          case "missing": line.Missing = String(); break;
          case "note": line.Note = String(); break;
          case "cell_notes": line.CellNotes = Dictionary(String)!; break;
          case "image_regions": line.ImageRegions = Dictionary(Region)!; break;
          default: throw new UnfamiliarException();
        }
      });
    }

    private SourceRegion? Region()
    {
      return Mapping(() => new SourceRegion(), (region, key) =>
      {
        switch (key)
        {
          case "x": region.X = Int(); break;
          case "y": region.Y = Int(); break;
          case "width": region.Width = Int(); break;
          case "height": region.Height = Int(); break;
          default: throw new UnfamiliarException();
        }
      });
    }

    /// <summary>A mapping read into a new object, or null where the YAML has a null; a repeated key's last value
    /// wins.</summary>
    private T? Mapping<T>(Func<T> create, Action<T, string> property) where T : class
    {
      if (Null())
      {
        return null;
      }
      Plain(Expect<MappingStart>());
      T value = create();
      while (parser.Current is not MappingEnd)
      {
        property(value, Key());
      }
      Advance();
      return value;
    }

    private Dictionary<string, T>? Dictionary<T>(Func<T?> read)
    {
      return Mapping(() => new Dictionary<string, T>(), (dictionary, key) => dictionary[key] = read()!);
    }

    private string Key()
    {
      if (IsNull(parser.Current))
      {
        throw new UnfamiliarException();
      }
      return Plain(Expect<Scalar>()).Value;
    }

    private string? String()
    {
      return Null() ? null : Plain(Expect<Scalar>()).Value;
    }

    /// <summary>Decimal integers only; YamlDotNet also reads hex, octal and quoted numbers, so those fall back.</summary>
    private int Int()
    {
      Scalar scalar = Plain(Expect<Scalar>());
      ReadOnlySpan<char> digits = scalar.Value.AsSpan(scalar.Value.StartsWith('-') ? 1 : 0);
      bool decimalInteger = scalar.Style == ScalarStyle.Plain && digits.Length is > 0 and < 10 &&
        (digits[0] != '0' || digits.Length == 1) && !digits.ContainsAnyExceptInRange('0', '9');
      if (!decimalInteger)
      {
        throw new UnfamiliarException();
      }
      return int.Parse(scalar.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    /// <summary>Consumes a null, written as YamlDotNet reads one: a plain scalar that is empty, ~ or null.</summary>
    private bool Null()
    {
      if (!IsNull(parser.Current))
      {
        return false;
      }
      Plain((Scalar)parser.Current!);
      Advance();
      return true;
    }

    private static bool IsNull(ParsingEvent? current)
    {
      return current is Scalar { Style: ScalarStyle.Plain, Value: "" or "~" or "null" or "Null" or "NULL" };
    }

    private static T Plain<T>(T node) where T : NodeEvent
    {
      if (!node.Anchor.IsEmpty || !node.Tag.IsEmpty)
      {
        throw new UnfamiliarException();
      }
      return node;
    }

    private T Expect<T>() where T : ParsingEvent
    {
      if (parser.Current is not T current)
      {
        throw new UnfamiliarException();
      }
      Advance();
      return current;
    }

    private void Advance()
    {
      parser.MoveNext();
    }


  }


}