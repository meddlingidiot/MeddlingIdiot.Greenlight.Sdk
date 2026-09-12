using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Greenlight.Sdk;
using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk.UnitTests;

/// <summary>
/// Stands in for a Greenlight host so the client's state machine can be driven exactly —
/// including the cases a real host makes hard to produce on demand: a version mismatch, a
/// silent handshake, a connection dropped mid-command.
/// </summary>
/// <remarks>
/// A test scripts the connections it wants with <see cref="Expect"/> and
/// <see cref="ExpectUnavailable"/>, in order. Once the script runs out every further
/// attempt reports "no Greenlight here", which is the honest default: an unscripted
/// reconnect should look like an absent host, not hang.
/// </remarks>
internal sealed class FakeGreenlightTransport : IGreenlightTransport
{
    private readonly ConcurrentQueue<FakeGreenlightConnection?> _script = new();
    private int _attempts;

    /// <summary>How many times the client has tried to connect.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Script the next connection attempt to succeed, and return the host side of it.</summary>
    public FakeGreenlightConnection Expect()
    {
        var connection = new FakeGreenlightConnection();
        _script.Enqueue(connection);
        return connection;
    }

    /// <summary>Script the next connection attempt to find nothing listening.</summary>
    public void ExpectUnavailable() => _script.Enqueue(null);

    public Task<IGreenlightConnection?> TryConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        return Task.FromResult<IGreenlightConnection?>(_script.TryDequeue(out var connection) ? connection : null);
    }
}

/// <summary>The host end of a scripted connection: push lines at the client, read what it sends back.</summary>
internal sealed class FakeGreenlightConnection : IGreenlightConnection
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<string> _outbound = new();
    private readonly SemaphoreSlim _written = new(0);

    /// <summary>True once the client has closed its end.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Push one raw line to the client. Malformed on purpose is fine.</summary>
    public void Send(string line) => _inbound.Writer.TryWrite(line);

    /// <summary>Push a protocol message to the client.</summary>
    public void Send<T>(T message) where T : class => Send(GreenlightProtocol.Serialize(message));

    /// <summary>Push the handshake. Defaults to a version the client accepts, with every command on offer.</summary>
    public void SendHello(int version = GreenlightProtocol.Version, string app = "1.0.0-test", params string[] commands) =>
        Send(new HelloMessage(version, app, commands.Length > 0
            ? commands
            : [CommandName.AcknowledgeBuilds, CommandName.AcknowledgePipeline, CommandName.RefreshNow, CommandName.ShowDashboard]));

    /// <summary>Push a snapshot, wrapped in its envelope.</summary>
    public void SendSnapshot(GreenlightSnapshot snapshot, long seq = 1) => Send(new SnapshotMessage(seq, snapshot));

    /// <summary>Close the connection the way a host that has died does: silently.</summary>
    public void Drop() => _inbound.Writer.TryComplete();

    /// <summary>Close the connection the way a host that means it does.</summary>
    public void SendByeAndDrop(string reason)
    {
        Send(new ByeMessage(reason));
        Drop();
    }

    /// <summary>Wait for the client to send a line, and return it. Fails the test on timeout.</summary>
    public async Task<string> ReceiveAsync(TimeSpan? timeout = null)
    {
        var acquired = await _written.WaitAsync(timeout ?? TestTiming.Timeout);
        Assert.True(acquired, "The client sent nothing within the timeout.");
        Assert.True(_outbound.TryDequeue(out var line));
        return line!;
    }

    /// <summary>Wait for the client's next command.</summary>
    public async Task<CommandMessage> ReceiveCommandAsync(TimeSpan? timeout = null)
    {
        var line = await ReceiveAsync(timeout);
        Assert.Equal(MessageType.Command, GreenlightProtocol.PeekType(line));
        return GreenlightProtocol.Deserialize<CommandMessage>(line)!;
    }

    async Task<string?> IGreenlightConnection.ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    Task IGreenlightConnection.WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _outbound.Enqueue(line);
        _written.Release();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Waiting helpers. The client is deliberately asynchronous — it reports state from a
/// background loop — so tests poll for a condition rather than assert straight after an
/// act, which would race the loop and pass or fail on machine speed.
/// </summary>
internal static class TestTiming
{
    /// <summary>Generous enough for a loaded CI agent, short enough that a genuine hang still fails fast.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Reconnect settings that keep a test's retry loop in the milliseconds.</summary>
    public static GreenlightClientOptions FastOptions(IGreenlightTransport transport) => new()
    {
        Transport = transport,
        MinReconnectDelay = TimeSpan.FromMilliseconds(10),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
        HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        CommandTimeout = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>Poll until the condition holds, or fail with <paramref name="because"/>.</summary>
    public static Task Until(Func<bool> condition, string because, TimeSpan? timeout = null) =>
        Until(condition, () => because, timeout);

    /// <summary>
    /// As above, but the failure message is built only if the wait actually fails — so it
    /// can quote a log that is still filling up while we wait.
    /// </summary>
    public static async Task Until(Func<bool> condition, Func<string> because, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? Timeout;
        while (deadline.Elapsed < limit)
        {
            if (condition()) return;
            await Task.Delay(5);
        }

        Assert.Fail($"Timed out after {limit.TotalSeconds:0.#}s waiting until {because()}.");
    }
}
