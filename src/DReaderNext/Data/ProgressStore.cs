namespace DReaderNext.Data;

/// <summary>一本书的阅读位置。</summary>
public sealed class ReadingPosition
{
    /// <summary>书籍完整路径（与原程序一致，以路径为键，同书不同路径互不覆盖）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>绝对行号（0 基）。-1 表示未知，只能用 Percent 反推（旧数据导入后的常态）。</summary>
    public int Line { get; set; } = -1;

    /// <summary>阅读百分比 0-100。</summary>
    public double Percent { get; set; }

    public int TotalLines { get; set; }

    /// <summary>最后阅读时间。旧 Bookmark.lst 无年份，导入时留空。</summary>
    public DateTime? LastRead { get; set; }
}

/// <summary>阅读进度表（新格式，JSON）。书签列表属于 P3，暂不在此。</summary>
public sealed class ProgressStore
{
    private const string FileName = "progress.json";

    public List<ReadingPosition> Books { get; set; } = new();

    public static ProgressStore Load() => Storage.LoadJson<ProgressStore>(FileName) ?? new ProgressStore();

    public void Save() => Storage.SaveJson(FileName, this);

    public ReadingPosition? Find(string bookPath) =>
        Books.FirstOrDefault(b => string.Equals(b.Path, bookPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>最近读过的一本（用于「自动打开上次阅读记录」）。</summary>
    public ReadingPosition? MostRecent() =>
        Books.Where(b => b.LastRead.HasValue).OrderByDescending(b => b.LastRead).FirstOrDefault();

    /// <summary>记录阅读位置，并同步算出百分比。</summary>
    public void Update(string bookPath, int line, int totalLines)
    {
        ReadingPosition pos = Find(bookPath) ?? Add(bookPath);

        pos.Line = line;
        pos.TotalLines = totalLines;
        pos.Percent = totalLines > 0 ? Math.Clamp(line * 100.0 / totalLines, 0, 100) : 0;
        pos.LastRead = DateTime.Now;
    }

    /// <summary>合并旧 Bookmark.lst 导入的记录：已存在的不覆盖，缺的补上。返回新增条数。</summary>
    public int MergeLegacy(IEnumerable<ReadingPosition> legacy)
    {
        int added = 0;
        foreach (ReadingPosition item in legacy)
        {
            if (item.Path.Length == 0 || Find(item.Path) is not null)
                continue;
            Books.Add(item);
            added++;
        }
        return added;
    }

    private ReadingPosition Add(string bookPath)
    {
        var pos = new ReadingPosition { Path = bookPath };
        Books.Add(pos);
        return pos;
    }
}
