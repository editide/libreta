namespace Libreta;

/// <summary>A presentation over the book graph. Its folder supplies the lexical scope used by short
/// line names, so a nested view can reuse its parent's definitions without copying them.</summary>
public class View
{
  /// <summary>Starts the id of a view <see cref="ForLine"/> makes for a line no table shows.</summary>
  public const string LinePrefix = "line:";

  public Book Book { get; private init; } = null!;
  public string Id { get; private init; } = "";
  public string Scope { get; private init; } = "";
  public List<string> Periods => Book.PeriodsFor(Scope);
  public Dictionary<string, Fact> Facts => Book.Facts;
  public Dictionary<string, Formula> Formulas => Book.Formulas;
  public Dictionary<string, ColumnFormula> ColumnFormulas => Book.ColumnFormulas;
  public List<Check> Checks { get; private init; } = new List<Check>();
  public ViewFile Presentation { get; private init; } = new ViewFile();
  public Formatter Formatter { get; private init; } = null!;

  public static View Load(Book book, string folder)
  {
    string scope = book.Scope(folder);
    string? checkScope = scope;
    while (checkScope is not null && checkScope.Length > 0 && !book.Checks.Any(check => check.Scope == checkScope))
    {
      int separator = checkScope.LastIndexOf('.');
      checkScope = separator < 0 ? "" : checkScope[..separator];
      // An ancestor with its own view shows its checks there, over the periods it presents.
      if (book.ViewIdsByScope.ContainsKey(checkScope))
      {
        checkScope = null;
      }
    }

    string id = book.ViewFoldersById.Single(entry => Path.GetFullPath(entry.Value) == Path.GetFullPath(folder)).Key;
    return new View
    {
      Book = book,
      Id = id,
      Scope = scope,
      Checks = book.Checks.Where(check => check.Scope == checkScope).ToList(),
      Presentation = book.Presentations[id],
      Formatter = book.FormatterFor(folder)
    };
  }

  /// <summary>A line no table shows, presented above the lines its formula reads, so a link to it has a page.
  /// </summary>
  public static View ForLine(Book book, string line)
  {
    string scope = book.Formulas.TryGetValue(line, out Formula? formula) ? formula.Scope : book.Facts[line].Scope;
    string folder = book.FoldersByScope.GetValueOrDefault(scope) ?? book.Root;
    var presentation = new ViewFile
    {
      Columns = book.PeriodsFor(scope).Select(period => new ViewColumn { Source = period }).ToList(),
      Rows = { new ViewRow { Line = line } }
    };
    var view = new View
    {
      Book = book,
      Id = $"{LinePrefix}{line}",
      Scope = scope,
      Presentation = presentation,
      Formatter = book.FormatterFor(folder)
    };
    presentation.Title = view.LabelOf(line);
    presentation.Subtitle = "No table shows this line, so it stands here with the lines it reads.";
    if (formula is not null)
    {
      List<string> inputs = new Engine(view).References(formula.Expression, null, scope, line)
        .Select(reference => reference.Line)
        .Where(input => input != line)
        .Distinct()
        .ToList();
      if (inputs.Count > 0)
      {
        presentation.Rows.Add(new ViewRow { Label = "Reads" });
        presentation.Rows.AddRange(inputs.Select(input => new ViewRow { Line = input }));
      }
    }
    return view;
  }

  internal static View ForCheckScope(Book book, string folder)
  {
    string scope = book.Scope(folder);
    return new View
    {
      Book = book,
      Scope = scope,
      Checks = book.Checks.Where(check => check.Scope == scope).ToList(),
      Formatter = book.FormatterFor(folder)
    };
  }

  public string? Resolve(string name)
  {
    return Book.Resolve(name, Scope);
  }

  public string ResolveColumn(string name)
  {
    return Book.ResolveColumnOrPeriod(name, Scope);
  }

  public string LabelOf(string name)
  {
    if (Book.Facts.TryGetValue(name, out Fact? fact) && !string.IsNullOrWhiteSpace(fact.Label))
    {
      return fact.Label;
    }
    if (Book.Formulas.TryGetValue(name, out Formula? formula) && !string.IsNullOrWhiteSpace(formula.Label))
    {
      return formula.Label;
    }
    return Humanize(name);
  }

  public string ColumnLabel(string name)
  {
    string resolved = ResolveColumn(name);
    if (!Book.ColumnFormulas.TryGetValue(resolved, out ColumnFormula? formula))
    {
      return name;
    }
    return string.IsNullOrWhiteSpace(formula.Label) ? Humanize(resolved) : formula.Label;
  }

  private static string Humanize(string name)
  {
    int bookSeparator = name.LastIndexOf("::", StringComparison.Ordinal);
    int separator = Math.Max(name.LastIndexOf('.'), bookSeparator < 0 ? -1 : bookSeparator + 1);
    string local = name[(separator + 1)..].Replace('_', ' ');
    return local.Length == 0 ? local : $"{char.ToUpperInvariant(local[0])}{local[1..]}";
  }


}
