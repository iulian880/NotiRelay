using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NotiRelay;

public sealed record HealthResponse(int StatusCode, string ReasonPhrase, string Body);

public sealed record NetworkProbeResult(IReadOnlyList<string> Lines, string Summary);

/// <summary>
/// Read-only network check for the debug log: which proxy Windows would use for ntfy.sh,
/// what DNS answers, whether port 443 accepts a connection, and what
/// https://ntfy.sh/v1/health answers through the app's own HttpClient (the same route real
/// deliveries take). It never publishes anything and never tries another route.
/// The steps are injected so the reporting logic is unit-tested without a network.
/// </summary>
public sealed class NetworkProbe
{
    public const string Host = "ntfy.sh";
    public const int Port = 443;
    public static readonly Uri HealthUri = new("https://ntfy.sh/v1/health");

    private const int MaxBodyLength = 200;

    private readonly Func<Uri, Uri?> _proxyFor;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPAddress, int, CancellationToken, Task> _connect;
    private readonly Func<Uri, CancellationToken, Task<HealthResponse>> _get;
    private readonly TimeSpan _stepTimeout;

    public NetworkProbe(
        Func<Uri, Uri?> proxyFor,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, Task> connect,
        Func<Uri, CancellationToken, Task<HealthResponse>> get,
        TimeSpan? stepTimeout = null)
    {
        _proxyFor = proxyFor;
        _resolve = resolve;
        _connect = connect;
        _get = get;
        _stepTimeout = stepTimeout ?? TimeSpan.FromSeconds(10);
    }

    public static NetworkProbe CreateDefault(HttpClient httpClient) => new(
        uri => HttpClient.DefaultProxy.IsBypassed(uri)
            ? null
            : HttpClient.DefaultProxy.GetProxy(uri),
        (host, token) => Dns.GetHostAddressesAsync(host, token),
        async (address, port, token) =>
        {
            using var socket = new Socket(
                address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, port), token);
        },
        async (uri, token) =>
        {
            using HttpResponseMessage response = await httpClient.GetAsync(uri, token);
            string body = await response.Content.ReadAsStringAsync(token);
            return new HealthResponse(
                (int)response.StatusCode, response.ReasonPhrase ?? string.Empty, body);
        });

    public async Task<NetworkProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string> { "Network check for ntfy.sh:" };

        // 1. Proxy (what Windows' proxy settings say for this address).
        Uri? proxy = null;
        try
        {
            proxy = _proxyFor(HealthUri);
            if (proxy is not null && proxy == HealthUri)
                proxy = null;

            lines.Add(proxy is null
                ? "  Proxy: none (direct connection)"
                : $"  Proxy: {WithoutCredentials(proxy)}");
        }
        catch (Exception ex)
        {
            lines.Add("  Proxy: could not be read: " + OneLine(ex));
        }

        // 2. DNS.
        IPAddress[] addresses = Array.Empty<IPAddress>();
        string? dnsProblem = null;
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = Timeout(cancellationToken);
            addresses = await _resolve(Host, timeout.Token);
            lines.Add(addresses.Length == 0
                ? $"  DNS: {Host} returned no addresses ({watch.ElapsedMilliseconds} ms)"
                : $"  DNS: {Host} -> {string.Join(", ", addresses.Select(a => a.ToString()))} " +
                  $"({watch.ElapsedMilliseconds} ms)");
            if (addresses.Length == 0)
                dnsProblem = "DNS returned no addresses";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            dnsProblem = "DNS lookup timed out";
            lines.Add($"  DNS: no answer within {_stepTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dnsProblem = "DNS lookup failed";
            lines.Add($"  DNS: failed after {watch.ElapsedMilliseconds} ms: {OneLine(ex)}");
        }

        // 3. TCP connect to port 443 (direct, without the proxy).
        string? tcpProblem = null;
        if (addresses.Length == 0)
        {
            lines.Add($"  TCP {Port}: skipped (no address)");
        }
        else
        {
            IPAddress target =
                addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses[0];

            watch.Restart();
            try
            {
                using var timeout = Timeout(cancellationToken);
                await _connect(target, Port, timeout.Token);
                lines.Add($"  TCP {Port} to {target}: connected ({watch.ElapsedMilliseconds} ms)");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                tcpProblem = $"TCP connection to port {Port} timed out";
                lines.Add($"  TCP {Port} to {target}: no answer within {_stepTimeout.TotalSeconds:0.#} s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tcpProblem = $"TCP connection to port {Port} failed";
                lines.Add($"  TCP {Port} to {target}: failed after {watch.ElapsedMilliseconds} ms: {OneLine(ex)}");
            }
        }

        // 4. HTTPS through the app's HttpClient (uses the proxy, like real deliveries).
        watch.Restart();
        string summary;
        try
        {
            HealthResponse response = await _get(HealthUri, cancellationToken);
            long ms = watch.ElapsedMilliseconds;

            lines.Add($"  HTTPS GET {HealthUri}: {response.StatusCode} {response.ReasonPhrase} " +
                      $"({ms} ms) {Shorten(response.Body)}");

            summary = response.StatusCode is >= 200 and < 300
                ? $"ntfy.sh reachable (HTTPS {response.StatusCode} in {ms} ms)."
                : $"ntfy.sh answered {response.StatusCode} {response.ReasonPhrase} to the health check.";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            lines.Add($"  HTTPS GET {HealthUri}: failed after {watch.ElapsedMilliseconds} ms:");
            lines.AddRange(DebugReport.Chain(ex)
                .Split(Environment.NewLine)
                .Select(line => "    " + line));

            string httpsProblem = "HTTPS request failed: " + Innermost(ex).Message;

            // Through a proxy, the direct DNS/TCP results do not decide the outcome.
            string reason = proxy is not null
                ? httpsProblem + " (via proxy)"
                : dnsProblem ?? tcpProblem ?? httpsProblem;

            summary = "ntfy.sh not reachable: " + reason + ".";
        }

        // .NET error messages can contain the proxy address including its user and password.
        return new NetworkProbeResult(
            lines.Select(DebugReport.HideUrlCredentials).ToList(),
            DebugReport.HideUrlCredentials(summary));
    }

    private CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_stepTimeout);
        return source;
    }

    private static string WithoutCredentials(Uri proxy)
    {
        var builder = new UriBuilder(proxy) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    private static Exception Innermost(Exception ex)
    {
        Exception current = ex;
        for (int i = 0; current.InnerException is not null && i < 10; i++)
            current = current.InnerException;
        return current;
    }

    private static string OneLine(Exception ex) =>
        DebugReport.Chain(ex).Replace(Environment.NewLine, " <- ");

    private static string Shorten(string body)
    {
        string flat = (body ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length > MaxBodyLength ? flat[..MaxBodyLength] + "…" : flat;
    }
}
