using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;

namespace Libreta;

/// <summary>Where a book's files are read from: the disk, or a commit's tree. Paths are the working tree's either way,
/// so a book read at a commit names its files as the working tree does.</summary>
public interface IBookFiles
{
  bool FileExists(string path);
  bool DirectoryExists(string path);
  byte[] ReadAllBytes(string path);

  string ReadAllText(string path)
  {
    using var reader = new StreamReader(new MemoryStream(ReadAllBytes(path)), Encoding.UTF8);
    return reader.ReadToEnd();
  }

  /// <summary>The files directly in a folder whose names match a pattern such as <c>*.csv</c>.</summary>
  IEnumerable<string> Files(string folder, string pattern);
  IEnumerable<string> Directories(string folder);
}

public sealed class DiskFiles : IBookFiles
{
  public static readonly DiskFiles Instance = new DiskFiles();

  public bool FileExists(string path)
  {
    return File.Exists(path);
  }

  public bool DirectoryExists(string path)
  {
    return Directory.Exists(path);
  }

  public byte[] ReadAllBytes(string path)
  {
    return File.ReadAllBytes(path);
  }

  public string ReadAllText(string path)
  {
    return File.ReadAllText(path);
  }

  public IEnumerable<string> Files(string folder, string pattern)
  {
    return Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
  }

  public IEnumerable<string> Directories(string folder)
  {
    return Directory.GetDirectories(folder);
  }


}

/// <summary>A commit's tree, listed once and read through one <c>git cat-file</c>, so a book reads as the commit left
/// it without being checked out, even where the working tree has since moved or deleted it.</summary>
public sealed class GitFiles : IBookFiles, IDisposable
{
  private readonly Dictionary<string, string> blobs;
  private readonly Dictionary<string, List<string>> files;
  private readonly Dictionary<string, List<string>> folders;
  private readonly HashSet<string> known;
  private readonly string marker;
  private readonly Process reader;
  private readonly object gate = new object();

  private GitFiles(string top, string commit, Dictionary<string, string> blobs)
  {
    this.blobs = blobs;
    files = new Dictionary<string, List<string>>(BookStructure.PathComparer);
    folders = new Dictionary<string, List<string>>(BookStructure.PathComparer);
    known = new HashSet<string>(BookStructure.PathComparer) { top };
    foreach (string path in blobs.Keys)
    {
      string folder = Path.GetDirectoryName(path)!;
      Add(files, folder, path);
      for (; known.Add(folder); folder = Path.GetDirectoryName(folder)!)
      {
        Add(folders, Path.GetDirectoryName(folder)!, folder);
      }
    }
    // Formats stop inheriting at the repository's root, which on disk is where .git is.
    marker = Path.Combine(top, ".git");
    Commit = commit;
    ProcessStartInfo start = Start(top, "cat-file", "--batch");
    start.RedirectStandardError = false;
    reader = Process.Start(start)!;
  }

  public string Commit { get; }

