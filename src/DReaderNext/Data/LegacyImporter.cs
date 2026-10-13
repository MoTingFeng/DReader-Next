using System.Globalization;
using System.Text;

namespace DReaderNext.Data;

/// <summary>
/// 旧程序数据迁移。全程只读，绝不修改 D:\APP\DR\ 下的原文件。
///
/// 格式依据实测（交接文档 §3.2 / §3.3），并补上实测才发现的细节：
///   - Bookmark.lst 每两条一组，分隔符 |:|
///   - 时间**不补零**（A 17:1:6），且**没有年份**
///   - 记录按时间倒序，最新一条在**文件头**
///   - 以完整路径为键
///   - 进度可能出现 1.#INF%（原程序除零 bug）
/// </summary>
public static class LegacyImporter
{
    public const string DefaultIniPath = @"D:\APP\DR\Ebook.ini";
    public const string DefaultBookmarkPath = @"D:\APP\DR\Bookmark.lst";

    private static readonly Encoding Gb18030;

    static LegacyImporter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030 = Encoding.GetEncoding("GB18030");
    }

    public static bool HasLegacyFiles(string? iniPath = null, string? bookmarkPath = null)
        => File.Exists(iniPath ?? DefaultIniPath) || File.Exists(bookmarkPath ?? DefaultBookmarkPath);

    /// <summary>把旧 Ebook.ini 的设置合并进 target。返回是否读到文件。</summary>
    public static bool ImportSettings(string iniPath, AppSettings target)
    {
        if (!File.Exists(iniPath))
            return false;

        Dictionary<string, string> ini = ParseIni(iniPath);

        // [字体]
        if (TryInt(ini, "字体", "字体大小", out int fontSize) && fontSize > 0)
            target.FontSize = fontSize;
        if (ini.TryGetValue("字体.字体名称", out string? fontName) && fontName.Length > 0)
            target.FontName = fontName;
        if (TryInt(ini, "字体", "字体颜色", out int fg))
            target.FontColor = BgrToHtml(fg);
        if (TryInt(ini, "字体", "自定义底色", out int bg))
            target.BackColor = BgrToHtml(bg);

        // [窗口]
        if (TryInt(ini, "窗口", "窗口宽度", out int w) && w > 0)
            target.WindowWidth = w;
        if (TryInt(ini, "窗口", "窗口高度", out int h) && h > 0)
            target.WindowHeight = h;

        // [其他] —— 不迁移「标题栏」：原程序那个开关管的是它自绘的信息栏，不是 Windows 标题栏，
        // 直接映射会让新程序凭空多出一个原程序没有的系统头部。
        // 也不迁移「自动更新」（功能已砍）。
        if (TryBool(ini, "其他", "贴边隐藏", out bool edgeHide))
            target.EdgeHide = edgeHide;
        if (TryBool(ini, "其他", "隐藏图标", out bool hideIcon))
            target.HideTrayIcon = hideIcon;
        if (TryBool(ini, "其他", "自动打开", out bool autoOpen))
            target.AutoOpenLast = autoOpen;

        return true;
    }

    /// <summary>解析旧 Bookmark.lst。行号留 -1，等打开书时按百分比换算。</summary>
    public static List<ReadingPosition> ImportBookmarks(string lstPath)
    {
        var result = new List<ReadingPosition>();
        if (!File.Exists(lstPath))
            return result;

        string? pendingPath = null;
        foreach (string raw in File.ReadAllLines(lstPath, Gb18030))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("...", StringComparison.Ordinal))
            {
                if (pendingPath is null)
                    continue;

                result.Add(new ReadingPosition
                {
                    Path = pendingPath,
                    Line = -1,
                    Percent = ParsePercent(line),
                    LastRead = null,
                });
                pendingPath = null;
                continue;
            }

            int sep = line.IndexOf("|:|", StringComparison.Ordinal);
            if (sep < 0)
                continue;

            string path = line[(sep + 3)..].Trim();
            if (path.Length > 0)
                pendingPath = path;
        }

        return result;
    }

    /// <summary>解析 "...92.7104%|:|..." 中的百分比；1.#INF% 之类脏数据一律按 0 处理。</summary>
    private static double ParsePercent(string progressLine)
    {
        int start = progressLine.IndexOf("...", StringComparison.Ordinal) + 3;
        int end = progressLine.IndexOf('%', Math.Max(0, start));
        if (end <= start)
            return 0;

        string num = progressLine[start..end];
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
               && !double.IsNaN(v) && !double.IsInfinity(v)
            ? Math.Clamp(v, 0, 100)
            : 0;
    }

    /// <summary>把 ini 摊平成 "section.key" -> value，避免为几个字段引入一个 ini 库。</summary>
    private static Dictionary<string, string> ParseIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = string.Empty;

        foreach (string raw in File.ReadAllLines(path, Gb18030))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            result[$"{section}.{line[..eq].Trim()}"] = line[(eq + 1)..].Trim();
        }

        return result;
    }

    private static bool TryInt(Dictionary<string, string> ini, string section, string key, out int value)
        => int.TryParse(ini.GetValueOrDefault($"{section}.{key}"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value);

    private static bool TryBool(Dictionary<string, string> ini, string section, string key, out bool value)
    {
        value = false;
        if (!ini.TryGetValue($"{section}.{key}", out string? raw))
            return false;

        if (raw is "真" or "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (raw is "假" or "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        return false;
    }

    /// <summary>原程序颜色存 BGR 整数（如 15527148 = 0xECECEC），转成 #RRGGBB。</summary>
    private static string BgrToHtml(int bgr)
    {
        int r = (bgr >> 16) & 0xFF;
        int g = (bgr >> 8) & 0xFF;
        int b = bgr & 0xFF;
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
