namespace DReaderNext;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

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
