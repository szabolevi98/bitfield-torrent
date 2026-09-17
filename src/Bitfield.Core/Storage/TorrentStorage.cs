using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Storage;

/// <summary>One of a torrent's files, and where it ended up on this machine.</summary>
public sealed record StoredFile(TorrentFile Entry, string FullPath)
{
    public long Offset => Entry.Offset;

    public long Length => Entry.Length;

    public long End => Entry.End;

    /// <summary>Padding exists to align the pieces around it and is never created.</summary>
    public bool IsPadding => Entry.IsPadding;
}

/// <summary>
/// The torrent on disk.
///
/// A torrent describes its content as one continuous stream of bytes with the
/// files laid end to end, and cuts pieces from that stream at fixed intervals
/// without regard for where one file stops. So a piece routinely has to be
/// written across two or more files, and the last piece of one file and the
/// first of the next are frequently the same piece. Everything here is that
/// arithmetic.
/// </summary>
public sealed partial class TorrentStorage : IAsyncDisposable
{
    private readonly Metainfo _torrent;
    private readonly StoredFile[] _files;
    private readonly Dictionary<string, SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _handleLock = new(1);

    public TorrentStorage(Metainfo torrent, string downloadDirectory)
    {
        _torrent = torrent;
        DownloadDirectory = Path.GetFullPath(downloadDirectory);

        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        _files = new StoredFile[torrent.Files.Count];

        for (int i = 0; i < torrent.Files.Count; i++)
        {
            TorrentFile entry = torrent.Files[i];
            string relative = LocalPath.FromTorrentPath(entry.Path);

            // Two paths that differ only in characters this platform cannot
            // keep would otherwise land on the same file and overwrite each
            // other, so the second one is given a name of its own.
            string candidate = relative;
            for (int attempt = 2; !taken.Add(candidate); attempt++)
            {
                string directory = Path.GetDirectoryName(relative) ?? "";
                string name = Path.GetFileNameWithoutExtension(relative);
                string extension = Path.GetExtension(relative);
                candidate = Path.Combine(directory, $"{name}_{attempt}{extension}");
            }

            _files[i] = new StoredFile(entry, Path.Combine(DownloadDirectory, candidate));
        }
    }

    public string DownloadDirectory { get; }

    public IReadOnlyList<StoredFile> Files => _files;

    /// <summary>
    /// Creates the directories and the files, each at its full length. On NTFS
    /// they are marked sparse first, so that a torrent claims disk space as its
    /// pieces arrive rather than all at once — a distinction that matters when
    /// a download is started and abandoned, and the reason a half-finished
    /// torrent does not occupy its full size.
    /// </summary>
    public void Create()
    {
        foreach (StoredFile file in _files)
        {
            if (file.IsPadding)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.FullPath)!);

            if (File.Exists(file.FullPath) && new FileInfo(file.FullPath).Length == file.Length)
            {
                continue;
            }

