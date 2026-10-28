namespace FastTranslation.Server.Domain;

/// <summary>List price for one model, in CNY.</summary>
public sealed record ModelPrice(string Model, decimal InputPerMillionTokens, decimal OutputPerMillionTokens, decimal PerImage);

/// <summary>
/// DashScope list prices (华北2 北京, 2026-10 sheet). Deliberately a table in code rather than
/// configuration: a wrong price silently over- or under-bills every account, and these numbers are
/// a published contract, not a per-deployment knob. Bump the table when the sheet moves.
/// </summary>
public static class Pricing
{
    public const string DefaultModel = "qwen-mt-flash";

    private static readonly Dictionary<string, ModelPrice> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen-mt-flash"] = new("qwen-mt-flash", 0.7m, 1.95m, 0m),
        ["qwen-mt-plus"] = new("qwen-mt-plus", 1.8m, 5.4m, 0m),
        ["qwen-mt-lite"] = new("qwen-mt-lite", 0.6m, 1.6m, 0m),
        ["qwen-mt-image-2.0"] = new("qwen-mt-image-2.0", 0m, 0m, 0.004m),
        ["qwen-mt-image"] = new("qwen-mt-image", 0m, 0m, 0.003m),
    };

    public static ModelPrice For(string? model) =>
        !string.IsNullOrWhiteSpace(model) && Table.TryGetValue(model, out var price)
            ? price
            : Table[DefaultModel];

    /// <summary>
    /// Text cost in micro-yuan (1e-6 CNY) so the ledger stays integer arithmetic. Micro-yuan is fine
    /// because one flash request costs roughly 7,000 of them — rounding never reaches a cent.
    /// </summary>
    public static long TextCostMicros(string? model, long tokensIn, long tokensOut)
    {
        if (tokensIn <= 0 && tokensOut <= 0) return 0;

        var price = For(model);
        var yuan = (tokensIn * price.InputPerMillionTokens + tokensOut * price.OutputPerMillionTokens) / 1_000_000m;
        return (long)decimal.Round(yuan * 1_000_000m, 0, MidpointRounding.AwayFromZero);
    }

    public static long ImageCostMicros(string? model) =>
        (long)decimal.Round(For(model).PerImage * 1_000_000m, 0, MidpointRounding.AwayFromZero);
}
