using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk;

/// <summary>
/// Attaches to the Greenlight running on this machine and keeps itself attached.
/// </summary>
/// <remarks>
/// <para>
/// The contract worth stating once: a consumer app that starts before Greenlight, runs
/// while Greenlight is updated and restarted underneath it, and outlives it, sees
/// <c>Unavailable → Connected → Unavailable → Connected</c> and never an exception. There
/// is no "the server must be up first" ordering to respect and nothing to retry by hand.
/// </para>
/// <para>
/// <b>Events are raised on a background thread.</b>
/// </para>
/// <code>
/// await using var greenlight = new GreenlightClient();
/// greenlight.Changed += (_, e) => Paint(e.Snapshot.Status, e.Snapshot.IsBuilding);
/// await greenlight.StartAsync();
/// </code>
/// </remarks>
public sealed class GreenlightClient : IGreenlightStatus, IAsyncDisposable
{
    private readonly GreenlightClientOptions _options;
    private readonly IGreenlightTransport _transport;

    // Commands in flight, by correlation id. A null result means the connection went away
    // before the ack did — distinct from a timeout, and reported as Unavailable.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AckMessage?>> _pending = new();

    private readonly object _watcherGate = new();  // not System.Threading.Lock: this package also targets net8.0
    private readonly List<Channel<GreenlightSnapshot>> _watchers = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IGreenlightConnection? _connection;
    private long _commandId;

    /// <summary>Whether the last close was the host saying the local API is switched off.</summary>
    private bool _disabledByHost;

    /// <summary>Connect to the Greenlight of the current user, with default settings.</summary>
    public GreenlightClient() : this(new GreenlightClientOptions()) { }

    /// <summary>Connect with the given settings.</summary>
    public GreenlightClient(GreenlightClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = options.Transport
                     ?? new NamedPipeTransport(options.PipeName ?? GreenlightProtocol.PipeNameForCurrentUser());
    }

    /// <inheritdoc />
    public GreenlightAvailability Availability { get; private set; } = GreenlightAvailability.Unavailable;

    /// <inheritdoc />
    public GreenlightSnapshot? Current { get; private set; }

    /// <summary>The Greenlight version on the other end, once attached. Null while unattached.</summary>
    public string? HostVersion { get; private set; }

    /// <summary>
    /// The commands this host says it will accept. Empty while unattached, and empty while
    /// attached to a host with commands switched off — which is what to grey a button on,
    /// rather than waiting for the refusal.
    /// </summary>
    public IReadOnlyList<string> SupportedCommands { get; private set; } = [];

    /// <inheritdoc />
    public event EventHandler<GreenlightSnapshotEventArgs>? Changed;

    /// <inheritdoc />
    public event EventHandler<GreenlightAvailabilityEventArgs>? AvailabilityChanged;

    /// <summary>
    /// Whether a Greenlight local API is listening for this user right now. A one-shot
    /// probe; for anything ongoing, start the client and watch <see cref="Availability"/>
    /// instead — this answer is stale the moment it is returned.
    /// </summary>
    public static bool IsRunning() => IsRunning(GreenlightProtocol.PipeNameForCurrentUser());

    /// <summary>As <see cref="IsRunning()"/>, for a specific pipe name.</summary>
    public static bool IsRunning(string pipeName) =>
        // Existence of the pipe, not a connection to it: connecting would occupy a server
        // instance and write a connect/disconnect pair into Greenlight's status log for what
        // is meant to be a passive question.
        OperatingSystem.IsWindows() && File.Exists($@"\\.\pipe\{pipeName}");

    /// <summary>
    /// Start connecting, and keep reconnecting until stopped. Returns as soon as the
    /// background loop is running — it does not wait for a connection and does not fail if
    /// Greenlight is absent.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) throw new InvalidOperationException("This client is already started.");
        cancellationToken.ThrowIfCancellationRequested();

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stop, close any open connection, and wait for the loop to unwind.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var loop = _loop;
        if (loop is null) return;

