using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Libreta;

/// <summary>The whole book as one workbook: a sheet per view in navigation order, each cell holding the value the page
/// shows, or the formula that calculates it, formatted by its units and styled by its row's role.</summary>
public static class XlsxExport
{
  public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

  private const int SheetNameLimit = 31;
  private const int NoteLineLength = 40;

  public static void Write(string root, Stream output)
  {
    Book book = Book.Load(root);
    List<ViewCatalogEntry> entries = CatalogLoader.Load(book, 1).Views;
    var builder = new Builder(book, entries.Select(entry => book.Views[entry.Id]).ToList());
    List<ExportSheet> sheets = entries.Select(entry => builder.Layout(book.Views[entry.Id], entry.Title)).ToList();
    if (builder.Supporting() is ExportSheet supporting)
    {
      sheets.Add(supporting);
    }
    builder.WriteFormulas();

    using SpreadsheetDocument document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
    WorkbookPart workbookPart = document.AddWorkbookPart();
    workbookPart.Workbook = new Workbook();
    workbookPart.AddNewPart<WorkbookStylesPart>().Stylesheet = builder.Styles.Stylesheet();
    Sheets sheetList = workbookPart.Workbook.AppendChild(new Sheets());
    uint sheetId = 1;
    int shapeBlocks = 0;
    foreach (ExportSheet sheet in sheets)
    {
      WorksheetPart part = workbookPart.AddNewPart<WorksheetPart>();
      part.Worksheet = Worksheet(sheet);
      Annotate(part, sheet, ref shapeBlocks);
      sheetList.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = sheetId++, Name = sheet.Name });
    }
    // The cached values are the engine's; Excel recalculates on opening rather than trusting them.
    workbookPart.Workbook.Append(new CalculationProperties { FullCalculationOnLoad = true });
  }

  /// <summary>A unique name Excel accepts: at most 31 characters, none of []:*?/\, no outer apostrophes, and never
  /// the reserved History.</summary>
  public static string SheetName(string title, HashSet<string> used)
  {
    string name = Regex.Replace(Regex.Replace(title, @"[\[\]:*?/\\]", " "), @"\s+", " ").Trim().Trim('\'').Trim();
    if (name.Length == 0)
    {
      name = "Sheet";
    }
    string candidate = Truncate(name, SheetNameLimit);
    for (int copy = 2; used.Contains(candidate) || candidate.Equals("History", StringComparison.OrdinalIgnoreCase);
      copy++)
    {
      string suffix = $" ({copy})";
      candidate = Truncate(name, SheetNameLimit - suffix.Length) + suffix;
    }
    used.Add(candidate);
    return candidate;
  }

  private static string Truncate(string name, int length)
  {
    return name.Length <= length ? name : name[..length].TrimEnd().TrimEnd('\'');
  }

  private static ExportCell Text(string? text, int width = 0, uint style = 0, string? comment = null)
  {
    return new ExportCell(text, null, style, width, Comment: comment);
  }

  /// <summary>A row's role rules across the row; a transposed view runs each line down a column, so its role rules
  /// down the column instead.</summary>
  private static Look Role(string style, bool column)
  {
    return style switch
    {
      "subtotal" => new Look(Emphasis: Emphasis.Bold, Rules: column ? Rules.Left : Rules.Top),
      "total" => new Look(Emphasis: Emphasis.Bold,
        Rules: column ? Rules.Left | Rules.Right : Rules.Top | Rules.DoubleBottom),
      "supplemental" => new Look(Emphasis: Emphasis.Italic, Ink: Ink.Muted),
      _ => new Look()
    };
  }

  /// <summary>What the inspector shows under Source, less the image.</summary>
  private static string? SourceText(SourcePayload? source)
  {
    if (source is null)
    {
      return null;
    }
    List<string> lines = source.Details.Select(detail => $"{detail.Key}: {detail.Value}").ToList();
    if (source.Url.Length > 0)
    {
      lines.Add(source.Url);
    }
    if (source.Note is not null)
    {
      lines.Add(source.Note);
    }
    return lines.Count == 0 ? null : string.Join("\n", lines);
  }

  private static string? Comment(string? source, string? note)
  {
    var sections = new List<string>();
    if (source is not null)
    {
      sections.Add("Source\n" + source);
    }
    if (note is not null)
    {
      sections.Add("Note\n" + note);
    }
    return sections.Count == 0 ? null : string.Join("\n\n", sections);
  }

  private static Worksheet Worksheet(ExportSheet sheet)
  {
    var data = new SheetData();
    for (int rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
    {
      var row = new Row { RowIndex = (uint)(rowIndex + 1) };
      for (int columnIndex = 0; columnIndex < sheet.Rows[rowIndex].Count; columnIndex++)
      {
        if (sheet.Rows[rowIndex][columnIndex] is ExportCell cell)
        {
          row.Append(Cell(cell, new ExcelAddress(sheet.Name, rowIndex, columnIndex, false).Cell));
        }
      }
      data.Append(row);
    }
    return new Worksheet(SheetViews(sheet), Columns(sheet), data);
  }

  /// <summary>Freezes the labels and the rows through the column labels, so both stay in view as the figures
  /// scroll, and hides the gridlines.</summary>
  private static SheetViews SheetViews(ExportSheet sheet)
  {
    PaneValues active = sheet.FrozenRows > 0 ? PaneValues.BottomRight : PaneValues.TopRight;
    string topLeft = new ExcelAddress(sheet.Name, sheet.FrozenRows, 1, false).Cell;
    var pane = new Pane
    {
      HorizontalSplit = 1,
      VerticalSplit = sheet.FrozenRows > 0 ? sheet.FrozenRows : null,
      TopLeftCell = topLeft,
      ActivePane = active,
      State = PaneStateValues.Frozen
    };
    var selection = new Selection
    {
      Pane = active,
      ActiveCell = topLeft,
      SequenceOfReferences = new ListValue<StringValue> { InnerText = topLeft }
    };
    // The page draws no grid, only the rules its roles call for.
    return new SheetViews(new SheetView(pane, selection) { ShowGridLines = false, WorkbookViewId = 0 });
  }

  /// <summary>Writes the sheet's comments as notes. Excel draws a note from a legacy VML shape rather than from the
  /// comments part, so each note needs both.</summary>
  private static void Annotate(WorksheetPart part, ExportSheet sheet, ref int shapeBlocks)
  {
    List<(int Row, int Column, string Text)> notes = sheet.Rows
      .SelectMany((row, rowIndex) => row.Select((cell, columnIndex) => (rowIndex, columnIndex, cell?.Comment)))
      .Where(note => note.Comment is not null)
      .Select(note => (note.rowIndex, note.columnIndex, note.Comment!))
      .ToList();
    if (notes.Count == 0)
    {
      return;
    }

    WorksheetCommentsPart commentsPart = part.AddNewPart<WorksheetCommentsPart>();
    commentsPart.Comments = new Comments(
      new Authors(new Author("Libreta")),
      new CommentList(notes.Select(note =>
        new Comment(new CommentText(new Run(
          new RunProperties(new FontSize { Val = 9 }, new RunFont { Val = "Tahoma" }),
          new Text(note.Text) { Space = SpaceProcessingModeValues.Preserve })))
        {
          Reference = new ExcelAddress(sheet.Name, note.Row, note.Column, false).Cell,
          AuthorId = 0
        })));

    // Shape ids are unique across the workbook, drawn from blocks of 1024 that each sheet claims in its id map.
    int blocks = notes.Count / 1024 + 1;
    int firstShape = (shapeBlocks + 1) * 1024 + 1;
    var vml = new StringBuilder();
    vml.Append("<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" ")
      .Append("xmlns:x=\"urn:schemas-microsoft-com:office:excel\">")
      .Append("<o:shapelayout v:ext=\"edit\"><o:idmap v:ext=\"edit\" data=\"")
      .Append(string.Join(",", Enumerable.Range(shapeBlocks + 1, blocks)))
      .Append("\"/></o:shapelayout>")
      .Append("<v:shapetype id=\"_x0000_t202\" coordsize=\"21600,21600\" o:spt=\"202\" ")
      .Append("path=\"m,l,21600r21600,l21600,xe\"><v:stroke joinstyle=\"miter\"/>")
      .Append("<v:path gradientshapeok=\"t\" o:connecttype=\"rect\"/></v:shapetype>");
    for (int index = 0; index < notes.Count; index++)
    {
      (int row, int column, string text) = notes[index];
      int lines = text.Split('\n').Sum(line => Math.Max(1, (line.Length + NoteLineLength - 1) / NoteLineLength));
      vml.Append($"<v:shape id=\"_x0000_s{firstShape + index}\" type=\"#_x0000_t202\" ")
        .Append("style=\"position:absolute;visibility:hidden\" fillcolor=\"#ffffe1\" o:insetmode=\"auto\">")
        .Append("<v:fill color2=\"#ffffe1\"/><v:shadow on=\"t\" color=\"black\" obscured=\"t\"/>")
        .Append("<v:path o:connecttype=\"none\"/><v:textbox style=\"mso-direction-alt:auto\"/>")
        .Append("<x:ClientData ObjectType=\"Note\"><x:MoveWithCells/><x:SizeWithCells/>")
        .Append($"<x:Anchor>{column + 1}, 15, {row}, 10, {column + 4}, 15, {row + lines + 1}, 4</x:Anchor>")
        .Append($"<x:AutoFill>False</x:AutoFill><x:Row>{row}</x:Row><x:Column>{column}</x:Column></x:ClientData>")
        .Append("</v:shape>");
    }
    vml.Append("</xml>");
    shapeBlocks += blocks;

    VmlDrawingPart drawing = part.AddNewPart<VmlDrawingPart>();
    using (var writer = new StreamWriter(drawing.GetStream(FileMode.Create)))
    {
      writer.Write(vml.ToString());
    }
    part.Worksheet!.Append(new LegacyDrawing { Id = part.GetIdOfPart(drawing) });
  }

  private static Cell Cell(ExportCell cell, string reference)
  {
    var element = new Cell { CellReference = reference, StyleIndex = cell.Style == 0 ? null : cell.Style };
    if (cell.Text is null && cell.Number is null)
    {
      return element;
    }
    if (cell.Number is double number)
    {
      if (cell.Formula is not null)
      {
        element.CellFormula = new CellFormula(cell.Formula);
      }
      element.CellValue = new CellValue(number.ToString("R", CultureInfo.InvariantCulture));
      return element;
    }
    element.DataType = CellValues.InlineString;
    element.InlineString = new InlineString(new Text(cell.Text!) { Space = SpaceProcessingModeValues.Preserve });
    return element;
  }

  /// <summary>Widths come from what each cell displays, measured in characters as Excel measures them.</summary>
  private static Columns Columns(ExportSheet sheet)
  {
    int count = sheet.Rows.Max(row => row.Count);
    var columns = new Columns();
    for (int index = 0; index < count; index++)
    {
      int widest = sheet.Rows.Where(row => index < row.Count).Max(row => row[index]?.Width ?? 0);
      columns.Append(new Column
      {
        Min = (uint)(index + 1),
        Max = (uint)(index + 1),
        Width = Math.Clamp(widest + 2, 10, 60),
        CustomWidth = true
      });
    }
    return columns;
  }

  /// <summary><c>FrozenRows</c> stay in view above the figures, and the label column beside them.</summary>
  private sealed record ExportSheet(string Name, List<List<ExportCell?>> Rows)
  {
    public int FrozenRows { get; set; }
  }

  /// <summary>Text, a number, which a formula may calculate, or neither for a blank that keeps its row's style;
  /// <c>Width</c> is the characters it displays, which sizes its column.</summary>
  private sealed record ExportCell(
    string? Text,
    double? Number,
    uint Style,
    int Width,
    string? Formula = null,
    string? Comment = null);

  /// <summary>How a cell looks; each distinct look becomes one cell format.</summary>
  private readonly record struct Look(
    string? NumberFormat = null,
    int Indent = 0,
    bool Right = false,
    Emphasis Emphasis = Emphasis.None,
    Rules Rules = Rules.None,
    Ink Ink = Ink.Default)
  {
    public Look With(Look role)
    {
      return this with
      {
        Emphasis = Emphasis | role.Emphasis,
        Rules = Rules | role.Rules,
        Ink = Ink == Ink.Default ? role.Ink : Ink
      };
    }
  }

  [Flags]
  private enum Emphasis
  {
    None = 0,
    Bold = 1,
    Italic = 2,
    Title = 4
  }

  /// <summary>A number's color tells where it comes from, as modelers code them: a hardcode, a calculation, or a
  /// link that only reads a cell on another sheet.</summary>
  private enum Ink
  {
    Default,
    Muted,
    Hardcode,
    Formula,
    Link
  }

  [Flags]
  private enum Rules
  {
    None = 0,
    Top = 1,
    Bottom = 2,
    DoubleBottom = 4,
    Left = 8,
    Right = 16
  }

  /// <summary>A calculated cell, written once every cell it reads has an address.</summary>
  private sealed record PendingFormula(ExportSheet Sheet, int Row, int Column, ResolvedCell Cell, bool Negated,
    Look Look);

  /// <summary>Lays out the sheets in two passes: every shown cell takes an address first, so a formula can read a
  /// cell on any sheet, including one laid out after its own.</summary>
  private sealed class Builder
  {
    private readonly Book book;
    private readonly List<View> views;
    private readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CellCoordinate, List<ExcelAddress>> addresses =
      new Dictionary<CellCoordinate, List<ExcelAddress>>();
    private readonly List<PendingFormula> formulas = new List<PendingFormula>();

    public Builder(Book book, List<View> views)
    {
      this.book = book;
      this.views = views;
    }

    public StyleTable Styles { get; } = new StyleTable();

    public ExportSheet Layout(View view, string title)
    {
      var engine = new Engine(view);
      ViewPayload payload = PayloadBuilder.Build(view, engine, 1);
      var sheet = new ExportSheet(SheetName(title, names), new List<List<ExportCell?>>
      {
        new List<ExportCell?> { Text(payload.Title, 0, Styles.Index(new Look(Emphasis: Emphasis.Title))) }
      });
      if (payload.Subtitle.Length > 0)
      {
        uint muted = Styles.Index(new Look(Ink: Ink.Muted));
        sheet.Rows.Add(new List<ExportCell?> { Text(payload.Subtitle, 0, muted) });
      }
      sheet.Rows.Add(new List<ExportCell?>());
      List<Look> columnRoles = payload.Columns.Select(column => Role(column.Style, true)).ToList();
      sheet.Rows.Add(Header(null, payload.Columns.Select((column, index) =>
        (column.Label, columnRoles[index], Comment(SourceText(column.Source), column.Note)))));
      sheet.FrozenRows = sheet.Rows.Count;

      foreach (RowPayload row in payload.Rows)
      {
        if (row.Kind == "spacer")
        {
          sheet.Rows.Add(new List<ExportCell?>());
          continue;
        }
        Look role = row.Kind == "header" ? new Look(Emphasis: Emphasis.Bold, Ink: Ink.Muted) : Role(row.Style, false);
        string? rowSource = SourceText(row.Source);
        var cells = new List<ExportCell?>
        {
          Text(row.Label, row.Label.Length + 2 * row.Indent, Styles.Index(role with { Indent = row.Indent }),
            Comment(rowSource, row.Note))
        };
        sheet.Rows.Add(cells);
        for (int index = 0; index < row.Cells.Count; index++)
        {
          CellPayload cell = row.Cells[index];
          ResolvedCell resolved = engine.Cell(new CellCoordinate(cell.Line, cell.Column));
          // The line's source and note sit on its label, so a cell notes only what is its own.
          string? source = SourceText(cell.Source);
          bool shared = source == rowSource || source == SourceText(payload.Columns[index].Source);
          cells.Add(Place(sheet, cells.Count, view, resolved, cell.Contra, role.With(columnRoles[index]),
            Comment(shared ? null : source, resolved.Note)));
        }
      }
      return sheet;
    }

    /// <summary>The cells formulas read that no view shows, such as an intermediate line, grouped by the folder that
    /// defines them.</summary>
    public ExportSheet? Supporting()
    {
      var engine = new Engine(views[0]);
      var seen = new HashSet<CellCoordinate>(addresses.Keys);
      var needed = new List<(CellCoordinate Coordinate, ResolvedCell? Cell)>();
      var pending = new Queue<CellCoordinate>(formulas.SelectMany(formula => Reads(formula.Cell)));
      while (pending.TryDequeue(out CellCoordinate coordinate))
      {
        if (!seen.Add(coordinate))
        {
          continue;
        }
        ResolvedCell? cell = Resolve(engine, coordinate);
        needed.Add((coordinate, cell));
        if (cell?.Value is not null && cell.Expression is not null)
        {
          foreach (CellCoordinate read in Reads(cell))
          {
            pending.Enqueue(read);
          }
        }
      }
      if (needed.Count == 0)
      {
        return null;
      }

      var sheet = new ExportSheet(SheetName("Supporting", names), new List<List<ExportCell?>>
      {
        new List<ExportCell?> { Text("Supporting") },
        new List<ExportCell?> { Text("Lines the views read but do not show") }
      });
      foreach (IGrouping<string, (CellCoordinate Coordinate, ResolvedCell? Cell)> block in
        needed.GroupBy(item => LineScope(item.Coordinate.Line)))
      {
        List<string> periods = book.PeriodsFor(block.Key);
        List<string> columns = block.Select(item => item.Coordinate.Column).Distinct()
          .OrderBy(column => periods.IndexOf(column) is int index and >= 0 ? index : int.MaxValue)
          .ToList();
        View view = views.FirstOrDefault(candidate => candidate.Scope == block.Key) ?? views[0];
        sheet.Rows.Add(new List<ExportCell?>());
        sheet.Rows.Add(Header(BlockTitle(block.Key),
          columns.Select(column => (ColumnTitle(column), new Look(), (string?)null))));
        foreach (IGrouping<string, (CellCoordinate Coordinate, ResolvedCell? Cell)> line in
          block.GroupBy(item => item.Coordinate.Line))
        {
          string label = view.LabelOf(line.Key);
          var cells = new List<ExportCell?> { Text(label, label.Length) };
          cells.AddRange(Enumerable.Repeat<ExportCell?>(null, columns.Count));
          sheet.Rows.Add(cells);
          foreach ((CellCoordinate coordinate, ResolvedCell? cell) in line)
          {
            int column = columns.IndexOf(coordinate.Column) + 1;
            cells[column] = cell is null
              ? null
              : Place(sheet, column, Formatting(view, cell.Units), cell, false, new Look(), null);
          }
        }
      }
      return sheet;
    }

    public void WriteFormulas()
    {
      var writer = new ExcelFormula(book, addresses);
      foreach (PendingFormula formula in formulas)
      {
        ResolvedCell cell = formula.Cell;
        List<ExportCell?> row = formula.Sheet.Rows[formula.Row];
        ExcelFormulaText text = writer.Write(cell.Expression!, cell.Column, cell.Scope!, cell.Line,
          formula.Sheet.Name, formula.Negated);
        row[formula.Column] = row[formula.Column]! with
        {
          Formula = text.Text,
          Style = Styles.Index(formula.Look with { Ink = InkOf(formula.Look.Ink, text.Linked) })
        };
      }
    }

    /// <summary>A formula keeps its row's muted ink, as the page does, unless it links to another sheet.</summary>
    private static Ink InkOf(Ink role, bool linked)
    {
      return linked ? Ink.Link : role == Ink.Muted ? Ink.Muted : Ink.Formula;
    }

    /// <summary>A cell at the end of the sheet's last row, taking its address and any formula it calculates.</summary>
    private ExportCell Place(ExportSheet sheet, int column, View view, ResolvedCell cell, bool contra, Look role,
      string? comment)
    {
      int row = sheet.Rows.Count - 1;
      var coordinate = new CellCoordinate(cell.Line, cell.Column);
      if (!addresses.TryGetValue(coordinate, out List<ExcelAddress>? shown))
      {
        shown = new List<ExcelAddress>();
        addresses.Add(coordinate, shown);
      }
      shown.Add(new ExcelAddress(sheet.Name, row, column, contra));

      if (cell.Text is not null)
      {
        return Text(cell.Text, cell.Text.Length, Styles.Index(role with { Ink = Ink.Hardcode, Right = true }), comment);
      }
      if (cell.Value is not double value)
      {
        string unresolved = view.Formatter.Unresolved;
        return Text(unresolved.Length == 0 ? null : unresolved, unresolved.Length, Styles.Index(role), comment);
      }
      string format = ExcelNumberFormat.For(view.Formatter.Spec(cell.Units), PayloadBuilder.ZeroDisplay(view, cell));
      // A formula's color waits on its text, which tells whether it is a link to another sheet.
      Look look = role with { NumberFormat = format, Ink = cell.Expression is null ? Ink.Hardcode : role.Ink };
      if (cell.Expression is not null)
      {
        formulas.Add(new PendingFormula(sheet, row, column, cell, contra, look));
      }
      // Negating a zero would write -0, which Excel shows in the negative section.
      double shownValue = contra && value != 0 ? -value : value;
      int width = PayloadBuilder.Display(view, cell, contra).Length;
      return new ExportCell(null, shownValue, Styles.Index(look), width, Comment: comment);
    }

    private List<ExportCell?> Header(string? title, IEnumerable<(string Label, Look Role, string? Comment)> labels)
    {
      var look = new Look(Emphasis: Emphasis.Bold, Rules: Rules.Bottom);
      var header = new List<ExportCell?> { Text(title, title?.Length ?? 0, Styles.Index(look)) };
      // Text aligns left by default, so column labels would sit away from the figures below them.
      header.AddRange(labels.Select(label => Text(label.Label, label.Label.Length,
        Styles.Index(look.With(label.Role) with { Right = true }), label.Comment)));
      return header;
    }

    private IEnumerable<CellCoordinate> Reads(ResolvedCell cell)
    {
      return ExcelFormula.Reads(book, cell.Expression!, cell.Column, cell.Scope!, cell.Line);
    }

    /// <summary>A cell only a branch the condition never takes reads may not resolve at all; it stays empty.</summary>
    private static ResolvedCell? Resolve(Engine engine, CellCoordinate coordinate)
    {
      try
      {
        return engine.Cell(coordinate);
      }
      catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
      {
        return null;
      }
    }

    private string LineScope(string line)
    {
      return book.Formulas.TryGetValue(line, out Formula? formula)
        ? formula.Scope
        : book.Facts.TryGetValue(line, out Fact? fact) ? fact.Scope : "";
    }

    private string BlockTitle(string scope)
    {
      return book.ViewIdsByScope.TryGetValue(scope, out string? id) && book.Presentations[id].Title.Length > 0
        ? book.Presentations[id].Title
        : scope.Length > 0 ? scope : book.Structure.Definition.File.Name;
    }

    private string ColumnTitle(string column)
    {
      return book.ColumnFormulas.TryGetValue(column, out ColumnFormula? formula) && formula.Label.Length > 0
        ? formula.Label
        : column;
    }

    /// <summary>The view that formats a supporting cell: the one over its folder, else the first that knows its
    /// units.</summary>
    private View Formatting(View preferred, string? units)
    {
      return preferred.Formatter.Defines(units)
        ? preferred
        : views.FirstOrDefault(view => view.Formatter.Defines(units)) ?? preferred;
    }


  }

  /// <summary>Cell formats by look, registered as cells need them; index 0 of each table is Excel's default.</summary>
  private sealed class StyleTable
  {
    private const uint FirstCustomFormat = 164;


    private readonly Dictionary<string, uint> numberFormats = new Dictionary<string, uint>();
    private readonly List<(Emphasis Emphasis, Ink Ink)> fonts =
      new List<(Emphasis, Ink)> { (Emphasis.None, Ink.Default) };
    private readonly List<Rules> borders = new List<Rules> { Rules.None };
    private readonly List<(uint NumberFormat, int Indent, bool Right, int Font, int Border)> cellFormats =
      new List<(uint, int, bool, int, int)> { (0, 0, false, 0, 0) };

    public uint Index(Look look)
    {
      uint formatId = 0;
      if (look.NumberFormat is not null && !numberFormats.TryGetValue(look.NumberFormat, out formatId))
      {
        formatId = FirstCustomFormat + (uint)numberFormats.Count;
        numberFormats.Add(look.NumberFormat, formatId);
      }
      return (uint)Register(cellFormats,
        (formatId, look.Indent, look.Right, Register(fonts, (look.Emphasis, look.Ink)), Register(borders, look.Rules)));
    }

    public Stylesheet Stylesheet()
    {
      var stylesheet = new Stylesheet();
      if (numberFormats.Count > 0)
      {
        stylesheet.Append(new NumberingFormats(numberFormats.Select(format =>
          new NumberingFormat { NumberFormatId = format.Value, FormatCode = format.Key }))
        {
          Count = (uint)numberFormats.Count
        });
      }
      stylesheet.Append(
        new Fonts(fonts.Select(Font)) { Count = (uint)fonts.Count },
        new Fills(
          new Fill(new PatternFill { PatternType = PatternValues.None }),
          new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        {
          Count = 2
        },
        new Borders(borders.Select(Border)) { Count = (uint)borders.Count },
        new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 })
        {
          Count = 1
        },
        new CellFormats(cellFormats.Select(CellFormat)) { Count = (uint)cellFormats.Count });
      return stylesheet;
    }

    private static int Register<T>(List<T> table, T entry)
    {
      int index = table.IndexOf(entry);
      if (index < 0)
      {
        table.Add(entry);
        index = table.Count - 1;
      }
      return index;
    }

    private static Font Font((Emphasis Emphasis, Ink Ink) look)
    {
      (Emphasis emphasis, Ink ink) = look;
      var font = new Font();
      if (emphasis.HasFlag(Emphasis.Bold) || emphasis.HasFlag(Emphasis.Title))
      {
        font.Append(new Bold());
      }
      if (emphasis.HasFlag(Emphasis.Italic))
      {
        font.Append(new Italic());
      }
      font.Append(new FontSize { Val = emphasis.HasFlag(Emphasis.Title) ? 14 : 11 });
      // Muted is the page's muted ink.
      string? color = ink switch
      {
        Ink.Muted => "FF616161",
        Ink.Hardcode => "FF0000FF",
        Ink.Formula => "FF000000",
        Ink.Link => "FF006600",
        _ => null
      };
      if (color is not null)
      {
        font.Append(new Color { Rgb = color });
      }
      font.Append(new FontName { Val = "Arial" });
      return font;
    }

    private static Border Border(Rules rules)
    {
      BorderStyleValues? bottom = rules.HasFlag(Rules.DoubleBottom) ? BorderStyleValues.Double
        : rules.HasFlag(Rules.Bottom) ? BorderStyleValues.Thin : null;
      return new Border(
        Side(new LeftBorder(), rules.HasFlag(Rules.Left) ? BorderStyleValues.Thin : null),
        Side(new RightBorder(), rules.HasFlag(Rules.Right) ? BorderStyleValues.Thin : null),
        Side(new TopBorder(), rules.HasFlag(Rules.Top) ? BorderStyleValues.Thin : null),
        Side(new BottomBorder(), bottom),
        new DiagonalBorder());
    }

    private static BorderPropertiesType Side(BorderPropertiesType side, BorderStyleValues? style)
    {
      if (style is not null)
      {
        side.Style = style;
        side.Append(new Color { Auto = true });
      }
      return side;
    }

    private static CellFormat CellFormat((uint NumberFormat, int Indent, bool Right, int Font, int Border) format)
    {
      var cellFormat = new CellFormat
      {
        NumberFormatId = format.NumberFormat,
        FontId = (uint)format.Font,
        FillId = 0,
        BorderId = (uint)format.Border,
        FormatId = 0,
        ApplyNumberFormat = format.NumberFormat != 0,
        ApplyFont = format.Font != 0,
        ApplyBorder = format.Border != 0
      };
      if (format.Indent > 0 || format.Right)
      {
        cellFormat.Alignment = new Alignment
        {
          Indent = format.Indent > 0 ? (uint)format.Indent : null,
          Horizontal = format.Right ? HorizontalAlignmentValues.Right : null
        };
        cellFormat.ApplyAlignment = true;
      }
      return cellFormat;
    }


  }


}