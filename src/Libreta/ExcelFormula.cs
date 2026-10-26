using System.Globalization;

namespace Libreta;

/// <summary>Where the workbook shows a cell, zero-based; <c>Negated</c> when it shows the value negated, as a contra
/// line does.</summary>
public readonly record struct ExcelAddress(string Sheet, int Row, int Column, bool Negated)
{
  public string Cell => $"{ColumnName(Column)}{Row + 1}";

  public static string ColumnName(int index)
  {
    string name = "";
    for (int remaining = index + 1; remaining > 0; remaining = (remaining - 1) / 26)
    {
      name = (char)('A' + (remaining - 1) % 26) + name;
    }
    return name;
  }
}

/// <summary>A formula without its leading equals sign; <c>Linked</c> when it only reads a cell on another sheet,
/// negated or not, and does no arithmetic of its own.</summary>
public readonly record struct ExcelFormulaText(string Text, bool Linked);

/// <summary>A formula as Excel writes it, reading each cell where the workbook shows it. Cells show the page's sign, so
/// a contra cell reads negated; the negations are folded into the arithmetic rather than stacked.</summary>
public sealed class ExcelFormula
{
  private readonly Book book;
  private readonly IReadOnlyDictionary<CellCoordinate, List<ExcelAddress>> addresses;

  public ExcelFormula(Book book, IReadOnlyDictionary<CellCoordinate, List<ExcelAddress>> addresses)
  {
    this.book = book;
    this.addresses = addresses;
  }

  /// <summary>Every cell the formula names, in both branches of a condition, since Excel carries both.</summary>
  public static IEnumerable<CellCoordinate> Reads(Book book, Expr expression, string column, string scope, string line)
  {
    return expression switch
    {
      RefExpr reference => Coordinate(book, reference, column, scope, line) is CellCoordinate coordinate
        ? new[] { coordinate }
        : Array.Empty<CellCoordinate>(),
      RangeExpr range => Coordinates(book, range, scope, line) ?? new List<CellCoordinate>(),
      PriorExpr prior => book.PriorPeriod(scope, column) is string priorColumn
        ? Reads(book, prior.Inner, priorColumn, scope, line)
        : Array.Empty<CellCoordinate>(),
      _ => Children(expression).SelectMany(child => Reads(book, child, column, scope, line))
    };
  }

  /// <summary>The formula as written on <paramref name="sheet"/>.</summary>
  public ExcelFormulaText Write(Expr expression, string column, string scope, string line, string sheet, bool negate)
  {
    Node node = Translate(expression, column, scope, line, sheet, false);
    Node formula = negate ? Negate(node) : node;
    // A contra cell's minus comes from the sign the page shows, not from any arithmetic the reader should audit.
    bool linked = formula is Atom { Linked: true } or Negation { Operand: Atom { Linked: true } };
    return new ExcelFormulaText(Render(formula), linked);
  }

  private Node Translate(Expr expression, string column, string scope, string line, string sheet, bool condition)
  {
    switch (expression)
    {
      case NumberExpr number:
        return new Atom(number.Value.ToString("R", CultureInfo.InvariantCulture));

      case RefExpr reference:
        return Coordinate(book, reference, column, scope, line) is CellCoordinate coordinate
          ? Reference(coordinate, sheet)
          : Unavailable;

      case PriorExpr prior:
        return book.PriorPeriod(scope, column) is string priorColumn
          ? Translate(prior.Inner, priorColumn, scope, line, sheet, condition)
          : Unavailable;

      case UnaryExpr unary:
        return Negate(Translate(unary.Operand, column, scope, line, sheet, false));

      case BinaryExpr binary:
        return Combine(binary.Op,
          Translate(binary.Left, column, scope, line, sheet, false),
          Translate(binary.Right, column, scope, line, sheet, false));

      case ComparisonExpr comparison:
        var compared = new Comparison(comparison.Op switch { "==" => "=", "!=" => "<>", _ => comparison.Op },
          Translate(comparison.Left, column, scope, line, sheet, false),
          Translate(comparison.Right, column, scope, line, sheet, false));
        // Excel compares to TRUE or FALSE, which SUM skips when it reads them from a cell; the engine yields 1 or 0.
        return condition ? compared : new Call("IF", new List<Node> { compared, new Atom("1"), new Atom("0") });

      case FunctionExpr { Name: "if" } conditional:
        return new Call("IF", new List<Node>
        {
          Translate(conditional.Arguments[0], column, scope, line, sheet, true),
          Translate(conditional.Arguments[1], column, scope, line, sheet, false),
          Translate(conditional.Arguments[2], column, scope, line, sheet, false)
        });

      case FunctionExpr function:
        return Aggregate(function, column, scope, line, sheet);

      default:
        throw new InvalidOperationException($"No Excel form for {expression.GetType().Name}");
    }
  }

