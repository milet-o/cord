using System.Net;
using System.Net.Sockets;
using Cord.Core.Consoles;
using Cord.Protocol.Discovery;
using Cord.Relay;

namespace Cord.Core.Tests;

public sealed class ConsoleSharingTests
{
    private static readonly IPAddress Home = IPAddress.Parse("192.168.0.130");

    private readonly List<FakeRelay> _started = [];

    [Fact]
    public void SharesTheConsoleAtItsAddress()
    {
        using var sharing = Sharing();

        sharing.Share(Entry(Home));

        Assert.Equal("PS5", sharing.HostId);
        Assert.Equal(Home, Assert.Single(_started).Console);
        Assert.Equal([IPAddress.Parse("26.50.127.143")], sharing.ShareAddresses);
        Assert.Equal(1, sharing.ClientCount);
        Assert.Null(sharing.Error);
    }

    [Fact]
    public void StopReleasesTheRelay()
    {
        using var sharing = Sharing();
        sharing.Share(Entry(Home));

        sharing.Stop();

        Assert.Null(sharing.HostId);
        Assert.True(_started[0].Disposed);
        Assert.Empty(sharing.ShareAddresses);
        Assert.Equal(0, sharing.ClientCount);
    }

    [Fact]
    public void ReportsWhyTheRelayCouldNotStart()
    {
        using var sharing = new ConsoleSharing(_ => throw new RelayException("UDP port 9302 is not available.", new SocketException()));

        sharing.Share(Entry(Home));

        Assert.Null(sharing.HostId);
        Assert.Equal("UDP port 9302 is not available.", sharing.Error);
        sharing.ClearError();
        Assert.Null(sharing.Error);
    }

    [Fact]
    public void CannotShareAConsoleThatWasNotFound()
    {
        using var sharing = Sharing();

        sharing.Share(Entry(null));

        Assert.Null(sharing.HostId);
        Assert.Empty(_started);
        Assert.NotNull(sharing.Error);
    }

    [Fact]
    public void FollowsTheConsoleToANewAddress()
    {
        using var sharing = Sharing();
        sharing.Share(Entry(Home));
        var moved = IPAddress.Parse("192.168.0.131");

        sharing.Follow([Entry(Home)]);
        Assert.Single(_started);
        sharing.Follow([Entry(null)]);
        Assert.Single(_started);
        sharing.Follow([Entry(moved)]);

        Assert.True(_started[0].Disposed);
        Assert.Equal(moved, _started[1].Console);
        Assert.Equal("PS5", sharing.HostId);
    }

    [Fact]
    public void StopsWhenTheConsoleIsForgotten()
    {
        using var sharing = Sharing();
        sharing.Share(Entry(Home));

        sharing.Follow([]);

        Assert.Null(sharing.HostId);
        Assert.True(_started[0].Disposed);
    }

    private ConsoleSharing Sharing() => new(console =>
    {
        var relay = new FakeRelay(console);
        _started.Add(relay);
        return relay;
    });

    private static ConsoleEntry Entry(IPAddress? address) =>
        new("PS5", "Living Room", address, ConsoleState.Ready, true, null, DateTimeOffset.UnixEpoch);

    private sealed class FakeRelay(IPAddress console) : IConsoleRelay
    {
        public bool Disposed { get; private set; }

        public IPAddress Console { get; } = console;

        public IReadOnlyList<IPAddress> ShareAddresses { get; } = [IPAddress.Parse("26.50.127.143")];

        public IReadOnlyCollection<IPAddress> Clients { get; } = [IPAddress.Parse("26.12.34.56")];

        public void Dispose() => Disposed = true;
    }
}
