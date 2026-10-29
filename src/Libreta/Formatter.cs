using System.Globalization;
using YamlDotNet.Serialization;

namespace Libreta;

/// <summary>Applies formats.yaml specs and knows nothing of particular units, so formatting is a yaml edit.</summary>
public class Formatter
{
  // Excel's 1900 date system, so a serial matches the same date pasted into a spreadsheet from March 1900 on.
  private const string IsoDate = "yyyy-MM-dd";

  private static readonly int EpochDayNumber = new DateOnly(1899, 12, 30).DayNumber;

  private readonly FormatsFile formats;

  public Formatter(FormatsFile formats)
  {
    this.formats = formats;
  }

  public string Unresolved => formats.Unresolved;

  public string Dash => formats.Dash;

  public string? Zero => formats.Zero;

  /// <summary>A calculated or hardcoded zero may read as a dash; a sourced fact keeps its printed zero, which may mean "tiny".</summary>
  public string FormatCalculated(string? units, double amount)
  {
    return amount == 0 && formats.Zero is not null ? formats.Zero : Format(units, amount);
  }

  public string? DefaultUnits => formats.DefaultUnits;

  /// <summary>Null for units with no format, which fail when displayed rather than here.</summary>
  public bool? IsDate(string? units)
  {
    return Find(units) is FormatSpec spec ? spec.Date is not null : null;
  }

  /// <summary>Null for units with no format, which fail when displayed rather than here.</summary>
  public bool? IsText(string? units)
  {
    return Find(units)?.Text;
  }

  private FormatSpec? Find(string? units)
  {
    string? key = units ?? formats.DefaultUnits;
    return key is null ? formats.Plain : formats.Formats.GetValueOrDefault(key);
  }

  /// <summary>Every numeric unit, with "" for a value that has none; dates and text are left out, having no scale.
  /// </summary>
  public Dictionary<string, UnitPayload> Units()
  {
    var units = new Dictionary<string, UnitPayload>();
    if (formats.DefaultUnits is null && formats.Plain.Date is null && !formats.Plain.Text)
    {
      units.Add("", Payload(formats.Plain));
    }
    foreach ((string name, FormatSpec spec) in
      formats.Formats.Where(format => format.Value.Date is null && !format.Value.Text))
    {
      units.Add(name, Payload(spec));
    }
    return units;
  }

  private static UnitPayload Payload(FormatSpec spec)
  {
    return new UnitPayload(spec.Scale, spec.Prefix, spec.Suffix);
  }

  public bool Defines(string? units)
  {
    string? key = units ?? formats.DefaultUnits;
    return key is null || formats.Formats.ContainsKey(key);
  }

  public string Format(string? units, double amount)
  {
    FormatSpec spec = Spec(units);
    if (spec.Date is not null)
    {
      return FormatDate(spec.Date, amount);
    }

    double scaled = amount * spec.Scale;
    string pattern = (spec.Separator ? "N" : "F") + spec.Decimals;
    string text = spec.Prefix + Math.Abs(scaled).ToString(pattern, CultureInfo.InvariantCulture) + spec.Suffix;

    if (scaled >= 0)
    {
      return text;
    }
    return spec.Negative == "parentheses" ? $"({text})" : $"-{text}";
  }

  /// <summary>A date's exact value is its ISO form, unambiguous whatever pattern the display uses.</summary>
  public string Exact(string? units, double amount)
  {
    return Spec(units).Date is null ? Exact(amount) : FormatDate(IsoDate, amount);
  }

  /// <summary>The unrounded value, shown beside the formatted one so rounding is visible.</summary>
  public static string Exact(double amount)
  {
    return amount.ToString("R", CultureInfo.InvariantCulture);
  }

  /// <summary>Dates are days since the epoch, so date arithmetic is plain subtraction.</summary>
  public static bool TryParseDate(string text, out double serial)
  {
    bool parsed = DateOnly.TryParseExact(text, IsoDate, CultureInfo.InvariantCulture, DateTimeStyles.None,
      out DateOnly date);
    serial = parsed ? date.DayNumber - EpochDayNumber : 0;
    return parsed;
  }