  private Node Aggregate(FunctionExpr function, string column, string scope, string line, string sheet)
  {
    string name = function.Name.ToUpperInvariant();
    if (function.Arguments is [RangeExpr only] && Coordinates(book, only, scope, line) is List<CellCoordinate> cells &&
      Span(cells, sheet) is (string negatedSpan, true))
    {
      // The negation of every cell moves outside, where min and max trade places.
      string swapped = name switch { "MIN" => "MAX", "MAX" => "MIN", _ => name };
      return Negate(new Call(swapped, new List<Node> { new Atom(negatedSpan) }));
    }

    var arguments = new List<Node>();
    foreach (Expr argument in function.Arguments)
    {
      if (argument is not RangeExpr range)
      {
        arguments.Add(Translate(argument, column, scope, line, sheet, false));
        continue;
      }
      if (Coordinates(book, range, scope, line) is not List<CellCoordinate> coordinates)
      {
        arguments.Add(Unavailable);
      }
      else if (Span(coordinates, sheet) is (string span, false))
      {
        arguments.Add(new Atom(span));
      }
      else
      {
        arguments.AddRange(coordinates.Select(coordinate => Reference(coordinate, sheet)));
      }
    }
    return new Call(name, arguments);
  }

  /// <summary>A range Excel can write as one span: cells in a single row or column, in order, alike in sign.</summary>
  private (string Text, bool Negated)? Span(List<CellCoordinate> coordinates, string sheet)
  {
    List<ExcelAddress> cells = coordinates.Select(coordinate => Address(coordinate, sheet)).ToList();
    ExcelAddress first = cells[0];
    bool row = cells.Select((cell, index) => (cell, index))
      .All(item => item.cell.Row == first.Row && item.cell.Column == first.Column + item.index);
    bool column = cells.Select((cell, index) => (cell, index))
      .All(item => item.cell.Column == first.Column && item.cell.Row == first.Row + item.index);
    if (!(row || column) || cells.Any(cell => cell.Sheet != first.Sheet || cell.Negated != first.Negated))
    {
      return null;
    }
    string span = cells.Count == 1 ? first.Cell : $"{first.Cell}:{cells[^1].Cell}";
    return (Qualifier(first, sheet) + span, first.Negated);
  }

  private Node Reference(CellCoordinate coordinate, string sheet)
  {
    ExcelAddress address = Address(coordinate, sheet);
    var atom = new Atom(Qualifier(address, sheet) + address.Cell, address.Sheet != sheet);
    return address.Negated ? new Negation(atom) : atom;
  }

  /// <summary>A cell shown on the formula's own sheet is read there, wherever else it also appears.</summary>
  private ExcelAddress Address(CellCoordinate coordinate, string sheet)
  {
    if (!addresses.TryGetValue(coordinate, out List<ExcelAddress>? shown))
    {
      throw new InvalidOperationException($"The workbook holds no cell for '{coordinate.Line}[{coordinate.Column}]'");
    }
    return shown.FirstOrDefault(address => address.Sheet == sheet, shown[0]);
  }

  private static string Qualifier(ExcelAddress address, string sheet)
  {
    return address.Sheet == sheet ? "" : $"'{address.Sheet.Replace("'", "''")}'!";
  }

  private static CellCoordinate? Coordinate(Book book, RefExpr reference, string column, string scope, string line)
  {
    string? name = book.ResolveSelf(reference.Name, scope, line);
    if (name is null)
    {
      return null;
    }
    return new CellCoordinate(name, book.ReferenceColumn(reference.Column, column, scope, name));
  }

