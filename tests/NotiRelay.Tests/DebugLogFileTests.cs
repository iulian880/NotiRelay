using NotiRelay;
using Xunit;

public class DebugLogFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "notirelay-debug-tests-" + Guid.NewGuid().ToString("N"));

    public DebugLogFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Open_FallsBackToNextDirectory_WhenFirstIsNotWritable()
    {
        // A directory "inside" a regular file can never be created, on any OS.
        string blocker = Path.Combine(_root, "blocker.txt");
        File.WriteAllText(blocker, "x");
        string unusable = Path.Combine(blocker, "sub");
        string fallback = Path.Combine(_root, "fallback");

        DebugLogFile? log = DebugLogFile.Open(
            new[] { unusable, fallback }, () => null, out string? error);

        Assert.NotNull(log);
        Assert.Null(error);
        Assert.Equal(Path.Combine(fallback, DebugLogFile.FileName), log!.FilePath);
        Assert.True(File.Exists(log.FilePath));
    }

    [Fact]
    public void Open_ReturnsNullWithReason_WhenNoDirectoryIsWritable()
    {
        string blocker = Path.Combine(_root, "blocker.txt");
        File.WriteAllText(blocker, "x");

        DebugLogFile? log = DebugLogFile.Open(
            new[] { Path.Combine(blocker, "a") }, () => null, out string? error);

        Assert.Null(log);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Format_StampsMillisecondsAndIndentsFollowingLines()
    {
        var time = new DateTime(2026, 11, 14, 9, 5, 7, 42);

        string text = DebugLogFile.Format(time, "first\nsecond");

        string[] lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2026-11-14 09:05:07.042  first", lines[0]);
        Assert.Equal(new string(' ', 25) + "second", lines[1]);
        Assert.EndsWith(Environment.NewLine, text);
    }

    [Fact]
    public void Redact_MasksTopicInNtfyUrl_EvenWhenShort()
    {
        string text = DebugLogFile.Redact("POST https://ntfy.sh/abc failed", "abc");

        Assert.DoesNotContain("ntfy.sh/abc", text);
        Assert.Contains("ntfy.sh/" + NtfyTopic.Mask("abc"), text);
    }

    [Fact]
    public void Redact_MasksBareTopic_WhenLongEnough()
    {
        string topic = "my-secret-topic-123";

        string text = DebugLogFile.Redact($"topic is {topic} here", topic);

        Assert.DoesNotContain(topic, text);
        Assert.Contains(NtfyTopic.Mask(topic), text);
    }

    [Fact]
    public void Redact_LeavesTextAlone_WhenNoTopic()
    {
        Assert.Equal("hello", DebugLogFile.Redact("hello", null));
        Assert.Equal("hello", DebugLogFile.Redact("hello", ""));
    }

    [Fact]
    public void TryWrite_AppendsRedactedLines()
    {
        string topic = "my-secret-topic-123";
        DebugLogFile log = DebugLogFile.Open(new[] { _root }, () => topic, out _)!;

        Assert.True(log.TryWrite(DateTime.Now, "one " + topic));
        Assert.True(log.TryWrite(DateTime.Now, "two"));

        string content = File.ReadAllText(log.FilePath);
        Assert.Contains("one " + NtfyTopic.Mask(topic), content);
        Assert.Contains("two", content);
        Assert.DoesNotContain(topic, content);
    }

    [Fact]
    public void TryWrite_RotatesToOldFile_WhenCapReached()
    {
        DebugLogFile log = DebugLogFile.Open(new[] { _root }, () => null, out _, maxBytes: 200)!;
        string oldPath = Path.Combine(_root, DebugLogFile.OldFileName);

        for (int i = 0; i < 20; i++)
            Assert.True(log.TryWrite(DateTime.Now, $"line {i} " + new string('x', 30)));

        Assert.True(File.Exists(oldPath));
        Assert.True(new FileInfo(log.FilePath).Length <= 200);
        Assert.True(new FileInfo(oldPath).Length <= 200);
        Assert.Contains("line 19", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void TryWrite_IsSafeFromSeveralThreads()
    {
        DebugLogFile log = DebugLogFile.Open(new[] { _root }, () => null, out _)!;

        Parallel.For(0, 200, i => log.TryWrite(DateTime.Now, $"entry {i:D3}"));

        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}  entry \d{3}$", line));
    }

    [Fact]
    public void Redact_HidesProxyCredentials_EvenWithoutTopic()
    {
        string text = DebugLogFile.Redact("proxy 'http://alice:s3cr3t@proxy:8080/' refused", null);

        Assert.DoesNotContain("s3cr3t", text);
    }

    [Fact]
    public void Redact_ShortTopic_RevealsNoCharacters()
    {
        // A 7-character topic: showing its last 4 would give most of it away.
        string text = DebugLogFile.Redact("topic abcdefg and https://ntfy.sh/abcdefg", "abcdefg");

        Assert.DoesNotContain("defg", text);
        Assert.Contains("••••", text);
    }

    [Fact]
    public void MaskTopic_ShowsLastFour_OnlyForLongTopics()
    {
        Assert.Equal("••••", DebugLogFile.MaskTopic("abcdefg"));
        Assert.Equal(NtfyTopic.Mask("my-secret-topic-123"), DebugLogFile.MaskTopic("my-secret-topic-123"));
    }

    [Fact]
    public void Redact_MasksEveryConfiguredTopic()
    {
        string[] topics = { "main-topic-1234567", "meetings-topic-987654" };

        string text = DebugLogFile.RedactAll(
            "sent to main-topic-1234567 and https://ntfy.sh/meetings-topic-987654", topics);

        Assert.DoesNotContain("main-topic-1234567", text);
        Assert.DoesNotContain("meetings-topic-987654", text);
    }

    [Fact]
    public void TryWrite_MasksTopicsFromAListProvider()
    {
        string[] topics = { "main-topic-1234567", "battery-topic-55555" };
        DebugLogFile log = DebugLogFile.OpenForTopics(new[] { _root }, () => topics, out _)!;

        log.TryWrite(DateTime.Now, "battery-topic-55555 and main-topic-1234567");

        string content = File.ReadAllText(log.FilePath);
        Assert.DoesNotContain("battery-topic-55555", content);
        Assert.DoesNotContain("main-topic-1234567", content);
    }
}
