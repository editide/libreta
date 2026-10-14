using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Libreta;

public record ViewQuery(string ViewId, string? Line = null, string? Column = null);

public record ViewTableColumnPayload(string Name, string Label);

public record ViewTableCellPayload(string Line, string Column);

public record ViewTableRowPayload
{
  public string? Name { get; init; }
  public string? Label { get; init; }
  public string Kind { get; init; } = "";
  public string? Style { get; init; }
  public int? Indent { get; init; }
  public string? Units { get; init; }
  public List<string?>? Exact { get; init; }
  public List<string>? Display { get; init; }
  public List<bool>? Unresolved { get; init; }
  /// <summary>Per cell, whether the view shows it negated; <c>Exact</c> stays in natural direction.</summary>
  public List<bool>? Contra { get; init; }
  /// <summary>Per cell, the book cell it shows, given only when one isn't the row's line in its column's period, as
  /// in a comparison, whose rows are books, or a transposed view.</summary>
  public List<ViewTableCellPayload>? Cells { get; init; }
}

public record ViewTablePayload(
  string Id,
  string Title,
  string? Subtitle,
  List<ViewTableColumnPayload> Columns,
  List<ViewTableRowPayload> Rows,
  List<CheckPayload> Checks,
  List<SensitivityPayload> Sensitivities);

/// <summary>A cell another reads or is read by. <c>View</c> names a view presenting it when this one does not; an input
/// this view does not show carries its own calculation, saving a second <c>--value</c>.</summary>
public record ValueCellPayload
{
  public string Line { get; init; } = "";
  public string? Column { get; init; }
  public string? Label { get; init; }
  public string? Display { get; init; }
  public string? View { get; init; }
  public string? Missing { get; init; }
  public string? Formula { get; init; }
  public string? Calculation { get; init; }
  public List<ValueCellPayload>? Inputs { get; init; }
}

public record ValueSourcePayload(
  string Table, string Url, string Image, Dictionary<string, string> Details, string? Note);

public record ValuePayload
{
  public string View { get; init; } = "";
  public string Line { get; init; } = "";
  public string Column { get; init; } = "";
  public string? Label { get; init; }
  public string? ColumnLabel { get; init; }
  public string Kind { get; init; } = "";
  public string? Units { get; init; }
  public string? Value { get; init; }
  public string Display { get; init; } = "";
  public bool? Unresolved { get; init; }
  public bool? Contra { get; init; }
  public string? Formula { get; init; }
  public string? Calculation { get; init; }
  public List<ValueCellPayload>? Inputs { get; init; }
  public string? Note { get; init; }
  public ValueSourcePayload? Source { get; init; }
  public List<ValueCellPayload>? Dependents { get; init; }
}

/// <summary>A line as a caller outside Libreta reads it, over every period its scope has, in natural direction even
/// when <c>Contra</c> has views show it negated.</summary>
public record LinePayload
{
  public string Name { get; init; } = "";
  public string? Error { get; init; }
  public string? Label { get; init; }
  public string? Kind { get; init; }
  public string? Units { get; init; }
  public bool? Contra { get; init; }
  public List<string>? Columns { get; init; }
  public List<string?>? Exact { get; init; }
  public List<string>? Display { get; init; }
  public List<bool>? Unresolved { get; init; }
}

public enum HeadlessCommand { List, View, Value, Lines, Check, Export, Url }

public enum DocumentationTopic { Index, Format, Running }

public class CommandLineOptions
{
  /// <summary>Null when no root was given, leaving the book to be discovered below the working folder.</summary>
  public string? Root { get; private init; }
  public int Port { get; private init; } = 5173;
  /// <summary>False means the port is a default the host may step past when it is already taken.</summary>
  public bool PortSpecified { get; private init; }
  public HeadlessCommand? Command { get; private init; }
  public ViewQuery? Query { get; private init; }
  public List<string>? Lines { get; private init; }
  /// <summary>The cell a launch opens the page at, by qualified line and column name.</summary>
  public (string Line, string Column)? Cell { get; private init; }
  /// <summary>The caller opens the page itself, so the server opens no tab, and stops if no page comes.</summary>
  public bool NoOpen { get; private init; }
  public string? Output { get; private init; }
  public bool Json { get; private init; }
  public bool Help { get; private init; }
  public DocumentationTopic? Documentation { get; private init; }

