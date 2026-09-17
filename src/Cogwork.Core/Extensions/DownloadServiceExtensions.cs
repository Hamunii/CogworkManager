using Downloader;

namespace Cogwork.Core.Extensions;

public readonly record struct ProgressContext(
    IProgress<double>? Progress,
    Action<IProgress<double>, long?>? OnContentLengthKnown,
    Func<IProgress<double>>? ProgressFactory = null
);

static class DownloadServiceExtensions
{
    extension(DownloadService downloader)
    {
        public void TrackDownloadProgress(ProgressContext progress)
        {
            if (progress.Progress is { } p)
            {
                downloader.DownloadStarted += (_, e) =>
                {
                    progress.OnContentLengthKnown?.Invoke(p, e.TotalBytesToReceive);
                };

                downloader.DownloadProgressChanged += (_, e) =>
                {
                    p.Report(e.ReceivedBytesSize);
                };
            }
        }
    }
}
