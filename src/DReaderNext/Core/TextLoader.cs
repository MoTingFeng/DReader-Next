using System.Text;

namespace DReaderNext.Core;

/// <summary>流式扫描的结果：行偏移表 + 章节表 + 判定出的编码。</summary>
public sealed class TextScanResult
{
    /// <summary>
    /// 每行的起始字节偏移，末尾多一项＝文件长度（读最后一页时当结束哨兵）。
    /// 所以 LineCount = LineOffsets.Length - 1。
    /// </summary>
    public required long[] LineOffsets { get; init; }

    public required Encoding Encoding { get; init; }
    public required string EncodingName { get; init; }
    public required List<Chapter> Chapters { get; init; }

    public int LineCount => Math.Max(0, LineOffsets.Length - 1);
}

/// <summary>
/// 文本加载：不把整本书读进内存，只流式扫一遍记下「每行起始字节偏移 + 章节表 + 编码」，
/// 正文内容在翻页时按偏移 seek 读取（见 Book.ReadLines）。
///
/// 之所以能做到纯字节扫描定位行边界：UTF-8 与 GB18030 的多字节字符，
/// 其所有字节都 ≥ 0x80，因此 0x0A/0x0D 只可能作为换行出现，不会被字符内部字节误伤。
/// UTF-16 则按 2 字节单元比对（低位或高位为换行码）。
/// </summary>
public static class TextLoader
{
    private const int BufferSize = 1 << 16;

    /// <summary>超过这个字节数的行不再参与章节识别（标题不可能这么长）。</summary>
    private const int MaxTitleBytes = 512;

    private static readonly Encoding StrictUtf8;
    private static readonly Encoding StrictGb18030;
    private static readonly Encoding LooseGb18030;

    static TextLoader()
    {
        // .NET 不自带 GB18030，必须先注册代码页编码器（由 WindowsDesktop 共享框架提供）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        StrictGb18030 = Encoding.GetEncoding("GB18030",
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        LooseGb18030 = Encoding.GetEncoding("GB18030");
    }

    /// <summary>扫描文件：判编码 + 记行偏移 + 识别章节。UTF-8 只扫一遍，其它编码扫两遍。</summary>
    public static TextScanResult Scan(string path)
    {
        long length = new FileInfo(path).Length;
        if (length == 0)
        {
            return new TextScanResult
            {
                LineOffsets = new[] { 0L },
                Encoding = Encoding.UTF8,
                EncodingName = "空文件",
                Chapters = new List<Chapter>(),
            };
        }

        var head = new byte[Math.Min(4, (int)length)];
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            probe.ReadExactly(head, 0, head.Length);
        }

        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            return Build(ScanWith(path, Encoding.UTF8, 3), Encoding.UTF8, "UTF-8 BOM", length);

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            return Build(ScanWith(path, Encoding.Unicode, 2), Encoding.Unicode, "UTF-16 LE", length);

        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            return Build(ScanWith(path, Encoding.BigEndianUnicode, 2), Encoding.BigEndianUnicode, "UTF-16 BE", length);

        // 无 BOM：先看头部像不像 UTF-16。ASCII 字符在 UTF-16 里必有一个 0x00 字节，
        // 且位置固定（LE 在奇数位、BE 在偶数位）。纯中文的 UTF-16 无 BOM 识别不了，
        // 但那种文件极少见——记事本另存为 Unicode 一定会写 BOM。
        Encoding? bomless = GuessBomlessUtf16(path, length);
        if (bomless is not null)
        {
            string name = bomless.CodePage == 1200 ? "UTF-16 LE（无 BOM）" : "UTF-16 BE（无 BOM）";
            return Build(ScanWith(path, bomless, 0), bomless, name, length);
        }

        // 无 BOM：先按严格 UTF-8 全程校验（顺便把偏移也记下来，通过就省掉一遍扫描）
        ScanState utf8 = ScanWith(path, StrictUtf8, 0, strict: true);
        if (utf8.Ok)
            return Build(utf8, StrictUtf8, "UTF-8", length);

