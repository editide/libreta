using System.Text;

namespace Libreta;

public static class Csv
{
  /// <summary>
  /// Splits one CSV line, where a cell in double quotes may hold commas and "" stands for a quote.
  /// Fails on an unterminated quote or text after a closing one; cells never span lines.
  /// </summary>
  public static bool TrySplit(string line, out string[] cells)
  {
    if (!line.Contains('"'))
    {
      cells = line.Split(',');
      return true;
    }

    var parsed = new List<string>();
    int index = 0;
    while (true)
    {
      int start = index;
      while (index < line.Length && char.IsWhiteSpace(line[index]))
      {
        index++;
      }
      if (index < line.Length && line[index] == '"')
      {
        var cell = new StringBuilder();
        index++;
        while (true)
        {
          if (index == line.Length)
          {
            cells = [];
            return false;
          }
          if (line[index] == '"')
          {
            if (index + 1 < line.Length && line[index + 1] == '"')
            {
              cell.Append('"');
              index += 2;
              continue;
            }
            index++;
            break;
          }
          cell.Append(line[index++]);
        }
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
          index++;
        }
        if (index < line.Length && line[index] != ',')
        {
          cells = [];
          return false;
        }
        parsed.Add(cell.ToString());
      }
      else
      {
        int comma = line.IndexOf(',', start);
        index = comma < 0 ? line.Length : comma;
        parsed.Add(line[start..index]);
      }
      if (index == line.Length)
      {
        cells = parsed.ToArray();
        return true;
      }
      index++;
    }
  }


}