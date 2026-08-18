using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace iRoute.Services;

internal static class BoundedNdjsonReader
{
    private const int MaximumLineLength = 65_536;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        Func<string, Exception?, Exception> errorFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(4_096);
        var lineBuffer = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        var line = DecodeLine(lineBuffer.WrittenSpan, errorFactory);
                        lineBuffer.Clear();
                        yield return line.EndsWith('\r') ? line[..^1] : line;
                        continue;
                    }

                    if (lineBuffer.WrittenCount >= MaximumLineLength)
                    {
                        throw errorFactory(
                            "The configured model gateway stream exceeded its line-size bound.",
                            null);
                    }

                    lineBuffer.GetSpan(1)[0] = value;
                    lineBuffer.Advance(1);
                }
            }

            if (lineBuffer.WrittenCount > 0)
            {
                yield return DecodeLine(lineBuffer.WrittenSpan, errorFactory);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static string DecodeLine(
        ReadOnlySpan<byte> value,
        Func<string, Exception?, Exception> errorFactory)
    {
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw errorFactory(
                "The configured model gateway stream returned invalid UTF-8.",
                exception);
        }
    }
}
