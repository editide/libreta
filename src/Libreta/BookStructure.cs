using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Libreta;

public sealed class BookDefinition
{
  public string Id { get; init; } = "";
  public string Root { get; init; } = "";
  public string Namespace { get; init; } = "";
  public string ViewPrefix { get; init; } = "";
  public BookFile File { get; init; } = new BookFile();
  public List<BookDefinition> Children { get; } = new List<BookDefinition>();
}

public sealed class BookStructure
{
  private static readonly HashSet<string> BookProperties = new HashSet<string>
  {
    "name", "short_name", "description", "units", "currency", "fiscal_year_end", "identifiers", "navigation", "books"
  };
  /// <summary>How a formula names every included book at once instead of listing them.</summary>
  public const string InclusionWildcard = "books";

  private static readonly Regex InclusionId = new Regex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

  private BookStructure(string root, BookDefinition definition)
  {
    Root = root;
    Definition = definition;
    Definitions = Flatten(definition).ToList();
  }

  public string Root { get; }
  public BookDefinition Definition { get; }
  public List<BookDefinition> Definitions { get; }

  public static BookStructure Load(string root)
  {
    string fullRoot = Path.GetFullPath(root);
    BookDefinition definition = LoadDefinition(fullRoot, "", "", "", new HashSet<string>(PathComparer));
    return new BookStructure(fullRoot, definition);
  }

  public IEnumerable<string> Files(BookDefinition definition, string fileName)
  {
    return OwnedDirectories(definition)
      .Select(directory => Path.Combine(directory, fileName))
      .Where(File.Exists)
      .OrderBy(path => path, PathComparer);
  }

  public IEnumerable<(string Path, string ScopeFolder)> FilesBelow(
    BookDefinition definition,
    string searchPattern,
    string requiredParent)
  {
    return OwnedDirectories(definition)
      .Select(directory => (Directory: directory, Root: OutermostNamedAncestor(definition, directory, requiredParent)))
      .Where(item => item.Root is not null)
      .SelectMany(item => Directory.GetFiles(item.Directory, searchPattern, SearchOption.TopDirectoryOnly)
        .Select(path => (Path: path, ScopeFolder: Path.GetDirectoryName(item.Root!)!)))
      .OrderBy(item => item.Path, PathComparer);
  }

  public IEnumerable<string> OwnedDirectories()
  {
    return Definitions.SelectMany(OwnedDirectories).Distinct(PathComparer);
  }

  public static string LocalScope(BookDefinition definition, string folder)
  {
    string relative = Path.GetRelativePath(definition.Root, folder);
    return relative == "." ? "" : Normalize(relative.Replace(Path.DirectorySeparatorChar, '.'));
  }

  /// <summary>A view's id within its own book, and its id from the root book's navigation.</summary>
  public static (string LocalId, string Id) ViewId(BookDefinition definition, string folder)
  {
    string localId = Path.GetRelativePath(definition.Root, folder).Replace(Path.DirectorySeparatorChar, '/');
    return (localId, definition.ViewPrefix.Length == 0 ? localId : $"{definition.ViewPrefix}/{localId}");
  }

  public static string Scope(BookDefinition definition, string folder)
  {
    return Qualify(definition.Namespace, LocalScope(definition, folder));
  }

  public static string Qualify(string bookNamespace, string localName)
  {
    string normalized = Normalize(localName);
    if (bookNamespace.Length == 0)
    {
      return normalized;
    }
    return normalized.Length == 0 ? bookNamespace : $"{bookNamespace}::{normalized}";
  }

  public static string Normalize(string name)
  {
    return name.Replace('/', '.').Replace('-', '_');
  }

  /// <summary>Whether a reference names the current row's line, directly or in another scope.</summary>
  public static bool IsSelf(string reference)
  {
    return reference == "self" || reference.EndsWith(".self", StringComparison.Ordinal);
  }

