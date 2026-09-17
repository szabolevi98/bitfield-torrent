using System.IO.Pipes;
using System.Text;

namespace Bitfield;

/// <summary>
/// Keeps one client running at a time, and hands a second launch's arguments to
/// the one already there.
///
/// It matters more here than in most applications: two copies would both try to
/// listen on the same port and both write the same torrents' files. And a
/// torrent opened from Explorer while the client is running has to reach that
/// client, or double-clicking a <c>.torrent</c> does nothing but fail.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\BitfieldTorrent.SingleInstance";
    private const string PipeName = "BitfieldTorrent.Open";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listening;

    private SingleInstance(Mutex mutex, bool first)
    {
        _mutex = mutex;
        IsFirst = first;
    }

    public bool IsFirst { get; }

    /// <summary>Raised when another launch asks this client to open something.</summary>
    public event Action<string>? OpenRequested;

    public static SingleInstance Acquire()
    {
        Mutex mutex = new(initiallyOwned: true, MutexName, out bool created);

        if (!created)
        {
            // Somebody else holds it. The mutex is still released on dispose,
            // which is harmless for a handle that was never owned.
            return new SingleInstance(mutex, first: false);
        }

        return new SingleInstance(mutex, first: true);
    }

    /// <summary>
    /// Sends what this launch was asked to open to the client already running.
    /// Returns false if it could not be reached, in which case the caller is
    /// better off carrying on than disappearing silently.
    /// </summary>
    public static bool Forward(string argument)
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);

            byte[] bytes = Encoding.UTF8.GetBytes(argument);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Starts listening for other launches. Only the first instance does this.</summary>
    public void Listen()
    {
        if (!IsFirst)
        {
            return;
        }

        _listening = new CancellationTokenSource();
        CancellationToken cancellationToken = _listening.Token;

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using NamedPipeServerStream pipe = new(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    using StreamReader reader = new(pipe, Encoding.UTF8);
                    string argument = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                    if (argument.Length > 0)
                    {
                        OpenRequested?.Invoke(argument.Trim());
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // A launch that gave up half way through is not worth
                    // stopping the listener over.
                }
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _listening?.Cancel();
        _listening?.Dispose();

        try
        {
            if (IsFirst)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // Not owned; nothing to release.
        }

        _mutex.Dispose();
    }
}