  /// <summary>A unit any formats.yaml defines replaces the built-in unit outright, so its definition reads complete;
  /// a file's defaults still beat the built-in units it leaves alone.</summary>
  public static FormatsFile Load(string viewFolder, string bookRoot, IDeserializer deserializer)
  {
    var formats = new FormatsFile();
    (FormatSpecOverride builtInDefaults, Dictionary<string, FormatSpecOverride> builtIns) =
      Merge(formats, new[] { BuiltIn(deserializer) });
    IEnumerable<FormatsOverride> files = Enumerable.Reverse(LocateAll(viewFolder, bookRoot))
      .Select(path => YamlFile.Load<FormatsOverride>(path, deserializer));
    (FormatSpecOverride defaults, Dictionary<string, FormatSpecOverride> formatOverrides) = Merge(formats, files);

    var fallback = new FormatSpec();
    var none = new FormatSpecOverride();
    FormatSpec Resolve(FormatSpecOverride format, FormatSpecOverride builtIn) => new FormatSpec
    {
      Scale = format.Scale ?? defaults.Scale ?? builtIn.Scale ?? builtInDefaults.Scale ?? fallback.Scale,
      Decimals = format.Decimals ?? defaults.Decimals ?? builtIn.Decimals ?? builtInDefaults.Decimals
        ?? fallback.Decimals,
      Separator = format.Separator ?? defaults.Separator ?? builtIn.Separator ?? builtInDefaults.Separator
        ?? fallback.Separator,
      Prefix = format.Prefix ?? defaults.Prefix ?? builtIn.Prefix ?? builtInDefaults.Prefix ?? fallback.Prefix,
      Suffix = format.Suffix ?? defaults.Suffix ?? builtIn.Suffix ?? builtInDefaults.Suffix ?? fallback.Suffix,
      Negative = format.Negative ?? defaults.Negative ?? builtIn.Negative ?? builtInDefaults.Negative
        ?? fallback.Negative,
      Date = format.Date ?? defaults.Date ?? builtIn.Date ?? builtInDefaults.Date,
      Text = format.Text ?? defaults.Text ?? builtIn.Text ?? builtInDefaults.Text ?? fallback.Text
    };

    formats.Plain = Resolve(none, none);
    foreach (string units in builtIns.Keys.Union(formatOverrides.Keys))
    {
      FormatSpecOverride builtIn = formatOverrides.ContainsKey(units) ? none : builtIns.GetValueOrDefault(units, none);
      formats.Formats.Add(units, Resolve(formatOverrides.GetValueOrDefault(units, none), builtIn));
    }
    return formats;
  }

  /// <summary>Merges files farthest first, writing their top-level defaults straight into the result.</summary>
  private static (FormatSpecOverride Defaults, Dictionary<string, FormatSpecOverride> Formats) Merge(
    FormatsFile formats, IEnumerable<FormatsOverride> layers)
  {
    var defaults = new FormatSpecOverride();
    var formatOverrides = new Dictionary<string, FormatSpecOverride>();
    foreach (FormatsOverride overrides in layers)
    {
      formats.DefaultUnits = overrides.Defaults.Units ?? formats.DefaultUnits;
      formats.Unresolved = overrides.Defaults.Unresolved ?? formats.Unresolved;
      formats.Dash = overrides.Defaults.Dash ?? formats.Dash;
      formats.Zero = overrides.Defaults.Zero ?? formats.Zero;
      Apply(defaults, overrides.Defaults);

      foreach ((string units, FormatSpecOverride formatOverride) in overrides.Formats)
      {
        if (!formatOverrides.TryGetValue(units, out FormatSpecOverride? accumulated))
        {
          accumulated = new FormatSpecOverride();
          formatOverrides.Add(units, accumulated);
        }
        Apply(accumulated, formatOverride);
      }
    }
    return (defaults, formatOverrides);
  }

