using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Libreta;

/// <summary>Keeps each picture's encoding on disk by a hash of its bytes and of the build that encoded it, so exporting
/// again encodes only the pictures that changed, and a build whose code changed encodes afresh. An entry unused for a
/// month is deleted.</summary>
public class PictureCache(string folder, Guid? build = null)
{
  private static readonly TimeSpan Unused = TimeSpan.FromDays(30);
  // A deterministic build's module version changes exactly when its code does.
  private readonly Guid build = build ?? typeof(PictureCache).Module.ModuleVersionId;
  private int pruned;

  /// <summary>The user's own cache folder, since what it holds comes from their books. Linux shares /tmp between
  /// users, so there it is the user's cache folder; elsewhere the temporary folder is the user's own.</summary>
  public static PictureCache User { get; } = new PictureCache(OperatingSystem.IsLinux()
    ? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache
      ? cache
      : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "libreta", "pictures")
    : Path.Combine(Path.GetTempPath(), "libreta", "pictures"));

  /// <summary>What <paramref name="encode"/> returns for <paramref name="picture"/>, null included, from the cache when
  /// it holds the answer. <paramref name="encoding"/> tells apart the encodings one build makes of the same picture.
  /// </summary>
  public byte[]? Get(byte[] picture, string encoding, Func<byte[]?> encode)
  {
    Prepare();
    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
    {
      hash.AppendData(build.ToByteArray());
      hash.AppendData(Encoding.UTF8.GetBytes(encoding + "\n"));
      hash.AppendData(picture);
      string entry = Path.Combine(folder, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
      // A file holds an encoding; an empty one records that there is none.
      try
      {
        byte[] cached = File.ReadAllBytes(entry);
        File.SetLastWriteTimeUtc(entry, DateTime.UtcNow);
        return cached.Length == 0 ? null : cached;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        // Not cached yet, or unreadable: encoded below.
      }
      byte[]? encoded = encode();
      Store(entry, encoded ?? []);
      return encoded;
    }
  }

  private void Prepare()
  {
    if (Interlocked.Exchange(ref pruned, 1) == 1)
    {
      return;
    }
    try
    {
      if (OperatingSystem.IsLinux())
      {
        Restrict(folder);
      }
      else
      {
        Directory.CreateDirectory(folder);
      }
      DateTime cutoff = DateTime.UtcNow - Unused;
      foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles())
      {
        if (file.LastWriteTimeUtc < cutoff)
        {
          file.Delete();
        }
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      // A cache that can't be pruned still answers; the next export tries again.
    }
  }

  /// <summary>Writes beside the entry and moves it into place, so an export running at the same time never reads half
  /// of one. A cache that can't be written costs only the next export's time.</summary>
  private static void Store(string entry, byte[] encoded)
  {
    string partial = $"{entry}.{Guid.NewGuid():N}.partial";
    try
    {
      File.WriteAllBytes(partial, encoded);
      File.Move(partial, entry, overwrite: true);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      File.Delete(partial);
    }
  }

  /// <summary>Makes each folder down to <paramref name="folder"/> under the cache root for its user alone.</summary>
  [UnsupportedOSPlatform("windows")]
  private static void Restrict(string folder)
  {
    const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    string libreta = Path.GetDirectoryName(folder)!;
    foreach (string path in new[] { libreta, folder })
    {
      if (!Directory.Exists(path))
      {
        Directory.CreateDirectory(path, owner);
      }
      else if (File.GetUnixFileMode(path) != owner)
      {
        File.SetUnixFileMode(path, owner);
      }
    }
  }


}