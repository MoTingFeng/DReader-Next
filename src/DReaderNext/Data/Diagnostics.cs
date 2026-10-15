using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DReaderNext.Data;

/// <summary>
/// 导出诊断日志：把环境快照 + 现有日志打成一个小 zip，方便用户附到 GitHub issue 里。
///
/// 隐私：所有 Windows 路径在打包前统一打码（只保留盘符与扩展名），因为日志里会出现
/// 「打开的是哪本书」这种个人信息——用户不该为了报 bug 而泄露自己在看什么。
/// </summary>
internal static class Diagnostics
{
    private static readonly Regex PathPattern =
        new(@"[A-Za-z]:\\[^\r\n]*|\\\\[^\r\n]*", RegexOptions.Compiled);

    /// <summary>导出到指定 zip。返回包里实际写入的文件名清单。</summary>
    public static IReadOnlyList<string> Export(string zipPath, IEnumerable<string> stateLines)
    {
        var written = new List<string>();

        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        Add(archive, written, "环境与状态.txt", BuildSnapshot(stateLines));
        AddLog(archive, written, "last-load.log");
        AddLog(archive, written, "error.log");
        AddLog(archive, written, "mouse-hook.log");
        AddLog(archive, written, "settings.json");

        return written;
    }

    private static string BuildSnapshot(IEnumerable<string> stateLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DReader-Next 诊断快照");
        sb.AppendLine($"导出时间     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序版本     : {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"系统         : {WindowsName()}  ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")} 系统)");
        sb.AppendLine($".NET Framework: {FrameworkVersion()}");
        sb.AppendLine($"当前区域     : {System.Globalization.CultureInfo.CurrentCulture.Name}");
        sb.AppendLine();
        sb.AppendLine("运行状态：");
        foreach (string line in stateLines)
            sb.AppendLine($"  {line}");
        sb.AppendLine();
        sb.AppendLine("（所有文件路径已打码：只保留盘符与扩展名）");
        return sb.ToString();
    }

    /// <summary>
    /// 真实系统版本。`Environment.OSVersion` 在 .NET Framework 上会按「兼容性」谎报成 6.2.9200，
    /// 对排查 DPI / WinForms 问题毫无用处，所以直接读注册表拿产品名 + 版本 + 内部版本号。
    /// </summary>
    private static string WindowsName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key is null)
                return Environment.OSVersion.ToString();

            string product = key.GetValue("ProductName") as string ?? "Windows";
            string display = key.GetValue("DisplayVersion") as string ?? string.Empty;
            string build = key.GetValue("CurrentBuildNumber") as string ?? string.Empty;

            // Win11 的注册表 ProductName 至今仍写「Windows 10」，靠内部版本号纠正（22000 起是 Win11）
            if (int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
                product = product.Replace("Windows 10", "Windows 11");

            return $"{product} {display}（Build {build}）".Replace(" （", "（").Trim();
        }
        catch (Exception)
        {
            return Environment.OSVersion.ToString();
        }
    }

    /// <summary>读注册表拿 .NET Framework 版本号——报 DPI/WinForms 问题时这个很关键。</summary>
    private static string FrameworkVersion()
    {
        try
        {
            object? release = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null);

            if (release is int value)
            {
                string name = value >= 533320 ? "4.8.1 或更高"
                    : value >= 528040 ? "4.8"
                    : value >= 461808 ? "4.7.2"
                    : "4.7.1 或更低";
                return $"{name}（Release={value}）";
            }

            return "未读到（注册表无 NDP\\v4\\Full\\Release）";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.GetType().Name}";
        }
    }

    private static void AddLog(ZipArchive archive, List<string> written, string fileName)
    {
        string path = Path.Combine(Storage.DataDir, fileName);
        if (!File.Exists(path))
            return;

        try
        {
            Add(archive, written, fileName, File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Add(archive, written, fileName + ".读取失败.txt", ex.Message);
        }
    }

    private static void Add(ZipArchive archive, List<string> written, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(MaskPaths(content));
        written.Add(entryName);
    }

    /// <summary>路径打码：C:\示例\某本书.txt → C:\…\***.txt</summary>
    private static string MaskPaths(string text)
        => PathPattern.Replace(text, m => Mask(m.Value));

    private static string Mask(string raw)
    {
        // 行尾可能跟着引号/逗号（JSON、日志里的引用），先摘出来
        int end = raw.Length;
        while (end > 0 && raw[end - 1] is '"' or '\'' or ',' or '\\')
            end--;

        string trail = raw.Substring(end);
        string path = raw.Substring(0, end).TrimEnd();
        if (path.Length == 0)
            return raw;

        string head = path.Length >= 2 && path[1] == ':' ? path.Substring(0, 2) : @"\\";
        string ext = Path.GetExtension(path);
        return $"{head}\\…\\***{ext}{trail}";
    }
}
