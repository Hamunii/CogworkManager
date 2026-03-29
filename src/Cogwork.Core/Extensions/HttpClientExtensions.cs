using System.Net;

namespace Cogwork.Core.Extensions;

public static class HttpClientExtensions
{
    public static async Task<HttpStatusCode> DownloadAsync(
        this HttpClient client,
        string requestUri,
        Stream destination,
        IProgress<double>? progress = null,
        Action<long?>? onContentLengthKnown = null,
        CancellationToken cancellationToken = default
    )
    {
        // Get the http headers first to examine the content length
        using var response = await client.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (!response.IsSuccessStatusCode)
            return response.StatusCode;

        var contentLength = response.Content.Headers.ContentLength;
        onContentLengthKnown?.Invoke(contentLength);

        using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
        // Ignore progress reporting when no progress reporter was
        // passed or when the content length is unknown
        if (progress is null || contentLength is null)
        {
            await download.CopyToAsync(destination, cancellationToken);
            return response.StatusCode;
        }

        // Convert absolute progress (bytes downloaded) into relative progress (0% - 100%)
        // var relativeProgress = new Progress<long>(totalBytes =>
        //     progress.Report((double)totalBytes / contentLength.Value)
        // );
        // Use extension method to report progress while downloading
        await download.CopyToAsync(destination, 81920, progress, cancellationToken);
        progress.Report(contentLength.Value);

        return response.StatusCode;
    }

    extension(HttpStatusCode self)
    {
        public bool IsSuccess => (int)self is >= 200 and < 300;
    }
}
