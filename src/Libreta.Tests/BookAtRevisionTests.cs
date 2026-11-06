using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Libreta.Tests;

[Collection(ConsoleCollection.Name)]
public class BookAtRevisionTests
{
  [Fact]
  public void ReadsTheBookAsTheCommitLeftIt()
  {
    // Arrange
    using var repository = new Repository();
    repository.Write("formats.yaml", "formats:\n  millions:\n    decimals: 2\n");
    repository.WriteBook("books/acme", "revenue,100");
    repository.Commit();
    repository.Write("books/acme/facts/facts.csv", "line_item,2025\nrevenue,200");
    repository.Write("books/acme/facts/later.csv", "line_item,2025\ncost,5");
    repository.Write("books/acme/facts/later.yaml", "table:\n  title: Later\nline_items:\n  cost:\n    label: Cost\n");
    File.Delete(repository.PathTo("formats.yaml"));

    // Act
    using var files = GitFiles.Open(repository.PathTo("books/acme"), "HEAD");
    Book book = Book.Load(repository.PathTo("books/acme"), files);
    View view = View.ForLine(book, "revenue");

    // Assert
    book.Root.ShouldBe(repository.PathTo("books/acme"));
    book.Facts["revenue"].Cells["2025"].Value.ShouldBe(100);
    book.Facts.ShouldNotContainKey("cost");
    view.Formatter.Format("millions", 100).ShouldBe("100.00");
  }

  [Fact]
  public void ReadsABookTheWorkingTreeNoLongerHas()
  {
    // Arrange
    using var repository = new Repository();
    repository.WriteBook("books/acme", "revenue,100");
    repository.WriteBytes("books/acme/facts/source.png", [1, 2, 3]);
    repository.Write("books/acme/facts/facts.yaml",
      "table:\n  title: Facts\nsource:\n  image: source.png\nline_items:\n  revenue:\n    label: Revenue\n");
    repository.Commit();
    Directory.Delete(repository.PathTo("books"), true);

    // Act
    Book book;
    using (var files = GitFiles.Open(repository.PathTo("books/acme"), "HEAD"))
    {
      book = Book.Load(repository.PathTo("books/acme"), files);
      files.ReadAllBytes(repository.PathTo("books/acme/facts/source.png")).ShouldBe(new byte[] { 1, 2, 3 });
    }

    // Assert
    book.Facts["revenue"].Cells["2025"].Value.ShouldBe(100);
  }

  [Fact]
  public void ListsFoldersNestedAtAnyDepth()
  {
    // Arrange
    using var repository = new Repository();
    repository.Write("a/b/c/deep.yaml", "x: 1");
    repository.Write("a/shallow.yaml", "x: 1");
    repository.Commit();

    // Act
    using var files = GitFiles.Open(repository.Root, "HEAD");

    // Assert
    files.Directories(repository.Root).ShouldBe([repository.PathTo("a")]);
    files.Directories(repository.PathTo("a")).ShouldBe([repository.PathTo("a/b")]);
    files.Directories(repository.PathTo("a/b")).ShouldBe([repository.PathTo("a/b/c")]);
    files.Files(repository.PathTo("a"), "*.yaml").ShouldBe([repository.PathTo("a/shallow.yaml")]);
    files.DirectoryExists(repository.PathTo("a/b")).ShouldBeTrue();
    files.DirectoryExists(repository.PathTo("a/z")).ShouldBeFalse();
  }

  [Fact]
  public void NamesARevisionThatDoesNotExist()
  {
    // Arrange
    using var repository = new Repository();
    repository.WriteBook("books/acme", "revenue,100");
    repository.Commit();

    // Act
    InvalidDataException exception = Should.Throw<InvalidDataException>(
      () => GitFiles.Open(repository.PathTo("books/acme"), "nowhere"));

    // Assert
    exception.Message.ShouldContain("nowhere");
  }

  [Fact]
  public void RunsAHeadlessCommandAtACommit()
  {
    // Arrange
    using var repository = new Repository();
    repository.WriteBook("books/acme", "revenue,100");
    repository.Commit();
    repository.Write("books/acme/facts/facts.csv", "line_item,2025\nrevenue,200");
    var output = new StringWriter();
    TextWriter original = Console.Out;
    Console.SetOut(output);

    // Act
    int exit;
    try
    {
      exit = HeadlessRunner.Run(repository.PathTo("books/acme"), HeadlessCommand.Lines, null, true, ["revenue"],
        "HEAD");
    }
    finally
    {
      Console.SetOut(original);
    }

    // Assert
    exit.ShouldBe(0);
    output.ToString().ShouldContain("\"100\"");
  }

  [Theory]
  [InlineData("--cell", "revenue", "2025")]
  [InlineData("--url")]
  [InlineData("--port", "5200")]
  public void AtReadsOnlyHeadlessly(params string[] option)
  {
    // Act
    ArgumentException exception = Should.Throw<ArgumentException>(
      () => CommandLineOptions.Parse(["books/acme", "--at", "HEAD~1", .. option]));

    // Assert
    exception.Message.ShouldContain("--at");
  }

  [Fact]
  public void ParsesAt()
  {
    // Act
    CommandLineOptions options = CommandLineOptions.Parse(["books/acme", "--lines", "--json", "--at", "HEAD~1"]);

    // Assert
    options.At.ShouldBe("HEAD~1");
  }

  private sealed class Repository : TempFolder
  {
    public Repository()
    {
      Git("init", "--quiet");
    }

    public void WriteBook(string folder, string fact)
    {
      Write($"{folder}/book.yaml", "name: Acme\nshort_name: Acme\nnavigation: []\n");
      Write($"{folder}/facts/facts.csv", $"line_item,2025\n{fact}");
      Write($"{folder}/facts/facts.yaml", "table:\n  title: Facts\nline_items:\n  revenue:\n    label: Revenue\n");
    }

    public void Commit()
    {
      Git("add", "--all");
      Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "book");
    }

    private void Git(params string[] arguments)
    {
      var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardError = true };
      foreach (string argument in arguments)
      {
        start.ArgumentList.Add(argument);
      }
      using Process process = Process.Start(start)!;
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      process.ExitCode.ShouldBe(0, error);
    }


  }


}