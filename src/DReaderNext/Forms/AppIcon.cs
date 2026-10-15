namespace DReaderNext.Forms;

/// <summary>
/// 应用图标。直接从自身 exe 里取——csproj 的 &lt;ApplicationIcon&gt; 已经把 app.ico 嵌进去了，
/// 这样托盘、任务栏、各对话框标题栏用的是同一个图标，不用再把 ico 当资源管一遍。
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Value
    {
        get
        {
            if (_cached is not null)
                return _cached;

            try
            {
                _cached = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch (Exception)
            {
                _cached = SystemIcons.Application;   // 图标取不到不该影响程序启动
            }

            return _cached;
        }
    }
}
