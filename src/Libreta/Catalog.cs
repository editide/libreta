namespace Libreta;

/// <summary>A view in navigation order, with the headings enclosing it in the sidebar, outermost first, and the id
/// prefix of the included book it belongs to, which a heading never hides.</summary>
public record ViewCatalogEntry(string Id, string Title, IReadOnlyList<CatalogHeading> Headings, string? Book = null);

/// <summary>A sidebar heading, placed before the view id's segment at Depth so it can sit inside a folder or parent
/// view.</summary>
public record CatalogHeading(string Name, int Depth);

public record ViewCatalog(
  string Name,
  string ShortName,
  List<ViewCatalogEntry> Views,
  long Version,
  string? Error = null);

/// <summary>The navigation a loaded book presents, validated against the views it owns.</summary>
public static class CatalogLoader
{
  public static ViewCatalog Load(Book book, long version)
  {
    BookStructure structure = book.Structure;
    var viewsByBook = new Dictionary<BookDefinition, Dictionary<string, ViewCatalogEntry>>();
    foreach (BookDefinition definition in structure.Definitions)
    {
      var discovered = new Dictionary<string, ViewCatalogEntry>();
      foreach (string path in structure.Files(definition, "view.yaml"))
      {
        (string localId, string id) = BookStructure.ViewId(definition, Path.GetDirectoryName(path)!);
        string title = book.Presentations[id].Title;
        discovered.Add(localId, new ViewCatalogEntry(id, title.Length > 0 ? title : Label(localId.Split('/')[^1]),
          Array.Empty<CatalogHeading>(), definition.ViewPrefix.Length == 0 ? null : definition.ViewPrefix));
      }
      viewsByBook.Add(definition, discovered);
      ValidateLocalNavigation(definition, discovered);
    }

    var views = new List<ViewCatalogEntry>();
    AddNavigation(structure.Definition, structure.Definition.File.Navigation, viewsByBook, views,
      Array.Empty<CatalogHeading>(), Depth(structure.Definition.ViewPrefix));
    return new ViewCatalog(
      structure.Definition.File.Name,
      structure.Definition.File.ShortName,
      views,
      version);
  }

  private static void ValidateLocalNavigation(
    BookDefinition definition,
    Dictionary<string, ViewCatalogEntry> discovered)
  {
    var included = new HashSet<string>();
    ValidateLocalItems(definition, definition.File.Navigation, null, discovered, included);
    string[] omitted = discovered.Keys.Except(included).OrderBy(id => id).ToArray();
    if (omitted.Length > 0)
    {
      throw new InvalidDataException(
        $"'{Path.Combine(definition.Root, "book.yaml")}' navigation omits: {string.Join(", ", omitted)}");
    }
  }

  private static void ValidateLocalItems(
    BookDefinition definition,
    List<NavigationItem> items,
    string? parent,
    Dictionary<string, ViewCatalogEntry> discovered,
    HashSet<string> included)
  {
    foreach (NavigationItem item in items)
    {
      if (item.Heading.Length > 0)
      {
        ValidateLocalItems(definition, item.Children, parent, discovered, included);
        continue;
      }
      if (item.Book.Length > 0)
      {
        if (!definition.Children.Any(child => child.Id == item.Book))
        {
          throw new InvalidDataException($"Navigation references unknown included book: {item.Book}");
        }
        continue;
      }
      if (parent is not null && !item.View.StartsWith($"{parent}/", StringComparison.Ordinal))
      {
        throw new InvalidDataException($"Navigation view '{item.View}' is not nested beneath '{parent}'");
      }
      if (!discovered.ContainsKey(item.View))
      {
        throw new InvalidDataException($"Navigation references unknown view: {item.View}");
      }
      if (!included.Add(item.View))
      {
        throw new InvalidDataException($"Navigation repeats view: {item.View}");
      }
      ValidateLocalItems(definition, item.Children, item.View, discovered, included);
    }
  }

  private static void AddNavigation(
    BookDefinition definition,
    List<NavigationItem> items,
    Dictionary<BookDefinition, Dictionary<string, ViewCatalogEntry>> viewsByBook,
    List<ViewCatalogEntry> views,
    IReadOnlyList<CatalogHeading> headings,
    int depth)
  {
    foreach (NavigationItem item in items)
    {
      if (item.Heading.Length > 0)
      {
        CatalogHeading[] nested = headings.Append(new CatalogHeading(item.Heading, depth)).ToArray();
        AddNavigation(definition, item.Children, viewsByBook, views, nested, depth);
        continue;
      }
      if (item.Book.Length > 0)
      {
        BookDefinition child = definition.Children.Single(candidate => candidate.Id == item.Book);
        AddNavigation(child, child.File.Navigation, viewsByBook, views, headings, Depth(child.ViewPrefix));
        continue;
      }
      ViewCatalogEntry view = viewsByBook[definition][item.View] with { Headings = headings };
      views.Add(view);
      AddNavigation(definition, item.Children, viewsByBook, views, headings, Depth(view.Id));
    }
  }

  private static int Depth(string path)
  {
    return path.Length == 0 ? 0 : path.Split('/').Length;
  }

  private static string Label(string segment)
  {
    return string.Join(' ', segment.Split('-').Select(word =>
      word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
  }


}