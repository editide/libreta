using System.Collections.Concurrent;
using System.Text.Json;

namespace Libreta;

/// <summary>Holds the book's payloads and rebuilds them when its files change on disk.</summary>
public class BookStore : IDisposable
{
  private const int ReloadDelayMilliseconds = 120;
  private readonly string root;
  private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
  private readonly List<FileSystemWatcher> bookWatchers = new List<FileSystemWatcher>();
  private readonly object gate = new object();
  private readonly object rebuildGate = new object();
  private readonly Timer reloadTimer;
  private HashSet<string>? ownedDirectories;
  private List<string> watchedRoots = [];
  private BookSnapshot snapshot = new BookSnapshot(
    new ViewCatalog("", "", new List<ViewCatalogEntry>(), 0),
    new ConcurrentDictionary<string, Lazy<CachedView>>(StringComparer.Ordinal),
    new HashSet<string>(),
    null,
    0);
  private long version;
  private long assetVersion;
  private bool disposed;

  public BookStore(string root, string assets)
  {
    this.root = root;
    reloadTimer = new Timer(_ => Rebuild(), null, Timeout.Infinite, Timeout.Infinite);
    RefreshBookWatchers();
    Rebuild();

    Watch(assets, "*", false, OnAssetChanged);

    // Inherited formats usually sit above the book root, so they need watching where they actually are.
    foreach (string formats in Formatter.LocateAll(root, root))
    {
      string relative = Path.GetRelativePath(root, formats);
      if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}"))
      {
        Watch(Path.GetDirectoryName(formats)!, "formats.yaml", false);
      }
    }
  }

  public ViewCatalog Catalog => Volatile.Read(ref snapshot).Catalog;

  /// <summary>The last book that loaded, kept through a load that fails.</summary>
  public Book? Book => Volatile.Read(ref snapshot).Book;

  /// <summary>A view of the book's, or a line no table shows, built on first request and cached for this version.
  /// </summary>
  public bool TryGetView(string id, out ViewPayload? payload)
  {
    if (TryGetCachedView(id, out CachedView? cached))
    {
      payload = cached!.Payload;
      return true;
    }
    payload = null;
    return false;
  }

  /// <summary>The cached JSON for a view, built once per book version.</summary>
  public bool TryGetViewJson(string id, out string? json)
  {
    if (TryGetCachedView(id, out CachedView? cached))
    {
      json = cached!.Json;
      return true;
    }
    json = null;
    return false;
  }

  private bool TryGetCachedView(string id, out CachedView? cached)
  {
    BookSnapshot current = Volatile.Read(ref snapshot);
    if (current.Book is not Book book)
    {
      cached = null;
      return false;
    }
    if (book.Views.TryGetValue(id, out View? view))
    {
      cached = current.Views.GetOrAdd(id,
        _ => new Lazy<CachedView>(() => BuildView(current, id, view), LazyThreadSafetyMode.ExecutionAndPublication))
        .Value;
      return true;
    }
    string line = id.StartsWith(View.LinePrefix, StringComparison.Ordinal) ? id[View.LinePrefix.Length..] : "";
    if (line.Length == 0 || !(book.Formulas.ContainsKey(line) || book.Facts.ContainsKey(line)))
    {
      cached = null;
      return false;
    }
    cached = current.Views.GetOrAdd(id,
      _ => new Lazy<CachedView>(() => BuildLineView(current, line), LazyThreadSafetyMode.ExecutionAndPublication))
      .Value;
    return true;
  }

  private static CachedView BuildView(BookSnapshot current, string id, View view)
  {
    ViewPayload payload;
    try
    {
      payload = PayloadBuilder.Build(view, new Engine(view), current.BookVersion);
    }
    catch (Exception exception)
    {
      string title = current.Catalog.Views.FirstOrDefault(entry => entry.Id == id)?.Title ?? id;
      payload = new ViewPayload { Title = title, Error = exception.Message, Version = current.BookVersion };
      Console.WriteLine($"  error     {DateTime.Now:HH:mm:ss}  {id}: {exception.Message}");
    }
    return new CachedView(payload, JsonSerializer.Serialize(payload, PayloadJson.Options));
  }

  private static CachedView BuildLineView(BookSnapshot current, string line)
  {
    ViewPayload payload;
    try
    {
      View view = View.ForLine(current.Book!, line);
      payload = PayloadBuilder.Build(view, new Engine(view), current.BookVersion);
    }
    catch (Exception exception)
    {
      payload = new ViewPayload { Title = line, Error = exception.Message, Version = current.BookVersion };
    }
    return new CachedView(payload, JsonSerializer.Serialize(payload, PayloadJson.Options));
  }

  public bool TryGetSourceImage(string image, out string? path, out string? contentType)
  {
    BookSnapshot current = Volatile.Read(ref snapshot);
    if (!current.SourceImages.Contains(image))
    {
      path = null;
      contentType = null;
      return false;
    }

    path = Path.Combine(root, image.Replace('/', Path.DirectorySeparatorChar));
    contentType = ImageContentType(path);
    return contentType is not null && File.Exists(path);
  }

  public static string? ImageContentType(string path)
  {
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
      ".png" => "image/png",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".webp" => "image/webp",
      _ => null
    };
  }

  /// <summary>Images the book's facts cite, relative to the root: the only files the page may request.</summary>
  public static HashSet<string> SourceImages(Book book)
  {
    return book.Facts.Values
      .SelectMany(fact => fact.Cells.Values)
      .Select(cell => cell.SourceImage)
      .Where(image => image.Length > 0)
      .ToHashSet(StringComparer.Ordinal);
  }

  public long Version => Interlocked.Read(ref version);

  /// <summary>Bumped when the page's own files change; the browser reloads itself in response.</summary>
  public long AssetVersion => Interlocked.Read(ref assetVersion);

  public void Dispose()
  {
    lock (gate)
    {
      disposed = true;
      foreach (FileSystemWatcher watcher in watchers)
      {
        watcher.Dispose();
      }
      watchers.Clear();
      foreach (FileSystemWatcher watcher in bookWatchers)
      {
        watcher.Dispose();
      }
      bookWatchers.Clear();
      reloadTimer.Dispose();
    }
    // Dispose can race a callback that already left the timer queue.
    lock (rebuildGate)
    {
      GC.SuppressFinalize(this);
    }
  }

  private void Watch(string path, string filter, bool recursive, FileSystemEventHandler? handler = null)
  {
    handler ??= OnChanged;
    FileSystemWatcher watcher = CreateWatcher(path, filter, recursive, handler);
    watchers.Add(watcher);
  }

  private FileSystemWatcher CreateWatcher(
    string path,
    string filter,
    bool recursive,
    FileSystemEventHandler handler)
  {
    var watcher = new FileSystemWatcher(path, filter)
    {
      IncludeSubdirectories = recursive,
      NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName |
        NotifyFilters.Size,
      EnableRaisingEvents = true
    };
    watcher.Changed += handler;
    watcher.Created += handler;
    watcher.Deleted += handler;
    watcher.Renamed += (sender, eventArgs) => handler(sender, eventArgs);
    watcher.Error += (_, _) => ScheduleReload();
    return watcher;
  }

  // A folder can vanish before its watcher opens. Skip it: a reload is coming, and throwing on this timer thread would
  // take the server down.
  private FileSystemWatcher? TryWatch(string directory)
  {
    try
    {
      return CreateWatcher(directory, "*", true, OnChanged);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException)
    {
      if (Directory.Exists(directory))
      {
        Console.WriteLine($"  error     {DateTime.Now:HH:mm:ss}  cannot watch {directory}: {exception.Message}");
      }
      return null;
    }
  }

  // Each FileSystemWatcher costs an inotify instance on Linux, of which a user gets 128 by default, so the book is
  // watched recursively from its outermost folders and events from folders it doesn't own, such as an excluded child
  // book's, are filtered out instead.
  private void RefreshBookWatchers()
  {
    HashSet<string>? owned;
    try
    {
      owned = BookStructure.Load(root).OwnedDirectories()
        .Select(Path.TrimEndingDirectorySeparator)
        .ToHashSet(BookStructure.PathComparer);
    }
    catch
    {
      owned = null;
    }
    List<string> roots = owned is null
      ? [root]
      : owned.Where(directory => Path.GetDirectoryName(directory) is not string parent || !owned.Contains(parent))
        .Order(BookStructure.PathComparer)
        .ToList();
    Volatile.Write(ref ownedDirectories, owned);
    if (roots.SequenceEqual(watchedRoots, BookStructure.PathComparer))
    {
      return;
    }

    List<FileSystemWatcher> replacements = roots
      .Select(TryWatch)
      .OfType<FileSystemWatcher>()
      .ToList();
    lock (gate)
    {
      if (disposed)
      {
        foreach (FileSystemWatcher watcher in replacements)
        {
          watcher.Dispose();
        }
        return;
      }
      foreach (FileSystemWatcher watcher in bookWatchers)
      {
        watcher.Dispose();
      }
      bookWatchers.Clear();
      bookWatchers.AddRange(replacements);
      watchedRoots = replacements.Count == roots.Count ? roots : [];
    }
  }

  private void OnAssetChanged(object sender, FileSystemEventArgs eventArgs)
  {
    Interlocked.Increment(ref assetVersion);
  }

  private void OnChanged(object sender, FileSystemEventArgs eventArgs)
  {
    if (!AffectsBook(eventArgs))
    {
      return;
    }
    ScheduleReload();
  }

  private bool AffectsBook(FileSystemEventArgs eventArgs)
  {
    if (Volatile.Read(ref ownedDirectories) is HashSet<string> owned &&
      !InOwnedDirectory(owned, eventArgs.FullPath) &&
      !(eventArgs is RenamedEventArgs moved && InOwnedDirectory(owned, moved.OldFullPath)))
    {
      return false;
    }
    if (IsBookFile(eventArgs.FullPath))
    {
      return true;
    }
    if (eventArgs is RenamedEventArgs renamed && IsBookFile(renamed.OldFullPath))
    {
      return true;
    }
    return Directory.Exists(eventArgs.FullPath) ||
      eventArgs.ChangeType is WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed;
  }

  private static bool InOwnedDirectory(HashSet<string> owned, string path)
  {
    return Path.GetDirectoryName(path) is string parent && owned.Contains(parent);
  }

  private static bool IsBookFile(string path)
  {
    return Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".csv" or ".png" or ".jpg" or ".jpeg" or ".webp";
  }

  private void ScheduleReload()
  {
    lock (gate)
    {
      if (disposed)
      {
        return;
      }
      reloadTimer.Change(ReloadDelayMilliseconds, Timeout.Infinite);
    }
  }

  private void Rebuild()
  {
    lock (rebuildGate)
    {
      lock (gate)
      {
        if (disposed)
        {
          return;
        }
      }
      RefreshBookWatchers();
      long next = Version + 1;
      Book book;
      ViewCatalog catalog;
      try
      {
        book = Book.Load(root);
        catalog = CatalogLoader.Load(book, next);
      }
      catch (Exception exception)
      {
        // Keep the last good navigation so the page can show the error without losing its place.
        BookSnapshot previous = Volatile.Read(ref snapshot);
        Volatile.Write(ref snapshot, previous with
        {
          Catalog = previous.Catalog with { Version = next, Error = exception.Message }
        });
        Interlocked.Exchange(ref version, next);
        Console.WriteLine($"  error     {DateTime.Now:HH:mm:ss}  book: {exception.Message}");
        return;
      }
      HashSet<string> sourceImages = SourceImages(book);
      Volatile.Write(ref snapshot, new BookSnapshot(
        catalog,
        new ConcurrentDictionary<string, Lazy<CachedView>>(StringComparer.Ordinal),
        sourceImages,
        book,
        next));
      Interlocked.Exchange(ref version, next);
      Console.WriteLine($"  reloaded  {DateTime.Now:HH:mm:ss}  {book.Views.Count} views");
    }
  }

  private record CachedView(ViewPayload Payload, string Json);

  private record BookSnapshot(
    ViewCatalog Catalog,
    ConcurrentDictionary<string, Lazy<CachedView>> Views,
    IReadOnlySet<string> SourceImages,
    Book? Book,
    long BookVersion);


}
