using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Libreta;

// Redirected output on Windows is encoded in the console's legacy code page, mangling symbols such as − and ÷ for
// whatever reads it. The console's own code page stays untouched, since changing it outlives the process.
if (OperatingSystem.IsWindows())
{
  if (Console.IsOutputRedirected)
  {
    Console.SetOut(Utf8Writer(Console.OpenStandardOutput()));
  }
  if (Console.IsErrorRedirected)
  {
    Console.SetError(Utf8Writer(Console.OpenStandardError()));
  }
}

CommandLineOptions options;
try
{
  options = CommandLineOptions.Parse(args);
}
catch (ArgumentException exception)
{
  Console.Error.WriteLine(exception.Message);
  CommandLineOptions.PrintUsage(Console.Error);
  return 1;
}

if (options.Help)
{
  CommandLineOptions.PrintUsage(Console.Out);
  return 0;
}

if (options.Documentation is not null)
{
  Documentation.Write(options.Documentation.Value, Console.Out);
  return 0;
}

string root;
if (options.Root is not null)
{
  root = Path.GetFullPath(options.Root);
  if (!File.Exists(Path.Combine(root, "book.yaml")))
  {
    Console.Error.WriteLine($"book root does not contain book.yaml: {root}");
    return 1;
  }
}
else
{
  string start = Directory.GetCurrentDirectory();
  List<string> found = BookDiscovery.Find(start);
  if (found.Count == 0)
  {
    Console.Error.WriteLine($"no book found under {start}; pass a book root.");
    CommandLineOptions.PrintUsage(Console.Error);
    return 1;
  }
  if (found.Count > 1)
  {
    Console.Error.WriteLine($"several books under {start}; pass the one you want:");
    foreach (string candidate in found)
    {
      Console.Error.WriteLine($"  {Path.GetRelativePath(start, candidate)}");
    }
    return 1;
  }
  root = found[0];
}

InstanceRegistry registry = InstanceRegistry.Default;
if (options.Command == HeadlessCommand.Url)
{
  ServerInstance? running = registry.Find(root);
  if (running is null)
  {
    Console.Error.WriteLine($"no server is running {root}.");
    return 1;
  }
  Console.WriteLine(running.Url);
  return 0;
}

if (options.Command == HeadlessCommand.Export)
{
  try
  {
    if (Path.GetExtension(options.Output!).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
    {
      // Built in memory so a book that fails to export leaves no half-written workbook behind.
      using var workbook = new MemoryStream();
      XlsxExport.Write(root, workbook);
      File.WriteAllBytes(options.Output!, workbook.ToArray());
    }
    else
    {
      File.WriteAllText(options.Output!, HtmlExport.Build(root, InterfaceFolder()));
    }
    Console.WriteLine($"  wrote {options.Output} ({new FileInfo(options.Output!).Length / 1024} KB)");
    return 0;
  }
  catch (Exception exception)
  {
    Console.Error.WriteLine(exception.Message);
    return 1;
  }
}

if (options.Command is not null)
{
  try
  {
    return HeadlessRunner.Run(root, options.Command.Value, options.Query, options.Json, options.Lines);
  }
  catch (Exception exception)
  {
    Console.Error.WriteLine(exception.Message);
    return 1;
  }
}

// dotnet watch restarts the process on edits it cannot apply in place, so only its first run opens a tab, and neither
// reuse nor idle shutdown may end the development server it is supervising.
bool watched = Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1";
bool firstRun = Environment.GetEnvironmentVariable("DOTNET_WATCH_ITERATION") is null or "1";

if (!watched && registry.Find(root) is ServerInstance existing)
{
  if (options.PortSpecified && existing.Port != options.Port)
  {
    Console.Error.WriteLine($"{root} is already served at {existing.Url}.");
    return 1;
  }
  if (options.Cell is (string line, string column))
  {
    return await SelectCell(existing, root, line, column, options);
  }
  Console.WriteLine($"  already serving {root} at {existing.Url}");
  if (!options.NoOpen)
  {
    Browser.Open(existing.Url);
  }
  return 0;
}

string query = "";
if (options.Cell is (string cellLine, string cellColumn))
{
  try
  {
    query = CellLink.Query(root, cellLine, cellColumn);
  }
  catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
  {
    Console.Error.WriteLine(exception.Message);
    return 1;
  }
}

string assets = InterfaceFolder();
using var store = new BookStore(root, assets);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
  ContentRootPath = AppContext.BaseDirectory,
  WebRootPath = assets
});
builder.Logging.ClearProviders();
int port;
try
{
  port = ChoosePort(options.Port, options.PortSpecified);
}
catch (IOException exception)
{
  Console.Error.WriteLine(exception.Message);
  return 1;
}
builder.WebHost.UseUrls($"http://localhost:{port}");
WebApplication app = builder.Build();
using var viewers = new Viewers(TimeSpan.FromSeconds(10), () =>
{
  Console.WriteLine($"  stopped   {DateTime.Now:HH:mm:ss}  no page open");
  app.Lifetime.StopApplication();
});
if (options.NoOpen && !watched)
{
  viewers.Expect();
}
var pages = new Pages();