  public static CommandLineOptions Parse(string[] arguments)
  {
    string? root = null;
    int first = 0;
    if (arguments.Length > 0 && !arguments[0].StartsWith('-'))
    {
      root = arguments[0];
      first = 1;
    }

    int port = 5173;
    bool portSpecified = false;
    HeadlessCommand? command = null;
    ViewQuery? query = null;
    List<string>? lines = null;
    (string, string)? cell = null;
    bool noOpen = false;
    string? output = null;
    bool json = false;
    DocumentationTopic? documentation = null;
    for (int index = first; index < arguments.Length; index++)
    {
      string argument = arguments[index];
      switch (argument)
      {
        case "--port":
          Require(arguments, index, 1);
          portSpecified = true;
          if (!int.TryParse(arguments[++index], out port) || port is < 1 or > 65535)
          {
            throw new ArgumentException("--port requires a number from 1 through 65535.");
          }
          break;
        case "--list":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.List;
          break;
        case "--view":
          RequireNoCommand(command, argument);
          Require(arguments, index, 1);
          command = HeadlessCommand.View;
          query = new ViewQuery(arguments[++index]);
          break;
        case "--value":
          RequireNoCommand(command, argument);
          Require(arguments, index, 3);
          command = HeadlessCommand.Value;
          query = new ViewQuery(arguments[index + 1], arguments[index + 2], arguments[index + 3]);
          index += 3;
          break;
        case "--lines":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Lines;
          lines = arguments.Skip(index + 1).TakeWhile(name => !name.StartsWith('-')).ToList();
          index += lines.Count;
          break;
        case "--cell":
          Require(arguments, index, 2);
          cell = (arguments[index + 1], arguments[index + 2]);
          index += 2;
          break;
        case "--no-open":
          noOpen = true;
          break;
        case "--check":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Check;
          break;
        case "--export":
          RequireNoCommand(command, argument);
          Require(arguments, index, 1);
          command = HeadlessCommand.Export;
          output = arguments[++index];
          if (Path.GetExtension(output).ToLowerInvariant() is not (".html" or ".htm" or ".xlsx"))
          {
            throw new ArgumentException("--export writes a page (.html) or a workbook (.xlsx); name the file for one.");
          }
          break;
        case "--url":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Url;
          break;
        case "--json":
          json = true;
          break;
        case "--docs":
          if (documentation is not null)
          {
            throw new ArgumentException("Use --docs only once.");
          }
          documentation = DocumentationTopic.Index;
          if (index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-'))
          {
            string topic = arguments[++index];
            documentation = topic switch
            {
              "format" => DocumentationTopic.Format,
              "running" => DocumentationTopic.Running,
              _ => throw new ArgumentException(
                $"Unknown documentation topic '{topic}'. Available: format, running.")
            };
          }
          break;
        case "--help":
        case "-h":
          return new CommandLineOptions { Help = true };
        default:
          throw new ArgumentException($"Unknown option '{argument}'.");
      }
    }

    if (command is not null && portSpecified)
    {
      throw new ArgumentException(
        "--port cannot be combined with --list, --view, --value, --lines, --check, --export or --url.");
    }
    if ((command is HeadlessCommand.Export or HeadlessCommand.Url || command is null && cell is null) && json)
    {
      throw new ArgumentException("--json requires --list, --view, --value, --lines, --check or --cell.");
    }
    if (cell is not null && command is not null)
    {
      throw new ArgumentException("--cell opens the page, so it cannot be combined with a headless command.");
    }
    if (noOpen && command is not null)
    {
      throw new ArgumentException(
        "--no-open leaves the server's page to the caller, so it cannot be combined with a headless command.");
    }
    if (documentation is not null &&
      (root is not null || command is not null || portSpecified || json || cell is not null))
    {
      throw new ArgumentException("--docs cannot be combined with a book root or another option.");
    }
    return new CommandLineOptions
    {
      Root = root,
      Port = port,
      PortSpecified = portSpecified,
      Command = command,
      Query = query,
      Lines = lines,
      Cell = cell,
      NoOpen = noOpen,
      Output = output,
      Json = json,
      Documentation = documentation
    };
  }

  public static void PrintUsage(TextWriter writer)
  {
    writer.WriteLine("usage:");
    writer.WriteLine("  libreta [<book-root>] [--port <n>] [--no-open]   (without --port, the first free port from " +
      "5173)");
    writer.WriteLine("  libreta [<book-root>] --cell <line> <column> [--no-open] [--json]   (the page at a cell, by " +
      "qualified line name; --json prints its address and how many open pages selected it)");
    writer.WriteLine("  libreta [<book-root>] --list [--json]");
    writer.WriteLine("  libreta [<book-root>] --view <view-id> [--json]");
    writer.WriteLine("  libreta [<book-root>] --value <view-id> <line> <column> [--json]");
    writer.WriteLine("  libreta [<book-root>] --lines [<line>...] [--json]   (lines by qualified name, or every line)");
    writer.WriteLine("  libreta [<book-root>] --check [--json]");
    writer.WriteLine("  libreta [<book-root>] --export <file.html>   (the whole book as one self-contained page)");
    writer.WriteLine("  libreta [<book-root>] --export <file.xlsx>   (the whole book as a workbook, a sheet per view)");
    writer.WriteLine("  libreta [<book-root>] --url   (the address of the server already running the book)");
    writer.WriteLine("  libreta --docs [format|running]");
    writer.WriteLine();
    writer.WriteLine("without a book root, the one book up to three folders below the working folder");
    writer.WriteLine("the server opens a browser tab, reuses one already serving the book, and stops with no tab open");
    writer.WriteLine("--no-open leaves opening the page to the caller, as a tool linking to the book does");
  }

  private static void Require(string[] arguments, int optionIndex, int valueCount)
  {
    if (optionIndex + valueCount >= arguments.Length ||
      arguments.Skip(optionIndex + 1).Take(valueCount).Any(argument => argument.StartsWith('-')))
    {
      throw new ArgumentException($"{arguments[optionIndex]} requires {valueCount} value(s).");
    }
  }

  private static void RequireNoCommand(HeadlessCommand? command, string argument)
  {
    if (command is not null)
    {
      throw new ArgumentException(
        $"Use only one of --list, --view, --value, --lines, --check, --export or --url; found '{argument}' after " +
        "another.");
    }
  }


}

/// <summary>The one book at or below the working folder. Descent stops at a book root, and the depth cap keeps a run
/// from the wrong folder off the rest of the disk.</summary>
public static class BookDiscovery
{
  private const int MaximumDepth = 3;