  private static List<CellCoordinate>? Coordinates(Book book, RangeExpr range, string scope, string line)
  {
    string? name = book.ResolveSelf(range.Name, scope, line);
    return name is null
      ? null
      : book.ColumnsInRange(name, range.StartColumn, range.EndColumn)
        .Select(rangeColumn => new CellCoordinate(name, rangeColumn))
        .ToList();
  }

  private static IEnumerable<Expr> Children(Expr expression)
  {
    return expression switch
    {
      UnaryExpr unary => new[] { unary.Operand },
      BinaryExpr binary => new[] { binary.Left, binary.Right },
      ComparisonExpr comparison => new[] { comparison.Left, comparison.Right },
      FunctionExpr function => function.Arguments,
      _ => Array.Empty<Expr>()
    };
  }

  /// <summary>Stands in for a read the engine could not make, such as the period before the first. Only a branch the
  /// condition never takes holds one, since a cell that needs it is unresolved and gets no formula.</summary>
  private static Node Unavailable => new Call("NA", new List<Node>());

  /// <summary>Negates by rewriting where that removes a sign: -(a - b) is b - a, and -(-a * b) is a * b. Each rewrite
  /// is exact in floating point, since negation only flips the sign bit.</summary>
  private static Node Negate(Node node)
  {
    return node switch
    {
      _ when Positive(node) is Node positive => positive,
      Binary { Op: '-' } binary => Combine('+', Negate(binary.Left), binary.Right),
      Binary { Op: '+' } binary => Combine('-', Negate(binary.Left), binary.Right),
      Binary { Op: '*' or '/' } binary => binary with { Left = Negate(binary.Left) },
      _ => new Negation(node)
    };
  }

  private static Node Combine(char op, Node left, Node right)
  {
    return (op, Positive(left), Positive(right)) switch
    {
      ('+', _, Node positive) => Combine('-', left, positive),
      ('+', Node positive, _) => new Binary('-', right, positive),
      ('-', _, Node positive) => Combine('+', left, positive),
      ('*' or '/', _, Node positive) => new Binary(op, Negate(left), positive),
      _ => new Binary(op, left, right)
    };
  }

  /// <summary>A term that reads negated, such as -a or -a * b, without its sign; null for any other.</summary>
  private static Node? Positive(Node node)
  {
    return node switch
    {
      Negation negation => negation.Operand,
      Binary { Op: '*' or '/' } binary when Positive(binary.Left) is Node left => binary with { Left = left },
      _ => null
    };
  }

  private static string Render(Node node)
  {
    switch (node)
    {
      case Atom atom:
        return atom.Text;

      case Call call:
        return $"{call.Name}({string.Join(",", call.Arguments.Select(Render))})";

      case Negation negation:
        return "-" + Operand(negation.Operand, negation.Operand.Precedence < negation.Precedence);

      case Binary binary:
        // Excel binds a unary minus tighter than a power, so -a^2 would square the negation.
        bool left = binary.Left.Precedence < binary.Precedence || (binary.Op == '^' && binary.Left is Negation);
        bool right = binary.Right.Precedence <= binary.Precedence;
        return Operand(binary.Left, left) + binary.Op + Operand(binary.Right, right);

      case Comparison comparison:
        return Operand(comparison.Left, comparison.Left.Precedence <= comparison.Precedence) + comparison.Op +
          Operand(comparison.Right, comparison.Right.Precedence <= comparison.Precedence);

      default:
        throw new InvalidOperationException($"No rendering for {node.GetType().Name}");
    }
  }

  private static string Operand(Node node, bool parenthesize)
  {
    return parenthesize ? $"({Render(node)})" : Render(node);
  }

  private abstract record Node(int Precedence);

  /// <summary><c>Linked</c> marks a reference to a cell on another sheet.</summary>
  private sealed record Atom(string Text, bool Linked = false) : Node(6);

  private sealed record Call(string Name, List<Node> Arguments) : Node(6);

  private sealed record Negation(Node Operand) : Node(5);

  private sealed record Binary(char Op, Node Left, Node Right) : Node(Op switch { '^' => 4, '*' or '/' => 3, _ => 2 });

  private sealed record Comparison(string Op, Node Left, Node Right) : Node(1);


}