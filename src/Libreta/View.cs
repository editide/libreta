using System.Globalization;
using System.Text;
using YamlDotNet.Serialization;

namespace Libreta;

public class Fact
{
  public string Name { get; set; } = "";
  public string Scope { get; set; } = "";
  public string Label { get; set; } = "";
  public string? Units { get; set; }
  public string? Currency { get; set; }
  public string? FiscalYearEnd { get; set; }

  /// <summary>The line runs against its subtotal: an expense or a contra account. Stored in natural direction; only
  /// the rendered cell is negated.</summary>
  public bool Contra { get; set; }

  /// <summary>An empty period cell means the item did not occur, so it resolves to zero rather than unresolved.
  /// Calculated columns still apply, so a bridge sums the zeros rather than being replaced by them.</summary>
  public bool MissingZero { get; set; }
  /// <summary>Its CSV cells are ISO dates, held as day serials.</summary>
  public bool Date { get; set; }
  /// <summary>One per fragment that wrote a distinct note, in load order.</summary>
  public List<string> Notes { get; set; } = new List<string>();
  public Dictionary<string, FactCell> Cells { get; set; } = new Dictionary<string, FactCell>();
}

public class FactCell
{
  public double Value { get; set; }
  public bool PrintedDash { get; set; }
  public string? Note { get; set; }
  /// <summary>The note on the line in the fact table this cell came from.</summary>
  public string? LineNote { get; set; }
  public string TableTitle { get; set; } = "";
  public SourceInfo Source { get; set; } = new SourceInfo();
  public string SourceImage { get; set; } = "";
  public SourceRegion? SourceRegion { get; set; }
  public string SourcePath { get; set; } = "";
}

public class Formula
{
  public string Name { get; set; } = "";
  public string Label { get; set; } = "";
  public string Scope { get; set; } = "";
  public Expr Expression { get; set; } = null!;
  public string Text { get; set; } = "";
  public string? Units { get; set; }
  public string? Note { get; set; }
  public Dictionary<string, string> CellNotes { get; set; } = new Dictionary<string, string>();
}

public class ColumnFormula
{
  public string Name { get; set; } = "";
  /// <summary>The name as written in its file, which a reference resolves from any scope it covers.</summary>
  public string Key { get; set; } = "";
  public string Label { get; set; } = "";
  public string Scope { get; set; } = "";
  public Expr Expression { get; set; } = null!;
  public string Text { get; set; } = "";
  public string? Units { get; set; }
  public string? Note { get; set; }
  public bool FactsOnly { get; set; }
}

public class CellOverride
{
  public string Line { get; set; } = "";
  public string Column { get; set; } = "";
  public string Scope { get; set; } = "";
  public double? Value { get; set; }
  public Expr? Expression { get; set; }
  public string? Text { get; set; }
  public string? Units { get; set; }
  public bool Blank { get; set; }
  public string? Note { get; set; }
}

public class Check
{
  public string Name { get; set; } = "";
  public string Scope { get; set; } = "";
  public Expr Expression { get; set; } = null!;
  public string Text { get; set; } = "";
  public double Expect { get; set; }
  public double Tolerance { get; set; }
  public string? Note { get; set; }
}

public class Sensitivity
{
  public string Name { get; set; } = "";
  public string Title { get; set; } = "";
  public string Scope { get; set; } = "";
  public SensitivityCoordinate Output { get; set; } = new SensitivityCoordinate();
  public List<SensitivityInputDefinition> Inputs { get; set; } = new List<SensitivityInputDefinition>();
  public Expr? Valid { get; set; }
}

/// <summary>The book's single namespaced graph. Facts and formulas are loaded once, then any view
/// can present them through a local, ancestor, or fully qualified reference.</summary>
public class Book
{
  public string Root { get; private init; } = "";
  public BookStructure Structure { get; private init; } = null!;
  public Dictionary<string, Fact> Facts { get; } = new Dictionary<string, Fact>();
  public Dictionary<string, Formula> Formulas { get; } = new Dictionary<string, Formula>();
  public Dictionary<string, ColumnFormula> ColumnFormulas { get; } = new Dictionary<string, ColumnFormula>();
  public Dictionary<(string Line, string Column), CellOverride> CellOverrides { get; } =
    new Dictionary<(string Line, string Column), CellOverride>();
  public List<Check> Checks { get; } = new List<Check>();
  public Dictionary<string, Sensitivity> Sensitivities { get; } = new Dictionary<string, Sensitivity>();
  public Dictionary<string, string> ViewIdsByScope { get; } = new Dictionary<string, string>();
  public Dictionary<string, string> ViewFoldersById { get; } = new Dictionary<string, string>();
  public Dictionary<string, ViewFile> Presentations { get; } = new Dictionary<string, ViewFile>();
  public Dictionary<string, View> Views { get; } = new Dictionary<string, View>();
  public Dictionary<string, string> FoldersByScope { get; } = new Dictionary<string, string>();
  private Dictionary<string, List<string>> PeriodsByScope { get; } = new Dictionary<string, List<string>>();
  private Dictionary<CellCoordinate, List<string>> ViewIdsByCoordinate { get; } =
    new Dictionary<CellCoordinate, List<string>>();
  private Dictionary<string, List<string>> ViewIdsByLine { get; } = new Dictionary<string, List<string>>();
  public IEnumerable<CellCoordinate> PresentedCoordinates => ViewIdsByCoordinate.Keys;

