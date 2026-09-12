using System.IO.Pipes;
using System.Text;

namespace Greenlight.Sdk;

/// <summary>
/// How <see cref="GreenlightClient"/> reaches the host. The named-pipe implementation is
/// the only one that exists; the seam is here so the client's state machine can be tested
/// without a running Greenlight, and so a second transport could be added later without
/// touching the public API.
/// </summary>
public interface IGreenlightTransport
{
    /// <summary>
    /// Connect, or return null if the host is not there. Returning null is the normal case,
    /// not an error: Greenlight not running is an expected state, so an implementation must
    /// not throw for it.
    /// </summary>
    Task<IGreenlightConnection?> TryConnectAsync(CancellationToken cancellationToken);
}

/// <summary>One open connection to the host. Lines of UTF-8 JSON in both directions.</summary>
public interface IGreenlightConnection : IAsyncDisposable
{
    /// <summary>The next line, or null once the host has closed the connection.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Write one line. Implementations must serialize concurrent writers.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
}

/// <summary>The real transport: Greenlight's per-user named pipe.</summary>
internal sealed class NamedPipeTransport(string pipeName) : IGreenlightTransport
{
    // Long enough to ride out the host's accept loop handing off to a fresh listener,
    // short enough that a client polling an absent Greenlight isn't sat blocking.
    private const int ConnectTimeoutMs = 500;

    public async Task<IGreenlightConnection?> TryConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            return new PipeConnection(pipe);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // No pipe, every server instance busy, or a pipe of that name owned by another
            // account whose ACL keeps us out. All of them mean the same thing to a consumer:
            // there is no Greenlight here to talk to.
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class PipeConnection : IGreenlightConnection
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public PipeConnection(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        _reader.ReadLineAsync(cancellationToken).AsTask();

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