  public static List<string> Find(string start)
  {
    var roots = new List<string>();
    Descend(new DirectoryInfo(start), 0, roots);
    roots.Sort(StringComparer.Ordinal);
    return roots;
  }

  private static void Descend(DirectoryInfo folder, int depth, List<string> roots)
  {
    if (File.Exists(Path.Combine(folder.FullName, "book.yaml")))
    {
      roots.Add(folder.FullName);
      return;
    }
    if (depth == MaximumDepth)
    {
      return;
    }
    DirectoryInfo[] children;
    try
    {
      children = folder.GetDirectories();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      return;
    }
    foreach (DirectoryInfo child in children.Where(Searchable))
    {
      Descend(child, depth + 1, roots);
    }
  }

  private static bool Searchable(DirectoryInfo folder)
  {
    return !folder.Name.StartsWith('.') &&
      folder.Name is not ("bin" or "obj") &&
      (folder.Attributes & FileAttributes.ReparsePoint) == 0;
  }


}

public record RequestedCell(string View, string Line, string Column)
{
  [JsonIgnore]
  public string Query => $"/?view={Uri.EscapeDataString(View)}&line={Uri.EscapeDataString(Line)}&column=" +
    Uri.EscapeDataString(Column);
}

public record CellAnswer(string Query, int Pages);

/// <summary>What <c>--cell --json</c> prints: the page's address at the cell, and how many open pages selected it.
/// </summary>
public record CellPage(string Url, int Pages);

public static class CellLink
{
  /// <summary>The page's query for a cell: the first view in navigation order presenting it, or failing that the
  /// line on its own, with the cell to select.</summary>
  public static string Query(string root, string name, string column)
  {
    return Resolve(Book.Load(root), name, column).Query;
  }

