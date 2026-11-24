using System.Text.Json.Serialization;

namespace Libreta;

public record ViewQuery(string ViewId, string? Line = null, string? Column = null);

public enum HeadlessCommand { List, View, Value, Lines, Check, Export, Url, SetRegions, ShowRegions }

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
  /// <summary>The file <c>--export</c> writes, or the folder <c>--show-regions</c> writes its images to.</summary>
  public string? Output { get; private init; }
  /// <summary>The fact table <c>--set-regions</c> writes or <c>--show-regions</c> reads, and the JSON file of regions
  /// <c>--set-regions</c> writes there.</summary>
  public string? RegionsTable { get; private init; }
  public string? RegionsFile { get; private init; }
  /// <summary>The revision a headless read takes the book as, read from git rather than the working tree.</summary>
  public string? At { get; private init; }
  public bool Json { get; private init; }
  public bool Help { get; private init; }
  public bool Version { get; private init; }
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
    string? regionsTable = null;
    string? regionsFile = null;
    bool json = false;
    string? at = null;
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
        case "--set-regions":
          RequireNoCommand(command, argument);
          Require(arguments, index, 2);
          command = HeadlessCommand.SetRegions;
          regionsTable = arguments[++index];
          regionsFile = arguments[++index];
          break;
        case "--show-regions":
          RequireNoCommand(command, argument);
          Require(arguments, index, 2);
          command = HeadlessCommand.ShowRegions;
          regionsTable = arguments[++index];
          output = arguments[++index];
          break;
        case "--url":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Url;
          break;
        case "--json":
          json = true;
          break;
        case "--at":
          Require(arguments, index, 1);
          at = arguments[++index];
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
        case "--version":
        case "-v":
          return new CommandLineOptions { Version = true };
        default:
          throw new ArgumentException($"Unknown option '{argument}'.");
      }
    }

    if (command is not null && portSpecified)
    {
      throw new ArgumentException(
        "--port cannot be combined with --list, --view, --value, --lines, --check, --export, --set-regions, " +
        "--show-regions or --url.");
    }
    if ((command is HeadlessCommand.Export or HeadlessCommand.Url or HeadlessCommand.SetRegions or
      HeadlessCommand.ShowRegions ||
      command is null && cell is null) && json)
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
    if (at is not null && command is not
      (HeadlessCommand.List or HeadlessCommand.View or HeadlessCommand.Value or HeadlessCommand.Lines or
      HeadlessCommand.Check or HeadlessCommand.Export))
    {
      throw new ArgumentException("--at reads a book as a revision left it, so it needs --list, --view, --value, " +
        "--lines, --check or --export.");
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
      RegionsTable = regionsTable,
      RegionsFile = regionsFile,
      At = at,
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
    writer.WriteLine("  libreta [<book-root>] --set-regions <table.yaml> <regions.json>   (a fact table's source image " +
      "and regions, measured by another tool)");
    writer.WriteLine("  libreta [<book-root>] --show-regions <table.yaml> <folder>   (each region cut from its image " +
      "and outlined, to read back against its value)");
    writer.WriteLine("  libreta [<book-root>] --url   (the address of the server already running the book)");
    writer.WriteLine("  libreta <book-root> <headless command> --at <revision>   (the book as a git revision left it, " +
      "read without checking it out; with --list, --view, --value, --lines, --check or --export)");
    writer.WriteLine("  libreta --docs [format|running]");
    writer.WriteLine("  libreta --version");
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
        $"Use only one of --list, --view, --value, --lines, --check, --export, --set-regions, --show-regions or " +
        $"--url; found '{argument}' after another.");
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
    column = book.CoordinateColumn(column, "", line);
    string view = book.PresentingView(line, column, "") ?? book.PresentingView(line, null, "") ??
      $"{View.LinePrefix}{line}";
    return new RequestedCell(view, line, column);
  }


}
