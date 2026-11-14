using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NotiRelay;

/// <summary>
/// Text for the debug log: the full cause chain of an error (with the codes Windows and
/// .NET attach to network failures) and the header written when a debug session starts.
/// </summary>
public static class DebugReport
{
    private const int MaxDepth = 10;

    // scheme://user:password@host -> scheme://••••@host (e.g. a proxy address inside a .NET error).
    private static readonly Regex UrlCredentials = new(
        @"\b([a-z][a-z0-9+.\-]*://)[^\s/@'""]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Removes user names and passwords embedded in URLs.</summary>
    public static string HideUrlCredentials(string text) =>
        string.IsNullOrEmpty(text) ? text : UrlCredentials.Replace(text, "$1••••@");

    /// <summary>One line per exception in the chain: type, message and error codes.</summary>
    public static string Chain(Exception ex)
    {
        var builder = new StringBuilder();
        int depth = 0;

        for (Exception? current = ex; current is not null && depth < MaxDepth;
             current = current.InnerException, depth++)
        {
            if (depth > 0)
                builder.AppendLine();

            builder
                .Append(depth == 0 ? "Error: " : "Caused by: ")
                .Append(current.GetType().FullName)
                .Append(": ")
                .Append(current.Message)
                .Append(" [")
                .Append(string.Join(", ", Facts(current)))
                .Append(']');
        }

        return builder.ToString();
    }

    /// <summary>The cause chain followed by the full exception text with stack traces.</summary>
    public static string Describe(Exception ex) =>
        Chain(ex) + Environment.NewLine + "Stack trace:" + Environment.NewLine + ex;

    public static string SessionHeader(
        string reason,
        string appVersion,
        string? exePath,
        string logPath)
    {
        string[] lines =
        {
            $"=== NotiRelay debug log started ({reason}) ===",
            $"App: NotiRelay {appVersion}",
            $"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $".NET: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})",
            $"Executable: {exePath ?? "(unknown)"}",
            $"Log file: {logPath}",
            $"Culture: {CultureInfo.CurrentCulture.Name}, time zone: {TimeZoneInfo.Local.Id}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> Facts(Exception ex)
    {
        yield return $"HResult 0x{ex.HResult:X8}";

        switch (ex)
        {
            case HttpRequestException http:
                if (http.StatusCode is { } status)
                    yield return $"HTTP {(int)status} {status}";
                yield return $"HttpRequestError {http.HttpRequestError}";
                break;

            case SocketException socket:
                yield return $"SocketError {socket.SocketErrorCode} ({socket.ErrorCode})";
                break;

            case Win32Exception win32:
                yield return $"NativeErrorCode {win32.NativeErrorCode}";
                break;
        }
    }
}
