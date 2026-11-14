using System.Net;
using System.Net.Http;
using System.Text;
using NotiRelay;
using Xunit;

public class NtfyClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<string> SentTitle(string title)
    {
        var handler = new CapturingHandler();
        var client = new NtfyClient("topic", new HttpClient(handler));

        await client.SendMessageAsync("body", title, "high");

        return string.Join("", handler.Request!.Headers.GetValues("Title"));
    }

    private static string DecodeRfc2047(string value)
    {
        const string prefix = "=?UTF-8?B?";
        if (!value.StartsWith(prefix) || !value.EndsWith("?="))
            return value;
        string b64 = value[prefix.Length..^2];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    [Fact]
    public async Task AsciiTitle_IsSentUnchanged()
    {
        Assert.Equal("Ana Pop", await SentTitle("Ana Pop"));
    }

    [Fact]
    public async Task DiacriticsTitle_IsAsciiOnTheWire_AndDecodesBack()
    {
        string sent = await SentTitle("Ștefan Țurcanu");

        // SocketsHttpHandler rejects non-ASCII header values.
        Assert.All(sent, c => Assert.True(c < 128, $"non-ASCII char '{c}' in header"));
        Assert.Equal("Ștefan Țurcanu", DecodeRfc2047(sent));
    }
}
