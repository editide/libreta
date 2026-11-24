using System.Globalization;

namespace Libreta;

public abstract class Expr
{
  /// <summary>Whether every reference names its column, so the expression has one value whatever column it is in.
  /// </summary>
  public static bool Pinned(Expr expression)
  {
    bool any = false;
    bool Walk(Expr node)
    {
      switch (node)
      {
        case RefExpr reference:
          any = true;
          return reference.Column is not null;
        case RangeExpr:
          any = true;
          return true;
        case PriorExpr prior:
          return Walk(prior.Inner);
        case UnaryExpr unary:
          return Walk(unary.Operand);
        case BinaryExpr binary:
          return Walk(binary.Left) && Walk(binary.Right);
        case ComparisonExpr comparison:
          return Walk(comparison.Left) && Walk(comparison.Right);
        case FunctionExpr function:
          return function.Arguments.All(Walk);
        default:
          return true;
      }
    }
    return Walk(expression) && any;
  }

  public static IEnumerable<Expr> Children(Expr expression)
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


}

public sealed class NumberExpr : Expr
{
  public double Value { get; init; }
}

/// <summary>A reference and the span it is written at, including any column subscript.</summary>
public sealed class RefExpr : Expr
{
  public string Name { get; init; } = "";
  public string? Column { get; init; }
  public int Start { get; init; }
  public int Length { get; init; }
}

public sealed class RangeExpr : Expr
{
  public string Name { get; init; } = "";
  public string StartColumn { get; init; } = "";
  public string EndColumn { get; init; } = "";
  public int Start { get; init; }
  public int Length { get; init; }
}

public sealed class PriorExpr : Expr
{
  public Expr Inner { get; init; } = null!;
}

public sealed class UnaryExpr : Expr
{
  public char Op { get; init; }
  public Expr Operand { get; init; } = null!;
}

public sealed class BinaryExpr : Expr
{
  public char Op { get; init; }
  public Expr Left { get; init; } = null!;
  public Expr Right { get; init; } = null!;
}

public sealed class ComparisonExpr : Expr
{
  public string Op { get; init; } = "";
  public Expr Left { get; init; } = null!;
  public Expr Right { get; init; } = null!;
}

public sealed class FunctionExpr : Expr
{
  public string Name { get; init; } = "";
  public List<Expr> Arguments { get; init; } = new List<Expr>();
}

public static class ExpressionParser
{
  public static Expr Parse(string source)
  {
    List<Token> tokens = Tokenize(source);
    int position = 0;
    Expr expr = ParseExpression(tokens, ref position);
    if (position != tokens.Count)
    {
      throw new FormatException($"Unexpected '{tokens[position].Text}' in formula: {source}");
    }
    return expr;
  }

  private static Expr ParseExpression(List<Token> tokens, ref int position)
  {
    Expr left = ParseAdditive(tokens, ref position);
    if (position >= tokens.Count || tokens[position].Text is not ("<" or "<=" or ">" or ">=" or "==" or "!="))
    {
      return left;
    }

    string op = tokens[position].Text;
    position++;
    Expr right = ParseAdditive(tokens, ref position);
    return new ComparisonExpr { Op = op, Left = left, Right = right };
  }

  private static Expr ParseAdditive(List<Token> tokens, ref int position)
  {
    Expr left = ParseTerm(tokens, ref position);
    while (position < tokens.Count && (tokens[position].Text == "+" || tokens[position].Text == "-"))
    {
      char op = tokens[position].Text[0];
      position++;
      Expr right = ParseTerm(tokens, ref position);
      left = new BinaryExpr { Op = op, Left = left, Right = right };
    }
    return left;
  }

  private static Expr ParseTerm(List<Token> tokens, ref int position)
  {
    Expr left = ParseUnary(tokens, ref position);
    while (position < tokens.Count && (tokens[position].Text == "*" || tokens[position].Text == "/"))
    {
      char op = tokens[position].Text[0];
      position++;
      Expr right = ParseUnary(tokens, ref position);
      left = new BinaryExpr { Op = op, Left = left, Right = right };
    }
    return left;
  }

  private static Expr ParseUnary(List<Token> tokens, ref int position)
  {
    if (position < tokens.Count && tokens[position].Text == "-")
    {
      position++;
      return new UnaryExpr { Op = '-', Operand = ParseUnary(tokens, ref position) };
    }
    return ParsePower(tokens, ref position);
  }

  private static Expr ParsePower(List<Token> tokens, ref int position)
  {
    Expr left = ParsePrimary(tokens, ref position);
    if (position >= tokens.Count || tokens[position].Text != "^")
    {
      return left;
    }

    position++;
    Expr right = ParseUnary(tokens, ref position);
    return new BinaryExpr { Op = '^', Left = left, Right = right };
  }