app.UseDefaultFiles();
// The page is served from source, and without a Cache-Control a browser reuses a script it judges fresh, running an
// edited file's old version.
app.UseStaticFiles(new StaticFileOptions
{
  OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache"
});

JsonSerializerOptions jsonOptions = PayloadJson.Options;
app.MapGet("/api/catalog", () => Results.Text(
  JsonSerializer.Serialize(store.Catalog, jsonOptions), "application/json"));
app.MapGet("/api/view", (string id) => store.TryGetView(id, out ViewPayload? payload)
  ? Results.Text(JsonSerializer.Serialize(payload, jsonOptions), "application/json")
  : Results.NotFound());
app.MapGet("/api/source-image", (string path) =>
  store.TryGetSourceImage(path, out string? image, out string? contentType)
    ? Results.File(image!, contentType, enableRangeProcessing: true)
    : Results.NotFound());

// Built from the book on disk on each request, like the command line's export, so it is never staler than the page.
app.MapGet("/api/export/{format}", (string format) =>
{
  string name = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(root))}.{format}";
  try
  {
    if (format == "xlsx")
    {
      using var workbook = new MemoryStream();
      XlsxExport.Write(root, workbook);
      return Results.File(workbook.ToArray(), XlsxExport.ContentType, name);
    }
    if (format == "html")
    {
      return Results.File(Encoding.UTF8.GetBytes(HtmlExport.Build(root, assets)), "text/html; charset=utf-8", name);
    }
    return Results.NotFound();
  }
  catch (Exception exception)
  {
    // The page shows the message, as the command line does, so a broken book says what to fix.
    return Results.Text(exception.Message, "text/plain", statusCode: StatusCodes.Status500InternalServerError);
  }
});

// A tool tracing a number asks here first, so an open page selects it, and only a book with no page open gets a tab.
app.MapPost("/api/cell", (string line, string column) =>
{
  if (store.Book is not Book book)
  {
    return Results.Text(store.Catalog.Error ?? "the book has not loaded", "text/plain",
      statusCode: StatusCodes.Status503ServiceUnavailable);
  }
  try
  {
    RequestedCell cell = CellLink.Resolve(book, line, column);
    int open = pages.Count;
    if (open > 0)
    {
      pages.Request(cell);
    }
    return Results.Json(new CellAnswer(cell.Query, open), jsonOptions);
  }
  catch (ArgumentException exception)
  {
    return Results.Text(exception.Message, "text/plain", statusCode: StatusCodes.Status404NotFound);
  }
});