  public static Book Load(string root)
  {
    IDeserializer deserializer = YamlFile.Deserializer();
    BookStructure structure = BookStructure.Load(root);
    var book = new Book { Root = structure.Root, Structure = structure };

    foreach (BookDefinition definition in structure.Definitions)
    {
      foreach ((string csvPath, string scopeFolder) in structure.FilesBelow(definition, "*.csv", "facts"))
      {
        string metadataPath = Path.ChangeExtension(csvPath, ".yaml");
        FactTableFile metadata = YamlFile.Load<FactTableFile>(metadataPath, deserializer);
        string scope = BookStructure.Scope(definition, scopeFolder);
        book.FoldersByScope.TryAdd(scope, scopeFolder);
        book.LoadFactTable(csvPath, metadataPath, metadata, scope, definition.File);
      }
    }

    var formulaFiles = new List<(string Scope, FormulasFile File)>();
    foreach ((BookDefinition definition, string path, string scope) in book.ScopedFiles("formulas.yaml"))
    {
      FormulasFile formulas = YamlFile.Load<FormulasFile>(path, deserializer);
      formulaFiles.Add((scope, formulas));
      foreach (KeyValuePair<string, FormulaDefinition> entry in formulas.Formulas)
      {
        (Expr expression, string text) = Expand(entry.Value.Formula, definition);
        string name = Qualify(scope, entry.Key);
        Add(book.Formulas, name, new Formula
        {
          Name = name,
          Label = entry.Value.Label,
          Scope = scope,
          Expression = expression,
          Text = text,
          Units = ValueOrDefault(entry.Value.Units, definition.File.Units),
          Note = entry.Value.Note?.Trim(),
          CellNotes = entry.Value.CellNotes.ToDictionary(note => note.Key, note => note.Value.Trim())
        });
      }
      foreach (KeyValuePair<string, ColumnFormulaDefinition> entry in formulas.ColumnFormulas)
      {
        string name = Qualify(scope, entry.Key);
        (Expr expression, string text) = Expand(entry.Value.Formula, definition);
        Add(book.ColumnFormulas, name, new ColumnFormula
        {
          Name = name,
          Key = entry.Key,
          Label = entry.Value.Label,
          Scope = scope,
          Expression = expression,
          Text = text,
          // Undeclared units stay null so the column adopts each line's units instead of the book default.
          Units = entry.Value.Units,
          Note = entry.Value.Note?.Trim(),
          FactsOnly = entry.Value.AppliesTo switch
          {
            null or "all" => false,
            "facts" => true,
            _ => throw new InvalidDataException(
              $"Column formula '{name}' has unknown applies_to '{entry.Value.AppliesTo}'; use 'all' or 'facts'")
          }
        });
      }
    }

    book.KeyFactCellsByColumn();

    foreach ((string scope, FormulasFile formulas) in formulaFiles)
    {
      foreach (KeyValuePair<string, Dictionary<string, CellOverrideDefinition>> lineEntry in formulas.CellOverrides)
      {
        string line = book.Resolve(lineEntry.Key, scope)
          ?? throw new InvalidDataException($"Cell override references unknown line '{lineEntry.Key}'");
        foreach (KeyValuePair<string, CellOverrideDefinition> columnEntry in lineEntry.Value)
        {
          string column = book.ResolveColumnOrPeriod(columnEntry.Key, scope);
          if (book.Facts.TryGetValue(line, out Fact? fact) && fact.Cells.ContainsKey(column))
          {
            throw new InvalidDataException($"Cell override cannot replace sourced fact '{line}[{columnEntry.Key}]'");
          }
          CellOverrideDefinition definition = columnEntry.Value;
          int definitions = (definition.Blank ? 1 : 0) + (definition.Formula is null ? 0 : 1) +
            (definition.Value is null ? 0 : 1);
          if (definitions > 1)
          {
            throw new InvalidDataException(
              $"Cell override '{lineEntry.Key}[{columnEntry.Key}]' must use only one of value, formula, or blank");
          }
          (Expr Expression, string Text)? formula = definition.Formula is null
            ? null
            : Expand(definition.Formula, book.DefinitionForScope(scope));
          Expr? expression = formula?.Expression;
          if (definitions == 0 && definition.Units is null)
          {
            throw new InvalidDataException($"Cell override '{lineEntry.Key}[{columnEntry.Key}]' is empty");
          }
          var cellOverride = new CellOverride
          {
            Line = line,
            Column = column,
            Scope = scope,
            Value = definition.Value,
            Expression = expression,
            Text = formula?.Text,
            Units = definition.Units,
            Blank = definition.Blank,
            Note = definition.Note?.Trim()
          };
          if (!book.CellOverrides.TryAdd((line, column), cellOverride))
          {
            throw new InvalidDataException($"Duplicate cell override '{lineEntry.Key}[{columnEntry.Key}]'");
          }
        }
      }
    }

    foreach ((BookDefinition definition, string path, string scope) in book.ScopedFiles("view.yaml"))
    {
      string folder = Path.GetDirectoryName(path)!;
      (_, string id) = BookStructure.ViewId(definition, folder);
      book.ViewIdsByScope.Add(scope, id);
      book.ViewFoldersById.Add(id, folder);
      ViewFile presentation = YamlFile.Load<ViewFile>(path, deserializer);
      book.Presentations.Add(id, presentation);
      book.AddPeriods(scope, presentation.Columns
        .Where(column => column.Source is not null)
        .Select(column => column.Source!)
        .Where(column => book.ResolveColumn(column, scope) is null));
    }

    foreach ((BookDefinition definition, string path, string scope) in book.ScopedFiles("checks.yaml"))
    {
      ChecksFile checks = YamlFile.Load<ChecksFile>(path, deserializer);
      foreach (KeyValuePair<string, CheckDefinition> entry in checks.Checks)
      {
        (Expr expression, string text) = Expand(entry.Value.Formula, definition);
        book.Checks.Add(new Check
        {
          Name = entry.Key,
          Scope = scope,
          Expression = expression,
          Text = text,
          Expect = entry.Value.Expect,
          Tolerance = entry.Value.Tolerance,
          Note = entry.Value.Note?.Trim()
        });
      }
    }

    foreach ((BookDefinition bookDefinition, string path, string scope) in book.ScopedFiles("sensitivities.yaml"))
    {
      SensitivitiesFile file = YamlFile.Load<SensitivitiesFile>(path, deserializer);
      foreach (KeyValuePair<string, SensitivityDefinition> entry in file.Sensitivities)
      {
        SensitivityDefinition definition = entry.Value;
        if (definition.Inputs.Count is < 1 or > 2)
        {
          throw new InvalidDataException($"Sensitivity '{entry.Key}' must define one or two inputs");
        }
        if (definition.Inputs.Any(input => input.Values.Count == 0))
        {
          throw new InvalidDataException($"Sensitivity '{entry.Key}' inputs must contain trial values");
        }
        if (definition.Inputs.SelectMany(input => input.Values).Any(value => !double.IsFinite(value)))
        {
          throw new InvalidDataException($"Sensitivity '{entry.Key}' trial values must be finite");
        }
        if (definition.Inputs.Any(input => input.Line.Length == 0 || input.Column.Length == 0) ||
          definition.Output.Line.Length == 0 || definition.Output.Column.Length == 0)
        {
          throw new InvalidDataException($"Sensitivity '{entry.Key}' coordinates require line and column");
        }
        string name = Qualify(scope, entry.Key);
        Add(book.Sensitivities, name, new Sensitivity
        {
          Name = name,
          Title = definition.Title.Length == 0 ? entry.Key : definition.Title,
          Scope = scope,
          Output = definition.Output,
          Inputs = definition.Inputs,
          Valid = definition.Valid is null
            ? null
            : Expand(definition.Valid, bookDefinition).Expression
        });
      }
    }

    book.IndexPresentations();
    return book;
  }

