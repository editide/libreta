namespace Libreta;

public record ResolvedCell(
  string Line,
  string Column,
  double? Value,
  string Kind,
  string? Units,
  Expr? Expression = null,
  string? Formula = null,
  string? Scope = null,
  FactCell? FactCell = null,
  string? Note = null,
  bool Omitted = false,
  bool? Date = null);

public readonly record struct ActiveDependency(string Reference, string? Line, string Column, string Scope);

/// <summary>One written reference and the cell it reads; two mentions of a line at two columns stay apart.</summary>
public readonly record struct FormulaReference(int Start, int Length, string Line, string? Column);

/// <summary>Resolves every line and column coordinate, preserving how each cell was produced.</summary>
public class Engine
{
  private readonly View _view;
  private readonly Dictionary<(string Line, string Column), ResolvedCell> memo = new();
  private readonly HashSet<(string Line, string Column)> resolving = new();
  private readonly HashSet<(string Line, string Column)> validated = new();
  private readonly IReadOnlyDictionary<(string Line, string Column), double> substitutions;

  public Engine(View view, IReadOnlyDictionary<(string Line, string Column), double>? substitutions = null)
  {
    _view = view;
    this.substitutions = substitutions ?? new Dictionary<(string Line, string Column), double>();
  }

  public ResolvedCell Cell(string name, string column)
  {
    string line = _view.Book.Resolve(name, _view.Scope) ?? name;
    return CellAt(line, _view.Book.CoordinateColumn(column, _view.Scope, line));
  }

  /// <summary>Resolves a canonical coordinate as given, without re-reading it through this view's scope.</summary>
  public ResolvedCell Cell(CellCoordinate coordinate)
  {
    return CellAt(coordinate.Line, coordinate.Column);
  }

  private ResolvedCell CellAt(string line, string column)
  {
    (string, string) key = (line, column);
    if (memo.TryGetValue(key, out ResolvedCell? cached))
    {
      return cached;
    }

    if (!resolving.Add(key))
    {
      throw new InvalidOperationException($"Circular reference resolving '{line}[{column}]'");
    }

    try
    {
      ResolvedCell result = Resolve(line, column);
      RequireDateUnits(result);
      memo[key] = result;
      return result;
    }
    finally
    {
      resolving.Remove(key);
    }
  }

  public double? Value(string name, string column)
  {
    return Cell(name, column).Value;
  }

  public double? Evaluate(Expr expr, string column, string scope, string? currentLine)
  {
    switch (expr)
    {
      case NumberExpr number:
        return number.Value;

      case RefExpr reference:
        string? name = _view.Book.ResolveSelf(reference.Name, scope, currentLine);
        if (name is null)
        {
          return null;
        }
        return CellAt(name, ReferenceColumn(reference, column, scope, name)).Value;

      case RangeExpr:
        throw new InvalidOperationException("A range can only be used as a function argument");

      case PriorExpr prior:
        string? priorPeriod = _view.Book.PriorPeriod(scope, column);
        return priorPeriod is null ? null : Evaluate(prior.Inner, priorPeriod, scope, currentLine);

      case UnaryExpr unary:
        double? operand = Evaluate(unary.Operand, column, scope, currentLine);
        return operand is null ? null : Finite(-operand.Value);

      case BinaryExpr binary:
        double? left = Evaluate(binary.Left, column, scope, currentLine);
        double? right = Evaluate(binary.Right, column, scope, currentLine);
        if (left is null || right is null)
        {
          return null;
        }
        double? result = binary.Op switch
        {
          '+' => left + right,
          '-' => left - right,
          '*' => left * right,
          '/' => right == 0 ? null : left / right,
          '^' => Math.Pow(left.Value, right.Value),
          _ => throw new InvalidOperationException($"Unknown operator '{binary.Op}'")
        };
        return result is null ? null : Finite(result.Value);

      case ComparisonExpr comparison:
        double? comparisonLeft = Evaluate(comparison.Left, column, scope, currentLine);
        double? comparisonRight = Evaluate(comparison.Right, column, scope, currentLine);
        if (comparisonLeft is null || comparisonRight is null)
        {
          return null;
        }
        return comparison.Op switch
        {
          "<" => comparisonLeft < comparisonRight ? 1 : 0,
          "<=" => comparisonLeft <= comparisonRight ? 1 : 0,
          ">" => comparisonLeft > comparisonRight ? 1 : 0,
          ">=" => comparisonLeft >= comparisonRight ? 1 : 0,
          "==" => comparisonLeft == comparisonRight ? 1 : 0,
          "!=" => comparisonLeft != comparisonRight ? 1 : 0,
          _ => throw new InvalidOperationException($"Unknown comparison '{comparison.Op}'")
        };

      case FunctionExpr function:
        return EvaluateFunction(function, column, scope, currentLine);

      default:
        throw new InvalidOperationException($"Unknown expression {expr.GetType().Name}");
    }
  }

