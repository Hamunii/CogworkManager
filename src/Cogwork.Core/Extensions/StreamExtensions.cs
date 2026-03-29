namespace Cogwork.Core.Extensions;

public static class StreamExtensions
{
    public static async Task CopyToAsync(
        this Stream source,
        Stream destination,
        int bufferSize,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Has to be readable", nameof(source));
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Has to be writable", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(bufferSize);

        Memory<byte> buffer = new byte[bufferSize];
        long totalBytesRead = 0;
        int bytesRead;
        while (
            (bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false))
            != 0
        )
        {
            await destination
                .WriteAsync(buffer[..bytesRead], cancellationToken)
                .ConfigureAwait(false);
            totalBytesRead += bytesRead;
            progress?.Report(totalBytesRead);
        }
    }
}