            using SafeFileHandle handle = File.OpenHandle(
                file.FullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

            MarkSparse(handle);
            RandomAccess.SetLength(handle, file.Length);
        }
    }

    /// <summary>
    /// Writes one verified piece, split across however many files it covers.
    /// Nothing calls this with a piece that has not passed its hash.
    /// </summary>
    public async Task WritePieceAsync(int piece, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length != _torrent.PieceLengthAt(piece))
        {
            throw new ArgumentException(
                $"piece {piece} is {_torrent.PieceLengthAt(piece)} bytes, got {data.Length}", nameof(data));
        }

        long offset = (long)piece * _torrent.PieceLength;

        foreach ((StoredFile file, long fileOffset, int length) in Spans(offset, data.Length))
        {
            if (file.IsPadding)
            {
                continue;
            }

            SafeFileHandle handle = await HandleAsync(file, cancellationToken).ConfigureAwait(false);
            await RandomAccess.WriteAsync(handle, data[..length], fileOffset, cancellationToken)
                .ConfigureAwait(false);

            data = data[length..];
        }
    }

    /// <summary>Reads back part of the stream, for verifying or for seeding.</summary>
    public async Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        foreach ((StoredFile file, long fileOffset, int length) in Spans(offset, buffer.Length))
        {
            if (file.IsPadding)
            {
                // Padding is never written, and reads back as the zeros a
                // padding file is defined to contain.
                buffer[..length].Span.Clear();
            }
            else
            {
                SafeFileHandle handle = await HandleAsync(file, cancellationToken).ConfigureAwait(false);

                int read = 0;
                while (read < length)
                {
                    int got = await RandomAccess
                        .ReadAsync(handle, buffer[read..length], fileOffset + read, cancellationToken)
                        .ConfigureAwait(false);

                    if (got == 0)
                    {
                        // Short file: whatever is missing reads as zeros, which
                        // will fail the piece's hash, which is the right answer.
                        buffer[read..length].Span.Clear();
                        break;
                    }

                    read += got;
                }
            }

            buffer = buffer[length..];
        }
    }

    /// <summary>
    /// Hashes everything on disk and reports which pieces are already complete.
    /// This is the slow, certain answer to what has been downloaded, used when
    /// there is no resume file to believe or when it cannot be trusted.
    /// </summary>
    public async Task<PieceBitfield> VerifyAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PieceBitfield have = new(_torrent.PieceCount);
        byte[] buffer = new byte[_torrent.PieceLength];
        byte[] hash = new byte[20];

        for (int piece = 0; piece < _torrent.PieceCount; piece++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int length = _torrent.PieceLengthAt(piece);
            await ReadAsync((long)piece * _torrent.PieceLength, buffer.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);

            SHA1.HashData(buffer.AsSpan(0, length), hash);
            if (hash.AsSpan().SequenceEqual(_torrent.PieceHash(piece)))
            {
                have.Set(piece);
            }

            progress?.Report((piece + 1) / (double)_torrent.PieceCount);
        }

        return have;
    }

    /// <summary>
    /// Which files a stretch of the torrent's byte stream falls in, and where
    /// inside each one. A piece that starts three bytes before the end of a
    /// file continues at the start of the next.
    /// </summary>
    private IEnumerable<(StoredFile File, long Offset, int Length)> Spans(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > _torrent.TotalLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), $"{offset}..{offset + count} is outside the torrent's {_torrent.TotalLength} bytes");
        }

        int index = IndexOf(offset);

        while (count > 0)
        {
            StoredFile file = _files[index];
            long within = offset - file.Offset;
            int length = (int)Math.Min(count, file.Length - within);

            if (length > 0)
            {
                yield return (file, within, length);
                offset += length;
                count -= length;
            }

            index++;
        }
    }

    /// <summary>The file containing a given offset, found by bisection.</summary>
    private int IndexOf(long offset)
    {
        int low = 0;
        int high = _files.Length - 1;

        while (low < high)
        {
            int middle = (low + high) / 2;
            if (offset >= _files[middle].End)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private async Task<SafeFileHandle> HandleAsync(StoredFile file, CancellationToken cancellationToken)
    {
        await _handleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_handles.TryGetValue(file.FullPath, out SafeFileHandle? handle))
            {
                return handle;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.FullPath)!);

            handle = File.OpenHandle(
                file.FullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                FileOptions.Asynchronous);

            if (RandomAccess.GetLength(handle) != file.Length)
            {
                MarkSparse(handle);
                RandomAccess.SetLength(handle, file.Length);
            }

            _handles[file.FullPath] = handle;
            return handle;
        }
        finally
        {
            _handleLock.Release();
        }
    }

    /// <summary>
    /// Asks NTFS to treat the file as sparse. Anything else — another file
    /// system, a network share — simply does not support it, and a file that is
    /// allocated up front is a waste of space rather than a failure.
    /// </summary>
    private static void MarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            const uint FsctlSetSparse = 0x000900C4;
            DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (EntryPointNotFoundException)
        {
            // Not worth failing a download over.
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    public async ValueTask DisposeAsync()
    {
        await _handleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (SafeFileHandle handle in _handles.Values)
            {
                handle.Dispose();
            }

            _handles.Clear();
        }
        finally
        {
            _handleLock.Release();
            _handleLock.Dispose();
        }
    }
}
