using System.Text;

namespace DReaderNext.Data;

/// <summary>
/// 未处理异常落盘。用户提 bug 时最需要的就是这个——否则程序崩了只剩一个弹框，什么线索都没留下。
/// 位置：data\error.log；用右键菜单的「导出诊断日志」可以把它一起打包发出来。
/// </summary>
internal static class ErrorLog
{
    public static string FilePath => Path.Combine(Storage.DataDir, "error.log");

    /// <summary>写一条异常记录。记日志本身绝不能再抛异常。</summary>
    public static void Write(string title, Exception? error)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}");
            sb.AppendLine($"  程序版本: {typeof(ErrorLog).Assembly.GetName().Version}");
            sb.AppendLine($"  系统: {Environment.OSVersion}  64位进程: {Environment.Is64BitProcess}");

            if (error is not null)
            {
                sb.AppendLine($"  {error.GetType().FullName}: {error.Message}");
                sb.AppendLine(error.StackTrace);
            }

            sb.AppendLine();
            File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception)
        {
            // 写不进去也不能再抛：这是最后一道兜底
        }
    }
}
