using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using NotiRelay;
using Xunit;

public class NtfyErrorsTests
{
    [Fact]
    public void NetworkFailure_ShowsTheRealReason_AndIsAnOutage()
    {
        var ex = new HttpRequestException("No such host is known. (ntfy.sh:443)");

        Assert.Equal(
            "could not reach ntfy.sh: No such host is known. (ntfy.sh:443)",
            NtfyErrors.Describe(ex));
        Assert.True(NtfyErrors.IsOutage(ex));
    }

    [Fact]
    public void TlsFailure_ShowsTheInnerReason()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid."));

        Assert.Equal(
            "could not reach ntfy.sh: The remote certificate is invalid.",
            NtfyErrors.Describe(ex));
    }

    [Fact]
    public void Timeout_IsAnOutage()
    {
        var ex = new TaskCanceledException();

        Assert.Equal("ntfy.sh did not answer within 15 seconds", NtfyErrors.Describe(ex));
        Assert.True(NtfyErrors.IsOutage(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "ntfy.sh rate limit reached (429)", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ntfy.sh answered 503 (ServiceUnavailable)", true)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, "the network proxy asked for sign-in (407)", true)]
    [InlineData(HttpStatusCode.Forbidden, "ntfy.sh answered 403 (Forbidden)", true)]
    [InlineData(HttpStatusCode.BadRequest, "ntfy.sh answered 400 (BadRequest)", false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "ntfy.sh answered 413 (RequestEntityTooLarge)", false)]
    public void HttpStatus_IsDescribed_AndOnlyRequestErrorsAreItemSpecific(
        HttpStatusCode status, string expected, bool outage)
    {
        var ex = new HttpRequestException("Response status code does not indicate success.", null, status);

        Assert.Equal(expected, NtfyErrors.Describe(ex));
        Assert.Equal(outage, NtfyErrors.IsOutage(ex));
    }
}