  /// <summary>Every written reference in order, in both branches of a condition: the page links all it shows.</summary>
  public List<FormulaReference> References(Expr expression, string? column, string scope, string? currentLine)
  {
    var references = new List<FormulaReference>();
    AppendReferences(expression, column, scope, currentLine, references);
    return references;
  }

  public List<ActiveDependency> ActiveDependencies(
    Expr expression,
    string column,
    string scope,
    string? currentLine)
  {
    var dependencies = new List<ActiveDependency>();
    AppendActiveDependencies(expression, column, scope, currentLine, dependencies);
    return dependencies;
  }

  /// <summary>Every cell the roots reach, keyed to its direct readers, even intermediates no table shows.</summary>
  public Dictionary<CellCoordinate, List<CellCoordinate>> Dependents(IEnumerable<CellCoordinate> roots)
  {
    var dependents = new Dictionary<CellCoordinate, List<CellCoordinate>>();
    var visited = new HashSet<CellCoordinate>();
    var pending = new Stack<CellCoordinate>(roots.Reverse());
    while (pending.TryPop(out CellCoordinate coordinate))
    {
      if (!visited.Add(coordinate))
      {
        continue;
      }
      List<ActiveDependency> dependencies;
      try
      {
        ResolvedCell cell = CellAt(coordinate.Line, coordinate.Column);
        if (cell.Expression is null)
        {
          continue;
        }
        dependencies = ActiveDependencies(
          cell.Expression, coordinate.Column, cell.Scope ?? _view.Scope, coordinate.Line);
      }
      catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
      {
        continue;
      }
      foreach (ActiveDependency dependency in dependencies)
      {
        if (dependency.Line is null)
        {
          continue;
        }
        var input = new CellCoordinate(dependency.Line, dependency.Column);
        if (!dependents.TryGetValue(input, out List<CellCoordinate>? readers))
        {
          readers = new List<CellCoordinate>();
          dependents[input] = readers;
        }
        if (!readers.Contains(coordinate))
        {
          readers.Add(coordinate);
        }
        pending.Push(input);
      }
    }
    return dependents;
  }

  public void ValidateCell(CellCoordinate coordinate)
  {
    try
    {
      ValidateCell(coordinate.Line, coordinate.Column, new HashSet<(string, string)>());
    }
    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
    {
      throw new InvalidDataException(
        $"Invalid formula at '{coordinate.Line}[{coordinate.Column}]': {exception.Message}", exception);
    }
    ResolvedCell cell = CellAt(coordinate.Line, coordinate.Column);
    if (!_view.Formatter.Defines(cell.Units))
    {
      throw new InvalidDataException($"'{coordinate.Line}[{coordinate.Column}]' has units " +
        $"'{cell.Units ?? _view.Formatter.DefaultUnits}' with no format. Add it to formats.yaml.");
    }
  }

