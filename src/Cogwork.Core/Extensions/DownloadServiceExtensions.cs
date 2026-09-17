using Cogwork.Core.Extensions;
using Downloader;

static class DownloadServiceExtensions
{
    extension(DownloadService downloader)
    {
        public void TrackDownloadProgress(
            ProgressContext progress
        )
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