  /// <summary>Every formats.yaml from the view folder to its project boundary, nearest first, possibly none.</summary>
  public static List<string> LocateAll(string viewFolder, string bookRoot)
  {
    var paths = new List<string>();
    string boundary = InheritanceRoot(bookRoot);
    DirectoryInfo? directory = new DirectoryInfo(viewFolder);
    while (directory is not null)
    {
      string candidate = Path.Combine(directory.FullName, "formats.yaml");
      if (File.Exists(candidate))
      {
        paths.Add(candidate);
      }
      if (PathEquals(directory.FullName, boundary))
      {
        break;
      }
      directory = directory.Parent;
    }
    if (directory is null)
    {
      throw new InvalidDataException($"'{viewFolder}' is not inside the format inheritance root '{boundary}'.");
    }
    return paths;
  }

  /// <summary>The formats.yaml beside this file, compiled in so a book with no formats.yaml still renders.</summary>
  private static FormatsOverride BuiltIn(IDeserializer deserializer)
  {
    using Stream stream = typeof(Formatter).Assembly.GetManifestResourceStream("Libreta.formats.yaml")
      ?? throw new InvalidOperationException("The built-in formats.yaml is missing.");
    using var reader = new StreamReader(stream);
    return deserializer.Deserialize<FormatsOverride>(reader.ReadToEnd());
  }

  private static string InheritanceRoot(string bookRoot)
  {
    string fullBookRoot = Path.GetFullPath(bookRoot);
    for (DirectoryInfo? directory = new DirectoryInfo(fullBookRoot);
      directory is not null;
      directory = directory.Parent)
    {
      string marker = Path.Combine(directory.FullName, ".git");
      if (Directory.Exists(marker) || File.Exists(marker))
      {
        return directory.FullName;
      }
    }

    for (DirectoryInfo? directory = new DirectoryInfo(fullBookRoot);
      directory is not null;
      directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "formats.yaml")))
      {
        return directory.FullName;
      }
    }
    return fullBookRoot;
  }

  public FormatSpec Spec(string? units)
  {
    string? key = units ?? formats.DefaultUnits;
    if (key is null)
    {
      return formats.Plain;
    }
    if (!formats.Formats.TryGetValue(key, out FormatSpec? spec))
    {
      throw new FormatException($"No format defined for units '{key}'. Add it to formats.yaml.");
    }
    return spec;
  }

  /// <summary>Excel's EOMONTH: the last day of the month a truncated number of months from the date.</summary>
  public static double EndOfMonth(double serial, double months)
  {
    DateOnly moved = ToDate(serial).AddMonths((int)Math.Truncate(months));
    return new DateOnly(moved.Year, moved.Month, DateTime.DaysInMonth(moved.Year, moved.Month)).DayNumber -
      EpochDayNumber;
  }

  private static DateOnly ToDate(double serial)
  {
    return DateOnly.FromDayNumber(EpochDayNumber + (int)Math.Floor(serial));
  }

  private static string FormatDate(string pattern, double serial)
  {
    return ToDate(serial).ToString(pattern, CultureInfo.InvariantCulture);
  }

  private static bool PathEquals(string left, string right)
  {
    return string.Equals(
      Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
      Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
  }

  private static void Apply(FormatSpecOverride format, FormatSpecOverride overrides)
  {
    format.Scale = overrides.Scale ?? format.Scale;
    format.Decimals = overrides.Decimals ?? format.Decimals;
    format.Separator = overrides.Separator ?? format.Separator;
    format.Prefix = overrides.Prefix ?? format.Prefix;
    format.Suffix = overrides.Suffix ?? format.Suffix;
    format.Negative = overrides.Negative ?? format.Negative;
    format.Date = overrides.Date ?? format.Date;
    format.Text = overrides.Text ?? format.Text;
  }

  private class FormatsOverride
  {
    public DefaultsOverride Defaults { get; set; } = new DefaultsOverride();
    public Dictionary<string, FormatSpecOverride> Formats { get; set; } = new Dictionary<string, FormatSpecOverride>();
  }

  private class DefaultsOverride : FormatSpecOverride
  {
    public string? Units { get; set; }
    public string? Unresolved { get; set; }
    public string? Dash { get; set; }
    public string? Zero { get; set; }
  }

  private class FormatSpecOverride
  {
    public double? Scale { get; set; }
    public int? Decimals { get; set; }
    public bool? Separator { get; set; }
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public string? Negative { get; set; }
    public string? Date { get; set; }
    public bool? Text { get; set; }
  }


}