  public string? Units(string name)
  {
    if (_view.Formulas.TryGetValue(name, out Formula? formula) && formula.Units is not null)
    {
      return formula.Units;
    }
    return _view.Facts.TryGetValue(name, out Fact? fact) ? fact.Units : null;
  }

  public List<CheckResult> RunChecks(IEnumerable<Check>? requestedChecks = null)
  {
    var results = new List<CheckResult>();
    foreach (Check check in requestedChecks ?? _view.Checks)
    {
      foreach (string period in _view.Book.CheckPeriods(check.Scope))
      {
        double? actual = Evaluate(check.Expression, period, check.Scope, null);
        if (actual is null)
        {
          if (Availability(check.Expression, period, check.Scope, null, new HashSet<(string, string)>())
            == EvaluationAvailability.Absent ||
            ReadsOnlyEmptyFacts(check.Expression, period, check.Scope, null, new HashSet<(string, string)>()))
          {
            continue;
          }
          results.Add(new CheckResult(check.Name, period, null, false));
          continue;
        }
        DateKind(check.Expression, period, check.Scope, null, $"check '{check.Name}' in {period}");
        double delta = actual.Value - check.Expect;
        results.Add(new CheckResult(check.Name, period, delta, Math.Abs(delta) <= check.Tolerance));
      }
    }
    return results;
  }

  // A period the source never covered, such as an annual column for a balance-sheet tie, is not a failed extraction.
  // Any unknown reference or entered input keeps the period, so a partially extracted period still fails.
  private bool ReadsOnlyEmptyFacts(
    Expr expression,
    string column,
    string scope,
    string? currentLine,
    HashSet<(string Line, string Column)> path)
  {
    bool readsFact = false;
    foreach (ActiveDependency dependency in ActiveDependencies(expression, column, scope, currentLine))
    {
      if (dependency.Line is null || !path.Add((dependency.Line, dependency.Column)))
      {
        return false;
      }
      try
      {
        ResolvedCell cell = CellAt(dependency.Line, dependency.Column);
        if (cell.Value is not null)
        {
          return false;
        }
        if (cell.Expression is not null)
        {
          if (!ReadsOnlyEmptyFacts(cell.Expression, dependency.Column, cell.Scope ?? scope, dependency.Line, path))
          {
            return false;
          }
          readsFact = true;
        }
        else if (_view.Facts.ContainsKey(dependency.Line))
        {
          readsFact = true;
        }
        else
        {
          return false;
        }
      }
      catch (InvalidOperationException)
      {
        return false;
      }
      finally
      {
        path.Remove((dependency.Line, dependency.Column));
      }
    }
    return readsFact;
  }

  private ResolvedCell Resolve(string line, string column)
  {
    if (substitutions.TryGetValue((line, column), out double substituted))
    {
      string? units = _view.Facts.TryGetValue(line, out Fact? substitutedFact) &&
        substitutedFact.Cells.ContainsKey(column)
          ? substitutedFact.Units
          : _view.Book.CellOverrides.TryGetValue((line, column), out CellOverride? substitutedOverride)
            ? substitutedOverride.Units ?? DefaultUnits(line, column)
            : DefaultUnits(line, column);
      return new ResolvedCell(line, column, substituted, "trial", units);
    }
    if (_view.Facts.TryGetValue(line, out Fact? fact) && fact.Cells.TryGetValue(column, out FactCell? factCell))
    {
      return new ResolvedCell(line, column, factCell.Value, "fact", fact.Units, FactCell: factCell,
        Note: factCell.Note, Date: fact.Date);
    }

    if (_view.Book.CellOverrides.TryGetValue((line, column), out CellOverride? cellOverride))
    {
      if (cellOverride.Blank)
      {
        return new ResolvedCell(line, column, null, "blank", cellOverride.Units ?? DefaultUnits(line, column),
          Note: cellOverride.Note);
      }
      if (cellOverride.Value is not null)
      {
        return new ResolvedCell(line, column, cellOverride.Value, "fact",
          cellOverride.Units ?? DefaultUnits(line, column), Note: cellOverride.Note, Date: false);
      }
      if (cellOverride.Expression is not null)
      {
        double? result = Evaluate(cellOverride.Expression, column, cellOverride.Scope, line);
        return new ResolvedCell(line, column, result, "formula", cellOverride.Units ?? DefaultUnits(line, column),
          cellOverride.Expression, cellOverride.Text, cellOverride.Scope, Note: cellOverride.Note,
          Date: result is null ? null : DateKind(cellOverride.Expression, column, cellOverride.Scope, line, $"'{line}[{column}]'"));
      }
      ResolvedCell inherited = ResolveDefault(line, column);
      return inherited with
      {
        Units = cellOverride.Units ?? inherited.Units,
        Note = cellOverride.Note ?? inherited.Note
      };
    }

    return ResolveDefault(line, column);
  }