// The page holds this open, re-fetches whenever the book changes on disk, and selects each cell a tool requests.
app.MapGet("/api/events", async (HttpContext context, CancellationToken cancellation) =>
{
  context.Response.Headers.ContentType = "text/event-stream";
  context.Response.Headers.CacheControl = "no-cache";
  using IDisposable? viewer = watched ? null : viewers.Connect();
  using IDisposable page = pages.Connect();
  long seen = store.Version;
  long seenCell = pages.Version;
  long seenAssets = store.AssetVersion;
  await context.Response.WriteAsync($"data: {seen}\n\n", cancellation);
  await context.Response.Body.FlushAsync(cancellation);
  while (!cancellation.IsCancellationRequested)
  {
    if (store.AssetVersion != seenAssets)
    {
      seenAssets = store.AssetVersion;
      await context.Response.WriteAsync($"event: assets\ndata: {seenAssets}\n\n", cancellation);
      await context.Response.Body.FlushAsync(cancellation);
    }
    if (pages.Since(ref seenCell) is RequestedCell cell)
    {
      await context.Response.WriteAsync($"event: cell\ndata: {JsonSerializer.Serialize(cell, jsonOptions)}\n\n",
        cancellation);
      await context.Response.Body.FlushAsync(cancellation);
    }
    if (store.Version != seen)
    {
      seen = store.Version;
      await context.Response.WriteAsync($"data: {seen}\n\n", cancellation);
      await context.Response.Body.FlushAsync(cancellation);
    }
    await Task.Delay(200, cancellation);
  }
});

Console.WriteLine($"  {root}");
Console.WriteLine($"  interface {assets}");
Console.WriteLine(
  $"  {store.Catalog.Views.Count} views, watching for changes, serving http://localhost:{port}");
await app.StartAsync();
var instance = new ServerInstance(Environment.ProcessId, root, port);
registry.Register(instance);
try
{
  if (options.Cell is (string line, string column))
  {
    ShowCell(instance.Url + query, 0, line, column, firstRun && !options.NoOpen, options.Json);
  }
  else if (firstRun && !options.NoOpen)
  {
    Browser.Open(instance.Url);
  }
  await app.WaitForShutdownAsync();
}
finally
{
  registry.Unregister(instance);
}
return 0;

static TextWriter Utf8Writer(Stream stream)
{
  return TextWriter.Synchronized(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
}

/// <summary>Has the server's open page select a cell, or opens a tab at it when none is open. A server older than
/// the request gets a tab regardless.</summary>
static async Task<int> SelectCell(ServerInstance server, string root, string line, string column,
  CommandLineOptions options)
{
  using var client = new HttpClient();
  string url = $"{server.Url}/api/cell?line={Uri.EscapeDataString(line)}&column={Uri.EscapeDataString(column)}";
  using HttpResponseMessage response = await client.PostAsync(url, null);
  string body = await response.Content.ReadAsStringAsync();
  if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed && body.Length == 0)
  {
    try
    {
      ShowCell(server.Url + CellLink.Query(root, line, column), 0, line, column, !options.NoOpen, options.Json);
      return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }
  if (!response.IsSuccessStatusCode)
  {
    Console.Error.WriteLine(body);
    return 1;
  }
  CellAnswer answer = JsonSerializer.Deserialize<CellAnswer>(body, PayloadJson.Options)!;
  ShowCell(server.Url + answer.Query, answer.Pages, line, column, !options.NoOpen, options.Json);
  return 0;
}

/// <summary>Opens a tab at the cell when no open page selected it, unless the caller opens the page itself, and says
/// where the cell is; with --json, as one line a tool reads.</summary>
static void ShowCell(string url, int pages, string line, string column, bool open, bool json)
{
  if (json)
  {
    var terminal = new JsonSerializerOptions(PayloadJson.Options)
    {
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    Console.WriteLine(JsonSerializer.Serialize(new CellPage(url, pages), terminal));
  }
  else if (pages > 0)
  {
    Console.WriteLine($"  selected {line} in {column} on the page at {url}");
  }
  if (pages == 0 && open)
  {
    Browser.Open(url);
  }
}

/// <summary>An explicit port fails if taken; a default steps upward. Releasing the probe before Kestrel binds is a
/// race, acceptable for one local user.</summary>
static int ChoosePort(int requested, bool specified)
{
  for (int candidate = requested; candidate <= 65535; candidate++)
  {
    try
    {
      // Kestrel binds "localhost" on both loopbacks, so a port half-taken is still unusable.
      using var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
      using var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
      v4.Bind(new IPEndPoint(IPAddress.Loopback, candidate));
      v6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, candidate));
      return candidate;
    }
    catch (SocketException)
    {
      if (specified)
      {
        throw new IOException($"port {candidate} is already in use; another instance may be running.");
      }
    }
  }
  throw new IOException($"no free port at or above {requested}.");
}