  private static Expr ParsePrimary(List<Token> tokens, ref int position)
  {
    if (position >= tokens.Count)
    {
      throw new FormatException("Formula ended unexpectedly");
    }

    Token token = tokens[position];
    if (token.Kind == TokenKind.Number)
    {
      position++;
      return new NumberExpr { Value = double.Parse(token.Text, CultureInfo.InvariantCulture) };
    }

    if (token.Text == "(")
    {
      position++;
      Expr inner = ParseExpression(tokens, ref position);
      Expect(tokens, ref position, ")");
      return inner;
    }

    if (token.Kind == TokenKind.Identifier)
    {
      position++;
      bool isCall = position < tokens.Count && tokens[position].Text == "(";
      if (!isCall)
      {
        if (position >= tokens.Count || tokens[position].Text != "[")
        {
          return new RefExpr { Name = token.Text, Start = token.Start, Length = token.Text.Length };
        }

        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.String)
        {
          throw new FormatException("Expected a quoted column name");
        }
        string startColumn = tokens[position].Text;
        position++;
        if (position < tokens.Count && tokens[position].Text == ":")
        {
          position++;
          if (position >= tokens.Count || tokens[position].Kind != TokenKind.String)
          {
            throw new FormatException("Expected a quoted range end");
          }
          string endColumn = tokens[position].Text;
          position++;
          Expect(tokens, ref position, "]");
          return new RangeExpr
          {
            Name = token.Text,
            StartColumn = startColumn,
            EndColumn = endColumn,
            Start = token.Start,
            Length = tokens[position - 1].End - token.Start
          };
        }

        Expect(tokens, ref position, "]");
        return new RefExpr
        {
          Name = token.Text,
          Column = startColumn,
          Start = token.Start,
          Length = tokens[position - 1].End - token.Start
        };
      }

      position++;
      var arguments = new List<Expr>();
      if (position >= tokens.Count || tokens[position].Text != ")")
      {
        while (true)
        {
          arguments.Add(ParseExpression(tokens, ref position));
          if (position >= tokens.Count || tokens[position].Text != ",")
          {
            break;
          }
          position++;
        }
      }
      Expect(tokens, ref position, ")");

      if (token.Text == "prior")
      {
        ExpectArguments(token.Text, arguments, 1, 1);
        return new PriorExpr { Inner = arguments[0] };
      }
      if (token.Text is "sum" or "min" or "max" or "average" or "median")
      {
        ExpectArguments(token.Text, arguments, 1, int.MaxValue);
        return new FunctionExpr { Name = token.Text, Arguments = arguments };
      }
      if (token.Text == "if")
      {
        ExpectArguments(token.Text, arguments, 3, 3);
        return new FunctionExpr { Name = token.Text, Arguments = arguments };
      }
      if (token.Text == "eomonth")
      {
        ExpectArguments(token.Text, arguments, 2, 2);
        if (arguments.Any(argument => argument is RangeExpr))
        {
          throw new FormatException("Function 'eomonth' takes a date and a number of months, not a range");
        }
        return new FunctionExpr { Name = token.Text, Arguments = arguments };
      }
      throw new FormatException($"Unknown function '{token.Text}'");
    }

    throw new FormatException($"Unexpected '{token.Text}' in formula");
  }

  private static void ExpectArguments(string function, List<Expr> arguments, int minimum, int maximum)
  {
    if (arguments.Count < minimum || arguments.Count > maximum)
    {
      string expected = minimum == maximum ? minimum.ToString() : $"at least {minimum}";
      string plural = minimum == 1 ? "" : "s";
      throw new FormatException($"Function '{function}' expects {expected} argument{plural}");
    }
  }

  private static void Expect(List<Token> tokens, ref int position, string text)
  {
    if (position >= tokens.Count || tokens[position].Text != text)
    {
      throw new FormatException($"Expected '{text}'");
    }
    position++;
  }

  private static List<Token> Tokenize(string source)
  {
    List<Token> tokens = new List<Token>();
    int index = 0;
    while (index < source.Length)
    {
      char current = source[index];
      if (char.IsWhiteSpace(current))
      {
        index++;
        continue;
      }

      if (char.IsDigit(current) || (current == '.' && index + 1 < source.Length && char.IsDigit(source[index + 1])))
      {
        int start = index;
        while (index < source.Length && (char.IsDigit(source[index]) || source[index] == '.'))
        {
          index++;
        }
        tokens.Add(new Token(TokenKind.Number, source[start..index], start, index));
        continue;
      }

      if (char.IsLetter(current) || current == '_')
      {
        int start = index;
        while (index < source.Length)
        {
          if (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '.')
          {
            index++;
            continue;
          }
          if (index + 2 < source.Length && source[index] == ':' && source[index + 1] == ':' &&
            (char.IsLetter(source[index + 2]) || source[index + 2] == '_'))
          {
            index += 2;
            continue;
          }
          break;
        }
        tokens.Add(new Token(TokenKind.Identifier, source[start..index], start, index));
        continue;
      }

      if (current == '"')
      {
        int start = ++index;
        while (index < source.Length && source[index] != '"')
        {
          index++;
        }
        if (index >= source.Length)
        {
          throw new FormatException("Unterminated string in formula");
        }
        tokens.Add(new Token(TokenKind.String, source[start..index], start - 1, index + 1));
        index++;
        continue;
      }

      if (index + 1 < source.Length && source[index..(index + 2)] is "<=" or ">=" or "==" or "!=")
      {
        tokens.Add(new Token(TokenKind.Symbol, source[index..(index + 2)], index, index + 2));
        index += 2;
        continue;
      }

      if ("+-*/^()[],:<>".Contains(current))
      {
        tokens.Add(new Token(TokenKind.Symbol, current.ToString(), index, index + 1));
        index++;
        continue;
      }

      throw new FormatException($"Unexpected character '{current}' in formula");
    }
    return tokens;
  }

  private enum TokenKind
  {
    Number,
    Identifier,
    String,
    Symbol
  }

  private readonly record struct Token(TokenKind Kind, string Text, int Start, int End);
}