  private void AppendReferences(
    Expr expression,
    string? column,
    string scope,
    string? currentLine,
    List<FormulaReference> references)
  {
    switch (expression)
    {
      case RefExpr reference:
        string? line = _view.Book.ResolveSelf(reference.Name, scope, currentLine);
        if (line is null)
        {
          return;
        }
        references.Add(new FormulaReference(reference.Start, reference.Length, line,
          _view.Book.ReferenceColumn(reference.Column, column, scope, line)));
        return;

      case RangeExpr range:
        // A range is written once and read many times, so it names its line and leaves the column open.
        string? rangeLine = _view.Book.ResolveSelf(range.Name, scope, currentLine);
        if (rangeLine is not null)
        {
          references.Add(new FormulaReference(range.Start, range.Length, rangeLine, null));
        }
        return;

      case PriorExpr prior:
        AppendReferences(prior.Inner, column is null ? null : _view.Book.PriorPeriod(scope, column),
          scope, currentLine, references);
        return;

      case UnaryExpr unary:
        AppendReferences(unary.Operand, column, scope, currentLine, references);
        return;

      case BinaryExpr binary:
        AppendReferences(binary.Left, column, scope, currentLine, references);
        AppendReferences(binary.Right, column, scope, currentLine, references);
        return;

      case ComparisonExpr comparison:
        AppendReferences(comparison.Left, column, scope, currentLine, references);
        AppendReferences(comparison.Right, column, scope, currentLine, references);
        return;

      case FunctionExpr function:
        foreach (Expr argument in function.Arguments)
        {
          AppendReferences(argument, column, scope, currentLine, references);
        }
        return;
    }
  }

