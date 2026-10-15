using DReaderNext.Core;

namespace DReaderNext.Forms;

/// <summary>小说目录窗口：列出章节，双击或回车跳转。</summary>
public sealed class TocForm : Form
{
    private readonly IReadOnlyList<Chapter> _chapters;
    private readonly ListBox _list = new();

    public Chapter? SelectedChapter { get; private set; }

    public TocForm(IReadOnlyList<Chapter> chapters, int currentIndex)
    {
        _chapters = chapters;

        Text = "小说目录";
        // net48 没有 ApplicationDefaultFont，补回原默认字体（列表本身用 10pt，见下）
        Font = new Font("宋体", 12f);
        Icon = AppIcon.Value;
        Width = 440;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _list.Dock = DockStyle.Fill;
        _list.IntegralHeight = false;
        _list.Font = new Font("宋体", 10f);
        foreach (Chapter chapter in chapters)
            _list.Items.Add(chapter.Title);

        if (currentIndex >= 0 && currentIndex < _list.Items.Count)
        {
            _list.SelectedIndex = currentIndex;
            // 让当前章节可见（列表很长时默认会停在顶部）
            _list.TopIndex = Math.Max(0, Math.Min(currentIndex, Math.Max(0, _list.Items.Count - 1)));
        }

        _list.DoubleClick += (_, _) => Confirm();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            Confirm();
        };

        var jump = new Button { Text = "跳转", Dock = DockStyle.Bottom, Height = 34 };
        jump.Click += (_, _) => Confirm();

        Controls.Add(_list);
        Controls.Add(jump);
        CancelButton = null;
    }

    private void Confirm()
    {
        int index = _list.SelectedIndex;
        if (index < 0)
            return;

        SelectedChapter = _chapters[index];
        DialogResult = DialogResult.OK;
        Close();
    }
}
