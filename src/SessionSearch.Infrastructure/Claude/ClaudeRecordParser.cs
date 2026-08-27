using System.Globalization;
using System.Text;
using System.Text.Json;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Text;

namespace SessionSearch.Infrastructure.Claude;

internal enum ClaudeSessionIdState
{
    Missing,
    Valid,
    Invalid,
}

internal enum ClaudeRecordDisposition
{
    Searchable,
    RecognizedIgnored,
    Unsupported,
}

internal readonly record struct ClaudeRecordInspection(
    ClaudeSessionIdState SessionIdState,
    Guid SessionId,
    bool IsSubstantiveTopLevel,
    bool IsSubstantiveMessage,
    string? Directory,
    string? Branch,
    string? Model,
    DateTimeOffset? TimestampUtc);

internal static class ClaudeRecordParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = ProviderLimits.MaxJsonDepth,
    };

    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json) =>
        JsonDocument.Parse(utf8Json, DocumentOptions);

    public static ClaudeRecordInspection Inspect(JsonElement root)
    {
        (ClaudeSessionIdState sessionIdState, Guid sessionId) = ReadSessionId(root);
        string? type = ReadString(root, "type");
        bool isMeta = ReadBoolean(root, "isMeta");
        bool isSidechain = ReadBoolean(root, "isSidechain");
        bool isSubstantiveMessage = IsSubstantiveMessage(root, type, isMeta);

        string? model = null;
        if (root.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.Object)
        {
            model = ReadString(message, "model");
        }

        return new ClaudeRecordInspection(
            sessionIdState,
            sessionId,
            isSubstantiveMessage && !isSidechain,
            isSubstantiveMessage,
            ReadString(root, "cwd"),
            ReadString(root, "gitBranch"),
            model,
            ReadTimestamp(root));
    }

    public static bool IsKnownRecordType(JsonElement root) =>
        ClassifyRecordType(ReadString(root, "type")) != ClaudeRecordDisposition.Unsupported;

    public static bool TryAppendRecords(
        JsonElement root,
        ProviderSource source,
        long lineOffset,
        List<ProviderRecord> destination)
    {
        string? type = ReadString(root, "type");
        DateTimeOffset? timestamp = ReadTimestamp(root);
        var pending = new List<PendingRecord>(2);
        bool extractionLimitExceeded = false;

        switch (type)
        {
            case "custom-title":
                AddSimpleText(
                    ReadString(root, "customTitle"),
                    ProviderRecordKind.ExplicitName,
                    null,
                    pending,
                    ref extractionLimitExceeded);
                break;
            case "ai-title":
                AddSimpleText(
                    ReadString(root, "aiTitle"),
                    ProviderRecordKind.AiTitle,
                    null,
                    pending,
                    ref extractionLimitExceeded);
                break;
            case "user":
                AppendUserRecords(root, pending, ref extractionLimitExceeded);
                break;
            case "assistant":
                AppendAssistantRecords(root, pending, ref extractionLimitExceeded);
                break;
        }

        if (extractionLimitExceeded)
        {
            return false;
        }

        long sequence = lineOffset;
        foreach (PendingRecord record in pending)
        {
            foreach (string segment in SplitForStorage(record.Text))
            {
                destination.Add(new ProviderRecord(
                    source.Owner,
                    source.RelativePath,
                    sequence,
                    timestamp,
                    record.Kind,
                    segment,
                    record.UserTextKind,
                    source.Kind == ProviderSourceKind.Child));
                sequence++;
            }
        }

        return true;
    }

    public static (ClaudeSessionIdState State, Guid SessionId) ReadSessionId(JsonElement root)
    {
        if (!root.TryGetProperty("sessionId", out JsonElement sessionId))
        {
            return (ClaudeSessionIdState.Missing, Guid.Empty);
        }

        if (sessionId.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(sessionId.GetString(), "D", out Guid parsed))
        {
            return (ClaudeSessionIdState.Invalid, Guid.Empty);
        }

        return (ClaudeSessionIdState.Valid, parsed);
    }

    private static void AppendUserRecords(
        JsonElement root,
        List<PendingRecord> pending,
        ref bool extractionLimitExceeded)
    {
        string? userType = ReadString(root, "userType");
        if (ReadBoolean(root, "isMeta")
            || ReadBoolean(root, "isSynthetic")
            || (userType is not null
                && !string.Equals(userType, "external", StringComparison.Ordinal))
            || !root.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !string.Equals(ReadString(message, "role"), "user", StringComparison.Ordinal)
            || !message.TryGetProperty("content", out JsonElement content))
        {
            return;
        }

        var humanText = new ClaudeTextAccumulator();
        var toolText = new ClaudeTextAccumulator();
        AppendContent(content, humanText, toolText, userContext: true);
        extractionLimitExceeded = humanText.Exceeded || toolText.Exceeded;
        if (extractionLimitExceeded)
        {
            return;
        }

        AddAccumulator(
            humanText,
            ProviderRecordKind.UserText,
            UserTextKind.Human,
            pending);
        AddAccumulator(
            toolText,
            ProviderRecordKind.ToolText,
            UserTextKind.Tool,
            pending);
    }

    private static void AppendAssistantRecords(
        JsonElement root,
        List<PendingRecord> pending,
        ref bool extractionLimitExceeded)
    {
        if (ReadBoolean(root, "isMeta")
            || !root.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !string.Equals(ReadString(message, "role"), "assistant", StringComparison.Ordinal)
            || !message.TryGetProperty("content", out JsonElement content))
        {
            return;
        }

        var assistantText = new ClaudeTextAccumulator();
        var toolText = new ClaudeTextAccumulator();
        AppendContent(content, assistantText, toolText, userContext: false);
        extractionLimitExceeded = assistantText.Exceeded || toolText.Exceeded;
        if (extractionLimitExceeded)
        {
            return;
        }

        AddAccumulator(
            assistantText,
            ProviderRecordKind.AssistantText,
            null,
            pending);
        AddAccumulator(
            toolText,
            ProviderRecordKind.ToolText,
            UserTextKind.Tool,
            pending);
    }

    private static void AppendContent(
        JsonElement content,
        ClaudeTextAccumulator primaryText,
        ClaudeTextAccumulator toolText,
        bool userContext)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            primaryText.Add(content.GetString());
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                primaryText.Add(item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? blockType = ReadString(item, "type");
            if (string.Equals(blockType, "text", StringComparison.Ordinal))
            {
                primaryText.Add(ReadString(item, "text"));
            }
            else if (userContext && string.Equals(blockType, "tool_result", StringComparison.Ordinal))
            {
                if (item.TryGetProperty("content", out JsonElement toolResult))
                {
                    AppendToolResult(toolResult, toolText);
                }
            }
            else if (!userContext && string.Equals(blockType, "tool_use", StringComparison.Ordinal))
            {
                toolText.Add(ReadString(item, "name"));
                if (item.TryGetProperty("input", out JsonElement input))
                {
                    AppendStringLeaves(input, toolText);
                }
            }
        }
    }

    private static void AppendToolResult(JsonElement content, ClaudeTextAccumulator toolText)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            toolText.Add(content.GetString());
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                toolText.Add(item.GetString());
            }
            else if (item.ValueKind == JsonValueKind.Object
                && string.Equals(ReadString(item, "type"), "text", StringComparison.Ordinal))
            {
                toolText.Add(ReadString(item, "text"));
            }
        }
    }

    private static void AppendStringLeaves(JsonElement value, ClaudeTextAccumulator text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                text.Add(value.GetString());
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    AppendStringLeaves(item, text);
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    AppendStringLeaves(property.Value, text);
                }

                break;
        }
    }

    private static bool IsSubstantiveMessage(JsonElement root, string? type, bool isMeta)
    {
        string? userType = ReadString(root, "userType");
        if (isMeta
            || ReadBoolean(root, "isSynthetic")
            || (type == "user"
                && userType is not null
                && !string.Equals(userType, "external", StringComparison.Ordinal))
            || (type is not "user" and not "assistant")
            || !root.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? expectedRole = type == "user" ? "user" : "assistant";
        return string.Equals(ReadString(message, "role"), expectedRole, StringComparison.Ordinal);
    }

    private static void AddSimpleText(
        string? value,
        ProviderRecordKind kind,
        UserTextKind? userTextKind,
        List<PendingRecord> pending,
        ref bool extractionLimitExceeded)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(value) > ProviderLimits.MaxExtractedTextBytes)
        {
            extractionLimitExceeded = true;
            return;
        }

        pending.Add(new PendingRecord(kind, value, userTextKind));
    }

    private static void AddAccumulator(
        ClaudeTextAccumulator accumulator,
        ProviderRecordKind kind,
        UserTextKind? userTextKind,
        List<PendingRecord> pending)
    {
        string? value = accumulator.GetText();
        if (!string.IsNullOrEmpty(value))
        {
            pending.Add(new PendingRecord(kind, value, userTextKind));
        }
    }

    private static IEnumerable<string> SplitForStorage(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= ProviderLimits.MaxStoredSegmentBytes)
        {
            yield return value;
            yield break;
        }

        int segmentStart = 0;
        int segmentBytes = 0;
        int index = 0;
        while (index < value.Length)
        {
            Rune rune = Rune.GetRuneAt(value, index);
            if (segmentBytes > 0
                && segmentBytes + rune.Utf8SequenceLength > ProviderLimits.MaxStoredSegmentBytes)
            {
                yield return value[segmentStart..index];
                segmentStart = index;
                segmentBytes = 0;
            }

            segmentBytes += rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }

        if (segmentStart < value.Length)
        {
            yield return value[segmentStart..];
        }
    }

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static ClaudeRecordDisposition ClassifyRecordType(string? type) => type switch
    {
        "user" or "assistant" or "ai-title" or "custom-title" =>
            ClaudeRecordDisposition.Searchable,
        "system" or
        "last-prompt" or
        "summary" or
        "progress" or
        "file-history-snapshot" or
        "queue-operation" or
        "pr-link" or
        "agent-name" or
        "attachment" or
        "permission-mode" or
        "mode" or
        "file-history-delta" or
        "atis-latch" or
        "bridge-session" or
        "frame-link" or
        "artifact-autoreact-ledger" or
        "artifact-comment-monitor" or
        "cost-state" or
        "fork-context-ref" => ClaudeRecordDisposition.RecognizedIgnored,
        _ => ClaudeRecordDisposition.Unsupported,
    };

    private static bool ReadBoolean(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ReadTimestamp(JsonElement value)
    {
        string? timestamp = ReadString(value, "timestamp");
        return DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private sealed record PendingRecord(
        ProviderRecordKind Kind,
        string Text,
        UserTextKind? UserTextKind);

    private sealed class ClaudeTextAccumulator
    {
        private readonly StringBuilder builder = new();
        private int byteCount;

        public bool Exceeded { get; private set; }

        public void Add(string? value)
        {
            if (Exceeded || string.IsNullOrEmpty(value))
            {
                return;
            }

            int separatorBytes = builder.Length == 0 ? 0 : 1;
            int valueBytes = Encoding.UTF8.GetByteCount(value);
            if ((long)byteCount + separatorBytes + valueBytes > ProviderLimits.MaxExtractedTextBytes)
            {
                Exceeded = true;
                builder.Clear();
                byteCount = 0;
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
                byteCount++;
            }

            builder.Append(value);
            byteCount += valueBytes;
        }

        public string? GetText() => builder.Length == 0 ? null : builder.ToString();
    }
}