  private void AppendActiveDependencies(
    Expr expression,
    string column,
    string scope,
    string? currentLine,
    List<ActiveDependency> dependencies)
  {
    switch (expression)
    {
      case NumberExpr:
        return;

      case RefExpr reference:
        string? referenceLine = _view.Book.ResolveSelf(reference.Name, scope, currentLine);
        dependencies.Add(new ActiveDependency(
          reference.Name,
          referenceLine,
          referenceLine is null ? reference.Column ?? column : ReferenceColumn(reference, column, scope, referenceLine),
          scope));
        return;

      case RangeExpr range:
        string? rangeLine = _view.Book.ResolveSelf(range.Name, scope, currentLine);
        if (rangeLine is null)
        {
          dependencies.Add(new ActiveDependency(range.Name, null, column, scope));
          return;
        }
        foreach (string rangeColumn in _view.Book.ColumnsInRange(rangeLine, range.StartColumn, range.EndColumn))
        {
          dependencies.Add(new ActiveDependency(range.Name, rangeLine, rangeColumn, scope));
        }
        return;

      case PriorExpr prior:
        string? priorColumn = _view.Book.PriorPeriod(scope, column);
        if (priorColumn is not null)
        {
          AppendActiveDependencies(prior.Inner, priorColumn, scope, currentLine, dependencies);
        }
        return;

      case UnaryExpr unary:
        AppendActiveDependencies(unary.Operand, column, scope, currentLine, dependencies);
        return;

      case BinaryExpr binary:
        AppendActiveDependencies(binary.Left, column, scope, currentLine, dependencies);
        AppendActiveDependencies(binary.Right, column, scope, currentLine, dependencies);
        return;

      case ComparisonExpr comparison:
        AppendActiveDependencies(comparison.Left, column, scope, currentLine, dependencies);
        AppendActiveDependencies(comparison.Right, column, scope, currentLine, dependencies);
        return;

      case FunctionExpr { Name: "if" } conditional:
        Expr condition = conditional.Arguments[0];
        AppendActiveDependencies(condition, column, scope, currentLine, dependencies);
        double? selected = Evaluate(condition, column, scope, currentLine);
        if (selected is not null)
        {
          AppendActiveDependencies(
            conditional.Arguments[selected == 0 ? 2 : 1], column, scope, currentLine, dependencies);
        }
        return;

      case FunctionExpr function:
        foreach (Expr argument in function.Arguments)
        {
          AppendActiveDependencies(argument, column, scope, currentLine, dependencies);
        }
        return;
    }
  }

  private void ValidateCell(string line, string column, HashSet<(string Line, string Column)> path)
  {
    (string, string) coordinate = (line, column);
    if (validated.Contains(coordinate))
    {
      return;
    }
    if (!path.Add(coordinate))
    {
      throw new InvalidOperationException($"Circular reference resolving '{line}[{column}]'");
    }

    try
    {
      ResolvedCell cell = CellAt(line, column);
      if (cell.Expression is null)
      {
        return;
      }
      foreach (ActiveDependency dependency in ActiveDependencies(
        cell.Expression, column, cell.Scope ?? _view.Scope, line))
      {
        if (dependency.Line is null)
        {
          if (!BookStructure.IsSelf(dependency.Reference))
          {
            throw new InvalidDataException(
              $"Unknown reference '{dependency.Reference}' resolving '{line}[{column}]'");
          }
          continue;
        }
        ValidateCell(dependency.Line, dependency.Column, path);
      }
      validated.Add(coordinate);
    }
    finally
    {
      path.Remove(coordinate);
    }
  }

  private string ReferenceColumn(RefExpr reference, string column, string scope, string line)
  {
    return _view.Book.ReferenceColumn(reference.Column, column, scope, line);
  }

  private ResolvedCell ResolveDefault(string line, string column)
  {
    if (!_view.Facts.ContainsKey(line) && !_view.Formulas.ContainsKey(line))
    {
      return new ResolvedCell(line, column, null, "missing", Units(line));
    }

    if (_view.Facts.TryGetValue(line, out Fact? fact) && fact.MissingZero &&
      !_view.ColumnFormulas.ContainsKey(column))
    {
      return new ResolvedCell(line, column, 0, "fact", fact.Units, Omitted: true, Date: fact.Date);
    }

    if (ColumnFormulaFor(line, column) is ColumnFormula columnFormula)
    {
      double? result = Evaluate(columnFormula.Expression, column, columnFormula.Scope, line);
      return new ResolvedCell(line, column, result, "formula", columnFormula.Units ?? Units(line),
        columnFormula.Expression, columnFormula.Text, columnFormula.Scope,
        Date: result is null ? null : DateKind(columnFormula.Expression, column, columnFormula.Scope, line, $"'{line}[{column}]'"));
    }

    if (_view.Formulas.TryGetValue(line, out Formula? formula))
    {
      double? result = Evaluate(formula.Expression, column, formula.Scope, line);
      return new ResolvedCell(line, column, result, "formula", formula.Units,
        formula.Expression, formula.Text, formula.Scope, Note: formula.CellNotes.GetValueOrDefault(column),
        Date: result is null ? null : DateKind(formula.Expression, column, formula.Scope, line, $"'{line}[{column}]'"));
    }

    return new ResolvedCell(line, column, null, "missing", Units(line), Date: fact?.Date);
  }