  /// <summary>A CSV header named like a column formula is that column, so its sourced cells take the column's
  /// coordinate and win over the formula.</summary>
  private void KeyFactCellsByColumn()
  {
    foreach (Fact fact in Facts.Values)
    {
      foreach (string period in fact.Cells.Keys.ToList())
      {
        string column = ResolveColumnOrPeriod(period, fact.Scope);
        if (column != period)
        {
          fact.Cells[column] = fact.Cells[period];
          fact.Cells.Remove(period);
        }
      }
    }
  }

  /// <summary>Every file of one name the book owns, with the scope its folder gives it.</summary>
  private IEnumerable<(BookDefinition Definition, string Path, string Scope)> ScopedFiles(string fileName)
  {
    foreach (BookDefinition definition in Structure.Definitions)
    {
      foreach (string path in Structure.Files(definition, fileName))
      {
        string folder = Path.GetDirectoryName(path)!;
        string scope = BookStructure.Scope(definition, folder);
        FoldersByScope.TryAdd(scope, folder);
        yield return (definition, path, scope);
      }
    }
  }

  public string? PresentingView(string line, string? column, string currentView, string? preferredView = null)
  {
    List<string>? candidates = column is null
      ? ViewIdsByLine.GetValueOrDefault(line)
      : ViewIdsByCoordinate.GetValueOrDefault(new CellCoordinate(line, column));
    if (preferredView is not null && preferredView != currentView && candidates?.Contains(preferredView) == true)
    {
      return preferredView;
    }
    return candidates?.FirstOrDefault(candidate => candidate != currentView);
  }

  public string? PresentingViewIncludingCurrent(string line, string column, string currentView)
  {
    List<string>? candidates = ViewIdsByCoordinate.GetValueOrDefault(new CellCoordinate(line, column));
    return candidates?.Contains(currentView) == true ? currentView : candidates?.FirstOrDefault();
  }

