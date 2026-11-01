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
}