  private string? DefaultUnits(string line, string column)
  {
    return ColumnFormulaFor(line, column) is ColumnFormula columnFormula
      ? columnFormula.Units ?? Units(line)
      : Units(line);
  }

  private ColumnFormula? ColumnFormulaFor(string line, string column)
  {
    return _view.ColumnFormulas.TryGetValue(column, out ColumnFormula? columnFormula) &&
      (!columnFormula.FactsOnly || _view.Facts.ContainsKey(line))
        ? columnFormula
        : null;
  }

  private static double? Finite(double value)
  {
    return double.IsFinite(value) ? value : null;
  }

  private EvaluationAvailability Availability(
    Expr expression,
    string column,
    string scope,
    string? currentLine,
    HashSet<(string Line, string Column)> path)
  {
    switch (expression)
    {
      case PriorExpr prior:
        string? priorPeriod = _view.Book.PriorPeriod(scope, column);
        return priorPeriod is null
          ? EvaluationAvailability.Absent
          : Availability(prior.Inner, priorPeriod, scope, currentLine, path);

      case RefExpr reference:
        string? name = _view.Book.ResolveSelf(reference.Name, scope, currentLine);
        if (name is null)
        {
          return EvaluationAvailability.Unresolved;
        }
        string referenceColumn = ReferenceColumn(reference, column, scope, name);
        (string, string) key = (name, referenceColumn);
        if (!path.Add(key))
        {
          return EvaluationAvailability.Unresolved;
        }
        try
        {
          ResolvedCell cell = CellAt(name, referenceColumn);
          if (cell.Value is not null)
          {
            return EvaluationAvailability.Available;
          }
          return cell.Expression is null
            ? EvaluationAvailability.Unresolved
            : Availability(cell.Expression, referenceColumn, cell.Scope ?? scope, name, path);
        }
        catch (InvalidOperationException)
        {
          return EvaluationAvailability.Unresolved;
        }
        finally
        {
          path.Remove(key);
        }

      case UnaryExpr unary:
        return Availability(unary.Operand, column, scope, currentLine, path);

      case NumberExpr:
        return EvaluationAvailability.Available;

      case BinaryExpr binary:
        EvaluationAvailability binaryInputs = Combine(
          Availability(binary.Left, column, scope, currentLine, path),
          Availability(binary.Right, column, scope, currentLine, path));
        return binaryInputs == EvaluationAvailability.Available
          ? EvaluationAvailability.Unresolved
          : binaryInputs;

      case ComparisonExpr comparison:
        EvaluationAvailability comparisonInputs = Combine(
          Availability(comparison.Left, column, scope, currentLine, path),
          Availability(comparison.Right, column, scope, currentLine, path));
        return comparisonInputs == EvaluationAvailability.Available
          ? EvaluationAvailability.Unresolved
          : comparisonInputs;

      case FunctionExpr { Name: "if" } function:
        double? condition = Evaluate(function.Arguments[0], column, scope, currentLine);
        if (condition is null)
        {
          return Availability(function.Arguments[0], column, scope, currentLine, path);
        }
        return Availability(function.Arguments[condition != 0 ? 1 : 2], column, scope, currentLine, path);

      case FunctionExpr function:
        EvaluationAvailability availability = EvaluationAvailability.Available;
        foreach (Expr argument in function.Arguments)
        {
          if (argument is RangeExpr range)
          {
            string? rangeName = _view.Book.ResolveSelf(range.Name, scope, currentLine);
            if (rangeName is null)
            {
              return EvaluationAvailability.Unresolved;
            }
            foreach (string rangeColumn in _view.Book.ColumnsInRange(rangeName, range.StartColumn, range.EndColumn))
            {
              availability = Combine(availability,
                Availability(new RefExpr { Name = rangeName, Column = rangeColumn },
                  column, scope, currentLine, path));
            }
          }
          else
          {
            availability = Combine(availability, Availability(argument, column, scope, currentLine, path));
          }
        }
        return availability == EvaluationAvailability.Available
          ? EvaluationAvailability.Unresolved
          : availability;

      default:
        return EvaluationAvailability.Unresolved;
    }
  }

