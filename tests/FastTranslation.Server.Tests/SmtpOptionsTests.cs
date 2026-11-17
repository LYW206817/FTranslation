using FastTranslation.Server;
using Xunit;

namespace FastTranslation.Server.Tests;

public class SmtpOptionsTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "support@example.com")]
    [InlineData("smtp.example.com", "")]
    // 示例文件里的占位符不是空串，必须按未配置处理，否则发码时会去连一个假主机。
    [InlineData("__SMTP_HOST__", "__SMTP_FROM__")]
    public void IsConfigured_RejectsEmptyAndPlaceholderValues(string host, string from)
    {
        var options = new SmtpOptions { Host = host, From = from };

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_AcceptsARealHostAndSender()
    {
        var options = new SmtpOptions
        {
            Host = "smtp.example.com",
            From = "support@example.com",
        };

        Assert.True(options.IsConfigured);
    }
}