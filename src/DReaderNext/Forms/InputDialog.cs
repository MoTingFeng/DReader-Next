namespace DReaderNext.Forms;

/// <summary>
/// 极简输入框。WinForms 没有内置 InputBox，为这一处输入去引一个库不值得。
/// </summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _input = new();

    private InputDialog(string title, string prompt, string defaultValue)
    {
        Text = title;
        // net48 没有 ApplicationDefaultFont（.NET 6+ 源生成器），原默认字体「宋体 12pt」在此补回
        Font = new Font("宋体", 12f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 130);

        var label = new Label
        {
            Text = prompt,
            AutoSize = false,
        };
        label.SetBounds(14, 12, 392, 36);

        _input.Text = defaultValue;
        _input.SetBounds(14, 50, 392, 26);
        _input.SelectAll();

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
        ok.SetBounds(222, 88, 88, 30);

        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(318, 88, 88, 30);

        Controls.Add(label);
        Controls.Add(_input);
        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>弹出输入框；用户取消时返回 null。</summary>
    public static string? Show(IWin32Window owner, string title, string prompt, string defaultValue)
    {
        using var dlg = new InputDialog(title, prompt, defaultValue);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._input.Text : null;
    }
}