  private static EvaluationAvailability Combine(
    EvaluationAvailability left,
    EvaluationAvailability right)
  {
    if (left == EvaluationAvailability.Unresolved || right == EvaluationAvailability.Unresolved)
    {
      return EvaluationAvailability.Unresolved;
    }
    return left == EvaluationAvailability.Absent || right == EvaluationAvailability.Absent
      ? EvaluationAvailability.Absent
      : EvaluationAvailability.Available;
  }

  private double? EvaluateFunction(FunctionExpr function, string column, string scope, string? currentLine)
  {
    if (function.Name == "if")
    {
      double? condition = Evaluate(function.Arguments[0], column, scope, currentLine);
      return condition is null
        ? null
        : Evaluate(function.Arguments[condition != 0 ? 1 : 2], column, scope, currentLine);
    }
    if (function.Name == "eomonth")
    {
      double? start = Evaluate(function.Arguments[0], column, scope, currentLine);
      double? months = Evaluate(function.Arguments[1], column, scope, currentLine);
      return start is null || months is null ? null : Formatter.EndOfMonth(start.Value, months.Value);
    }

    var values = new List<double>();
    foreach (Expr argument in function.Arguments)
    {
      if (argument is RangeExpr range)
      {
        string? name = _view.Book.ResolveSelf(range.Name, scope, currentLine);
        if (name is null)
        {
          return null;
        }
        foreach (string rangeColumn in _view.Book.ColumnsInRange(name, range.StartColumn, range.EndColumn))
        {
          double? value = CellAt(name, rangeColumn).Value;
          if (value is null)
          {
            return null;
          }
          values.Add(value.Value);
        }
        continue;
      }

      double? scalar = Evaluate(argument, column, scope, currentLine);
      if (scalar is null)
      {
        return null;
      }
      values.Add(scalar.Value);
    }

    double result = function.Name switch
    {
      "sum" => values.Sum(),
      "min" => values.Min(),
      "max" => values.Max(),
      "average" => values.Average(),
      "median" => Median(values),
      _ => throw new InvalidOperationException($"Unknown function '{function.Name}'")
    };
    return Finite(result);
  }

  private void RequireDateUnits(ResolvedCell cell)
  {
    if (cell.Value is null || cell.Date is not bool date || _view.Formatter.IsDate(cell.Units) is not bool formatted ||
      date == formatted)
    {
      return;
    }
    string units = cell.Units ?? _view.Formatter.DefaultUnits ?? "(none)";
    throw new InvalidDataException(date
      ? $"'{cell.Line}[{cell.Column}]' holds a date but its units '{units}' have no date format"
      : $"'{cell.Line}[{cell.Column}]' holds a number but its units '{units}' format a date");
  }