        _loop = null;
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The loop's own cancellation. Expected.
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        CompleteWatchers();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GreenlightSnapshot> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<GreenlightSnapshot>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        lock (_watcherGate) _watchers.Add(channel);

        try
        {
            // Whoever starts watching mid-stream should not have to wait for the next change
            // to learn what the state already is.
            if (Current is { } current) yield return current;

            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return snapshot;
        }
        finally
        {
            lock (_watcherGate) _watchers.Remove(channel);
        }
    }

    /// <inheritdoc />
    public Task<CommandResult> AcknowledgeBuildsAsync(IEnumerable<long> buildIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildIds);
        var ids = buildIds as IReadOnlyList<long> ?? buildIds.ToList();
        return ids.Count == 0
            ? Task.FromResult(CommandResult.Ok)
            : SendAsync(CommandName.AcknowledgeBuilds, new CommandArgs(BuildIds: ids), cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommandResult> AcknowledgePipelineAsync(string project, string pipeline, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeline);
        return SendAsync(CommandName.AcknowledgePipeline, new CommandArgs(Project: project, Pipeline: pipeline), cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommandResult> RefreshNowAsync(CancellationToken cancellationToken = default) =>
        SendAsync(CommandName.RefreshNow, null, cancellationToken);

    /// <inheritdoc />
    public Task<CommandResult> ShowDashboardAsync(CancellationToken cancellationToken = default) =>
        SendAsync(CommandName.ShowDashboard, null, cancellationToken);

    private async Task<CommandResult> SendAsync(string name, CommandArgs? args, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || Availability != GreenlightAvailability.Connected)
            return CommandResult.Unavailable;

        var id = Interlocked.Increment(ref _commandId).ToString(CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource<AckMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;

        try
        {
            await connection.WriteLineAsync(
                GreenlightProtocol.Serialize(new CommandMessage(id, name, args)), cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CommandTimeout);

            var ack = await pending.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (ack is null) return CommandResult.Unavailable;      // connection died first
            return ack.Ok ? CommandResult.Ok : CommandResult.Refused(ack.Error, ack.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CommandResult.TimedOut;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe went away between the check above and the write.
            return CommandResult.Unavailable;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = _options.MinReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (Availability == GreenlightAvailability.Connected)
                SetAvailability(GreenlightAvailability.Connecting);

            IGreenlightConnection? connection;
            try
            {
                connection = await _transport.TryConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A transport that throws for something other than "not running" is still,
                // from here, a Greenlight we cannot reach.
                connection = null;
            }

            if (connection is null)
            {
                // Disabled outlives the connection that reported it: once the user switches
                // the local API off the pipe is gone, so every attempt from then on fails,
                // and reporting plain Unavailable would throw away the one piece of
                // information the consumer can actually act on.
                SetAvailability(_disabledByHost ? GreenlightAvailability.Disabled : GreenlightAvailability.Unavailable);
                if (!await DelayAsync(delay, cancellationToken).ConfigureAwait(false)) break;
                delay = NextDelay(delay);
                continue;
            }

            SessionOutcome outcome;
            try
            {
                outcome = await RunSessionAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CloseAsync(connection).ConfigureAwait(false);
                break;
            }
            catch
            {
                outcome = SessionOutcome.Dropped;
            }

            await CloseAsync(connection).ConfigureAwait(false);

            delay = outcome switch
            {
                // A handshake we cannot speak will not fix itself in a second; wait out the
                // full interval rather than spinning against an incompatible host.
                SessionOutcome.Incompatible => _options.MaxReconnectDelay,
                // We got as far as a working session, so the host is healthy and whatever
                // ended it is worth retrying promptly.
                SessionOutcome.Attached => _options.MinReconnectDelay,
                _ => delay,
            };

            if (outcome != SessionOutcome.Incompatible)
                SetAvailability(_disabledByHost ? GreenlightAvailability.Disabled : GreenlightAvailability.Unavailable);

            if (!await DelayAsync(delay, cancellationToken).ConfigureAwait(false)) break;
            if (outcome == SessionOutcome.Dropped) delay = NextDelay(delay);
        }

        SetAvailability(GreenlightAvailability.Unavailable);
    }

    private async Task<SessionOutcome> RunSessionAsync(IGreenlightConnection connection, CancellationToken cancellationToken)
    {
        var hello = await ReadHelloAsync(connection, cancellationToken).ConfigureAwait(false);
        if (hello is null) return SessionOutcome.Dropped;

        if (hello.V != GreenlightProtocol.Version)
        {
            SetAvailability(GreenlightAvailability.Incompatible);
            return SessionOutcome.Incompatible;
        }

        _disabledByHost = false;
        HostVersion = hello.App;
        SupportedCommands = hello.Commands ?? [];
        _connection = connection;
        SetAvailability(GreenlightAvailability.Connected);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;              // host closed
                if (!Dispatch(line)) break;           // host said goodbye
            }
        }
        finally
        {
            _connection = null;
            HostVersion = null;
            SupportedCommands = [];
            Current = null;
            FailPending();
        }

        return SessionOutcome.Attached;
    }

    private async Task<HelloMessage?> ReadHelloAsync(IGreenlightConnection connection, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HandshakeTimeout);

        try
        {
            var line = await connection.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null || GreenlightProtocol.PeekType(line) != MessageType.Hello) return null;
            return GreenlightProtocol.Deserialize<HelloMessage>(line);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A host that connects and then says nothing is not a host we can use.
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Handle one received line. Returns false when the host has said goodbye.</summary>
    private bool Dispatch(string line)
    {
        // Anything unrecognised — an unknown message type, a field that will not parse, an
        // outright malformed line — is skipped, never fatal. That tolerance is what lets a
        // newer Greenlight add messages without breaking apps already installed.
        try
        {
            switch (GreenlightProtocol.PeekType(line))
            {
                case MessageType.Snapshot:
                    if (GreenlightProtocol.Deserialize<SnapshotMessage>(line)?.Data is { } snapshot)
                        Publish(snapshot);
                    return true;

                case MessageType.Ack:
                    if (GreenlightProtocol.Deserialize<AckMessage>(line) is { } ack &&
                        _pending.TryGetValue(ack.Id, out var pending))
                        pending.TrySetResult(ack);
                    return true;

                case MessageType.Bye:
                    _disabledByHost = GreenlightProtocol.Deserialize<ByeMessage>(line)?.Reason == ByeReason.Disabled;
                    return false;

                default:
                    return true;
            }
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private void Publish(GreenlightSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, new GreenlightSnapshotEventArgs(snapshot));

        lock (_watcherGate)
            foreach (var watcher in _watchers)
                watcher.Writer.TryWrite(snapshot);
    }

    private void SetAvailability(GreenlightAvailability availability)
    {
        if (Availability == availability) return;
        Availability = availability;
        if (availability != GreenlightAvailability.Connected) Current = null;
        AvailabilityChanged?.Invoke(this, new GreenlightAvailabilityEventArgs(availability));
    }

    private void FailPending()
    {
        foreach (var (id, pending) in _pending.ToArray())
        {
            pending.TrySetResult(null);
            _pending.TryRemove(id, out _);
        }
    }

    private void CompleteWatchers()
    {
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers) watcher.Writer.TryComplete();
            _watchers.Clear();
        }
    }

    private static async Task CloseAsync(IGreenlightConnection connection)
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch { /* closing a connection that is already gone is not news */ }
    }

    /// <summary>Returns false if cancellation cut the wait short.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private TimeSpan NextDelay(TimeSpan current)
    {
        var doubled = current + current;
        return doubled > _options.MaxReconnectDelay ? _options.MaxReconnectDelay : doubled;
    }

    private enum SessionOutcome
    {
        /// <summary>Never got a usable handshake.</summary>
        Dropped,
        /// <summary>Attached, and served until the host closed.</summary>
        Attached,
        /// <summary>The host speaks a protocol version we do not.</summary>
        Incompatible,
    }
}
