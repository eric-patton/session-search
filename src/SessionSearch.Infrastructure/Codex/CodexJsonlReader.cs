using System.Buffers;
using SessionSearch.Core.Providers;

namespace SessionSearch.Infrastructure.Codex;

internal readonly record struct CodexJsonlReadOutcome(
    long LastCompleteOffset,
    bool HasTrailingPartialLine,
    bool ReachedSnapshotLength,
    bool StoppedEarly);

internal delegate void CodexJsonlLineHandler(
    long startOffset,
    ReadOnlyMemory<byte> utf8Json,
    bool oversized);

internal static class CodexJsonlReader
{
    private const int ReadBufferBytes = 64 * 1024;
    private const int InitialLineBufferBytes = 4 * 1024;
    private const int MaximumPooledLineBufferBytes = 256 * 1024;

    public static async ValueTask<CodexJsonlReadOutcome> ReadAsync(
        string path,
        long startOffset,
        long snapshotLength,
        CodexJsonlLineHandler handleLine,
        CancellationToken cancellationToken,
        Func<long, bool>? stopAfterCompleteLine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handleLine);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotLength);
        cancellationToken.ThrowIfCancellationRequested();

        if (startOffset > snapshotLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startOffset),
                startOffset,
                "The committed offset is beyond the current source length.");
        }

        await using FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = ReadBufferBytes,
            });
        stream.Position = startOffset;

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        byte[] lineBuffer = ArrayPool<byte>.Shared.Rent(InitialLineBufferBytes);
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
                    cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                for (int index = 0; index < bytesRead; index++)
                {
                    if ((cursor & ((1024 * 1024) - 1)) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

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
                            return new CodexJsonlReadOutcome(
                                lastCompleteOffset,
                                HasTrailingPartialLine: false,
                                reachedSnapshot,
                                StoppedEarly: !reachedSnapshot);
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
            return new CodexJsonlReadOutcome(
                lastCompleteOffset,
                HasTrailingPartialLine: cursor > lastCompleteOffset,
                reachedSnapshotLength,
                StoppedEarly: false);
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
        byte[] replacement = nextLength <= MaximumPooledLineBufferBytes
            ? ArrayPool<byte>.Shared.Rent(nextLength)
            : GC.AllocateUninitializedArray<byte>(nextLength);
        current.AsSpan(0, usedLength).CopyTo(replacement);
        ReturnLineBuffer(current);
        return replacement;
    }

    private static void ReturnLineBuffer(byte[] buffer)
    {
        if (buffer.Length <= MaximumPooledLineBufferBytes)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value) =>
        value.Length >= 3 && value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF;
}
