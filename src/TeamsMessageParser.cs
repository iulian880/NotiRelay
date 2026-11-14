
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotiRelay;

public sealed record TeamsInboxMessage(
    string MessageId,
    string ConversationId,
    string SenderName,
    string SenderId,
    string Text,
    DateTimeOffset? CreatedAt,
    string WebUrl,
    string MessageType);

public static class TeamsMessageParser
{
    private static readonly Regex ScriptStyleRegex = new(
        @"<(script|style)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.Compiled);

    private static readonly Regex CommentRegex = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EmojiTagRegex = new(
        @"<emoji\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImageTagRegex = new(
        @"<img\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AttachmentTagRegex = new(
        @"<attachment\b[^>]*>.*?</attachment\s*>|<attachment\b[^>]*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.Compiled);

    private const string ImagePlaceholder = "[Image]";
    private const string AttachmentPlaceholder = "[Attachment]";

    private static readonly Regex AltAttributeRegex = new(
        @"\balt\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LineBreakRegex = new(
        @"<\s*br\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockStartRegex = new(
        @"<\s*(p|div|li|tr|blockquote|h[1-6])\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockEndRegex = new(
        @"<\s*/\s*(p|div|li|tr|blockquote|h[1-6])\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]*>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HorizontalWhitespaceRegex = new(
        @"[ \t\f\v]+",
        RegexOptions.Compiled);

    private static readonly Regex ExcessiveNewlinesRegex = new(
        @"\n{3,}",
        RegexOptions.Compiled);

    public static bool TryParse(
        string json,
        out TeamsInboxMessage? result,
        out string error) =>
        TryParse(json, out result, out error, out _);

    /// <summary>
    /// Like <see cref="TryParse(string, out TeamsInboxMessage?, out string)"/>, but tells a
    /// complete message that simply has nothing to forward (a system event, a deleted
    /// message, a card without text) apart from a file that may still be syncing:
    /// <paramref name="nothingToForward"/> is set only in the first case, so callers can
    /// record it as handled instead of retrying it on every launch.
    /// </summary>
    public static bool TryParse(
        string json,
        out TeamsInboxMessage? result,
        out string error,
        out TeamsInboxMessage? nothingToForward)
    {
        result = null;
        nothingToForward = null;
        error = string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "JSON root must be an object.";
                return false;
            }

            if (!root.TryGetProperty("message", out JsonElement message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                error = "Missing message object.";
                return false;
            }

            string messageId =
                GetString(root, "messageId") ??
                GetString(message, "id") ??
                string.Empty;

            string conversationId =
                GetString(root, "conversationId") ??
                GetString(message, "chatId") ??
                string.Empty;

            if (string.IsNullOrWhiteSpace(messageId))
            {
                error = "Message ID is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                error = "Conversation ID is missing.";
                return false;
            }

            string senderName = "Unknown sender";
            string senderId = string.Empty;

            if (message.TryGetProperty("from", out JsonElement from) &&
                from.ValueKind == JsonValueKind.Object &&
                from.TryGetProperty("user", out JsonElement user) &&
                user.ValueKind == JsonValueKind.Object)
            {
                senderName =
                    GetString(user, "displayName") ?? senderName;

                senderId =
                    GetString(user, "id") ?? string.Empty;
            }

            string content = string.Empty;
            string contentType = string.Empty;

            if (message.TryGetProperty("body", out JsonElement body) &&
                body.ValueKind == JsonValueKind.Object)
            {
                content = GetString(body, "content") ?? string.Empty;

                contentType =
                    GetString(body, "contentType") ?? string.Empty;
            }

            // Files shared in Teams arrive as an <attachment> placeholder in
            // the body plus metadata in message.attachments. Without this the
            // body has no text left and the message can never be parsed.
            List<string> attachmentNames = GetAttachmentNames(message);

            bool hasAttachmentTag =
                AttachmentTagRegex.IsMatch(content);

            string text = string.IsNullOrWhiteSpace(content)
                ? string.Empty
                : ConvertToPlainText(content, contentType);

            if (attachmentNames.Count > 0)
            {
                string files = string.Join(
                    "\n",
                    attachmentNames.Select(name => "\U0001F4CE " + name));

                text = string.IsNullOrWhiteSpace(text)
                    ? files
                    : text + "\n" + files;
            }
            else if (string.IsNullOrWhiteSpace(text) && hasAttachmentTag)
            {
                text = AttachmentPlaceholder;
            }

            DateTimeOffset? createdAt = null;

            string? timestamp = GetString(
                message,
                "createdDateTime");

            if (DateTimeOffset.TryParse(
                timestamp,
                out DateTimeOffset parsedTimestamp))
            {
                createdAt = parsedTimestamp.ToUniversalTime();
            }

            string webUrl =
                GetString(message, "webUrl") ?? string.Empty;

            string messageType =
                GetString(message, "messageType") ?? "message";

            var parsed = new TeamsInboxMessage(
                messageId,
                conversationId,
                senderName,
                senderId,
                text,
                createdAt,
                webUrl,
                messageType);

            if (string.IsNullOrWhiteSpace(text))
            {
                // Complete, but nothing to forward: permanent, not a sync problem.
                error = $"Message has no readable text (type: {messageType}).";
                nothingToForward = parsed with { Text = string.Empty };
                return false;
            }

            result = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = "Invalid Teams JSON: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "Message parsing failed: " + ex.Message;
            return false;
        }
    }

    private static string? GetString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(
            propertyName,
            out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static List<string> GetAttachmentNames(JsonElement message)
    {
        var names = new List<string>();

        if (!message.TryGetProperty("attachments", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (JsonElement attachment in list.EnumerateArray())
        {
            if (attachment.ValueKind != JsonValueKind.Object)
                continue;

            string? name = GetString(attachment, "name");

            if (!string.IsNullOrWhiteSpace(name))
                names.Add(NormalizeText(name));
        }

        return names;
    }

    private static string ConvertToPlainText(
        string content,
        string contentType)
    {
        if (!string.Equals(
            contentType,
            "html",
            StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeText(WebUtility.HtmlDecode(content));
        }

        string text = content;

        // Remove scripts, styles and HTML comments.
        text = ScriptStyleRegex.Replace(text, string.Empty);
        text = CommentRegex.Replace(text, string.Empty);

        // Convert Teams custom emoji tags using their alt attributes.
        text = EmojiTagRegex.Replace(text, match =>
            ExtractAltText(match.Value));

        // File placeholders; file names are added from message.attachments.
        text = AttachmentTagRegex.Replace(text, string.Empty);

        // Convert image alt text, or a placeholder for images without one.
        text = ImageTagRegex.Replace(text, match =>
        {
            string alt = ExtractAltText(match.Value);

            return string.IsNullOrWhiteSpace(alt)
                ? " " + ImagePlaceholder + " "
                : alt;
        });

        // Preserve meaningful line breaks.
        text = LineBreakRegex.Replace(text, "\n");

        // Preserve paragraph and block boundaries.
        text = BlockStartRegex.Replace(text, "\n");
        text = BlockEndRegex.Replace(text, "\n");

        // Remove remaining HTML tags.
        text = HtmlTagRegex.Replace(text, string.Empty);

        // Decode HTML entities.
        text = WebUtility.HtmlDecode(text);

        return NormalizeText(text);
    }

    private static string ExtractAltText(string tag)
    {
        Match match = AltAttributeRegex.Match(tag);

        if (!match.Success)
            return string.Empty;

        string value =
            match.Groups[1].Success ? match.Groups[1].Value :
            match.Groups[2].Success ? match.Groups[2].Value :
            match.Groups[3].Value;

        return WebUtility.HtmlDecode(value);
    }

    private static string NormalizeText(string text)
    {
        text = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ')
            .Replace("\u200B", string.Empty);

        text = HorizontalWhitespaceRegex.Replace(text, " ");
        text = ExcessiveNewlinesRegex.Replace(text, "\n\n");

        return text.Trim();
    }
}
