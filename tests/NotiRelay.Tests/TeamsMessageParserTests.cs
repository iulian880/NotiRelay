using System.Text.Json;
using NotiRelay;
using Xunit;

public class TeamsMessageParserTests
{
    private static string Payload(string bodyHtml, object[]? attachments = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["id"] = "1700000000000",
            ["chatId"] = "19:chat@thread.v2",
            ["messageType"] = "message",
            ["from"] = new { user = new { id = "u1", displayName = "Ana Pop" } },
            ["body"] = new { contentType = "html", content = bodyHtml },
            ["attachments"] = attachments ?? Array.Empty<object>()
        };

        return JsonSerializer.Serialize(new { message });
    }

    [Fact]
    public void PlainTextMessage_StillParses()
    {
        bool ok = TeamsMessageParser.TryParse(
            Payload("<p>Salut</p>"), out var msg, out var error);

        Assert.True(ok, error);
        Assert.Equal("Salut", msg!.Text);
    }

    [Fact]
    public void FileOnlyMessage_ParsesWithAttachmentName()
    {
        string json = Payload(
            "<attachment id=\"abc123\"></attachment>",
            new object[]
            {
                new
                {
                    id = "abc123",
                    contentType = "reference",
                    contentUrl = "https://contoso.sharepoint.com/raport.pdf",
                    name = "raport.pdf"
                }
            });

        bool ok = TeamsMessageParser.TryParse(json, out var msg, out var error);

        Assert.True(ok, error);
        Assert.Contains("raport.pdf", msg!.Text);
    }

    [Fact]
    public void TextWithFile_KeepsTextAndListsFile()
    {
        string json = Payload(
            "<p>Vezi atasat</p><attachment id=\"abc123\"></attachment>",
            new object[] { new { id = "abc123", contentType = "reference", name = "plan.xlsx" } });

        bool ok = TeamsMessageParser.TryParse(json, out var msg, out var error);

        Assert.True(ok, error);
        Assert.Contains("Vezi atasat", msg!.Text);
        Assert.Contains("plan.xlsx", msg.Text);
    }

    [Fact]
    public void ImageWithoutAlt_Parses()
    {
        string json = Payload(
            "<p><img src=\"https://graph.microsoft.com/v1.0/chats/x/messages/y/hostedContents/z/$value\" itemtype=\"http://schema.skype.com/AMSImage\"></p>");

        bool ok = TeamsMessageParser.TryParse(json, out var msg, out var error);

        Assert.True(ok, error);
        Assert.Equal("[Image]", msg!.Text);
    }

    [Fact]
    public void AttachmentTagWithoutMetadata_Parses()
    {
        string json = Payload("<attachment id=\"abc123\"></attachment>");

        bool ok = TeamsMessageParser.TryParse(json, out var msg, out var error);

        Assert.True(ok, error);
        Assert.Equal("[Attachment]", msg!.Text);
    }

    [Fact]
    public void CompleteMessageWithoutText_IsReportedAsNothingToForward()
    {
        string json = Payload("<systemEventMessage/>")
            .Replace("\"messageType\":\"message\"", "\"messageType\":\"systemEventMessage\"");

        bool ok = TeamsMessageParser.TryParse(
            json, out var msg, out var error, out var nothingToForward);

        Assert.False(ok);
        Assert.Null(msg);
        Assert.NotNull(nothingToForward);
        Assert.Equal("19:chat@thread.v2:1700000000000", MissedMessages.MessageKey(nothingToForward!));
        Assert.Contains("systemEventMessage", error);
    }

    [Fact]
    public void TruncatedFile_IsNotNothingToForward_SoItIsRetried()
    {
        string json = Payload("<p>Salut</p>")[..40];

        bool ok = TeamsMessageParser.TryParse(json, out _, out _, out var nothingToForward);

        Assert.False(ok);
        Assert.Null(nothingToForward);
    }
}
