using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using NotiRelay;
using Xunit;

public class DebugReportTests
{
    private static Exception Thrown(Exception ex)
    {
        try { throw ex; }
        catch (Exception caught) { return caught; }
    }

    [Fact]
    public void Chain_ListsInnerSocketErrorAndHttpRequestError()
    {
        var socket = new SocketException((int)SocketError.HostNotFound);
        var http = new HttpRequestException(
            HttpRequestError.NameResolutionError, "No such host is known. (ntfy.sh:443)", socket);

        string chain = DebugReport.Chain(http);

        Assert.Contains("Error: System.Net.Http.HttpRequestException: No such host is known", chain);
        Assert.Contains("HttpRequestError NameResolutionError", chain);
        Assert.Contains("Caused by: System.Net.Sockets.SocketException", chain);
        Assert.Contains("SocketError HostNotFound", chain);
        Assert.Contains("HResult 0x", chain);
    }

    [Fact]
    public void Chain_ShowsHttpStatus_WhenServerAnswered()
    {
        var http = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);

        Assert.Contains("HTTP 403 Forbidden", DebugReport.Chain(http));
    }

    [Fact]
    public void Describe_AddsStackTrace()
    {
        Exception ex = Thrown(new InvalidOperationException("boom"));

        string text = DebugReport.Describe(ex);

        Assert.Contains("Error: System.InvalidOperationException: boom", text);
        Assert.Contains("Stack trace:", text);
        Assert.Contains(nameof(Thrown), text);
    }

    [Fact]
    public void SessionHeader_NamesReasonVersionAndPaths()
    {
        string header = DebugReport.SessionHeader(
            "turned on", "1.2.3", @"C:\Apps\NotiRelay.exe", @"C:\Apps\NotiRelay-debug.log");

        Assert.Contains("turned on", header);
        Assert.Contains("NotiRelay 1.2.3", header);
        Assert.Contains(@"C:\Apps\NotiRelay.exe", header);
        Assert.Contains(@"C:\Apps\NotiRelay-debug.log", header);
        Assert.Contains(".NET", header);
    }

    [Fact]
    public void HideUrlCredentials_RemovesUserAndPassword()
    {
        string text = DebugReport.HideUrlCredentials(
            "The proxy tunnel request to proxy 'http://alice:s3cr3t@proxy.corp:8080/' failed");

        Assert.DoesNotContain("s3cr3t", text);
        Assert.DoesNotContain("alice", text);
        Assert.Contains("http://••••@proxy.corp:8080/", text);
    }

    [Fact]
    public void HideUrlCredentials_LeavesPlainUrlsAlone()
    {
        const string text = "GET https://ntfy.sh/v1/health?a=b@c via http://proxy:8080/ failed";

        Assert.Equal(text, DebugReport.HideUrlCredentials(text));
    }
}