  /// <summary>Whether an expression that evaluated yields a date or a number, rejecting arithmetic that has no
  /// meaning for a day serial. It reads only what evaluation read, so it meets no cycle evaluation avoided.</summary>
  private bool? DateKind(Expr expr, string column, string scope, string? currentLine, string where)
  {
    switch (expr)
    {
      case NumberExpr:
        return false;

      case RefExpr reference:
        string? name = _view.Book.ResolveSelf(reference.Name, scope, currentLine);
        return name is null ? null : CellAt(name, ReferenceColumn(reference, column, scope, name)).Date;

      case PriorExpr prior:
        string? priorPeriod = _view.Book.PriorPeriod(scope, column);
        return priorPeriod is null ? null : DateKind(prior.Inner, priorPeriod, scope, currentLine, where);

      case UnaryExpr unary:
        bool? operand = DateKind(unary.Operand, column, scope, currentLine, where);
        return operand == true ? throw new InvalidDataException($"{where} negates a date") : operand;

      case BinaryExpr binary:
        bool? left = DateKind(binary.Left, column, scope, currentLine, where);
        bool? right = DateKind(binary.Right, column, scope, currentLine, where);
        if (left != true && right != true)
        {
          return left is null || right is null ? null : false;
        }
        return binary.Op switch
        {
          '+' when left == true && right == true => throw new InvalidDataException($"{where} adds two dates"),
          '+' => right is null || left is null ? null : true,
          '-' when left == true && right == true => false,
          '-' when right == true => throw new InvalidDataException($"{where} subtracts a date from a number"),
          '-' => right is null ? null : true,
          '*' => throw new InvalidDataException($"{where} multiplies a date"),
          '/' => throw new InvalidDataException($"{where} divides a date"),
          _ => throw new InvalidDataException($"{where} raises a date to a power")
        };

      case ComparisonExpr comparison:
        bool? comparedLeft = DateKind(comparison.Left, column, scope, currentLine, where);
        bool? comparedRight = DateKind(comparison.Right, column, scope, currentLine, where);
        if (comparedLeft is bool l && comparedRight is bool r && l != r)
        {
          throw new InvalidDataException($"{where} compares a date with a number");
        }
        return false;

      case FunctionExpr function:
        return FunctionDateKind(function, column, scope, currentLine, where);

      default:
        return null;
    }
  }

  private bool? FunctionDateKind(FunctionExpr function, string column, string scope, string? currentLine, string where)
  {
    if (function.Name == "if")
    {
      DateKind(function.Arguments[0], column, scope, currentLine, where);
      double? condition = Evaluate(function.Arguments[0], column, scope, currentLine);
      return condition is null
        ? null
        : DateKind(function.Arguments[condition != 0 ? 1 : 2], column, scope, currentLine, where);
    }
    if (function.Name == "eomonth")
    {
      bool? start = DateKind(function.Arguments[0], column, scope, currentLine, where);
      bool? months = DateKind(function.Arguments[1], column, scope, currentLine, where);
      if (start == false)
      {
        throw new InvalidDataException($"{where} takes the eomonth of a number");
      }
      if (months == true)
      {
        throw new InvalidDataException($"{where} moves eomonth by a date");
      }
      return start is null || months is null ? null : true;
    }

    var kinds = new List<bool?>();
    foreach (Expr argument in function.Arguments)
    {
      if (argument is RangeExpr range)
      {
        string? name = _view.Book.ResolveSelf(range.Name, scope, currentLine);
        if (name is not null)
        {
          kinds.AddRange(_view.Book.ColumnsInRange(name, range.StartColumn, range.EndColumn)
            .Select(rangeColumn => CellAt(name, rangeColumn).Date));
        }
        continue;
      }
      kinds.Add(DateKind(argument, column, scope, currentLine, where));
    }

    if (!kinds.Contains(true))
    {
      return kinds.Contains(null) ? null : false;
    }
    if (kinds.Contains(false))
    {
      throw new InvalidDataException($"{where} mixes dates and numbers in {function.Name}");
    }
    return function.Name is "min" or "max"
      ? kinds.Contains(null) ? null : true
      : throw new InvalidDataException($"{where} takes the {function.Name} of dates");
  }

  private static double Median(List<double> values)
  {
    values.Sort();
    int midpoint = values.Count / 2;
    return values.Count % 2 == 1
      ? values[midpoint]
      : (values[midpoint - 1] + values[midpoint]) / 2;
  }


}

internal enum EvaluationAvailability { Available, Absent, Unresolved }

public readonly record struct CheckResult(string Name, string Period, double? Delta, bool Passed);