using DReaderNext.Data;

namespace DReaderNext;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // net48 没有 .NET 6+ 的 ApplicationConfiguration 源生成器，手写等价初始化。
        // 高 DPI 不在这里设（SetHighDpiMode 是 .NET Core API），由 app.manifest 声明 PerMonitorV2。
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 未处理异常一律落盘（data\error.log），并提示用户可以用「导出诊断日志」把它发出来。
        // 不做这一步的话，用户遇到崩溃只能描述「它突然没了」，没法排查。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            ErrorLog.Write("UI 线程未处理异常", e.Exception);
            MessageBox.Show(
                "程序遇到一个问题，已记录到 data\\error.log。\n" +
                "可以用右键菜单 → 导出诊断日志，把它发给作者。",
                "DReader-Next", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorLog.Write("非 UI 线程未处理异常" + (e.IsTerminating ? "（进程将退出）" : string.Empty),
                e.ExceptionObject as Exception);

        // 单实例：重复启动只提示，不再开第二个窗口（文案沿用原程序）
        using var mutex = new Mutex(initiallyOwned: true, @"Local\DReaderNext.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("程序已经运行，请勿重复打开。", "DReader-Next",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? startFile = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;

        Application.Run(new Forms.MainForm(startFile));
    }
}