        // 不是合法 UTF-8 → 整体按 GB18030 重扫
        ScanState gb = ScanWith(path, StrictGb18030, 0, strict: true);
        if (gb.Ok)
            return Build(gb, StrictGb18030, "GB18030", length);

        // 两种严格解码都失败（截断或混合字节）：宽松模式保证能打开而不是崩掉
        ScanState loose = ScanWith(path, LooseGb18030, 0, strict: false);
        return Build(loose, LooseGb18030, "GB18030（含无法识别字节）", length);
    }

    /// <summary>把文件整体解码成字符串。只有验证工具在用，主程序走 Scan。</summary>
    public static (string Text, string EncodingName) Load(string path)
        => Decode(File.ReadAllBytes(path));

    public static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return (string.Empty, "空文件");

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");

        try
        {
            return (StrictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException) { }

        try
        {
            return (StrictGb18030.GetString(bytes), "GB18030");
        }
        catch (DecoderFallbackException) { }

        return (LooseGb18030.GetString(bytes), "GB18030（含无法识别字节）");
    }

    // ---------------------------------------------------------------- 扫描实现

    private sealed class ScanState
    {
        public bool Ok = true;
        public readonly List<long> Offsets = new();
        public readonly List<(int Line, string Title, ChapterKind Kind)> Strong = new();
        public readonly List<(int Line, string Title, ChapterKind Kind)> WithNumber = new();
    }

    private static TextScanResult Build(ScanState state, Encoding encoding, string name, long length)
    {
        if (state.Offsets.Count == 0 || state.Offsets[^1] < length)
            state.Offsets.Add(length);

        // 主力规则几乎没命中时才退而采用「含数字序号」的那一套
        List<Chapter> chapters = state.Strong.Count >= 3
            ? Materialize(state.Strong)
            : state.WithNumber.Count >= 3
                ? Materialize(state.WithNumber)
                : BuildFallbackSegments(state.Offsets.Count - 1);

        return new TextScanResult
        {
            LineOffsets = state.Offsets.ToArray(),
            Encoding = encoding,
            EncodingName = name,
            Chapters = chapters,
        };
    }

    private static List<Chapter> Materialize(List<(int Line, string Title, ChapterKind Kind)> hits)
    {
        var result = new List<Chapter>(hits.Count);
        for (int i = 0; i < hits.Count; i++)
            result.Add(new Chapter(i, hits[i].Line, hits[i].Title, hits[i].Kind));
        return result;
    }

    private static List<Chapter> BuildFallbackSegments(int lineCount)
    {
        const int segment = 500;
        var result = new List<Chapter>();
        if (lineCount == 0)
            return result;

        for (int start = 0; start < lineCount; start += segment)
        {
            int end = Math.Min(start + segment, lineCount);
            result.Add(new Chapter(result.Count, start,
                $"分段 {result.Count + 1}（第 {start + 1}-{end} 行）", ChapterKind.Text)
            {
                LineCount = end - start,
            });
        }
        return result;
    }

    private static ScanState ScanWith(string path, Encoding encoding, long startOffset, bool strict = false)
    {
        var state = new ScanState();
        state.Offsets.Add(startOffset);

        int unit = IsUtf16(encoding) ? 2 : 1;
        bool littleEndian = encoding.CodePage == 1200;

        var buffer = new byte[BufferSize];
        var charBuffer = new char[BufferSize + 4];
        // 固定缓冲：十几万行若逐行 new List / ToArray，那些短命垃圾会把 GC 保留推高好几 MB
        var lineBuffer = new byte[MaxTitleBytes];
        int lineLength = 0;
        Decoder decoder = encoding.GetDecoder();

        long position = startOffset;
        bool lastWasCr = false;
        int lineIndex = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        stream.Seek(startOffset, SeekOrigin.Begin);

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (strict)
            {
                try
                {
                    decoder.Convert(buffer, 0, read, charBuffer, 0, charBuffer.Length,
                        flush: false, out _, out _, out _);
                }
                catch (DecoderFallbackException)
                {
                    state.Ok = false;
                    return state;
                }
            }

            for (int i = 0; i + unit <= read; i += unit)
            {
                bool isCr = IsNewlineAt(buffer, i, unit, littleEndian, 0x0D);
                bool isLf = IsNewlineAt(buffer, i, unit, littleEndian, 0x0A);

                if (!isCr && !isLf)
                {
                    if (lineLength < MaxTitleBytes)
                    {
                        for (int k = 0; k < unit && lineLength < MaxTitleBytes; k++)
                            lineBuffer[lineLength++] = buffer[i + k];
                    }
                    lastWasCr = false;
                    continue;
                }

                // \r\n 的第二半：这一行已在 \r 处收尾，只需把下一行起点推过 \n
                if (isLf && lastWasCr)
                {
                    state.Offsets[^1] = position + i + unit;
                    lastWasCr = false;
                    continue;
                }

                CaptureLine(state, encoding, lineBuffer, lineLength, lineIndex);
                lineLength = 0;
                lineIndex++;

                state.Offsets.Add(position + i + unit);
                lastWasCr = isCr;
            }

            position += read;
        }

        // 文件末尾没有换行时，最后一行还没收
        if (lineLength > 0)
            CaptureLine(state, encoding, lineBuffer, lineLength, lineIndex);

        return state;
    }

    private static void CaptureLine(ScanState state, Encoding encoding, byte[] lineBuffer, int lineLength, int lineIndex)
    {
        // 缓冲被填满说明这一行远超标题长度，而且截断点可能落在多字节字符中间，直接跳过
        if (lineLength == 0 || lineLength >= MaxTitleBytes)
            return;

        string line;
        try
        {
            line = encoding.GetString(lineBuffer, 0, lineLength);
        }
        catch (DecoderFallbackException)
        {
            // 万一仍截断在字符中间（严格解码会抛异常），放弃这一行即可
            return;
        }
        if (!ChapterParser.Match(line, out bool isStrong, out ChapterKind kind))
            return;

        var entry = (lineIndex, line.Trim(), kind);
        state.WithNumber.Add(entry);
        if (isStrong)
            state.Strong.Add(entry);
    }

    /// <summary>
    /// 无 BOM 的 UTF-16 启发：ASCII 字符在 UTF-16 里必然带一个 0x00 字节，且位置固定
    /// （LE 在奇数位、BE 在偶数位）。只看前 4 KB，0x00 占比不够就直接判否。
    /// </summary>
    private static Encoding? GuessBomlessUtf16(string path, long length)
    {
        int sample = (int)Math.Min(4096, length);
        if (sample < 16)
            return null;

        var buffer = new byte[sample];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.ReadExactly(buffer, 0, sample);
        }

        int evenZero = 0, oddZero = 0;
        for (int i = 0; i < sample; i++)
        {
            if (buffer[i] != 0)
                continue;
            if ((i & 1) == 0)
                evenZero++;
            else
                oddZero++;
        }

        if ((evenZero + oddZero) / (double)sample < 0.25)
            return null;

        if (oddZero > evenZero * 2)
            return Encoding.Unicode;
        if (evenZero > oddZero * 2)
            return Encoding.BigEndianUnicode;
        return null;
    }

    private static bool IsUtf16(Encoding encoding)
        => encoding.CodePage is 1200 or 1201;

    /// <summary>当前位置是不是换行符。单字节编码直接比；UTF-16 比对有效那一位。</summary>
    private static bool IsNewlineAt(byte[] buffer, int index, int unit, bool littleEndian, byte code)
    {
        if (unit == 1)
            return buffer[index] == code;

        return littleEndian
            ? buffer[index] == code && buffer[index + 1] == 0
            : buffer[index] == 0 && buffer[index + 1] == code;
    }
}