  /// <summary>Resolves a reference that may end in <c>self</c>, the current row's line. <c>interim.self</c> names that
  /// line in another scope, as a bridge column reading one folder down needs.</summary>
  public string? ResolveSelf(string name, string scope, string? currentLine)
  {
    string normalized = BookStructure.Normalize(name);
    if (normalized == "self")
    {
      return currentLine;
    }
    if (!normalized.EndsWith(".self", StringComparison.Ordinal))
    {
      return Resolve(name, scope);
    }
    if (currentLine is null)
    {
      return null;
    }

    int lineSeparator = Math.Max(
      currentLine.LastIndexOf('.'),
      currentLine.LastIndexOf("::", StringComparison.Ordinal) + 1);
    string line = currentLine[(lineSeparator + 1)..];
    string prefix = normalized[..^".self".Length];
    BookDefinition owner = DefinitionForScope(scope);
    foreach (string candidateScope in EnclosingScopes(LocalScope(owner, scope)))
    {
      string candidate = BookStructure.Qualify(owner.Namespace, Qualify(Qualify(candidateScope, prefix), line));
      if (Contains(candidate))
      {
        return candidate;
      }
    }

    string absolute = BookStructure.Qualify(owner.Namespace, Qualify(prefix, line));
    return Contains(absolute) ? absolute : null;
  }

  public string? Resolve(string name, string scope)
  {
    return Lookup(name, scope, Contains);
  }

  public string? ResolveColumn(string name, string scope)
  {
    return Lookup(name, scope, ColumnFormulas.ContainsKey);
  }

  public string? ResolveSensitivity(string name, string scope)
  {
    return Lookup(name, scope, Sensitivities.ContainsKey);
  }

  /// <summary>Qualified names are taken as written; short ones are tried in each enclosing scope.</summary>
  private string? Lookup(string name, string scope, Func<string, bool> exists)
  {
    string normalized = BookStructure.Normalize(name);
    BookDefinition owner = DefinitionForScope(scope);
    if (!normalized.Contains('.') && !normalized.Contains("::", StringComparison.Ordinal))
    {
      foreach (string candidateScope in EnclosingScopes(LocalScope(owner, scope)))
      {
        string candidate = BookStructure.Qualify(owner.Namespace, Qualify(candidateScope, normalized));
        if (exists(candidate))
        {
          return candidate;
        }
      }
    }
    string qualified = BookStructure.Qualify(owner.Namespace, normalized);
    return exists(qualified) ? qualified : null;
  }

  private static IEnumerable<string> EnclosingScopes(string scope)
  {
    while (true)
    {
      yield return scope;
      int separator = scope.LastIndexOf('.');
      if (separator < 0)
      {
        yield break;
      }
      scope = scope[..separator];
    }
  }

  /// <summary>Walks inclusion ids such as <c>sector::nvidia</c> from the book owning the reference.</summary>
  public BookDefinition? ResolveBook(string reference, string scope)
  {
    BookDefinition owner = DefinitionForScope(scope);
    foreach (string segment in reference.Split("::", StringSplitOptions.RemoveEmptyEntries))
    {
      string normalized = BookStructure.Normalize(segment);
      BookDefinition? child = owner.Children
        .FirstOrDefault(candidate => BookStructure.Normalize(candidate.Id) == normalized);
      if (child is null)
      {
        return null;
      }
      owner = child;
    }
    return owner == DefinitionForScope(scope) ? null : owner;
  }

  /// <summary>Whether a scope names a loaded folder, including one that only groups others.</summary>
  private bool ContainsScope(string scope)
  {
    return FoldersByScope.Keys.Any(known => known == scope || known.StartsWith($"{scope}.", StringComparison.Ordinal));
  }

  /// <summary>The folder a comparison row names, found outward from the view like a short line reference.</summary>
  public string? ResolveScope(string name, string scope)
  {
    string normalized = BookStructure.Normalize(name);
    BookDefinition owner = DefinitionForScope(scope);
    return EnclosingScopes(LocalScope(owner, scope))
      .Select(candidateScope => BookStructure.Qualify(owner.Namespace, Qualify(candidateScope, normalized)))
      .FirstOrDefault(ContainsScope);
  }

  public string ResolveColumnOrPeriod(string name, string scope)
  {
    return ResolveColumn(name, scope) ?? name;
  }

  public string? ResolveCoordinateColumn(string name, string scope, string line)
  {
    string lineScope = Formulas.TryGetValue(line, out Formula? formula)
      ? formula.Scope
      : Facts.TryGetValue(line, out Fact? fact) ? fact.Scope : scope;
    string? calculated = ResolveColumn(name, lineScope);
    if (calculated is not null)
    {
      return calculated;
    }
    return PeriodsFor(lineScope).Contains(name) ? name : null;
  }

