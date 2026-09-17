using System.Net;

namespace Bitfield.Core.Trackers;

/// <summary>
/// Announces over HTTP. The request is a GET with everything in the query
/// string; the reply is a bencoded dictionary.
/// </summary>
public sealed class HttpTrackerClient : IDisposable
{
    /// <summary>
    /// A tracker reply is a short dictionary and a few hundred peers at most. A
    /// cap keeps a hostile or broken tracker from being able to hand over
    /// something large enough to matter.
    /// </summary>
    private const int MaxReplyBytes = 1 << 20;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HttpTrackerClient(HttpClient? http = null)
    {
        _ownsClient = http == null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,

            // Trackers are contacted often and from several torrents at once,
            // and some are slow enough that a stale connection is likelier than
            // a fast one.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Bitfield/0.1");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip");
    }

    public async Task<AnnounceResponse> AnnounceAsync(
        Uri announce,
        AnnounceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (announce.Scheme is not ("http" or "https"))
        {
            throw new TrackerException($"{announce.Scheme} is not an HTTP tracker");
        }

        Uri uri = request.ToUri(announce);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new TrackerException($"{announce.Host} could not be reached: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TrackerException($"{announce.Host} did not answer in time");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TrackerException($"{announce.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            byte[] body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            return AnnounceResponse.Parse(body);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];

        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxReplyBytes)
            {
                throw new TrackerException($"the tracker's reply is longer than {MaxReplyBytes} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
