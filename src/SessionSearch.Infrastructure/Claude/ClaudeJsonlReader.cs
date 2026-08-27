using System.Buffers;
using SessionSearch.Core.Providers;

namespace SessionSearch.Infrastructure.Claude;

internal readonly record struct ClaudeJsonlReadOutcome(
    long LastCompleteOffset,
    bool HasPartialTail,
    bool ReachedSnapshotLength,
    bool StoppedEarly);

internal delegate void ClaudeJsonlLineHandler(
    long startOffset,
    ReadOnlyMemory<byte> utf8Json,
    bool isOversized);

internal static class ClaudeJsonlReader
{
    private const int ReadBufferSize = 64 * 1024;
    private const int InitialLineBufferSize = 4 * 1024;
    private const int MaximumPooledLineBufferSize = 256 * 1024;

    public static async ValueTask<ClaudeJsonlReadOutcome> ReadAsync(
        string path,
        long startOffset,
        long snapshotLength,
        ClaudeJsonlLineHandler handleLine,
        CancellationToken cancellationToken,
        Func<long, bool>? stopAfterCompleteLine = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handleLine);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotLength);

        cancellationToken.ThrowIfCancellationRequested();
        if (startOffset > snapshotLength)
        {
            return new ClaudeJsonlReadOutcome(startOffset, false, false, false);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Seek(startOffset, SeekOrigin.Begin);

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        byte[] lineBuffer = ArrayPool<byte>.Shared.Rent(InitialLineBufferSize);
        try
        {
            int lineLength = 0;
            bool lineIsOversized = false;
            long cursor = startOffset;
            long lineStartOffset = startOffset;
            long lastCompleteOffset = startOffset;

            while (cursor < snapshotLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(readBuffer.Length, snapshotLength - cursor);
                int bytesRead = await stream.ReadAsync(
                    readBuffer.AsMemory(0, requested),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                for (int index = 0; index < bytesRead; index++)
                {
                    byte value = readBuffer[index];
                    cursor++;

                    if (value == (byte)'\n')
                    {
                        if (lineIsOversized)
                        {
                            handleLine(lineStartOffset, ReadOnlyMemory<byte>.Empty, true);
                        }
                        else
                        {
                            int contentLength = lineLength > 0 && lineBuffer[lineLength - 1] == (byte)'\r'
                                ? lineLength - 1
                                : lineLength;
                            ReadOnlyMemory<byte> content = lineBuffer.AsMemory(0, contentLength);
                            if (lineStartOffset == 0 && HasUtf8Bom(content.Span))
                            {
                                content = content[3..];
                            }

                            handleLine(lineStartOffset, content, false);
                        }

                        lineLength = 0;
                        lineIsOversized = false;
                        lineStartOffset = cursor;
                        lastCompleteOffset = cursor;
                        if (stopAfterCompleteLine?.Invoke(lastCompleteOffset) == true)
                        {
                            bool reachedSnapshot = cursor == snapshotLength;
                            return new ClaudeJsonlReadOutcome(
                                lastCompleteOffset,
                                false,
                                reachedSnapshot,
                                !reachedSnapshot);
                        }

                        continue;
                    }

                    if (lineIsOversized)
                    {
                        continue;
                    }

                    if (lineLength == ProviderLimits.MaxJsonlRecordBytes)
                    {
                        lineLength = 0;
                        lineIsOversized = true;
                        continue;
                    }

                    if (lineLength == lineBuffer.Length)
                    {
                        lineBuffer = GrowLineBuffer(lineBuffer, lineLength);
                    }

                    lineBuffer[lineLength] = value;
                    lineLength++;
                }
            }

            bool reachedSnapshotLength = cursor == snapshotLength;
            bool hasPartialTail = cursor > lastCompleteOffset;
            return new ClaudeJsonlReadOutcome(
                lastCompleteOffset,
                hasPartialTail,
                reachedSnapshotLength,
                false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ReturnLineBuffer(lineBuffer);
        }
    }

    private static byte[] GrowLineBuffer(byte[] current, int usedLength)
    {
        int nextLength = Math.Min(current.Length * 2, ProviderLimits.MaxJsonlRecordBytes);
        byte[] replacement = nextLength <= MaximumPooledLineBufferSize
            ? ArrayPool<byte>.Shared.Rent(nextLength)
            : GC.AllocateUninitializedArray<byte>(nextLength);
        current.AsSpan(0, usedLength).CopyTo(replacement);
        ReturnLineBuffer(current);
        return replacement;
    }

    private static void ReturnLineBuffer(byte[] buffer)
    {
        if (buffer.Length <= MaximumPooledLineBufferSize)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value) =>
        value.Length >= 3 && value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF;
}
