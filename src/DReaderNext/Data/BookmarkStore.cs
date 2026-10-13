namespace DReaderNext.Data;

/// <summary>一条书签：指向某本书的某一行。</summary>
public sealed class Bookmark
{
    /// <summary>书籍完整路径（与原程序一致，以路径为键）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>书签所在行的绝对行号。</summary>
    public int Line { get; set; }

    /// <summary>插入时的章节标题，用于列表里辨认位置。</summary>
    public string Chapter { get; set; } = string.Empty;

    /// <summary>该行开头的一小段文字，便于肉眼确认。</summary>
    public string Preview { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>列表标题：书名 · 章节（没章节就退回行号）。</summary>
    public string DisplayTitle
    {
        get
        {
            string book = System.IO.Path.GetFileNameWithoutExtension(Path);
            string where = Chapter.Length > 0 ? Chapter : $"第 {Line + 1} 行";
            return $"{book} · {where}";
        }
    }
}

/// <summary>书签表（跨书存放，JSON）。新插入的排在最前。</summary>
public sealed class BookmarkStore
{
    private const string FileName = "bookmarks.json";

    public List<Bookmark> Items { get; set; } = new();

    public static BookmarkStore Load() => Storage.LoadJson<BookmarkStore>(FileName) ?? new BookmarkStore();

    public void Save() => Storage.SaveJson(FileName, this);

    /// <summary>同一本书的同一行是否已有书签。</summary>
    public bool Exists(string path, int line) =>
        Items.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase) && b.Line == line);

    public void Add(Bookmark bookmark) => Items.Insert(0, bookmark);

    public void Remove(Bookmark bookmark) => Items.Remove(bookmark);
}
