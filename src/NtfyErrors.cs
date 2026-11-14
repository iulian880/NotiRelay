using System.Net;
using System.Net.Http;

namespace NotiRelay;

/// <summary>
/// Turns delivery exceptions into a reason a person can act on, and says whether the
/// failure is about ntfy.sh as a whole (an outage: every queued item would fail the same
/// way) or about one request. Messages never contain the topic.
/// </summary>
public static class NtfyErrors
{
    public static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException =>
            "ntfy.sh did not answer within 15 seconds",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "ntfy.sh rate limit reached (429)",
        HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired } =>
            "the network proxy asked for sign-in (407)",
        HttpRequestException { StatusCode: { } status } =>
            $"ntfy.sh answered {(int)status} ({status})",
        HttpRequestException http =>
            "could not reach ntfy.sh: " + Detail(http),
        _ => ex.GetType().Name + ": " + ex.Message
    };

    public static bool IsOutage(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => true,
        HttpRequestException { StatusCode: { } status } => !IsRequestSpecific(status),
        HttpRequestException => true,
        _ => false
    };

    // Only these say something about the individual message; anything else
    // (network, proxy, rate limit, server errors, access) affects every item.
    private static bool IsRequestSpecific(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest
            or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.RequestUriTooLong
            or HttpStatusCode.UnprocessableEntity;

    // TLS failures put the useful part in the inner exception.
    private static string Detail(HttpRequestException http) =>
        http.InnerException is { } inner &&
        http.Message.Contains("inner exception", StringComparison.OrdinalIgnoreCase)
            ? inner.Message
            : http.Message;
}
