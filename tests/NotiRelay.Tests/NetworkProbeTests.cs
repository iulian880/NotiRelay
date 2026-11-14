using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using NotiRelay;
using Xunit;

public class NetworkProbeTests
{
    private static readonly IPAddress Address = IPAddress.Parse("159.203.148.75");

    private static NetworkProbe Probe(
        Func<Uri, Uri?>? proxyFor = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, Task>? connect = null,
        Func<Uri, CancellationToken, Task<HealthResponse>>? get = null,
        TimeSpan? stepTimeout = null) =>
        new(
            proxyFor ?? (_ => null),
            resolve ?? ((_, _) => Task.FromResult(new[] { Address })),
            connect ?? ((_, _, _) => Task.CompletedTask),
            get ?? ((_, _) => Task.FromResult(new HealthResponse(200, "OK", "{\"healthy\":true}"))),
            stepTimeout);

    [Fact]
    public async Task AllStepsSucceed_ReportsEachStepAndReachable()
    {
        NetworkProbeResult result = await Probe().RunAsync(CancellationToken.None);
        string text = string.Join("\n", result.Lines);

        Assert.Contains("Proxy: none", text);
        Assert.Contains("159.203.148.75", text);
        Assert.Contains("TCP 443 to 159.203.148.75: connected", text);
        Assert.Contains("HTTPS GET https://ntfy.sh/v1/health: 200 OK", text);
        Assert.Contains("healthy", text);
        Assert.StartsWith("ntfy.sh reachable", result.Summary);
    }

    [Fact]
    public async Task DnsFails_SkipsTcp_StillTriesHttps_AndSummaryNamesDns()
    {
        bool httpsTried = false;

        NetworkProbeResult result = await Probe(
            resolve: (_, _) => throw new SocketException((int)SocketError.HostNotFound),
            get: (_, _) =>
            {
                httpsTried = true;
                throw new HttpRequestException("No such host is known.");
            }).RunAsync(CancellationToken.None);

        string text = string.Join("\n", result.Lines);

        Assert.Contains("DNS: failed", text);
        Assert.Contains("SocketException", text);
        Assert.Contains("TCP 443: skipped", text);
        Assert.True(httpsTried);
        Assert.Contains("DNS", result.Summary);
        Assert.StartsWith("ntfy.sh not reachable", result.Summary);
    }

    [Fact]
    public async Task Proxy_IsShownWithoutCredentials()
    {
        NetworkProbeResult result = await Probe(
            proxyFor: _ => new Uri("http://alice:s3cr3t@proxy.corp.example:8080/"))
            .RunAsync(CancellationToken.None);

        string text = string.Join("\n", result.Lines);

        Assert.Contains("proxy.corp.example:8080", text);
        Assert.DoesNotContain("s3cr3t", text);
        Assert.DoesNotContain("alice", text);
    }

    [Fact]
    public async Task ProxyReturningDestination_MeansDirect()
    {
        NetworkProbeResult result = await Probe(proxyFor: uri => uri)
            .RunAsync(CancellationToken.None);

        Assert.Contains("Proxy: none", string.Join("\n", result.Lines));
    }

    [Fact]
    public async Task DnsTimeout_IsReported()
    {
        NetworkProbeResult result = await Probe(
            resolve: async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Array.Empty<IPAddress>();
            },
            stepTimeout: TimeSpan.FromMilliseconds(50))
            .RunAsync(CancellationToken.None);

        Assert.Contains("DNS: no answer within", string.Join("\n", result.Lines));
    }

    [Fact]
    public async Task TcpFails_IsReported()
    {
        NetworkProbeResult result = await Probe(
            connect: (_, _, _) => throw new SocketException((int)SocketError.ConnectionRefused))
            .RunAsync(CancellationToken.None);

        Assert.Contains("TCP 443 to 159.203.148.75: failed", string.Join("\n", result.Lines));
    }

    [Fact]
    public async Task HttpsFails_ReportsChainAndSummary()
    {
        NetworkProbeResult result = await Probe(
            get: (_, _) => throw new HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new IOException("Received an unexpected EOF")))
            .RunAsync(CancellationToken.None);

        string text = string.Join("\n", result.Lines);

        Assert.Contains("HTTPS GET https://ntfy.sh/v1/health: failed", text);
        Assert.Contains("Caused by: System.IO.IOException: Received an unexpected EOF", text);
        Assert.StartsWith("ntfy.sh not reachable", result.Summary);
        Assert.Contains("HTTPS", result.Summary);
    }

    [Fact]
    public async Task HttpsAnswersWithError_IsStillReachableButFlagged()
    {
        NetworkProbeResult result = await Probe(
            get: (_, _) => Task.FromResult(new HealthResponse(403, "Forbidden", "blocked")))
            .RunAsync(CancellationToken.None);

        Assert.Contains("403 Forbidden", string.Join("\n", result.Lines));
        Assert.Contains("403", result.Summary);
    }

    [Fact]
    public async Task Summary_HidesProxyCredentialsFromErrorMessages()
    {
        NetworkProbeResult result = await Probe(
            proxyFor: _ => new Uri("http://alice:s3cr3t@proxy:8080/"),
            get: (_, _) => throw new HttpRequestException(
                "The proxy tunnel request to proxy 'http://alice:s3cr3t@proxy:8080/' " +
                "failed with status code '407'."))
            .RunAsync(CancellationToken.None);

        Assert.DoesNotContain("s3cr3t", result.Summary);
        Assert.DoesNotContain("s3cr3t", string.Join("\n", result.Lines));
    }
}