  /// <summary>Reduces a written column to a canonical one: the line's own calculated column or period first, then the
  /// referring scope's. Selection, evaluation and the command line share it.</summary>
  public string CoordinateColumn(string name, string scope, string line)
  {
    return ResolveCoordinateColumn(name, scope, line) ?? ResolveColumnOrPeriod(name, scope);
  }

  public List<string> ColumnsInRange(string line, string start, string end)
  {
    string scope = Formulas.TryGetValue(line, out Formula? formula)
      ? formula.Scope
      : Facts.TryGetValue(line, out Fact? fact) ? fact.Scope : "";
    List<string> periods = PeriodsFor(scope);
    int startIndex = periods.IndexOf(start);
    int endIndex = periods.IndexOf(end);
    if (startIndex < 0 || endIndex < 0)
    {
      throw new InvalidDataException($"Range '{line}[\"{start}\":\"{end}\"]' references an unknown column");
    }
    if (startIndex > endIndex)
    {
      throw new InvalidDataException($"Range '{line}[\"{start}\":\"{end}\"]' runs backward");
    }
    return periods.GetRange(startIndex, endIndex - startIndex + 1);
  }

  public string Scope(string folder)
  {
    string fullFolder = Path.GetFullPath(folder);
    BookDefinition? definition = Structure.Definitions
      .Where(candidate => IsWithin(candidate.Root, fullFolder))
      .OrderByDescending(candidate => candidate.Root.Length)
      .FirstOrDefault();
    if (definition is null)
    {
      throw new InvalidDataException($"'{folder}' is not inside the loaded book");
    }
    return BookStructure.Scope(definition, fullFolder);
  }

  public List<string> PeriodsFor(string scope)
  {
    BookDefinition owner = DefinitionForScope(scope);
    foreach (string local in EnclosingScopes(LocalScope(owner, scope)))
    {
      if (PeriodsByScope.TryGetValue(BookStructure.Qualify(owner.Namespace, local), out List<string>? periods))
      {
        return periods;
      }
    }
    return PeriodsByScope.GetValueOrDefault(owner.Namespace) ?? new List<string>();
  }

  public List<string> CheckPeriods(string scope)
  {
    List<string> periods = PeriodsFor(scope);
    if (periods.Count > 0)
    {
      return periods;
    }
    BookDefinition owner = DefinitionForScope(scope);
    string localScope = LocalScope(owner, scope);
    return PeriodsByScope
      .Where(entry => DefinitionForScope(entry.Key) == owner)
      .Where(entry =>
      {
        string entryScope = LocalScope(owner, entry.Key);
        return localScope.Length == 0 || entryScope == localScope ||
          entryScope.StartsWith($"{localScope}.", StringComparison.Ordinal);
      })
      .SelectMany(entry => entry.Value)
      .Distinct()
      .ToList();
  }

  public string? PriorPeriod(string scope, string period)
  {
    List<string> periods = CheckPeriods(scope);
    int index = periods.IndexOf(period);
    return index <= 0 ? null : periods[index - 1];
  }

  private void AddPeriods(string scope, IEnumerable<string> periods)
  {
    if (!PeriodsByScope.TryGetValue(scope, out List<string>? scopePeriods))
    {
      scopePeriods = new List<string>();
      PeriodsByScope[scope] = scopePeriods;
    }
    foreach (string period in periods.Where(period => !scopePeriods.Contains(period)))
    {
      scopePeriods.Add(period);
    }
  }

  private void IndexPresentations()
  {
    var indexed = new HashSet<string>();
    IEnumerable<string> ordered = NavigationViewIds(Structure.Definition)
      .Concat(ViewFoldersById.Keys.OrderBy(id => id, StringComparer.Ordinal));
    foreach (string id in ordered.Where(indexed.Add))
    {
      if (!ViewFoldersById.TryGetValue(id, out string? folder))
      {
        continue;
      }
      View view = View.Load(this, folder);
      Views.Add(id, view);
      foreach (CellCoordinate coordinate in ResolvedTable.Build(view).Coordinates().Distinct())
      {
        AddPresentation(ViewIdsByCoordinate, coordinate, id);
        AddPresentation(ViewIdsByLine, coordinate.Line, id);
      }
    }
  }

  private static IEnumerable<string> NavigationViewIds(BookDefinition definition)
  {
    return NavigationViewIds(definition, definition.File.Navigation);
  }

  private static IEnumerable<string> NavigationViewIds(
    BookDefinition definition,
    IEnumerable<NavigationItem> navigation)
  {
    foreach (NavigationItem item in navigation)
    {
      if (item.Book.Length > 0)
      {
        BookDefinition? child = definition.Children.FirstOrDefault(candidate => candidate.Id == item.Book);
        if (child is not null)
        {
          foreach (string id in NavigationViewIds(child))
          {
            yield return id;
          }
        }
        continue;
      }

      yield return definition.ViewPrefix.Length == 0 ? item.View : $"{definition.ViewPrefix}/{item.View}";
      foreach (string id in NavigationViewIds(definition, item.Children))
      {
        yield return id;
      }
    }
  }