  public static RequestedCell Resolve(Book book, string name, string column)
  {
    string line = book.Resolve(name, "") ?? throw new ArgumentException($"Unknown line '{name}'.");
    string view = book.PresentingView(line, column, "") ?? book.PresentingView(line, null, "") ??
      $"{View.LinePrefix}{line}";
    return new RequestedCell(view, line, column);
  }


}

public static class HeadlessRunner
{
  // The default encoder escapes formula punctuation like + and ' for embedding in HTML, which a terminal never needs.
  private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(PayloadJson.Options)
  {
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  private static readonly JsonSerializerOptions ViewJsonOptions = new JsonSerializerOptions(PayloadJson.Options)
  {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  public static int Run(
    string root, HeadlessCommand command, ViewQuery? query, bool json, IReadOnlyList<string>? lines = null)
  {
    return command switch
    {
      HeadlessCommand.List => RunList(root, json),
      HeadlessCommand.Lines => RunLines(root, lines!, json),
      HeadlessCommand.Check => RunCheck(root, json),
      HeadlessCommand.View or HeadlessCommand.Value => RunView(root, query!, json),
      _ => throw new ArgumentOutOfRangeException(nameof(command), command, "not a console command")
    };
  }

  private static int RunList(string root, bool json)
  {
    ViewCatalog catalog = CatalogLoader.Load(Book.Load(root), 1);
    if (json)
    {
      Console.WriteLine(JsonSerializer.Serialize(catalog.Views, JsonOptions));
      return 0;
    }
    foreach (ViewCatalogEntry entry in catalog.Views)
    {
      Console.WriteLine(entry.Id);
    }
    return 0;
  }

  /// <summary>Loads the book once for every line, so a caller reading many pays one start, and reads every line in the
  /// book when no name is given. Exits non-zero only when a name is unknown or fails to evaluate; a cell with no value
  /// is marked, not a failure.</summary>
  private static int RunLines(string root, IReadOnlyList<string> names, bool json)
  {
    Book book = Book.Load(root);
    var views = new Dictionary<string, (View View, Engine Engine)>();
    IEnumerable<string> asked = names.Count > 0
      ? names
      : book.Facts.Keys.Concat(book.Formulas.Keys).Distinct().Order(StringComparer.Ordinal);
    List<LinePayload> lines = asked.Select(name => ReadLine(book, views, name)).ToList();
    if (json)
    {
      Console.WriteLine(JsonSerializer.Serialize(lines, JsonOptions));
    }
    else
    {
      int width = lines.Select(line => line.Name.Length).DefaultIfEmpty().Max();
      foreach (LinePayload line in lines)
      {
        string values = line.Error is not null
          ? $"error: {line.Error}"
          : string.Join(", ", line.Columns!.Zip(line.Display!, (column, display) => $"{column} {display}"));
        Console.WriteLine($"{line.Name.PadRight(width)}  {values}");
      }
    }
    return lines.Any(line => line.Error is not null) ? 1 : 0;
  }

  private static LinePayload ReadLine(Book book, Dictionary<string, (View View, Engine Engine)> views, string name)
  {
    string? line = book.Resolve(name, "");
    if (line is null)
    {
      return new LinePayload { Name = name, Error = $"Unknown line '{name}'." };
    }
    try
    {
      book.Facts.TryGetValue(line, out Fact? fact);
      string scope = fact?.Scope ?? book.Formulas[line].Scope;
      // Each scope formats with the formats.yaml nearest its folder, as a view there would.
      if (!views.TryGetValue(scope, out (View View, Engine Engine) scoped))
      {
        View view = View.ForCheckScope(book, book.FoldersByScope[scope]);
        scoped = (view, new Engine(view));
        views[scope] = scoped;
      }
      List<string> periods = book.PeriodsFor(scope);
      // Calculated columns follow the periods, by the names a reference writes them with.
      List<(string Name, string Column)> calculated = book.ColumnFormulas.Values
        .Select(formula => formula.Key)
        .Distinct()
        .Where(key => !periods.Contains(key))
        .Select(key => (Name: key, Column: book.ResolveColumn(key, scope)))
        .Where(column => column.Column is string resolved &&
          (!book.ColumnFormulas[resolved].FactsOnly || fact is not null))
        .Select(column => (column.Name, column.Column!))
        .ToList();
      List<string> columns = [.. periods, .. calculated.Select(column => column.Name)];
      List<ResolvedCell> cells = periods
        .Concat(calculated.Select(column => column.Column))
        .Select(column => scoped.Engine.Cell(new CellCoordinate(line, column)))
        .ToList();
      Formatter formatter = scoped.View.Formatter;
      return new LinePayload
      {
        Name = name,
        Label = scoped.View.LabelOf(line),
        Kind = fact is null ? "formula" : "fact",
        Units = scoped.Engine.Units(line) ?? formatter.DefaultUnits,
        Contra = fact?.Contra == true ? true : null,
        Columns = columns,
        Exact = cells
          .Select(cell => cell.Value is null ? null : formatter.Exact(cell.Units, cell.Value.Value))
          .ToList(),
        Display = cells.Select(cell => cell.Value is null
          ? formatter.Unresolved
          : PayloadBuilder.Display(scoped.View, cell, false)).ToList(),
        Unresolved = cells.Any(cell => cell.Value is null) ? cells.Select(cell => cell.Value is null).ToList() : null
      };
    }
    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
    {
      return new LinePayload { Name = name, Error = exception.Message };
    }
  }

  /// <summary>Every check in one pass, exiting non-zero on a failure, view error or missing line so a script can gate
  /// on it.</summary>
  private static int RunCheck(string root, bool json)
  {
    Book book = Book.Load(root);
    ViewCatalog catalog = CatalogLoader.Load(book, 1);
    var checkedViews = new List<CheckedView>();
    var checkedScopes = new HashSet<string>();
    foreach (ViewCatalogEntry entry in catalog.Views)
    {
      View view = book.Views[entry.Id];
      checkedScopes.Add(view.Scope);
      try
      {
        ResolvedTable table = ResolvedTable.Build(view);
        // A line that is neither fact nor formula renders as an easy-to-miss gap on the page; here it is a defect.
        List<string> missing = table.Coordinates()
          .Select(coordinate => coordinate.Line)
          .Distinct()
          .Where(line => !view.Facts.ContainsKey(line) && !view.Formulas.ContainsKey(line))
          .ToList();
        var engine = new Engine(view);
        foreach (CellCoordinate coordinate in table.Coordinates()
          .Where(coordinate => view.Facts.ContainsKey(coordinate.Line) || view.Formulas.ContainsKey(coordinate.Line)))
        {
          engine.ValidateCell(coordinate);
        }
        List<Check> checks = book.Checks.Where(check => check.Scope == view.Scope).ToList();
        List<CheckPayload> checkPayloads = PayloadBuilder.BuildChecks(view, engine, checks);
        checkedViews.Add(new CheckedView(entry.Id, entry.Title, null, checkPayloads, missing));
      }
      catch (Exception exception)
      {
        checkedViews.Add(new CheckedView(
          entry.Id, entry.Title, exception.Message, new List<CheckPayload>(), new List<string>()));
      }
    }
    foreach (IGrouping<string, Check> orphaned in book.Checks
      .Where(check => !checkedScopes.Contains(check.Scope))
      .GroupBy(check => check.Scope))
    {
      string id = orphaned.Key.Replace('.', '/');
      try
      {
        View view = View.ForCheckScope(book, book.FoldersByScope[orphaned.Key]);
        List<CheckPayload> checks = PayloadBuilder.BuildChecks(view, new Engine(view), orphaned);
        checkedViews.Add(new CheckedView(id, id, null, checks, new List<string>()));
      }
      catch (Exception exception)
      {
        checkedViews.Add(new CheckedView(
          id, id, exception.Message, new List<CheckPayload>(), new List<string>()));
      }
    }

    int passed = checkedViews.Sum(view => view.Checks.Count(check => check.Passed));
    int failed = checkedViews.Sum(view => view.Checks.Count(check => !check.Passed));
    int errors = checkedViews.Count(view => view.Error is not null);
    int missingLines = checkedViews.Sum(view => view.Missing.Count);
    // A formula pinned to one column repeats its value under every column of its scope.
    List<string> repeated = book.Formulas.Values
      .Where(formula => Expr.Pinned(formula.Expression) && book.PeriodsFor(formula.Scope).Count > 1)
      .Select(formula => formula.Name)
      .Order(StringComparer.Ordinal)
      .ToList();
    bool ok = failed == 0 && errors == 0 && missingLines == 0;

    if (json)
    {
      Console.WriteLine(JsonSerializer.Serialize(
        new
        {
          Ok = ok,
          Passed = passed,
          Failed = failed,
          Errors = errors,
          MissingLines = missingLines,
          Views = checkedViews,
          Repeated = repeated
        },
        JsonOptions));
      return ok ? 0 : 1;
    }

    int width = checkedViews.Count == 0 ? 0 : checkedViews.Max(view => view.Id.Length);
    foreach (CheckedView view in checkedViews)
    {
      Console.WriteLine($"{view.Id.PadRight(width)}  {Summarize(view)}");
      foreach (CheckPayload check in view.Checks.Where(check => !check.Passed))
      {
        Console.WriteLine($"    {check.Name}: {Deltas(check)}");
      }
      foreach (string line in view.Missing)
      {
        Console.WriteLine($"    missing line: {line}");
      }
    }
    foreach (string line in repeated)
    {
      Console.WriteLine($"repeated line: {line}, the same in every column; move it to a folder of one column");
    }
    Console.WriteLine();
    Console.WriteLine($"{passed + failed} checks in {checkedViews.Count} views: {passed} passed, {failed} failed, " +
      $"{errors} {Plural(errors, "view")} in error, {missingLines} missing {Plural(missingLines, "line")}" +
      (repeated.Count == 0 ? "" : $", {repeated.Count} repeated {Plural(repeated.Count, "line")}"));
    return ok ? 0 : 1;
  }

  private static string Plural(int count, string word)
  {
    return count == 1 ? word : $"{word}s";
  }

  private static string Summarize(CheckedView view)
  {
    if (view.Error is not null)
    {
      return $"error: {view.Error}";
    }
    int failed = view.Checks.Count(check => !check.Passed);
    string checks = view.Checks.Count == 0 ? "no checks"
      : failed == 0 ? $"{view.Checks.Count} passed"
      : $"{view.Checks.Count - failed} passed, {failed} failed";
    return view.Missing.Count == 0 ? checks : $"{checks}, {view.Missing.Count} missing";
  }

  private static string Deltas(CheckPayload check)
  {
    return check.Periods.Count == 0
      ? "no period carries its facts"
      : string.Join(", ", check.Periods.Zip(check.Deltas, (period, delta) => $"{period} {delta}"));
  }

  private static int RunView(string root, ViewQuery query, bool json)
  {
    View view = LoadView(root, query.ViewId);
    if (query.Line is not null)
    {
      return RunValue(view, query, json);
    }
    ViewPayload payload = PayloadBuilder.Build(view, new Engine(view), 1);
    if (json)
    {
      ViewTablePayload table = ViewTablePayloadBuilder.Build(query.ViewId, payload);
      Console.WriteLine(JsonSerializer.Serialize(table, ViewJsonOptions));
    }
    else
    {
      WriteTable(payload);
    }
    return 0;
  }

  /// <summary>Addresses a cell by coordinate, so a line and column name the same value in any orientation.</summary>
  private static int RunValue(View view, ViewQuery query, bool json)
  {
    ResolvedTable table = ResolvedTable.Build(view);
    string line = view.Resolve(query.Line!)
      ?? throw new InvalidDataException($"Unknown line '{query.Line}' in view '{query.ViewId}'.");
    string column = view.Book.CoordinateColumn(query.Column!, view.Scope, line);
    (int Row, int Column)? position = null;
    for (int row = 0; row < table.Rows.Count && position is null; row++)
    {
      for (int candidate = 0; candidate < table.Columns.Count; candidate++)
      {
        CellCoordinate? coordinate = table.Coordinate(row, candidate);
        if (coordinate?.Line == line && coordinate?.Column == column)
        {
          position = (row, candidate);
          break;
        }
      }
    }
    if (position is null)
    {
      IEnumerable<string> columns = table.Coordinates()
        .Where(coordinate => coordinate.Line == line)
        .Select(coordinate => ShortColumn(coordinate.Column, view.Scope))
        .Distinct();
      throw new InvalidDataException($"'{query.Line}[{query.Column}]' is not presented by view '{query.ViewId}'. " +
        $"Columns for this line: {string.Join(", ", columns)}");
    }

    var engine = new Engine(view);
    CellPayload cell = PayloadBuilder.BuildCell(view, engine, table, position.Value.Row, position.Value.Column).Cell;
    bool unresolved = cell.Unresolved || cell.Exact is null;
    if (json)
    {
      // An unresolved cell still prints, since its inputs show which one is missing.
      ValuePayload payload = ValuePayloadBuilder.Build(view, engine, query.ViewId, cell);
      Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
      return unresolved ? 1 : 0;
    }
    if (unresolved)
    {
      throw new InvalidDataException($"Value '{query.Line}[{query.Column}]' is unresolved.");
    }
    Console.WriteLine(cell.Exact);
    return 0;
  }

  /// <summary>A column formula's name as a user types it: bare within its own view, scoped elsewhere.</summary>
  private static string ShortColumn(string column, string scope)
  {
    string prefix = $"{scope}.";
    return column.StartsWith(prefix, StringComparison.Ordinal) ? column[prefix.Length..] : column;
  }

  private static View LoadView(string root, string id)
  {
    Book book = Book.Load(root);
    ViewCatalog catalog = CatalogLoader.Load(book, 1);
    if (!catalog.Views.Any(entry => entry.Id == id))
    {
      throw new InvalidDataException(
        $"Unknown view '{id}'. Available: {string.Join(", ", catalog.Views.Select(entry => entry.Id))}");
    }
    return book.Views[id];
  }

  private static void WriteTable(ViewPayload payload)
  {
    Console.WriteLine(payload.Title);
    if (payload.Subtitle.Length > 0)
    {
      Console.WriteLine(payload.Subtitle);
    }
    Console.WriteLine();

    var header = new List<string> { "Row", "Label" };
    header.AddRange(payload.Columns.Select(column => column.Label));
    WriteMarkdownRow(header);
    WriteMarkdownRow(header.Select(_ => "---"));
    foreach (RowPayload row in payload.Rows.Where(row => row.Kind != "spacer"))
    {
      var values = new List<string> { row.Name, row.Label };
      values.AddRange(row.Cells.Select(cell => cell.Display));
      values.AddRange(Enumerable.Repeat("", payload.Columns.Count - row.Cells.Count));
      WriteMarkdownRow(values);
    }

    if (payload.Checks.Count == 0)
    {
      return;
    }
    int passed = payload.Checks.Count(check => check.Passed);
    Console.WriteLine();
    Console.WriteLine($"Checks: {passed} passed, {payload.Checks.Count - passed} failed");
    foreach (CheckPayload check in payload.Checks.Where(check => !check.Passed))
    {
      Console.WriteLine($"- {check.Name}: {Deltas(check)}");
    }
  }

  private static void WriteMarkdownRow(IEnumerable<string> values)
  {
    Console.WriteLine($"| {string.Join(" | ", values.Select(Escape))} |");
  }

  private static string Escape(string value)
  {
    return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
  }

  private record CheckedView(
    string Id, string Title, string? Error, List<CheckPayload> Checks, List<string> Missing);


}

public static class ViewTablePayloadBuilder
{
  public static ViewTablePayload Build(string id, ViewPayload payload)
  {
    List<ViewTableColumnPayload> columns = payload.Columns
      .Select(column => new ViewTableColumnPayload(column.Name, column.Label))
      .ToList();
    List<ViewTableRowPayload> rows = payload.Rows.Select(row => Row(row, columns)).ToList();
    return new ViewTablePayload(
      id,
      payload.Title,
      Present(payload.Subtitle),
      columns,
      rows,
      payload.Checks,
      payload.Sensitivities);
  }

  private static ViewTableRowPayload Row(RowPayload row, List<ViewTableColumnPayload> columns)
  {
    bool hasCells = row.Cells.Count > 0;
    bool elsewhere = row.Cells.Where((cell, index) => cell.Line != row.Name || cell.Column != columns[index].Name)
      .Any();
    return new ViewTableRowPayload
    {
      Name = Present(row.Name),
      Label = Present(row.Label),
      Kind = row.Kind,
      Style = Present(row.Style),
      Indent = row.Indent == 0 ? null : row.Indent,
      Units = Present(row.Units),
      Exact = hasCells ? row.Cells.Select(cell => cell.Exact).ToList() : null,
      Display = hasCells ? row.Cells.Select(cell => cell.Display).ToList() : null,
      Unresolved = row.Cells.Any(cell => cell.Unresolved)
        ? row.Cells.Select(cell => cell.Unresolved).ToList()
        : null,
      Contra = row.Cells.Any(cell => cell.Contra) ? row.Cells.Select(cell => cell.Contra).ToList() : null,
      Cells = elsewhere ? row.Cells.Select(cell => new ViewTableCellPayload(cell.Line, cell.Column)).ToList() : null
    };
  }

  private static string? Present(string value)
  {
    return value.Length == 0 ? null : value;
  }


}

/// <summary>A cell for someone reading JSON rather than the page: the calculation as one string, and the cells it
/// reads and is read by without the page's rendering detail.</summary>
public static class ValuePayloadBuilder
{
  public static ValuePayload Build(View view, Engine engine, string viewId, CellPayload cell)
  {
    string columnLabel = view.ColumnLabel(cell.Column);
    return new ValuePayload
    {
      View = viewId,
      Line = cell.Line,
      Column = cell.Column,
      Label = view.LabelOf(cell.Line),
      ColumnLabel = columnLabel == cell.Column ? null : columnLabel,
      Kind = cell.Kind,
      Units = cell.Units.Length == 0 ? null : cell.Units,
      Value = cell.Exact,
      Display = cell.Display,
      Unresolved = cell.Unresolved ? true : null,
      Contra = cell.Contra ? true : null,
      Formula = cell.Formula?.Text,
      Calculation = cell.Calculation is null ? null : Text(cell.Calculation),
      Inputs = cell.Calculation is null ? null : Inputs(engine, cell.Calculation),
      Note = cell.Note,
      Source = cell.Source is null
        ? null
        : new ValueSourcePayload(
          cell.Source.Table, cell.Source.Url, cell.Source.Image, cell.Source.Details, cell.Source.Note),
      Dependents = cell.Dependents.Count == 0
        ? null
        : cell.Dependents
          .Select(dependent => new ValueCellPayload
          {
            Line = dependent.Line,
            Column = dependent.Column,
            Label = dependent.Label,
            Display = dependent.Value,
            View = dependent.SourceView
          })
          .ToList()
    };
  }

  // The page's blank for an unresolved input vanishes in a single string.
  private static string Text(CalculationPayload calculation)
  {
    return string.Concat(calculation.Tokens.Select(token =>
      token.Dependency is not null && token.Text.Length == 0 ? "?" : token.Text));
  }

  private static List<ValueCellPayload>? Inputs(Engine engine, CalculationPayload calculation)
  {
    List<ValueCellPayload> inputs = calculation.Tokens
      .Where(token => token.Dependency is not null)
      .DistinctBy(token => (token.Dependency, token.Column))
      .Select(token => new ValueCellPayload
      {
        Line = token.Dependency!,
        Column = token.Column,
        Label = token.Label,
        Display = token.Missing is null ? token.Text : null,
        View = token.SourceView,
        Missing = token.Missing,
        Formula = token.Expansion is null ? null : engine.Cell(token.Dependency!, token.Column!).Formula,
        Calculation = token.Expansion is null ? null : Text(token.Expansion),
        Inputs = token.Expansion is null ? null : Inputs(engine, token.Expansion)
      })
      .ToList();
    return inputs.Count == 0 ? null : inputs;
  }


}