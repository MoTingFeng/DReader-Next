using System.Text.Encodings.Web;
using System.Text.Json;

namespace DReaderNext.Data;

/// <summary>
/// 数据目录与 JSON 读写。
/// 绿色优先：先试 exe 旁的 data\，确实写不进去（例如 exe 放在 Program Files）才回退 %APPDATA%。
/// </summary>
public static class Storage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 中文不转义成 \uXXXX，配置文件保持可读、可手改
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DataDir { get; } = ResolveDataDir();

    private static string ResolveDataDir()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(local);
            // 目录建得出来不代表写得进去（只读目录/权限收紧），真写一次才算数
            string probe = Path.Combine(local, ".writable");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return local;
        }
        catch (Exception)
        {
            string roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DReaderNext");
            Directory.CreateDirectory(roaming);
            return roaming;
        }
    }

    public static T? LoadJson<T>(string fileName) where T : class
    {
        string path = Path.Combine(DataDir, fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception)
        {
            // 配置损坏不该让程序起不来：退回默认值，坏文件留作排查
            return null;
        }
    }

    public static void SaveJson<T>(string fileName, T value) where T : class
    {
        string path = Path.Combine(DataDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
    }
}