  private static void AddPresentation<TKey>(Dictionary<TKey, List<string>> index, TKey key, string view)
    where TKey : notnull
  {
    if (!index.TryGetValue(key, out List<string>? views))
    {
      views = new List<string>();
      index[key] = views;
    }
    if (!views.Contains(view))
    {
      views.Add(view);
    }
  }

  private void LoadFactTable(
    string csvPath,
    string metadataPath,
    FactTableFile metadata,
    string scope,
    BookFile bookFile)
  {
    string[] lines = File.ReadAllLines(csvPath);
    if (lines.Length == 0)
    {
      throw new InvalidDataException($"'{csvPath}' is empty");
    }
    string[] header = lines[0].Split(',');
    if (header.Length < 2 || header[0].Trim() != "line_item")
    {
      throw new InvalidDataException($"'{csvPath}:1' must begin with line_item and at least one period");
    }
    List<string> periods = header.Skip(1).Select(column => column.Trim()).ToList();
    string? emptyPeriod = periods.FirstOrDefault(period => period.Length == 0);
    if (emptyPeriod is not null)
    {
      throw new InvalidDataException($"'{csvPath}:1' contains an empty period");
    }
    string? duplicatePeriod = periods.GroupBy(period => period).FirstOrDefault(group => group.Count() > 1)?.Key;
    if (duplicatePeriod is not null)
    {
      throw new InvalidDataException($"'{csvPath}:1' contains duplicate period '{duplicatePeriod}'");
    }
    ValidateConvention(metadataPath, "defaults.printed", metadata.Defaults.Printed, "as_is", "negated");
    ValidateConvention(metadataPath, "defaults.sign", metadata.Defaults.Sign, "additive", "contra");
    ValidateConvention(metadataPath, "defaults.missing", metadata.Defaults.Missing, "blank", "zero");
    foreach ((string lineName, LineItem lineItem) in metadata.LineItems)
    {
      if (lineItem.Printed is not null)
      {
        ValidateConvention(metadataPath, $"line_items.{lineName}.printed", lineItem.Printed, "as_is", "negated");
      }
      if (lineItem.Sign is not null)
      {
        ValidateConvention(metadataPath, $"line_items.{lineName}.sign", lineItem.Sign, "additive", "contra");
      }
      if (lineItem.Missing is not null)
      {
        ValidateConvention(metadataPath, $"line_items.{lineName}.missing", lineItem.Missing, "blank", "zero");
      }
    }
    AddPeriods(scope, periods);
    string sourceImage = ResolveSourceImage(metadataPath, metadata.Source.Image);
    var loadedLines = new HashSet<string>();

    for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
    {
      string line = lines[lineIndex];
      if (line.Trim().Length == 0)
      {
        continue;
      }
      string[] cells = line.Split(',');
      if (cells.Length != header.Length)
      {
        throw new InvalidDataException(
          $"'{csvPath}:{lineIndex + 1}' has {header.Length} cells in its header but found {cells.Length}");
      }
      string localName = cells[0].Trim();
      if (localName.Length == 0)
      {
        throw new InvalidDataException($"'{csvPath}:{lineIndex + 1}' has an empty line item");
      }
      if (!loadedLines.Add(localName))
      {
        throw new InvalidDataException($"'{csvPath}:{lineIndex + 1}' contains duplicate line '{localName}'");
      }
      metadata.LineItems.TryGetValue(localName, out LineItem? lineItem);
      string printed = lineItem?.Printed ?? metadata.Defaults.Printed;
      string sign = lineItem?.Sign ?? metadata.Defaults.Sign;
      bool negated = printed == "negated";
      string name = Qualify(scope, localName);
      Dictionary<string, SourceRegion> sourceRegions = lineItem?.ImageRegions ?? new Dictionary<string, SourceRegion>();
      foreach ((string period, SourceRegion region) in sourceRegions)
      {
        string path = $"line_items.{localName}.image_regions.{period}";
        if (!periods.Contains(period))
        {
          throw new InvalidDataException($"'{metadataPath}' {path} names unknown period '{period}'");
        }
        if (sourceImage.Length == 0)
        {
          throw new InvalidDataException($"'{metadataPath}' {path} requires source.image");
        }
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0)
        {
          throw new InvalidDataException(
            $"'{metadataPath}' {path} must have non-negative x and y and positive width and height");
        }
      }
      Dictionary<string, string> cellNotes = lineItem?.CellNotes ?? new Dictionary<string, string>();
      foreach (string period in cellNotes.Keys)
      {
        if (!periods.Contains(period))
        {
          throw new InvalidDataException(
            $"'{metadataPath}' line_items.{localName}.cell_notes names unknown period '{period}'");
        }
      }
      var fact = new Fact
      {
        Name = name,
        Scope = scope,
        Label = lineItem?.Label ?? "",
        Units = ValueOrDefault(lineItem?.Units, metadata.Defaults.Units, bookFile.Units),
        Currency = ValueOrDefault(metadata.Defaults.Currency, bookFile.Currency),
        FiscalYearEnd = ValueOrDefault(metadata.Table.FiscalYearEnd, bookFile.FiscalYearEnd),
        Contra = sign == "contra",
        MissingZero = (lineItem?.Missing ?? metadata.Defaults.Missing) == "zero",
        Notes = string.IsNullOrWhiteSpace(lineItem?.Note)
          ? new List<string>()
          : new List<string> { lineItem.Note.Trim() }
      };

      bool numbers = false;
      for (int index = 0; index < periods.Count; index++)
      {
        string cell = cells[index + 1].Trim();
        if (cell.Length == 0)
        {
          continue;
        }
        if (cell is "—" or "–" or "-")
        {
          numbers = true;
          fact.Cells[periods[index]] = Cell(periods[index], 0, true);
          continue;
        }
        if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
          double.IsFinite(value))
        {
          numbers = true;
          fact.Cells[periods[index]] = Cell(periods[index], negated ? -value : value, false);
          continue;
        }
        if (!Formatter.TryParseDate(cell, out double serial))
        {
          throw new InvalidDataException(
            $"'{csvPath}:{lineIndex + 1}' has invalid value '{cell}' for {localName}[{periods[index]}]");
        }
        fact.Date = true;
        fact.Cells[periods[index]] = Cell(periods[index], serial, false);
      }
      if (numbers && fact.Date)
      {
        throw new InvalidDataException($"'{csvPath}:{lineIndex + 1}' mixes dates and numbers in {localName}");
      }
      MergeFact(csvPath, fact);

