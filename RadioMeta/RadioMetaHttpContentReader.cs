using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RadioMeta;

internal static class RadioMetaHttpContentReader
{
    internal static async Task<string> ReadStringBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidDataException("The response exceeded the allowed size.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The response exceeded the allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        Encoding encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet?.Trim(' ', '\"', '\'');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch { /* Unknown charset: use UTF-8 with BOM detection below. */ }
        }
        using var bytes = new MemoryStream(output.ToArray(), writable: false);
        using var reader = new StreamReader(bytes, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
