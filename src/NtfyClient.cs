
using System.Net.Http;
using System.Text;

namespace NotiRelay;

public sealed class NtfyClient
{
    private readonly string _topic;
    private readonly HttpClient _httpClient;

    public NtfyClient(string topic, HttpClient httpClient)
    {
        _topic = topic;
        _httpClient = httpClient;
    }

    public async Task SendMessageAsync(
        string message,
        string title = "Microsoft Teams",
        string priority = "high",
        CancellationToken cancellationToken = default)
    {
        string url =
            "https://ntfy.sh/" + Uri.EscapeDataString(_topic);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            url);

        request.Content = new StringContent(
            message,
            Encoding.UTF8,
            "text/plain");

        request.Headers.TryAddWithoutValidation(
            "Title",
            EncodeHeaderValue(title));
        request.Headers.TryAddWithoutValidation("Priority", priority);

        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    // HttpClient rejects non-ASCII header values (e.g. a sender name with
    // diacritics), which made every delivery attempt fail. ntfy accepts
    // RFC 2047 encoded-words, so non-ASCII values are sent that way.
    private static string EncodeHeaderValue(string value)
    {
        if (value.All(c => c >= 0x20 && c < 0x7F))
            return value;

        return "=?UTF-8?B?" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) +
            "?=";
    }

    public Task SendSyntheticTestAsync(
        CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(
            "Test reușit! NotiRelay comunică cu ntfy.",
            "NOTIRELAY TEST",
            "high",
            cancellationToken);
    }
}
