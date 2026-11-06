using System.Buffers.Binary;
using System.Text;

namespace FastTranslation.Core.Lexicon;

/// <summary>
/// 只读词库（FTD1）：启动时把词头与偏移读进内存，查询时按偏移读一条记录。
/// 不用 SQLite 的理由是桌面端只需要「一个词一次点查」，为它带原生依赖不划算；
/// 实测 5.8 万条索引约占几 MB，单次查询 0.3 ms 量级。
///
/// 文件由 tools/dictionary/build-ecdict.py 生成，数据来自 ECDICT（MIT）。
/// </summary>
public sealed class EcdictLexicon : ILexicon
{
    private const int MaxEntries = 500_000;
    private const int MaxWordBytes = 120;
    private const int MaxPayloadBytes = 8 * 1024;

    private readonly string _path;
    private readonly Dictionary<string, EntryRef> _index;

    private readonly record struct EntryRef(int Offset, int Length);

    private EcdictLexicon(string path, Dictionary<string, EntryRef> index)
    {
        _path = path;
        _index = index;
    }

    /// <summary>
    /// 读取索引。文件缺失或损坏时返回 null——这只意味着没有词典卡，
    /// 在线翻译必须照常工作，所以这里不向上抛。
    /// </summary>
    public static EcdictLexicon? TryLoad(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("FTD1"u8)) return null;

            var count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if (count is < 0 or > MaxEntries) return null;

            var index = new Dictionary<string, EntryRef>(count, StringComparer.OrdinalIgnoreCase);
            Span<byte> wordLengthBytes = stackalloc byte[2];
            Span<byte> tail = stackalloc byte[8];
            var wordBuffer = new byte[MaxWordBytes];

            for (var i = 0; i < count; i++)
            {
                stream.ReadExactly(wordLengthBytes);
                var wordLength = BinaryPrimitives.ReadInt16LittleEndian(wordLengthBytes);
                if (wordLength is <= 0 or > MaxWordBytes) return null;

                var wordBytes = wordBuffer.AsSpan(0, wordLength);
                stream.ReadExactly(wordBytes);
                stream.ReadExactly(tail);

                var offset = BinaryPrimitives.ReadInt32LittleEndian(tail[..4]);
                var length = BinaryPrimitives.ReadInt32LittleEndian(tail[4..]);
                if (offset < 0 || length is <= 0 or > MaxPayloadBytes) return null;

                index[Encoding.UTF8.GetString(wordBytes)] = new EntryRef(offset, length);
            }
            return new EcdictLexicon(path, index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    public bool TryLookup(string word, out DictionaryEntry entry)
    {
        entry = null!;
        if (!_index.TryGetValue(word, out var reference)) return false;

        try
        {
            // 每次查询开一次文件：条目只有几百字节，打开成本远小于一次网络请求，
            // 换来不需要管理跨线程共享的句柄生命周期。
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var payload = new byte[reference.Length];
            stream.Position = reference.Offset;
            stream.ReadExactly(payload);

            var fields = Encoding.UTF8.GetString(payload).Split('\t');
            if (fields.Length < 5) return false;

            var senses = EcdictText.ParseSenses(fields[2]);
            if (senses.Count == 0) return false;

            var headword = fields[0].Length == 0 ? word : fields[0];
            entry = new DictionaryEntry(
                headword,
                EcdictText.FormatPhonetic(fields[1]),
                senses,
                EcdictText.ParseForms(headword, fields[3]),
                EcdictText.ParseTags(fields[4]));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }
}
