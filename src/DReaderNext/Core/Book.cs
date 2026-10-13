using System.Text;

namespace DReaderNext.Core;

/// <summary>
/// 一本已加载的书。只持有「每行的字节偏移 + 章节表 + 编码」，
/// 正文内容在翻页时按偏移从文件里读一小段——所以常驻内存与书的体积基本无关。
///
/// 分页仍以逻辑行为单位（交接文档没定义，这里定死）：
/// 行号不随窗口尺寸变化，进度记录才能在换尺寸后依然准确。
/// </summary>
public sealed class Book
{
    private readonly long[] _lineOffsets;
    private readonly Encoding _encoding;

    public string Path { get; }
    public string Title { get; }
    public string EncodingName { get; }

    /// <summary>总行数。</summary>
    public int LineCount { get; }

    public IReadOnlyList<Chapter> Chapters { get; }

    public Book(string path, TextScanResult scan)
    {
        Path = path;
        Title = System.IO.Path.GetFileNameWithoutExtension(path);
        EncodingName = scan.EncodingName;
        _lineOffsets = scan.LineOffsets;
        _encoding = scan.Encoding;
        LineCount = scan.LineCount;
        Chapters = scan.Chapters;
    }

    /// <summary>每页显示的行数，由界面按窗口高度计算后写入。</summary>
    public int RowsPerPage { get; set; } = 20;

    public int TotalPages =>
        Math.Max(1, (int)Math.Ceiling(LineCount / (double)Math.Max(1, RowsPerPage)));

    public int LineToPage(int line) =>
        Math.Clamp(line / Math.Max(1, RowsPerPage), 0, TotalPages - 1);

    /// <summary>取某页对应的行区间 [start, start+count)。</summary>
    public (int Start, int Count) PageRange(int page)
    {
        int start = Math.Clamp(page, 0, TotalPages - 1) * RowsPerPage;
        return (start, Math.Max(0, Math.Min(RowsPerPage, LineCount - start)));
    }

    /// <summary>某行所属的章节下标；无章节时返回 -1。</summary>
    public int ChapterAtLine(int line)
    {
        int found = -1;
        for (int i = 0; i < Chapters.Count; i++)
        {
            if (Chapters[i].StartLine > line)
                break;
            found = i;
        }
        return found;
    }

    /// <summary>
    /// 按需读取 [startLine, startLine+count) 行的原文（含行间换行，可直接显示）。
    /// 只从文件里取这一段字节，不触碰其余部分。
    /// </summary>
    public string ReadLines(int startLine, int count)
    {
        if (count <= 0 || LineCount == 0)
            return string.Empty;

        int start = Math.Clamp(startLine, 0, LineCount - 1);
        int end = Math.Min(start + count, LineCount);

        long from = _lineOffsets[start];
        long to = _lineOffsets[end];
        int length = (int)(to - from);
        if (length <= 0)
            return string.Empty;

        var buffer = new byte[length];
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(from, SeekOrigin.Begin);
        stream.ReadExactly(buffer, 0, length);

        return _encoding.GetString(buffer);
    }

}
