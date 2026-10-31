using System.Runtime.CompilerServices;
using FastTranslation.Core.Documents;

namespace FastTranslation.Core.Tests.Documents;

/// <summary>
/// 把文档翻译的请求节流放开到「不等」。
/// <para>
/// 生产值 30 RPM 等于每个请求等 2 秒、24,000 字符/分钟等于 4,000 字符的请求要排队 10 秒：
/// 一个 5 次请求的用例就要实等一分钟，整套文档测试会跑成钟点级。
/// 用模块初始化器而不是每个测试类的构造/析构，是因为 <see cref="SegmentTranslator.RequestsPerMinute"/>
/// 与 <see cref="SegmentTranslator.CharsPerMinute"/> 都是进程级静态量，逐类改动会在并行测试之间互相踩。
/// </para>
/// <para>
/// 节流本身另有 <c>RequestPacerTests</c> 用 600 RPM 验证「确实按间隔放行」。
/// </para>
/// </summary>
internal static class TestPacing
{
    [ModuleInitializer]
    internal static void Init()
    {
        SegmentTranslator.RequestsPerMinute = 60_000;
        // 字符量一起放开：只放开请求数的话，4,000 字符的合并请求仍会被字符窗口拦下。
        SegmentTranslator.CharsPerMinute = 60_000_000;
        // 失败退避同样置零：否则「重试用尽」这类用例要真等 1 + 2 秒。
        SegmentTranslator.RetryBackoff = TimeSpan.Zero;
    }
}
