using NotiRelay;
using Xunit;

public class NtfyTopicTests
{
    [Theory]
    [InlineData("  https://ntfy.sh/my-topic/ \n", "my-topic")]
    [InlineData("http://ntfy.sh/my-topic", "my-topic")]
    [InlineData("NTFY.SH/abc?x=1", "abc")]
    [InlineData("ntfy.sh/abc#frag", "abc")]
    [InlineData("plain_topic", "plain_topic")]
    [InlineData(null, "")]
    public void Normalize_StripsNtfyShLinkAndWhitespace(string? input, string expected)
    {
        Assert.Equal(expected, NtfyTopic.Normalize(input));
    }

    [Fact]
    public void Normalize_KeepsForeignHostSoValidationCanRejectIt()
    {
        Assert.Equal("ntfy.example.com/x", NtfyTopic.Normalize("https://ntfy.example.com/x"));
    }

    [Fact]
    public void Validate_AcceptsValidTopic()
    {
        Assert.Null(NtfyTopic.Validate("my-topic_01"));
    }

    [Fact]
    public void Validate_EmptyTopic_AsksForTopic()
    {
        Assert.Contains("Enter your ntfy topic", NtfyTopic.Validate(""));
    }

    [Fact]
    public void Validate_ForeignHost_ExplainsOnlyNtfyShIsSupported()
    {
        Assert.Contains("ntfy.sh", NtfyTopic.Validate("ntfy.example.com/x"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("ăîș-diacritics")]
    public void Validate_RejectsInvalidCharacters(string topic)
    {
        Assert.NotNull(NtfyTopic.Validate(topic));
    }

    [Fact]
    public void Validate_RejectsTooLong_AcceptsMaxLength()
    {
        Assert.NotNull(NtfyTopic.Validate(new string('a', NtfyTopic.MaxLength + 1)));
        Assert.Null(NtfyTopic.Validate(new string('a', NtfyTopic.MaxLength)));
    }

    [Theory]
    [InlineData("short", true)]
    [InlineData("elevenchars", true)]
    [InlineData("twelve-chars", false)]
    [InlineData("notirelay-7f3k9q2x", false)]
    public void IsWeak_FlagsTopicsShorterThanTwelve(string topic, bool weak)
    {
        Assert.Equal(weak, NtfyTopic.IsWeak(topic));
    }

    [Theory]
    [InlineData("notirelay-7f3k9q2x", "••••9q2x")]
    [InlineData("abcd", "••••")]
    [InlineData("abc", "••••")]
    public void Mask_ShowsOnlyLastFourCharacters(string topic, string expected)
    {
        Assert.Equal(expected, NtfyTopic.Mask(topic));
    }
}
