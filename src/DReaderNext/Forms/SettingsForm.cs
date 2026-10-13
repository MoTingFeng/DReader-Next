using DReaderNext.Data;

namespace DReaderNext.Forms;

/// <summary>
/// 设置面板：字体与配色、三个全局热键、重置。
/// 控件里的值是本地副本，只有点「确定」才写回传入的 AppSettings——取消不会留下任何改动。
/// </summary>
public sealed class SettingsForm : Form
{
    /// <summary>修饰键下拉（对应原程序的「功能键」）。</summary>
    private static readonly string[] ModifierChoices =
        { "无", "Ctrl", "Alt", "Shift", "Win", "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift" };

    /// <summary>按键下拉（对应原程序的「字符键」）。</summary>
    private static readonly string[] KeyChoices =
    {
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Space", "PageUp", "PageDown", "Left", "Right", "Up", "Down", "Home", "End",
    };

    /// <summary>原程序提供过的三个字体，其余从系统字体列表里补。</summary>
    private static readonly string[] PreferredFonts = { "宋体", "微软雅黑", "华文彩云" };

    private const string DefaultPrev = "Alt+1";
    private const string DefaultNext = "Alt+2";
    private const string DefaultBoss = "Alt+3";

    private readonly AppSettings _settings;

    private readonly ComboBox _fontName = new();
    private readonly NumericUpDown _fontSize = new();
    private readonly Button _foreButton = new();
    private readonly Button _backButton = new();

    private readonly ComboBox[] _modifierBoxes = new ComboBox[3];
    private readonly ComboBox[] _keyBoxes = new ComboBox[3];

