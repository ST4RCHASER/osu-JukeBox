#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online;

/// <summary>
/// The body-draining half of every HTTP mirror's <see cref="IBeatmapMirror.DownloadAsync"/>: all
/// three (<see cref="NerinyanMirror"/>, <see cref="CatboyMirror"/>, <see cref="OsuDirectMirror"/>)
/// differ only in the URL they build, so the copy itself lives here once.
/// </summary>
internal static class MirrorDownload
{
    /// <summary>Matches <see cref="Stream.CopyToAsync(Stream)"/>'s own default buffer size.</summary>
    private const int buffer_size = 81920;

    /// <summary>
    /// Copies <paramref name="response"/>'s body into <paramref name="destination"/>, invoking
    /// <paramref name="progress"/> once up front (so a consumer learns the total, and that a
    /// download has actually started, before any bytes arrive) and again after each chunk lands.
    ///
    /// <para>
    /// Hand-rolled rather than <see cref="HttpContent.CopyToAsync(Stream, CancellationToken)"/>
    /// purely because that gives no way to observe the transfer as it happens; when no
    /// <paramref name="progress"/> is supplied the behaviour is identical to what it did before.
    /// A mirror that omits <c>Content-Length</c> (chunked) reports a null total throughout, which
    /// is what drives the indeterminate spinner rather than a bar stuck at 0%.
    /// </para>
    /// </summary>
    public static async Task CopyAsync(HttpResponseMessage response, Stream destination, DownloadProgressCallback? progress, CancellationToken ct)
    {
        long? total = response.Content.Headers.ContentLength;

        if (progress == null)
        {
            await response.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
            return;
        }

        progress(0, total);

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(buffer_size);
        long read = 0;

        try
        {
            int count;

            while ((count = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                read += count;
                progress(read, total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