      FactCell Cell(string period, double value, bool printedDash)
      {
        return new FactCell
        {
          Value = value,
          PrintedDash = printedDash,
          Note = cellNotes.GetValueOrDefault(period)?.Trim(),
          LineNote = fact.Notes.FirstOrDefault(),
          TableTitle = metadata.Table.Title,
          Source = metadata.Source,
          SourceImage = sourceImage,
          SourceRegion = sourceRegions.GetValueOrDefault(period),
          SourcePath = csvPath
        };
      }
    }
  }

  private void MergeFact(string csvPath, Fact incoming)
  {
    if (!Facts.TryGetValue(incoming.Name, out Fact? existing))
    {
      Facts.Add(incoming.Name, incoming);
      return;
    }

    RequireMatchingFactMetadata(existing, incoming, csvPath, nameof(Fact.Units), existing.Units, incoming.Units);
    RequireMatchingFactMetadata(existing, incoming, csvPath, nameof(Fact.Currency), existing.Currency, incoming.Currency);
    RequireMatchingFactMetadata(
      existing, incoming, csvPath, nameof(Fact.FiscalYearEnd), existing.FiscalYearEnd, incoming.FiscalYearEnd);
    RequireMatchingFactMetadata(existing, incoming, csvPath, nameof(Fact.Contra), existing.Contra, incoming.Contra);
    RequireMatchingFactMetadata(existing, incoming, csvPath, nameof(Fact.Date), existing.Date, incoming.Date);
    RequireMatchingFactMetadata(
      existing, incoming, csvPath, nameof(Fact.MissingZero), existing.MissingZero, incoming.MissingZero);

    foreach ((string period, FactCell cell) in incoming.Cells)
    {
      if (existing.Cells.TryGetValue(period, out FactCell? previous))
      {
        throw new InvalidDataException(
          $"Duplicate fact '{incoming.Name}[{period}]' in '{previous.SourcePath}' and '{csvPath}'");
      }
      existing.Cells.Add(period, cell);
    }
    existing.Notes.AddRange(incoming.Notes.Where(note => !existing.Notes.Contains(note)));
  }

  private static void RequireMatchingFactMetadata<T>(
    Fact existing,
    Fact incoming,
    string csvPath,
    string property,
    T existingValue,
    T incomingValue)
  {
    if (!EqualityComparer<T>.Default.Equals(existingValue, incomingValue))
    {
      string existingPath = existing.Cells.Values.FirstOrDefault()?.SourcePath ?? existing.Name;
      throw new InvalidDataException(
        $"Fact line '{incoming.Name}' has conflicting {property} in '{existingPath}' and '{csvPath}'");
    }
  }

  private static string? ValueOrDefault(params string?[] values)
  {
    return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
  }

  private string ResolveSourceImage(string metadataPath, string image)
  {
    if (image.Length == 0)
    {
      return "";
    }

    string path = Path.GetFullPath(image, Path.GetDirectoryName(metadataPath)!);
    string relative = Path.GetRelativePath(Root, path);
    if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
      Path.IsPathRooted(relative))
    {
      throw new InvalidDataException($"'{metadataPath}' source.image must stay inside the book");
    }
    string extension = Path.GetExtension(path).ToLowerInvariant();
    if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
    {
      throw new InvalidDataException(
        $"'{metadataPath}' source.image must be a PNG, JPEG, or WebP image");
    }
    if (!File.Exists(path))
    {
      throw new InvalidDataException($"'{metadataPath}' source.image does not exist: {image}");
    }
    return relative.Replace(Path.DirectorySeparatorChar, '/');
  }

  private static void ValidateConvention(string path, string property, string value, params string[] allowed)
  {
    if (!allowed.Contains(value, StringComparer.Ordinal))
    {
      throw new InvalidDataException(
        $"'{path}' has invalid {property} value '{value}'; expected {string.Join(" or ", allowed)}");
    }
  }

  private bool Contains(string name)
  {
    return Facts.ContainsKey(name) || Formulas.ContainsKey(name);
  }

  private static string Qualify(string scope, string name)
  {
    string normalized = BookStructure.Normalize(name);
    return scope.Length == 0 ? normalized : $"{scope}.{normalized}";
  }

  private static readonly HashSet<string> Aggregates =
    new HashSet<string> { "sum", "min", "max", "average", "median" };

  /// <summary>Expands books::x inside an aggregate into one reference per included book, in text and tree.</summary>
  private static (Expr Expression, string Text) Expand(string formula, BookDefinition definition)
  {
    string text = formula.Trim();
    Expr expression = ExpressionParser.Parse(text);
    var wildcards = new List<(string Name, int Start, int Length)>();
    Collect(expression, wildcards);
    if (wildcards.Count == 0)
    {
      return (expression, text);
    }
    if (definition.Children.Count == 0)
    {
      throw new InvalidDataException($"'{wildcards[0].Name}' names every included book of a book that includes none");
    }
    int prefix = BookStructure.InclusionWildcard.Length + 2;
    var expanded = new StringBuilder();
    int end = 0;
    foreach ((string _, int start, int length) in wildcards.OrderBy(wildcard => wildcard.Start))
    {
      expanded.Append(text, end, start - end);
      string written = text.Substring(start + prefix, length - prefix);
      expanded.AppendJoin(", ", definition.Children.Select(child => $"{child.Id}::{written}"));
      end = start + length;
    }
    expanded.Append(text, end, text.Length - end);
    string expandedText = expanded.ToString();
    return (ExpressionParser.Parse(expandedText), expandedText);
  }

  private static void Collect(Expr expression, List<(string Name, int Start, int Length)> wildcards)
  {
    if (Wildcard(expression) is (string name, _, _))
    {
      throw new InvalidDataException(
        $"'{name}' names every included book, which only sum, min, max, average, or median can take as an argument");
    }
    foreach (Expr child in Children(expression))
    {
      if (expression is FunctionExpr function && Aggregates.Contains(function.Name) &&
        Wildcard(child) is (string, int, int) wildcard)
      {
        wildcards.Add(wildcard);
        continue;
      }
      Collect(child, wildcards);
    }
  }

  private static (string Name, int Start, int Length)? Wildcard(Expr expression)
  {
    (string Name, int Start, int Length)? reference = expression switch
    {
      RefExpr single => (single.Name, single.Start, single.Length),
      RangeExpr range => (range.Name, range.Start, range.Length),
      _ => null
    };
    return reference is not null &&
      BookStructure.Normalize(reference.Value.Name)
        .StartsWith($"{BookStructure.InclusionWildcard}::", StringComparison.Ordinal)
        ? reference
        : null;
  }

  private static IEnumerable<Expr> Children(Expr expression)
  {
    return expression switch
    {
      FunctionExpr function => function.Arguments,
      PriorExpr prior => new[] { prior.Inner },
      UnaryExpr unary => new[] { unary.Operand },
      BinaryExpr binary => new[] { binary.Left, binary.Right },
      ComparisonExpr comparison => new[] { comparison.Left, comparison.Right },
      _ => Array.Empty<Expr>()
    };
  }

  private BookDefinition DefinitionForScope(string scope)
  {
    return Structure.Definitions
      .Where(definition => definition.Namespace.Length == 0 || scope == definition.Namespace ||
        scope.StartsWith($"{definition.Namespace}::", StringComparison.Ordinal))
      .OrderByDescending(definition => definition.Namespace.Length)
      .First();
  }

  private static string LocalScope(BookDefinition definition, string scope)
  {
    if (definition.Namespace.Length == 0)
    {
      return scope;
    }
    return scope == definition.Namespace ? "" : scope[(definition.Namespace.Length + 2)..];
  }

  private static bool IsWithin(string root, string path)
  {
    string relative = Path.GetRelativePath(root, path);
    return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
      !Path.IsPathRooted(relative);
  }

  private static void Add<T>(Dictionary<string, T> values, string name, T value)
  {
    if (!values.TryAdd(name, value))
    {
      throw new InvalidOperationException($"Duplicate line '{name}'");
    }
  }


}

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
      Formatter = new Formatter(Formatter.Load(folder, book.Root, YamlFile.Deserializer()))
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
      Formatter = new Formatter(Formatter.Load(folder, book.Root, YamlFile.Deserializer()))
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
    IDeserializer deserializer = YamlFile.Deserializer();
    string scope = book.Scope(folder);
    return new View
    {
      Book = book,
      Scope = scope,
      Checks = book.Checks.Where(check => check.Scope == scope).ToList(),
      Formatter = new Formatter(Formatter.Load(folder, book.Root, deserializer))
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