    private Color _fore;
    private Color _back;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _fore = Parse(settings.FontColor, Color.FromArgb(0xEC, 0xEC, 0xEC));
        _back = Parse(settings.BackColor, Color.FromArgb(0xE8, 0xEC, 0xEC));

        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 400);

        BuildFontGroup();
        BuildHotkeyGroup();
        BuildButtons();

        LoadValues();
    }

    private void BuildFontGroup()
    {
        var box = new GroupBox { Text = "字体", Bounds = new Rectangle(12, 10, 446, 112) };

        box.Controls.Add(MakeLabel("字体名称", 14, 32));
        _fontName.SetBounds(88, 28, 168, 25);
        _fontName.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (string name in PreferredFonts)
            _fontName.Items.Add(name);
        foreach (FontFamily family in FontFamily.Families)
        {
            if (!_fontName.Items.Contains(family.Name))
                _fontName.Items.Add(family.Name);
        }
        box.Controls.Add(_fontName);

        box.Controls.Add(MakeLabel("字体大小", 270, 32));
        _fontSize.SetBounds(342, 28, 64, 25);
        _fontSize.Minimum = 6;
        _fontSize.Maximum = 72;
        box.Controls.Add(_fontSize);

        box.Controls.Add(MakeLabel("字体颜色", 14, 72));
        _foreButton.SetBounds(88, 68, 100, 28);
        _foreButton.Click += (_, _) => PickColor(isForeground: true);
        box.Controls.Add(_foreButton);

        box.Controls.Add(MakeLabel("自定义底色", 200, 72));
        _backButton.SetBounds(280, 68, 100, 28);
        _backButton.Click += (_, _) => PickColor(isForeground: false);
        box.Controls.Add(_backButton);

        Controls.Add(box);
    }

    private void BuildHotkeyGroup()
    {
        var box = new GroupBox { Text = "热键（全局生效）", Bounds = new Rectangle(12, 130, 446, 156) };
        string[] labels = { "上一页键", "下一页键", "老板键" };

        for (int i = 0; i < 3; i++)
        {
            int y = 28 + i * 36;

            box.Controls.Add(MakeLabel(labels[i], 14, y + 4));

            var modifier = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            modifier.SetBounds(96, y, 118, 25);
            modifier.Items.AddRange(ModifierChoices);
            box.Controls.Add(modifier);
            _modifierBoxes[i] = modifier;

            var key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            key.SetBounds(226, y, 118, 25);
            key.Items.AddRange(KeyChoices);
            box.Controls.Add(key);
            _keyBoxes[i] = key;
        }

        var resetHotkeys = new Button { Text = "重置热键", Bounds = new Rectangle(14, 118, 100, 28) };
        resetHotkeys.Click += (_, _) => ResetHotkeys();
        box.Controls.Add(resetHotkeys);

        var resetAll = new Button { Text = "重置默认参数", Bounds = new Rectangle(126, 118, 122, 28) };
        resetAll.Click += (_, _) => ResetAll();
        box.Controls.Add(resetAll);

        Controls.Add(box);
    }

    private void BuildButtons()
    {
        // 不用 DialogResult 自动关闭：老板键校验不过时不能让对话框消失
        var ok = new Button { Text = "确定" };
        ok.SetBounds(272, 356, 88, 30);
        ok.Click += (_, _) =>
        {
            if (Apply())
                DialogResult = DialogResult.OK;
        };

        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(368, 356, 88, 30);

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static Label MakeLabel(string text, int x, int y) =>
        new() { Text = text, AutoSize = true, Location = new Point(x, y) };

    private void LoadValues()
    {
        _fontName.Text = _settings.FontName;
        _fontSize.Value = Math.Clamp((decimal)_settings.FontSize, _fontSize.Minimum, _fontSize.Maximum);
        UpdateColorButtons();

        SetHotkeyRow(0, _settings.KeyPrev);
        SetHotkeyRow(1, _settings.KeyNext);
        SetHotkeyRow(2, _settings.KeyBoss);
    }

    private void SetHotkeyRow(int index, string spec)
    {
        (string modifier, string key) = SplitHotkey(spec);
        _modifierBoxes[index].SelectedItem = ModifierChoices.Contains(modifier) ? modifier : "Alt";

        _keyBoxes[index].SelectedItem = KeyChoices.Contains(key)
            ? key
            : KeyChoices[0];
    }

    private void PickColor(bool isForeground)
    {
        using var dlg = new ColorDialog
        {
            Color = isForeground ? _fore : _back,
            FullOpen = true,
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        if (isForeground)
            _fore = dlg.Color;
        else
            _back = dlg.Color;

        UpdateColorButtons();
    }

    private void UpdateColorButtons()
    {
        _foreButton.BackColor = _fore;
        _foreButton.Text = ColorTranslator.ToHtml(_fore);
        _backButton.BackColor = _back;
        _backButton.Text = ColorTranslator.ToHtml(_back);
    }

    private void ResetHotkeys()
    {
        SetHotkeyRow(0, DefaultPrev);
        SetHotkeyRow(1, DefaultNext);
        SetHotkeyRow(2, DefaultBoss);

        MessageBox.Show("已重置所有热键", "DReader-Next", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ResetAll()
    {
        var defaults = new AppSettings();

        _fontName.Text = defaults.FontName;
        _fontSize.Value = Math.Clamp((decimal)defaults.FontSize, _fontSize.Minimum, _fontSize.Maximum);
        _fore = Parse(defaults.FontColor, Color.FromArgb(0xEC, 0xEC, 0xEC));
        _back = Parse(defaults.BackColor, Color.FromArgb(0xE8, 0xEC, 0xEC));
        UpdateColorButtons();

        SetHotkeyRow(0, DefaultPrev);
        SetHotkeyRow(1, DefaultNext);
        SetHotkeyRow(2, DefaultBoss);

        // 沿用原程序文案
        MessageBox.Show("已重置所有设置，字体窗口大小重启后生效", "DReader-Next",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>校验并写回。返回 false 表示不通过、对话框不该关闭。</summary>
    private bool Apply()
    {
        string fontName = _fontName.Text.Trim();
        _settings.FontName = fontName.Length > 0 ? fontName : "宋体";
        _settings.FontSize = (float)_fontSize.Value;
        _settings.FontColor = ColorTranslator.ToHtml(_fore);
        _settings.BackColor = ColorTranslator.ToHtml(_back);

        _settings.KeyPrev = CombineHotkey(Selected(_modifierBoxes[0]), Selected(_keyBoxes[0]));
        _settings.KeyNext = CombineHotkey(Selected(_modifierBoxes[1]), Selected(_keyBoxes[1]));
        _settings.KeyBoss = CombineHotkey(Selected(_modifierBoxes[2]), Selected(_keyBoxes[2]));

        // 纯单键做全局热键会抢掉系统按键，老板键尤其不能这么干
        if (!_settings.KeyBoss.Contains('+'))
        {
            MessageBox.Show("老板键请至少带一个修饰键（Ctrl / Alt / Shift / Win）。", "DReader-Next",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _settings.Save();
        return true;
    }

    private static string Selected(ComboBox box) => box.SelectedItem as string ?? "无";

    private static string CombineHotkey(string modifier, string key) =>
        modifier.Length == 0 || modifier == "无" ? key : $"{modifier}+{key}";

    private static (string Modifier, string Key) SplitHotkey(string spec)
    {
        var modifiers = new List<string>();
        string key = string.Empty;

        foreach (string part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers.Add("Ctrl"); break;
                case "alt": modifiers.Add("Alt"); break;
                case "shift": modifiers.Add("Shift"); break;
                case "win": modifiers.Add("Win"); break;
                default: key = CanonicalKey(part); break;
            }
        }

        string modifier = modifiers.Count == 0 ? "无" : string.Join("+", modifiers);
        return (modifier, key);
    }

    private static string CanonicalKey(string key)
    {
        foreach (string candidate in KeyChoices)
        {
            if (candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return key.ToUpperInvariant();
    }

    private static Color Parse(string html, Color fallback)
    {
        try
        {
            return ColorTranslator.FromHtml(html);
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
