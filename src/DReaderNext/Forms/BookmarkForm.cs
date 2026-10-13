using DReaderNext.Data;

namespace DReaderNext.Forms;

/// <summary>
/// 历史书签窗口：双击跳转，可删除。
/// 删除直接落到传进来的 store 上，由调用方负责保存。
/// </summary>
public sealed class BookmarkForm : Form
{
    private readonly BookmarkStore _store;
    private readonly ListBox _list = new();

    public Bookmark? SelectedBookmark { get; private set; }

    public BookmarkForm(BookmarkStore store)
    {
        _store = store;

        Text = "历史书签";
        Width = 540;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _list.Dock = DockStyle.Fill;
        _list.IntegralHeight = false;
        _list.Font = new Font("宋体", 10f);
        _list.DoubleClick += (_, _) => Confirm();

        var jump = new Button { Text = "跳转", Dock = DockStyle.Bottom, Height = 34 };
        jump.Click += (_, _) => Confirm();

        var remove = new Button { Text = "删除", Dock = DockStyle.Bottom, Height = 34 };
        remove.Click += (_, _) => DeleteSelected();

        // 顺序：先 Fill，再 Bottom（WinForms 后添加的先占位）
        Controls.Add(_list);
        Controls.Add(remove);
        Controls.Add(jump);

        RefreshList();
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (Bookmark bm in _store.Items)
            _list.Items.Add($"{bm.DisplayTitle}    [{bm.CreatedAt:MM-dd HH:mm}]");
    }

    private void Confirm()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _store.Items.Count)
            return;

        SelectedBookmark = _store.Items[index];
        DialogResult = DialogResult.OK;
        Close();
    }

    private void DeleteSelected()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _store.Items.Count)
            return;

        Bookmark target = _store.Items[index];

        if (MessageBox.Show($"是否删除书签？\n\n{target.DisplayTitle}", "DReader-Next",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _store.Remove(target);
        RefreshList();
    }
}