/// <summary>Serves wwwroot from the source folder the build recorded, so page edits need no rebuild, falling back to
/// the published copy.</summary>
static string InterfaceFolder()
{
  string? sourceRoot = Assembly.GetEntryAssembly()?
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(attribute => attribute.Key == "SourceRoot")?.Value;

  if (sourceRoot is not null && Directory.Exists(Path.Combine(sourceRoot, "wwwroot")))
  {
    return Path.Combine(sourceRoot, "wwwroot");
  }

  for (DirectoryInfo? directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    directory is not null;
    directory = directory.Parent)
  {
    string candidate = Path.Combine(directory.FullName, "src", "Libreta", "wwwroot");
    if (Directory.Exists(candidate))
    {
      return candidate;
    }
  }
  return Path.Combine(AppContext.BaseDirectory, "wwwroot");
}

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
  private BookSnapshot snapshot = new BookSnapshot(
    new ViewCatalog("", "", new List<ViewCatalogEntry>(), 0),
    new Dictionary<string, ViewPayload>(),
    new HashSet<string>(),
    null);
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

  /// <summary>A view of the book's, or a line no table shows, built on request since only a link reaches one.
  /// </summary>
  public bool TryGetView(string id, out ViewPayload? payload)
  {
    BookSnapshot current = Volatile.Read(ref snapshot);
    if (current.Views.TryGetValue(id, out payload))
    {
      return true;
    }
    string line = id.StartsWith(View.LinePrefix, StringComparison.Ordinal) ? id[View.LinePrefix.Length..] : "";
    if (current.Book is not Book book || !(book.Formulas.ContainsKey(line) || book.Facts.ContainsKey(line)))
    {
      return false;
    }
    long built = current.Catalog.Version;
    try
    {
      View view = View.ForLine(book, line);
      payload = PayloadBuilder.Build(view, new Engine(view), built);
    }
    catch (Exception exception)
    {
      payload = new ViewPayload { Title = line, Error = exception.Message, Version = built };
    }
    return true;
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
  private FileSystemWatcher? TryWatch(string directory, bool recursive)
  {
    try
    {
      return CreateWatcher(directory, "*", recursive, OnChanged);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException)
    {
      return null;
    }
  }

  private void RefreshBookWatchers()
  {
    IEnumerable<string> directories;
    bool recursive;
    try
    {
      directories = BookStructure.Load(root).OwnedDirectories().ToList();
      recursive = false;
    }
    catch
    {
      directories = new[] { root };
      recursive = true;
    }

    List<FileSystemWatcher> replacements = directories
      .Select(directory => TryWatch(directory, recursive))
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

  private static bool AffectsBook(FileSystemEventArgs eventArgs)
  {
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
      var views = new Dictionary<string, ViewPayload>();
      foreach ((string id, View view) in book.Views)
      {
        try
        {
          views[id] = PayloadBuilder.Build(view, new Engine(view), next);
        }
        catch (Exception exception)
        {
          string title = catalog.Views.FirstOrDefault(entry => entry.Id == id)?.Title ?? id;
          views[id] = new ViewPayload { Title = title, Error = exception.Message, Version = next };
          Console.WriteLine($"  error     {DateTime.Now:HH:mm:ss}  {id}: {exception.Message}");
        }
      }
      HashSet<string> sourceImages = SourceImages(book);
      Volatile.Write(ref snapshot, new BookSnapshot(catalog, views, sourceImages, book));
      Interlocked.Exchange(ref version, next);
      Console.WriteLine($"  reloaded  {DateTime.Now:HH:mm:ss}  {views.Count} views");
    }
  }

  private record BookSnapshot(
    ViewCatalog Catalog,
    IReadOnlyDictionary<string, ViewPayload> Views,
    IReadOnlySet<string> SourceImages,
    Book? Book);


}