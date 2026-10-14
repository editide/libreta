using System.Net;
using System.Net.Sockets;
using Shouldly;
using Xunit;

namespace Libreta.Tests;

public class InstanceTests
{
  private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);
  private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

  [Fact]
  public void FindsTheServerRunningABook()
  {
    // Arrange
    using var folder = new TempFolder();
    using var listener = Listen();
    var registry = new InstanceRegistry(folder.Root);
    var spacex = new ServerInstance(Environment.ProcessId, folder.PathTo("books/spacex"), Port(listener));
    registry.Register(spacex);

    // Act
    ServerInstance? found = registry.Find(folder.PathTo("books/spacex/"));
    ServerInstance? other = registry.Find(folder.PathTo("books/acme"));

    // Assert
    found.ShouldBe(spacex);
    found!.Url.ShouldBe($"http://localhost:{spacex.Port}");
    other.ShouldBeNull();
  }

  [Fact]
  public void ForgetsAServerOnceItUnregisters()
  {
    // Arrange
    using var folder = new TempFolder();
    using var listener = Listen();
    var registry = new InstanceRegistry(folder.Root);
    var spacex = new ServerInstance(Environment.ProcessId, folder.PathTo("books/spacex"), Port(listener));
    registry.Register(spacex);

    // Act
    registry.Unregister(spacex);

    // Assert
    registry.Find(spacex.Root).ShouldBeNull();
    Directory.GetFiles(folder.Root).ShouldBeEmpty();
  }

  [Fact]
  public void DiscardsRecordsOfServersThatNoLongerAnswer()
  {
    // Arrange
    using var folder = new TempFolder();
    using var listener = Listen();
    int closedPort = Port(listener);
    listener.Stop();
    using var live = Listen();
    var registry = new InstanceRegistry(folder.Root);
    string root = folder.PathTo("books/spacex");
    registry.Register(new ServerInstance(int.MaxValue, root, Port(live)));
    registry.Register(new ServerInstance(Environment.ProcessId, root, closedPort));
    File.WriteAllText(Path.Combine(folder.Root, "garbled.json"), "{");

    // Act
    ServerInstance? found = registry.Find(root);

    // Assert
    found.ShouldBeNull();
    Directory.GetFiles(folder.Root).ShouldBeEmpty();
  }

  [Fact]
  public void StopsOnceTheLastPageHasBeenGoneForTheGracePeriod()
  {
    // Arrange
    using var stopped = new ManualResetEventSlim();
    using var viewers = new Viewers(Grace, stopped.Set);
    IDisposable first = viewers.Connect();
    IDisposable second = viewers.Connect();

    // Act
    first.Dispose();
    bool stoppedWithOnePage = stopped.Wait(Grace * 4);
    second.Dispose();

    // Assert
    stoppedWithOnePage.ShouldBeFalse();
    stopped.Wait(Patience).ShouldBeTrue();
  }

  [Fact]
  public void KeepsRunningWhenAPageReconnectsWithinTheGracePeriod()
  {
    // Arrange
    using var stopped = new ManualResetEventSlim();
    using var viewers = new Viewers(TimeSpan.FromMilliseconds(300), stopped.Set);
    viewers.Connect().Dispose();

    // Act
    using IDisposable reloaded = viewers.Connect();

    // Assert
    stopped.Wait(TimeSpan.FromMilliseconds(600)).ShouldBeFalse();
  }

  [Fact]
  public void WaitsForAFirstPageBeforeCountingDown()
  {
    // Arrange
    using var stopped = new ManualResetEventSlim();

    // Act
    using var viewers = new Viewers(Grace, stopped.Set);

    // Assert
    stopped.Wait(Grace * 4).ShouldBeFalse();
  }

  [Fact]
  public void StopsWhenNoFirstPageComesAfterAnOpeningLeftToTheCaller()
  {
    // Arrange
    using var stopped = new ManualResetEventSlim();
    using var viewers = new Viewers(Grace, stopped.Set);

    // Act
    viewers.Expect();

    // Assert
    stopped.Wait(Patience).ShouldBeTrue();
  }

  [Fact]
  public void CountsThePagesHoldingTheStreamOpen()
  {
    // Arrange
    var pages = new Pages();
    IDisposable first = pages.Connect();
    using IDisposable second = pages.Connect();

    // Act
    first.Dispose();
    first.Dispose();

    // Assert
    pages.Count.ShouldBe(1);
  }

  [Fact]
  public void PassesTheLatestRequestedCellOnToAPageOnce()
  {
    // Arrange
    var pages = new Pages();
    long seen = pages.Version;
    pages.Request(new RequestedCell("statement", "statement.revenue", "2024"));
    pages.Request(new RequestedCell("statement", "statement.revenue", "2025"));

    // Act
    RequestedCell? first = pages.Since(ref seen);
    RequestedCell? second = pages.Since(ref seen);

    // Assert
    first.ShouldBe(new RequestedCell("statement", "statement.revenue", "2025"));
    second.ShouldBeNull();
  }

  [Fact]
  public void APageConnectingLaterMissesEarlierRequests()
  {
    // Arrange
    var pages = new Pages();
    pages.Request(new RequestedCell("statement", "statement.revenue", "2025"));

    // Act
    long seen = pages.Version;

    // Assert
    pages.Since(ref seen).ShouldBeNull();
  }

  private static TcpListener Listen()
  {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return listener;
  }

  private static int Port(TcpListener listener)
  {
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }


}