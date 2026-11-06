using FastTranslation.Server.Domain;
using Xunit;

namespace FastTranslation.Server.Tests;

public class PricingTests
{
    [Fact]
    public void Asr_model_price_is_not_the_flash_fallback()
    {
        // 不进表会静默回落 flash 价:语音用量大,错价就是真金白银,所以锁死这两行。
        var price = Pricing.For("qwen-audio-3.1-asr-flash-streaming");
        Assert.Equal(6m, price.InputPerMillionTokens);
        Assert.Equal(4.5m, price.OutputPerMillionTokens);
        Assert.Equal(0m, price.PerImage);
    }

    [Theory]
    [InlineData("qwen-mt-flash", 100, 100)]
    [InlineData("qwen-mt-plus", 100, 270)]
    [InlineData("qwen-mt-lite", 100, 85)]
    [InlineData("some-future-model", 100, 100)]
    [InlineData("", 100, 100)]
    public void QuotaChars_bills_plus_at_its_real_cost_ratio(string model, int rawChars, int expected)
    {
        // plus 的每字符成本实测是 flash 的 2.68 倍(8.33 vs 3.11 微元),按 1:1 扣额度
        // 等于让用户用 5.9 元买走 7.33 元的成本。额度单位统一成 flash 等价字符。
        Assert.Equal(expected, Pricing.QuotaChars(model, rawChars));
    }

    [Fact]
    public void QuotaChars_never_rounds_a_request_down_to_zero()
    {
        // lite 的倍率小于 1:一个字符的请求若折成 0,就等于白送,还能绕开额度闸门。
        Assert.Equal(1, Pricing.QuotaChars("qwen-mt-lite", 1));
        Assert.Equal(0, Pricing.QuotaChars("qwen-mt-lite", 0));
    }
}