  private static BookDefinition LoadDefinition(
    string root,
    string id,
    string bookNamespace,
    string viewPrefix,
    HashSet<string> ancestors)
  {
    string canonicalRoot = Path.GetFullPath(root);
    if (!ancestors.Add(canonicalRoot))
    {
      throw new InvalidDataException($"book.yaml contains a recursive inclusion at '{canonicalRoot}'");
    }

    string path = Path.Combine(canonicalRoot, "book.yaml");
    BookFile file = LoadBookFile(path);
    var definition = new BookDefinition
    {
      Root = canonicalRoot,
      Id = id,
      Namespace = bookNamespace,
      ViewPrefix = viewPrefix,
      File = file
    };

    var ids = new HashSet<string>(StringComparer.Ordinal);
    var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (BookInclusion inclusion in file.Books)
    {
      if (!InclusionId.IsMatch(inclusion.Id))
      {
        throw new InvalidDataException(
          $"'{path}' inclusion id '{inclusion.Id}' must begin with a letter and contain only letters, digits, _ or -");
      }
      if (!ids.Add(inclusion.Id))
      {
        throw new InvalidDataException($"'{path}' repeats inclusion id '{inclusion.Id}'");
      }
      string normalizedId = Normalize(inclusion.Id);
      if (normalizedId == InclusionWildcard)
      {
        throw new InvalidDataException(
          $"'{path}' inclusion id '{inclusion.Id}' is reserved: a formula writes it to mean every included book");
      }
      if (!normalizedIds.Add(normalizedId))
      {
        throw new InvalidDataException($"'{path}' has a normalized inclusion id collision at '{inclusion.Id}'");
      }
      if (inclusion.Path.Length == 0 || Path.IsPathRooted(inclusion.Path))
      {
        throw new InvalidDataException($"'{path}' inclusion '{inclusion.Id}' must use a relative path");
      }
      string childRoot = Path.GetFullPath(inclusion.Path, canonicalRoot);
      string relative = Path.GetRelativePath(canonicalRoot, childRoot);
      if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        Path.IsPathRooted(relative))
      {
        throw new InvalidDataException($"'{path}' inclusion '{inclusion.Id}' must stay inside the parent book");
      }
      if (!File.Exists(Path.Combine(childRoot, "book.yaml")))
      {
        throw new InvalidDataException($"Included book '{inclusion.Id}' at '{childRoot}' does not contain book.yaml");
      }

      string childNamespace = Qualify(bookNamespace, normalizedId);
      string childViewPrefix = viewPrefix.Length == 0 ? inclusion.Id : $"{viewPrefix}/{inclusion.Id}";
      definition.Children.Add(LoadDefinition(
        childRoot,
        inclusion.Id,
        childNamespace,
        childViewPrefix,
        new HashSet<string>(ancestors, PathComparer)));
    }
    return definition;
  }

  private static BookFile LoadBookFile(string path)
  {
    var yaml = new YamlStream();
    try
    {
      using var reader = new StringReader(File.ReadAllText(path));
      yaml.Load(reader);
    }
    catch (YamlException exception)
    {
      throw new InvalidDataException($"Invalid YAML in '{path}': {exception.Message}", exception);
    }
    if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
    {
      throw new InvalidDataException($"'{path}' must contain one mapping");
    }
    foreach (YamlNode key in root.Children.Keys)
    {
      if (key is not YamlScalarNode scalar || scalar.Value is null || !BookProperties.Contains(scalar.Value))
      {
        string property = (key as YamlScalarNode)?.Value ?? key.ToString();
        throw new InvalidDataException($"book.yaml contains unknown property: {property}");
      }
    }

    var file = new BookFile
    {
      Name = Scalar(root, "name"),
      ShortName = Scalar(root, "short_name"),
      Description = Scalar(root, "description"),
      Units = Scalar(root, "units"),
      Currency = Scalar(root, "currency"),
      FiscalYearEnd = Scalar(root, "fiscal_year_end")
    };
    if (file.Name.Length == 0)
    {
      throw new InvalidDataException($"'{path}' must define name");
    }
    if (file.ShortName.Length == 0)
    {
      throw new InvalidDataException($"'{path}' must define short_name");
    }
    if (root.Children.TryGetValue(new YamlScalarNode("identifiers"), out YamlNode? identifiersNode))
    {
      if (identifiersNode is not YamlMappingNode identifiers)
      {
        throw new InvalidDataException("book.yaml identifiers must be a mapping");
      }
      file.Identifiers = ParseIdentifiers(identifiers);
    }
    if (!root.Children.TryGetValue(new YamlScalarNode("navigation"), out YamlNode? navigationNode) ||
      navigationNode is not YamlSequenceNode navigation)
    {
      throw new InvalidDataException("book.yaml must define navigation as a list");
    }
    file.Navigation = ParseNavigation(navigation);
    if (root.Children.TryGetValue(new YamlScalarNode("books"), out YamlNode? booksNode))
    {
      if (booksNode is not YamlSequenceNode books)
      {
        throw new InvalidDataException("book.yaml books must be a list");
      }
      file.Books = ParseBooks(books);
    }
    return file;
  }

  private static List<NavigationItem> ParseNavigation(YamlSequenceNode sequence)
  {
    var navigation = new List<NavigationItem>();
    foreach (YamlNode node in sequence.Children)
    {
      if (node is YamlScalarNode scalar && scalar.Value is not null)
      {
        navigation.Add(new NavigationItem { View = scalar.Value });
        continue;
      }
      if (node is not YamlMappingNode mapping || mapping.Children.Count != 1)
      {
        throw new InvalidDataException(
          "Each navigation item must be a view id, one view id with nested children, or a book entry");
      }
      KeyValuePair<YamlNode, YamlNode> entry = mapping.Children.Single();
      if (entry.Key is not YamlScalarNode key || key.Value is null)
      {
        throw new InvalidDataException("A navigation mapping must have a scalar key");
      }
      if (key.Value == "book" && entry.Value is YamlScalarNode book && book.Value is not null)
      {
        navigation.Add(new NavigationItem { Book = book.Value });
        continue;
      }
      if (entry.Value is not YamlSequenceNode children)
      {
        throw new InvalidDataException("A nested navigation item must map one view id to a list of children");
      }
      navigation.Add(new NavigationItem { View = key.Value, Children = ParseNavigation(children) });
    }
    return navigation;
  }

  private static List<BookInclusion> ParseBooks(YamlSequenceNode sequence)
  {
    var books = new List<BookInclusion>();
    foreach (YamlNode node in sequence.Children)
    {
      if (node is not YamlMappingNode mapping)
      {
        throw new InvalidDataException("Each book inclusion must define id and path");
      }
      foreach (YamlNode key in mapping.Children.Keys)
      {
        if (key is not YamlScalarNode scalar || scalar.Value is not ("id" or "path"))
        {
          string property = (key as YamlScalarNode)?.Value ?? key.ToString();
          throw new InvalidDataException($"book inclusion contains unknown property: {property}");
        }
      }
      string id = Scalar(mapping, "id");
      string path = Scalar(mapping, "path");
      if (id.Length == 0 || path.Length == 0)
      {
        throw new InvalidDataException("Each book inclusion must define id and path");
      }
      books.Add(new BookInclusion { Id = id, Path = path });
    }
    return books;
  }

  private static Dictionary<string, string> ParseIdentifiers(YamlMappingNode mapping)
  {
    var identifiers = new Dictionary<string, string>();
    foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
    {
      if (keyNode is not YamlScalarNode key || key.Value is null || key.Value.Length == 0 ||
        valueNode is not YamlScalarNode value || value.Value is null || value.Value.Length == 0)
      {
        throw new InvalidDataException("book.yaml identifiers must map non-empty names to non-empty scalar values");
      }
      identifiers.Add(key.Value, value.Value);
    }
    return identifiers;
  }

  private static IEnumerable<BookDefinition> Flatten(BookDefinition definition)
  {
    yield return definition;
    foreach (BookDefinition child in definition.Children)
    {
      foreach (BookDefinition descendant in Flatten(child))
      {
        yield return descendant;
      }
    }
  }

  private static string? OutermostNamedAncestor(BookDefinition definition, string directory, string name)
  {
    string? match = null;
    var current = new DirectoryInfo(directory);
    while (true)
    {
      if (current.Name == name)
      {
        match = current.FullName;
      }
      if (PathComparer.Equals(current.FullName, definition.Root) || current.Parent is null)
      {
        return match;
      }
      current = current.Parent;
    }
  }

  private static IEnumerable<string> OwnedDirectories(BookDefinition definition)
  {
    var pending = new Stack<string>();
    pending.Push(definition.Root);
    while (pending.Count > 0)
    {
      string directory = pending.Pop();
      yield return directory;
      foreach (string child in Directory.GetDirectories(directory).OrderByDescending(path => path, PathComparer))
      {
        if (File.Exists(Path.Combine(child, "book.yaml")))
        {
          continue;
        }
        pending.Push(child);
      }
    }
  }

  private static string Scalar(YamlMappingNode mapping, string key)
  {
    return mapping.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) &&
      node is YamlScalarNode scalar
      ? scalar.Value ?? ""
      : "";
  }

  internal static StringComparer PathComparer => OperatingSystem.IsWindows()
    ? StringComparer.OrdinalIgnoreCase
    : StringComparer.Ordinal;


}