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