  /// <summary>The tree of the commit a revision names, in the repository holding <paramref name="root"/>, which need
  /// not exist in the working tree.</summary>
  public static GitFiles Open(string root, string revision)
  {
    root = Path.GetFullPath(root);
    string existing = root;
    while (!Directory.Exists(existing))
    {
      existing = Path.GetDirectoryName(existing)
        ?? throw new InvalidDataException($"{root} is not in a git repository");
    }
    (int exit, string prefix, _) = Run(existing, "rev-parse", "--show-prefix");
    if (exit != 0)
    {
      throw new InvalidDataException($"{root} is not in a git repository");
    }
    // The top as the root spells it: git answers with symlinks resolved, such as macOS's /private/var for /var.
    string top = existing;
    foreach (string _ in prefix.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
      top = Path.GetDirectoryName(top)!;
    }
    (exit, string commit, _) = Run(top, "rev-parse", "--verify", "--quiet", "--end-of-options",
      $"{revision}^{{commit}}");
    if (exit != 0)
    {
      throw new InvalidDataException($"no revision '{revision}'");
    }
    commit = commit.Trim();
    (exit, string tree, string error) = Run(top, "ls-tree", "-r", "-z", "--full-tree", commit);
    if (exit != 0)
    {
      throw new InvalidDataException($"git ls-tree failed: {error.Trim()}");
    }
    var blobs = new Dictionary<string, string>(BookStructure.PathComparer);
    foreach (string entry in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
    {
      int tab = entry.IndexOf('\t');
      string[] fields = entry[..tab].Split(' ');
      if (fields[1] == "blob")
      {
        blobs[Path.Combine(top, entry[(tab + 1)..].Replace('/', Path.DirectorySeparatorChar))] = fields[2];
      }
    }
    return new GitFiles(top, commit, blobs);
  }

  public bool FileExists(string path)
  {
    path = Path.GetFullPath(path);
    return blobs.ContainsKey(path) || BookStructure.PathComparer.Equals(path, marker);
  }

  public bool DirectoryExists(string path)
  {
    return known.Contains(Path.GetFullPath(path));
  }

  public byte[] ReadAllBytes(string path)
  {
    if (!blobs.TryGetValue(Path.GetFullPath(path), out string? blob))
    {
      throw new FileNotFoundException($"Could not find file '{path}' at {Commit[..7]}.", path);
    }
    lock (gate)
    {
      reader.StandardInput.Write($"{blob}\n");
      reader.StandardInput.Flush();
      Stream output = reader.StandardOutput.BaseStream;
      string[] header = HeaderLine(output).Split(' ');
      if (header.Length != 3 || header[1] != "blob")
      {
        throw new InvalidDataException($"git cat-file couldn't read {path} at {Commit[..7]}");
      }
      byte[] content = new byte[int.Parse(header[2], System.Globalization.CultureInfo.InvariantCulture)];
      output.ReadExactly(content);
      output.ReadExactly(new byte[1]);
      return content;
    }
  }

  public IEnumerable<string> Files(string folder, string pattern)
  {
    return files.GetValueOrDefault(Path.GetFullPath(folder), [])
      .Where(path => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path)));
  }

  public IEnumerable<string> Directories(string folder)
  {
    return folders.GetValueOrDefault(Path.GetFullPath(folder), []);
  }

  public void Dispose()
  {
    reader.StandardInput.Close();
    if (!reader.WaitForExit(TimeSpan.FromSeconds(5)))
    {
      reader.Kill();
    }
    reader.Dispose();
  }

  private static void Add(Dictionary<string, List<string>> index, string key, string value)
  {
    if (!index.TryGetValue(key, out List<string>? list))
    {
      index[key] = list = [];
    }
    list.Add(value);
  }

  private static string HeaderLine(Stream stream)
  {
    var line = new List<byte>();
    for (int next = stream.ReadByte(); next != '\n'; next = stream.ReadByte())
    {
      if (next < 0)
      {
        throw new InvalidDataException("git cat-file stopped answering");
      }
      line.Add((byte)next);
    }
    return Encoding.UTF8.GetString(line.ToArray());
  }

  private static ProcessStartInfo Start(string folder, params string[] arguments)
  {
    var start = new ProcessStartInfo("git")
    {
      WorkingDirectory = folder,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (string argument in arguments)
    {
      start.ArgumentList.Add(argument);
    }
    return start;
  }

  private static (int Exit, string Output, string Error) Run(string folder, params string[] arguments)
  {
    ProcessStartInfo start = Start(folder, arguments);
    start.RedirectStandardInput = false;
    Process process;
    try
    {
      process = Process.Start(start)!;
    }
    catch (System.ComponentModel.Win32Exception)
    {
      throw new InvalidDataException("reading a book at a revision needs git on the PATH");
    }
    using (process)
    {
      Task<string> error = process.StandardError.ReadToEndAsync();
      string output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return (process.ExitCode, output, error.Result);
    }
  }


}