using System.Diagnostics;
using DReaderNext.Core;
using DReaderNext.Data;
using DReaderNext.Interop;

namespace DReaderNext.Forms;

/// <summary>
/// 主窗口。
/// P1 阅读核心：打开/拖放、编码探测、章节识别、分页、目录跳转、进度记忆。
/// P2 摸鱼形态：背景全透明（抠色）+ 透明度可调、无边框可拖动、置顶、托盘、
///              老板键与翻页全局热键、鼠标左右半区翻页、滚轮、贴边隐藏、
///              鼠标离开自动隐藏窗口、单实例（见 Program）。
/// </summary>
public sealed class MainForm : Form
{
    private const int HotkeyPrev = 1;
    private const int HotkeyNext = 2;
    private const int HotkeyBoss = 3;

    /// <summary>贴边隐藏后留在屏幕内的像素宽度，用于让鼠标能碰到并弹出。</summary>
    private const int EdgeStickPixels = 3;

    /// <summary>鼠标位移超过这个距离就算拖动窗口，而不是单击翻页。</summary>
    private const int DragThreshold = 4;

    /// <summary>
    /// 鼠标位置轮询间隔，同时也是「离开多久后隐藏」和「静止多久藏指针」的实际粒度：
    /// 用户把延迟设成 10 毫秒，也得等一个轮询周期才会触发，所以这里不能太大。
    /// 50 毫秒 ≈ 每秒 20 次 Cursor.Position 判断，开销可忽略。
    /// </summary>
    private const int AutoHidePollMs = 50;

    private readonly AppSettings _settings;
    private readonly ProgressStore _progress;

    private readonly RichTextBox _text = new();
    private readonly MenuStrip _menu = new();
    // 状态栏用 Panel + Label 而不是 StatusStrip：
    // 实测在 200% DPI 且窗口尺寸在运行中变化的情况下，StatusStrip 的两个项会被布局到
    // X=662、Y=42（而状态栏本身只有 660×41），一个字都显示不出来，强制 PerformLayout 也无效。
    private readonly Panel _statusBar = new();
    private readonly Label _statusLeft = new();
    private readonly Label _statusRight = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _contextMenu = new();
    private readonly System.Windows.Forms.Timer _edgeTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _autoHideTimer = new() { Interval = AutoHidePollMs };
    private readonly System.Windows.Forms.Timer _autoPageTimer = new();

    /// <summary>
    /// 鼠标离开窗口后的倒计时。走事件（MouseLeave）而不是只靠轮询，
    /// 延迟才是精确的——设 10 毫秒就真的约 10 毫秒后隐藏，不必等一个轮询周期。
    /// </summary>
    private readonly System.Windows.Forms.Timer _leaveTimer = new();

    /// <summary>自动翻页的间隔秒数，供设置对话框回填。</summary>
    private int _autoPageSeconds = 30;

    private readonly ToolStripMenuItem _miTransparent = new("背景全透明(&B)");
    private readonly ToolStripMenuItem _miClickThrough = new("鼠标穿透(&P)");
    private readonly ToolStripMenuItem _miTopMost = new("窗口置顶(&A)");
    private readonly ToolStripMenuItem _miSnapEdge = new("贴边隐藏(&E)");
    private readonly ToolStripMenuItem _miTitleBar = new("显示标题栏(&L)");
    private readonly ToolStripMenuItem _miAutoHide = new("鼠标离开自动隐藏(&M)");
    private readonly ToolStripMenuItem _miHideCursor = new("鼠标静止隐藏指针(&C)");
    private readonly ToolStripMenuItem _miDimFont = new("字体变暗(&D)");
    private readonly ToolStripMenuItem _miOpacity = new("透明度(&T)");

    private Book? _book;

    /// <summary>当前页首行的绝对行号。阅读位置只由它表示，页号是派生值。</summary>
    private int _currentLine;

    private int _rowsPerPage = 20;
    private readonly string? _pendingStartFile;

    // 拖动与单击的区分
    private Point _pressScreen;
    private Point _pressWindowLocation;
    private bool _dragging;

    // 「鼠标穿透」关闭时用的低层鼠标钩子（把窗口矩形内的鼠标事件接管过来）
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;   // 必须留引用：被 GC 回收会直接崩
    private IntPtr _mouseHook;
    private bool _hookPressed;
    private bool _hookDragging;
    private Point _hookPressScreen;
    private Point _hookPressWindowLocation;
    private int _hookEventsLogged;

    // 隐藏（老板键或鼠标离开自动隐藏都用这一套状态）
    private bool _isHidden;
    private Point _hiddenRestoreLocation;

    /// <summary>
    /// 只有鼠标「进过窗口」之后离开才触发自动隐藏。
    ///
    /// 初始为 false：刚启动时窗口会一直显示，哪怕鼠标不在窗口上——否则用户刚打开、
    /// 还没看见它就自己消失了。要等鼠标移进过窗口（或点击过）之后，离开才会触发隐藏。
    /// 同样是这个标志，让热键翻页把窗口唤出后不会被立刻又藏掉。
    /// </summary>
    private bool _autoHideArmed;

    private int _cursorOutsideMs;

    // 鼠标静止隐藏指针（原程序的「鼠标隐藏」）
    private Point _lastCursorPosition;
    private int _cursorIdleMs;

    /// <summary>指针是否正被隐藏。Cursor.Hide/Show 是引用计数的，必须靠它保证成对调用。</summary>
    private bool _cursorHidden;

    // 贴边隐藏
    private bool _edgeHidden;
    private Point _edgeRestoreLocation;

    /// <summary>是否有本程序的模态对话框开着（打开文件、小说目录）。</summary>
    private bool _modalOpen;

    private int _hotkeyFailures;

    /// <summary>当前 DPI 缩放因子（1.0 = 96 DPI）。配置里的窗口宽高按逻辑像素理解。</summary>
    private float DpiScale => DeviceDpi / 96f;

    public MainForm(string? startFile)
    {
        _pendingStartFile = startFile;
        _settings = AppSettings.Load();
        _progress = ProgressStore.Load();

        // net48 没有 ApplicationDefaultFont（.NET 6+ 源生成器），原默认字体「宋体 12pt」在此补回；
        // 必须在建控件之前设，子控件才能继承到。
        Font = new Font("宋体", 12f);

        BuildUi();
        BuildContextMenu();
        BuildTray();
        ApplySettings();

        AllowDrop = true;
        KeyPreview = true;

        Load += OnFormLoad;
        FormClosing += OnFormClosing;
        KeyDown += OnKeyDown;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        ClientSizeChanged += OnClientSizeChanged;
        LocationChanged += OnLocationChanged;
        MouseEnter += OnFormMouseEnter;
        _edgeTimer.Tick += OnEdgeTimerTick;
        _autoHideTimer.Tick += OnAutoHideTick;
        _autoPageTimer.Tick += OnAutoPageTick;
        _leaveTimer.Tick += OnLeaveTimerTick;
        MouseLeave += OnFormMouseLeave;
        _text.MouseLeave += OnFormMouseLeave;

        // 正文区接管鼠标：拖动窗口、左右半区翻页、滚轮翻页
        _text.MouseDown += OnTextMouseDown;
        _text.MouseMove += OnTextMouseMove;
        _text.MouseUp += OnTextMouseUp;
        _text.MouseWheel += OnTextMouseWheel;
        _text.MouseEnter += OnFormMouseEnter;
    }

    // ---------------------------------------------------------------- 界面构建

    private void BuildUi()
    {
        Text = "DReader-Next";

        var fileMenu = new ToolStripMenuItem("文件(&F)");
        fileMenu.DropDownItems.Add("打开(&O)...", null, async (_, _) => await PromptOpenAsync());
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("退出(&X)", null, (_, _) => Close());

        var readMenu = new ToolStripMenuItem("阅读(&R)");
        readMenu.DropDownItems.Add("上一页(&P)", null, (_, _) => PrevPage());
        readMenu.DropDownItems.Add("下一页(&N)", null, (_, _) => NextPage());
        readMenu.DropDownItems.Add(new ToolStripSeparator());
        readMenu.DropDownItems.Add("小说目录(&C)", null, (_, _) => ShowToc());

        _menu.Items.Add(fileMenu);
        _menu.Items.Add(readMenu);
        MainMenuStrip = _menu;

        _text.Dock = DockStyle.Fill;
        _text.ReadOnly = true;
        _text.BorderStyle = BorderStyle.None;
        _text.WordWrap = true;
        _text.ScrollBars = RichTextBoxScrollBars.None;   // 分页显示，不需要滚动条
        _text.TabStop = false;

        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.Height = 26;
        _statusBar.Padding = new Padding(6, 0, 6, 0);

        _statusLeft.Dock = DockStyle.Fill;
        _statusLeft.TextAlign = ContentAlignment.MiddleLeft;
        _statusLeft.AutoEllipsis = true;

        _statusRight.Dock = DockStyle.Right;
        _statusRight.AutoSize = true;
        _statusRight.TextAlign = ContentAlignment.MiddleRight;

        // 后添加的先占位：Right 先占住右侧，剩下的宽度归 Fill
        _statusBar.Controls.Add(_statusLeft);
        _statusBar.Controls.Add(_statusRight);

        // 顺序要紧：先加 Fill 的，再加 Top/Bottom 的（WinForms 后添加的先占位）
        Controls.Add(_text);
        Controls.Add(_statusBar);
        Controls.Add(_menu);
    }

    private void BuildContextMenu()
    {
        _contextMenu.Items.Add("打开文件(&O)...", null, async (_, _) => await PromptOpenAsync());
        _contextMenu.Items.Add("小说目录(&C)", null, (_, _) => ShowToc());
        _contextMenu.Items.Add("插入书签(&I)", null, (_, _) => InsertBookmark());
        _contextMenu.Items.Add("历史书签(&K)", null, (_, _) => ShowBookmarks());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("上一页(&P)", null, (_, _) => PrevPage());
        _contextMenu.Items.Add("下一页(&N)", null, (_, _) => NextPage());
        _contextMenu.Items.Add("自动翻页(&A)...", null, (_, _) => StartAutoPage());
        _contextMenu.Items.Add("停止翻页(&T)", null, (_, _) => StopAutoPage());
        _contextMenu.Items.Add(new ToolStripSeparator());

        foreach (int percent in new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20 })
        {
            int captured = percent;
            _miOpacity.DropDownItems.Add(new ToolStripMenuItem($"{percent}%", null,
                (_, _) => SetOpacity(captured / 100.0)));
        }
        _contextMenu.Items.Add(_miOpacity);

        _miTransparent.Click += (_, _) =>
        {
            _settings.TransparentBackground = !_settings.TransparentBackground;
            ApplyAppearance();
            _settings.Save();
        };
        _contextMenu.Items.Add(_miTransparent);

        _miClickThrough.Click += (_, _) =>
        {
            _settings.ClickThrough = !_settings.ClickThrough;
            UpdateMouseHook();
            _settings.Save();
        };
        _contextMenu.Items.Add(_miClickThrough);

        _miTopMost.Click += (_, _) =>
        {
            _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
            ApplyAppearance();
            _settings.Save();
        };
        _contextMenu.Items.Add(_miTopMost);

        _miSnapEdge.Click += (_, _) =>
        {
            _settings.SnapToEdge = !_settings.SnapToEdge;
            _settings.Save();
        };
        _contextMenu.Items.Add(_miSnapEdge);

        _miAutoHide.Click += (_, _) =>
        {
            _settings.AutoHideOnMouseLeave = !_settings.AutoHideOnMouseLeave;
            _cursorOutsideMs = 0;
            _settings.Save();
        };
        _contextMenu.Items.Add(_miAutoHide);

        _miHideCursor.Click += (_, _) =>
        {
            _settings.HideCursorWhenIdle = !_settings.HideCursorWhenIdle;
            ShowCursorAgain();
            _settings.Save();
        };
        _contextMenu.Items.Add(_miHideCursor);

        _miDimFont.Click += (_, _) => ToggleDimFont();
        _contextMenu.Items.Add(_miDimFont);

        var autoHideDelay = new ToolStripMenuItem("离开多久后隐藏(&D)...");
        autoHideDelay.Click += (_, _) =>
        {
            string? input = InputDialog.Show(this, "自动隐藏延迟",
                "鼠标离开窗口多少毫秒后隐藏？（100 – 3600000）",
                _settings.AutoHideDelayMs.ToString());

            if (input is null)
                return;

            if (!int.TryParse(input.Trim(), out int ms) || ms < 10 || ms > 3_600_000)
            {
                MessageBox.Show(
                    $"请输入 10 到 3600000 之间的毫秒数。\n（实际触发粒度是 {AutoHidePollMs} 毫秒的轮询周期）",
                    "DReader-Next", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.AutoHideDelayMs = ms;
            _cursorOutsideMs = 0;
            _settings.Save();
        };
        _contextMenu.Items.Add(autoHideDelay);

        _miTitleBar.Click += (_, _) =>
        {
            // 有系统标题栏时由系统负责拖动，无边框时由正文区的鼠标逻辑负责
            _settings.ShowTitleBar = !_settings.ShowTitleBar;
            ApplySettings();
            _settings.Save();
        };
        _contextMenu.Items.Add(_miTitleBar);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("跳转页面(&G)...", null, (_, _) => JumpToPage());
        _contextMenu.Items.Add("比率跳转(&R)...", null, (_, _) => JumpToPercent());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("设置(&S)...", null, (_, _) => OpenSettings());
        _contextMenu.Items.Add("关于(&A)...", null, (_, _) => ShowAbout());
        _contextMenu.Items.Add("退出(&X)", null, (_, _) => Close());

        _contextMenu.Opening += (_, _) => SyncContextMenuChecks(autoHideDelay);

        ContextMenuStrip = _contextMenu;
        // 正文控件填满整个窗口，右键必须也走这个菜单——无边框模式下它是主要入口
        _text.ContextMenuStrip = _contextMenu;
    }

    private void SyncContextMenuChecks(ToolStripMenuItem autoHideDelay)
    {
        _miTransparent.Checked = _settings.TransparentBackground;
        _miClickThrough.Checked = _settings.ClickThrough;
        _miTopMost.Checked = _settings.AlwaysOnTop;
        _miSnapEdge.Checked = _settings.SnapToEdge;
        _miAutoHide.Checked = _settings.AutoHideOnMouseLeave;
        _miHideCursor.Checked = _settings.HideCursorWhenIdle;
        _miDimFont.Checked = _settings.DimFont;
        _miTitleBar.Checked = _settings.ShowTitleBar;

        // 延迟项跟着开关走：关闭自动隐藏时置灰，标题里带上当前值
        autoHideDelay.Enabled = _settings.AutoHideOnMouseLeave;
        autoHideDelay.Text = $"离开多久后隐藏（当前 {_settings.AutoHideDelayMs} 毫秒）...";

        foreach (ToolStripItem item in _miOpacity.DropDownItems)
        {
            if (item is not ToolStripMenuItem mi)
                continue;

            mi.Checked = int.TryParse(mi.Text?.TrimEnd('%'), out int percent)
                         && Math.Abs(_settings.WindowOpacity * 100 - percent) < 0.5;
        }
    }

    private void BuildTray()
    {
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "DReader-Next";

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示/隐藏(&S)", null, (_, _) => ToggleWindow());
        trayMenu.Items.Add("上一页(&P)", null, (_, _) => PrevPage());
        trayMenu.Items.Add("下一页(&N)", null, (_, _) => NextPage());
        trayMenu.Items.Add("小说目录(&C)", null, (_, _) => ShowToc());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出(&X)", null, (_, _) => Close());

        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.Visible = true;
    }

    // ---------------------------------------------------------------- 形态

    private void ApplySettings()
    {
        Font font;
        try
        {
            font = new Font(_settings.FontName, _settings.FontSize);
        }
        catch (Exception)
        {
            font = new Font("宋体", 12f);
        }
        _text.Font = font;

        FormBorderStyle = _settings.ShowTitleBar ? FormBorderStyle.Sizable : FormBorderStyle.None;
        ShowInTaskbar = !_settings.HideTrayIcon;

        if (_settings.WindowX >= 0 && _settings.WindowY >= 0)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(_settings.WindowX, _settings.WindowY);
        }

        ApplyAppearance();
    }

    /// <summary>
    /// 应用窗口形态。透明分两层，互不冲突：
    ///   TransparencyKey（抠色）—— 底色像素被完全抠掉，只剩文字浮在桌面上，即原程序的「背景全透明」；
    ///   Opacity（整体不透明度）—— 剩下的部分还能调浓淡，即原程序的「透明度可调」。
    /// </summary>
    private void ApplyAppearance()
    {
        TopMost = _settings.AlwaysOnTop;
        Opacity = Compat.Clamp(_settings.WindowOpacity, 0.2, 1.0);

        Color key = ParseColor(_settings.BackColor, Color.FromArgb(0xE8, 0xEC, 0xEC));

        // 还没打开书时不做透明：那时窗口里只有状态栏一行提示字，
        // 把背景抠掉之后它飘在桌面上等于看不见，用户会以为程序根本没启动。
        bool transparent = _settings.TransparentBackground && _book is not null;

        TransparencyKey = transparent ? key : Color.Empty;

        // 透明模式下菜单栏没有意义（悬浮窗靠右键菜单），状态栏则设成键色一起被抠掉
        _menu.Visible = !transparent;
        _statusBar.BackColor = transparent ? key : SystemColors.Control;

        ApplyReadingColors(key, transparent);
        UpdateMouseHook();
    }

    /// <summary>
    /// 正文配色。透明模式下沿用原程序的浅灰字——那套配色本来就是为「浮在深色桌面上」设计的；
    /// 不透明模式下浅灰字配浅灰底亮度差只有 0.003，等于隐形，必须换深色。
    /// </summary>
    private void ApplyReadingColors(Color background, bool transparent)
    {
        Color fore = ParseColor(_settings.FontColor, Color.FromArgb(0xEC, 0xEC, 0xEC));

        if (!transparent && Math.Abs(Luminance(fore) - Luminance(background)) < 0.25)
            fore = Color.FromArgb(0x20, 0x20, 0x20);

        // 字体变暗：整体压暗一档，摸鱼时更不显眼（对应原程序的「字体变暗」）
        if (_settings.DimFont)
            fore = Color.FromArgb(fore.R * 6 / 10, fore.G * 6 / 10, fore.B * 6 / 10);

        _text.ForeColor = fore;
        _text.BackColor = background;
        BackColor = background;

        Color statusFore = transparent ? fore : SystemColors.ControlText;
        _statusLeft.ForeColor = statusFore;
        _statusRight.ForeColor = statusFore;
    }

    private void SetOpacity(double value)
    {
        _settings.WindowOpacity = Compat.Clamp(value, 0.2, 1.0);
        ApplyAppearance();
        _settings.Save();
    }

    private static Color ParseColor(string html, Color fallback)
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

    /// <summary>相对亮度（0=黑，1=白），用于判断文字与底色对比是否够看。</summary>
    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    // ---------------------------------------------------------------- 生命周期

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        // 句柄与 DPI 已确定，此时设窗口尺寸才不会被自动缩放改写。
        // 配置里的宽高是「逻辑像素」：原程序 DPI 不感知，在 200% 缩放下由系统替它放大；
        // 新程序声明了 PerMonitorV2，必须自己换算，否则窗口视觉上只剩一半大。
        ClientSize = new Size(
            (int)Math.Round(Math.Max(240, _settings.WindowWidth) * DpiScale),
            (int)Math.Round(Math.Max(200, _settings.WindowHeight) * DpiScale));

        // 尺寸变了重排一次（状态栏已换成 Panel + Label，不再有 StatusStrip 那种布局错位问题）
        PerformLayout();

        RecalcRowsPerPage();

        RegisterHotkeys();
        _autoHideTimer.Start();

        string? file = _pendingStartFile;
        if (file is null && _settings.AutoOpenLast
            && _settings.LastBookPath is { Length: > 0 } last && File.Exists(last))
        {
            file = last;
        }

        if (file is not null)
            await LoadBookAsync(file);
        else
            SetStatus("请自行打开小说！", string.Empty);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveProgress();
        UnregisterHotkeys();
        RemoveMouseHook();

        _autoHideTimer.Stop();
        _edgeTimer.Stop();
        _autoPageTimer.Stop();
        _leaveTimer.Stop();

        // 指针若正被隐藏，别把它带出程序（Cursor 的计数是进程级的）
        ShowCursorAgain();

        _tray.Visible = false;

        // 存回逻辑像素，这样换个 DPI 的显示器打开还是同样的视觉大小
        _settings.WindowWidth = (int)Math.Round(ClientSize.Width / DpiScale);
        _settings.WindowHeight = (int)Math.Round(ClientSize.Height / DpiScale);
        if (WindowState == FormWindowState.Normal && !_edgeHidden && !_isHidden)
        {
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
        }
        _settings.Save();
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_book is null)
            return;

        int before = _rowsPerPage;
        RecalcRowsPerPage();
        if (_rowsPerPage != before)
            RenderPage();
    }

    // ---------------------------------------------------------------- 显示与隐藏

    private void HideWindow()
    {
        if (_isHidden)
            return;

        _hiddenRestoreLocation = Location;
        Hide();
        _isHidden = true;
    }

    private void ShowWindow()
    {
        if (!_isHidden)
        {
            Activate();
            return;
        }

        Location = _hiddenRestoreLocation;
        Show();
        _isHidden = false;
        _cursorOutsideMs = 0;
        Activate();
    }

    private void ToggleWindow()
    {
        if (_isHidden)
            ShowWindow();
        else
            HideWindow();
    }

    // ---------------------------------------------------------------- 鼠标离开自动隐藏

    /// <summary>
    /// 轮询光标位置而不是用 MouseLeave：正文是一个填满窗口的 RichTextBox，
    /// 鼠标在子控件间移动、快速甩出、或经过菜单时，MouseLeave 的时序并不可靠。
    /// </summary>
    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        Point cursor = Cursor.Position;
        UpdateCursorVisibility(cursor);

        if (!_settings.AutoHideOnMouseLeave)
            return;

        if (Bounds.Contains(cursor))
        {
            // 鼠标回到窗口内 → 重新武装，下一次离开才会再触发
            _autoHideArmed = true;
            _cursorOutsideMs = 0;
            return;
        }

        if (_isHidden || !_autoHideArmed)
            return;

        // 对话框/菜单打开着、或正在拖动窗口时，鼠标本来就在窗口外，不能算「离开」
        if (_modalOpen || _dragging
            || _contextMenu.Visible
            || _tray.ContextMenuStrip?.Visible == true)
        {
            _cursorOutsideMs = 0;
            return;
        }

        _cursorOutsideMs += AutoHidePollMs;
        if (_cursorOutsideMs < Math.Max(AutoHidePollMs, _settings.AutoHideDelayMs))
            return;

        _cursorOutsideMs = 0;
        _autoHideArmed = false;
        HideWindow();
    }

    /// <summary>
    /// 鼠标在窗口内静止一段时间后隐藏指针，一动就恢复（原程序的「鼠标隐藏」）。
    ///
    /// Cursor.Hide/Show 是引用计数的：多调一次 Hide 而没有配对的 Show，指针会永久消失。
    /// 所以这里的每一次 Hide 都由 _cursorHidden 记着，Show 也只有置位时才调。
    /// </summary>
    private void UpdateCursorVisibility(Point current)
    {
        if (!_settings.HideCursorWhenIdle)
        {
            ShowCursorAgain();
            return;
        }

        bool moved = current != _lastCursorPosition;
        _lastCursorPosition = current;

        if (moved)
        {
            _cursorIdleMs = 0;
            ShowCursorAgain();
            return;
        }

        // 指针在窗口外时归「窗口自动隐藏」管，这里不碰
        if (_cursorHidden || !Bounds.Contains(current))
            return;

        _cursorIdleMs += AutoHidePollMs;
        if (_cursorIdleMs < Math.Max(AutoHidePollMs, _settings.CursorIdleDelayMs))
            return;

        Cursor.Hide();
        _cursorHidden = true;
    }

    /// <summary>指针一动就恢复；没隐藏过时什么都不做，避免多调一次 Show 把计数打乱。</summary>
    private void ShowCursorAgain()
    {
        _cursorIdleMs = 0;

        if (!_cursorHidden)
            return;

        Cursor.Show();
        _cursorHidden = false;
    }

    // ---------------------------------------------------------------- 字体变暗与关于

    private void ToggleDimFont()
    {
        _settings.DimFont = !_settings.DimFont;
        ApplyAppearance();
        _settings.Save();

        SetStatusTemp(_settings.DimFont ? "字体已变暗" : "字体已恢复正常");
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "DReader-Next 0.1.0\n\n" +
            "原程序 DReader 7.3.0.0（易语言，作者 Dim，2022-03-18）的功能等效重写版。\n\n" +
            "已实现：\n" +
            "  打开 txt、拖入文件、编码自动识别（UTF-8 / UTF-16 / GB18030）\n" +
            "  章节识别与目录跳转、分页阅读、阅读进度记忆\n" +
            "  背景全透明与透明度可调、无边框拖动、窗口置顶、贴边隐藏\n" +
            "  老板键与翻页全局热键、鼠标左右半区翻页、滚轮、托盘\n" +
            "  鼠标离开自动隐藏窗口、鼠标静止隐藏指针、字体变暗\n" +
            "  书签、跳转页面、比率跳转、自动翻页、设置\n\n" +
            "不包含：EPUB、自动更新（原更新地址早已失效）",
            "关于 DReader-Next", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------------------------------------------------------- 全局热键

    private void RegisterHotkeys()
    {
        UnregisterHotkeys();
        _hotkeyFailures = 0;

        if (!TryRegisterHotkey(HotkeyPrev, _settings.KeyPrev)) _hotkeyFailures++;
        if (!TryRegisterHotkey(HotkeyNext, _settings.KeyNext)) _hotkeyFailures++;
        if (!TryRegisterHotkey(HotkeyBoss, _settings.KeyBoss)) _hotkeyFailures++;

        // 热键被别的程序占用时必须明说，不能静默失效
        if (_hotkeyFailures > 0)
            SetStatusTemp($"{_hotkeyFailures} 个热键注册失败，可能已被其它程序占用");
    }

    private bool TryRegisterHotkey(int id, string spec)
    {
        (uint modifiers, uint vk) = ParseHotkey(spec);
        if (vk == 0)
            return false;

        return NativeMethods.RegisterHotKey(Handle, id,
            modifiers | (uint)NativeMethods.HotkeyModifiers.NoRepeat, vk);
    }

    private void UnregisterHotkeys()
    {
        if (!IsHandleCreated)
            return;

        NativeMethods.UnregisterHotKey(Handle, HotkeyPrev);
        NativeMethods.UnregisterHotKey(Handle, HotkeyNext);
        NativeMethods.UnregisterHotKey(Handle, HotkeyBoss);
    }

    /// <summary>解析 "Alt+1" 这类热键串。</summary>
    private static (uint Modifiers, uint Vk) ParseHotkey(string spec)
    {
        uint modifiers = 0;
        uint vk = 0;

        foreach (string raw in spec.Split('+'))
        {
            // net48 没有 StringSplitOptions.TrimEntries（.NET 5+），自己修剪并跳过空段
            string part = raw.Trim();
            if (part.Length == 0)
                continue;

            switch (part.ToLowerInvariant())
            {
                case "alt": modifiers |= (uint)NativeMethods.HotkeyModifiers.Alt; break;
                case "ctrl" or "control": modifiers |= (uint)NativeMethods.HotkeyModifiers.Control; break;
                case "shift": modifiers |= (uint)NativeMethods.HotkeyModifiers.Shift; break;
                case "win": modifiers |= (uint)NativeMethods.HotkeyModifiers.Win; break;
                default: vk = ParseVirtualKey(part); break;
            }
        }

        return (modifiers, vk);
    }

    private static uint ParseVirtualKey(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return c;
        }

        return key.ToUpperInvariant() switch
        {
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "SPACE" => 0x20, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            _ => 0,
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HotkeyPrev:
                    PrevPage();
                    return;
                case HotkeyNext:
                    NextPage();
                    return;
                case HotkeyBoss:
                    ToggleWindow();
                    return;
            }
        }

        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- 贴边隐藏

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!_settings.SnapToEdge || _edgeHidden)
            return;

        // 拖完停一会儿再判断，避免拖动过程中反复缩进
        _edgeTimer.Stop();
        _edgeTimer.Start();
    }

    private void OnEdgeTimerTick(object? sender, EventArgs e)
    {
        _edgeTimer.Stop();
        if (!_settings.SnapToEdge || _edgeHidden || _isHidden)
            return;

        Rectangle area = Screen.FromControl(this).WorkingArea;
        const int stick = 8;

        int side = 0;
        if (Math.Abs(Left - area.Left) <= stick)
            side = -1;
        else if (Math.Abs(Right - area.Right) <= stick)
            side = 1;

        if (side == 0)
            return;

        _edgeRestoreLocation = Location;
        _edgeHidden = true;

        Left = side < 0 ? area.Left - Width + EdgeStickPixels : area.Right - EdgeStickPixels;
    }

    private void OnFormMouseEnter(object? sender, EventArgs e)
    {
        // 鼠标回来了，取消「离开即隐藏」的倒计时
        _leaveTimer.Stop();

        if (!_edgeHidden)
            return;

        _edgeHidden = false;
        Location = _edgeRestoreLocation;
    }

    /// <summary>
    /// 鼠标离开窗口。这里只启动倒计时，真正隐藏交给 OnLeaveTimerTick——
    /// 因为鼠标在窗口内的子控件之间移动同样会触发 MouseLeave，不能立刻当成「离开了」。
    /// </summary>
    private void OnFormMouseLeave(object? sender, EventArgs e)
    {
        if (!_settings.AutoHideOnMouseLeave || !_autoHideArmed || _isHidden)
            return;
        if (_modalOpen || _dragging)
            return;

        // 延迟设成 10 毫秒就真的按 10 毫秒走，不再受轮询周期限制
        _leaveTimer.Interval = Math.Max(10, _settings.AutoHideDelayMs);
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    private void OnLeaveTimerTick(object? sender, EventArgs e)
    {
        _leaveTimer.Stop();

        if (!_settings.AutoHideOnMouseLeave || !_autoHideArmed || _isHidden)
            return;

        // 关键兜底：鼠标可能只是在窗口内的子控件间挪了一下，或者已经回来了
        if (Bounds.Contains(Cursor.Position))
            return;

        if (_modalOpen || _dragging
            || _contextMenu.Visible
            || _tray.ContextMenuStrip?.Visible == true)
            return;

        _autoHideArmed = false;
        _cursorOutsideMs = 0;
        HideWindow();
    }

    // ---------------------------------------------------------------- 打开与加载

    private async Task PromptOpenAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "请选择文件：",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };

        _modalOpen = true;
        try
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
                await LoadBookAsync(dlg.FileName);
        }
        finally
        {
            _modalOpen = false;
            _cursorOutsideMs = 0;
        }
    }

    private async Task LoadBookAsync(string path)
    {
        try
        {
            SetStatus("小说加载中，请耐心等候…", string.Empty);
            UseWaitCursor = true;

            var sw = Stopwatch.StartNew();

            // 大文件的解码与章节扫描都在后台线程，避免界面卡死（原程序的主要毛病）
            Book book = await Task.Run(() =>
            {
                TextScanResult scan = TextLoader.Scan(path);
                return new Book(path, scan);
            });

            sw.Stop();

            // 解码用的字节数组与整篇字符串此刻都已不再引用，但它们占的是大对象堆（LOH）。
            // LOH 回收后不会自动压缩，碎片会一直占着工作集——实测 10 MB 的书因此多挂约 20 MB。
            // 这里只在加载完成后回收并压缩一次，不影响阅读时的性能。
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            _book = book;
            _currentLine = ResolveStartLine(book);

            RecalcRowsPerPage();
            RenderPage();

            _settings.LastBookPath = path;
            _settings.Save();

            // 有书之后才应用透明（见 ApplyAppearance 里的说明），配色也随之从不透明档切到透明档
            ApplyAppearance();

            WriteLoadLog(path, book, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"载入失败！\n\n{ex.Message}", "DReader-Next",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus("载入失败！", string.Empty);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    /// <summary>决定从哪一行开始读：优先绝对行号，其次按旧数据的百分比换算。</summary>
    private int ResolveStartLine(Book book)
    {
        ReadingPosition? pos = _progress.Find(book.Path);
        if (pos is null)
            return 0;

        if (pos.Line >= 0 && pos.Line < book.LineCount)
            return pos.Line;

        if (pos.Percent > 0 && book.LineCount > 0)
            return Compat.Clamp((int)(book.LineCount * pos.Percent / 100.0), 0, book.LineCount - 1);

        return 0;
    }

    // ---------------------------------------------------------------- 渲染与翻页

    private void RecalcRowsPerPage()
    {
        if (_book is null)
            return;

        int lineHeight = Math.Max(1, _text.Font.Height);
        int usable = Math.Max(lineHeight, _text.ClientSize.Height - 4);
        _rowsPerPage = Math.Max(1, usable / lineHeight);
        _book.RowsPerPage = _rowsPerPage;
    }

    private void RenderPage()
    {
        if (_book is null)
            return;

        RecalcRowsPerPage();

        if (_book.LineCount == 0)
        {
            _text.Text = string.Empty;
            _currentLine = 0;
            UpdateStatus();
            return;
        }

        (int start, int count) = _book.PageRange(_book.LineToPage(_currentLine));
        _currentLine = start;

        _text.Text = count > 0 ? _book.ReadLines(start, count) : string.Empty;
        _text.SelectionStart = 0;
        _text.SelectionLength = 0;

        UpdateStatus();
    }

    private void NextPage()
    {
        if (_book is null)
            return;

        if (_currentLine + _rowsPerPage >= _book.LineCount)
        {
            SetStatusTemp("已是最后一页");
            return;
        }

        // 翻页说明用户正在看：窗口若被藏起来就先唤出
        ShowWindow();

        _currentLine += _rowsPerPage;
        RenderPage();
        SaveProgress();
    }

    private void PrevPage()
    {
        if (_book is null)
            return;

        if (_currentLine <= 0)
        {
            SetStatusTemp("已是第一页");
            return;
        }

        ShowWindow();

        _currentLine = Math.Max(0, _currentLine - _rowsPerPage);
        RenderPage();
        SaveProgress();
    }

    private void ShowToc()
    {
        if (_book is null)
        {
            SetStatusTemp("请先打开小说！");
            return;
        }

        _modalOpen = true;
        try
        {
            using var toc = new TocForm(_book.Chapters, _book.ChapterAtLine(_currentLine));
            if (toc.ShowDialog(this) == DialogResult.OK && toc.SelectedChapter is { } chapter)
            {
                _currentLine = chapter.StartLine;
                RenderPage();
                SaveProgress();
            }
        }
        finally
        {
            _modalOpen = false;
            _cursorOutsideMs = 0;
        }
    }

    // ---------------------------------------------------------------- 书签

    private void InsertBookmark()
    {
        if (_book is null)
        {
            SetStatusTemp("请先打开小说！");
            return;
        }

        BookmarkStore store = BookmarkStore.Load();

        if (store.Exists(_book.Path, _currentLine))
        {
            SetStatusTemp("此页已经是书签了");
            return;
        }

        // 沿用原程序文案
        if (MessageBox.Show("是否设置此页为书签？", "DReader-Next",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        int ci = _book.ChapterAtLine(_currentLine);
        string preview = _currentLine < _book.LineCount ? _book.ReadLines(_currentLine, 1).Trim() : string.Empty;
        if (preview.Length > 40)
            preview = preview.Substring(0, 40);

        store.Add(new Bookmark
        {
            Path = _book.Path,
            Line = _currentLine,
            Chapter = ci >= 0 ? _book.Chapters[ci].Title : string.Empty,
            Preview = preview,
            CreatedAt = DateTime.Now,
        });
        store.Save();

        SetStatusTemp("已插入书签");
    }

    private async void ShowBookmarks()
    {
        BookmarkStore store = BookmarkStore.Load();
        if (store.Items.Count == 0)
        {
            SetStatusTemp("还没有书签");
            return;
        }

        _modalOpen = true;
        try
        {
            using var dlg = new BookmarkForm(store);
            DialogResult result = dlg.ShowDialog(this);
            store.Save();   // 对话框里可能删过条目

            if (result == DialogResult.OK && dlg.SelectedBookmark is { } bookmark)
                await JumpToBookmarkAsync(bookmark);
        }
        finally
        {
            _modalOpen = false;
            _cursorOutsideMs = 0;
        }
    }

    /// <summary>跳到书签；书签属于别的书时先把那本打开。</summary>
    private async Task JumpToBookmarkAsync(Bookmark bookmark)
    {
        if (!File.Exists(bookmark.Path))
        {
            MessageBox.Show($"这本书已经不在了：\n{bookmark.Path}", "DReader-Next",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!string.Equals(_book?.Path, bookmark.Path, StringComparison.OrdinalIgnoreCase))
            await LoadBookAsync(bookmark.Path);

        if (_book is null)
            return;

        _currentLine = Compat.Clamp(bookmark.Line, 0, Math.Max(0, _book.LineCount - 1));
        RenderPage();
        SaveProgress();
        ShowWindow();
    }

    // ---------------------------------------------------------------- 跳转与设置

    private void JumpToPage()
    {
        if (_book is null)
        {
            SetStatusTemp("请先打开小说！");
            return;
        }

        string? input = InputDialog.Show(this, "跳转页面",
            $"请输入欲跳至页数：（1 – {_book.TotalPages}）",
            (_book.LineToPage(_currentLine) + 1).ToString());

        if (input is null)
            return;

        if (!int.TryParse(input.Trim(), out int page) || page < 1 || page > _book.TotalPages)
        {
            MessageBox.Show("页码无效", "DReader-Next", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _currentLine = (page - 1) * _rowsPerPage;
        RenderPage();
        SaveProgress();
        ShowWindow();
    }

    private void JumpToPercent()
    {
        if (_book is null)
        {
            SetStatusTemp("请先打开小说！");
            return;
        }

        double current = _book.LineCount > 0 ? _currentLine * 100.0 / _book.LineCount : 0;

        string? input = InputDialog.Show(this, "比率跳转",
            "请输入欲跳至进度：（0 – 100，可带小数）", current.ToString("F1"));

        if (input is null)
            return;

        if (!double.TryParse(input.Trim(), out double percent) || percent < 0 || percent > 100)
        {
            MessageBox.Show("进度无效", "DReader-Next", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _currentLine = Compat.Clamp((int)(_book.LineCount * percent / 100.0), 0,
            Math.Max(0, _book.LineCount - 1));
        RenderPage();
        SaveProgress();
        ShowWindow();
    }

    private void OpenSettings()
    {
        _modalOpen = true;
        try
        {
            using var dlg = new SettingsForm(_settings);
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            ApplySettings();     // 字体与配色立即生效
            RegisterHotkeys();   // 热键可能改了，重新注册
        }
        finally
        {
            _modalOpen = false;
            _cursorOutsideMs = 0;
        }
    }

    // ---------------------------------------------------------------- 自动翻页

    private void StartAutoPage()
    {
        if (_book is null)
        {
            // 沿用原程序文案
            MessageBox.Show("请加载小说后再使用自动阅读", "DReader-Next",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? input = InputDialog.Show(this, "自动翻页设置",
            "请输入自动翻页间隔：（单位为秒）", _autoPageSeconds.ToString());

        if (input is null)
            return;

        if (!int.TryParse(input.Trim(), out int seconds) || seconds < 1 || seconds > 3600)
        {
            MessageBox.Show("间隔无效，请输入 1 到 3600 之间的秒数。", "DReader-Next",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _autoPageSeconds = seconds;
        _autoPageTimer.Interval = seconds * 1000;
        _autoPageTimer.Start();

        SetStatusTemp($"自动翻页已开始，每 {seconds} 秒翻一页");
    }

    private void StopAutoPage()
    {
        if (!_autoPageTimer.Enabled)
        {
            SetStatusTemp("当前没有在自动翻页");
            return;
        }

        _autoPageTimer.Stop();
        SetStatusTemp("已停止翻页");
    }

    private void OnAutoPageTick(object? sender, EventArgs e)
    {
        if (_book is null)
        {
            _autoPageTimer.Stop();
            return;
        }

        // 到末页自动停下，别一直弹「已是最后一页」
        if (_currentLine + _rowsPerPage >= _book.LineCount)
        {
            _autoPageTimer.Stop();
            SetStatusTemp("已到最后一页，自动翻页停止");
            return;
        }

        NextPage();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.PageDown:
            case Keys.Right:
            case Keys.Space:
                NextPage();
                e.Handled = true;
                break;
            case Keys.PageUp:
            case Keys.Left:
                PrevPage();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- 鼠标交互

    private void OnTextMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        _pressScreen = _text.PointToScreen(e.Location);
        _pressWindowLocation = Location;
        _dragging = false;
    }

    private void OnTextMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        Point now = _text.PointToScreen(e.Location);
        int dx = now.X - _pressScreen.X;
        int dy = now.Y - _pressScreen.Y;

        if (!_dragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
            _dragging = true;

        if (_dragging)
        {
            Location = new Point(_pressWindowLocation.X + dx, _pressWindowLocation.Y + dy);
            _text.SelectionLength = 0;   // 拖窗口时正文会被顺带选中，这里清掉
        }
    }

    private void OnTextMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        if (_dragging)
        {
            _dragging = false;
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.Save();
            return;
        }

        // 没拖动过 = 单击：左半区下翻页、右半区上翻页（对应原程序「点击左区域 / 单击右区域」）
        if (e.X < _text.ClientSize.Width / 2)
            NextPage();
        else
            PrevPage();
    }

    // ---------------------------------------------------------------- 鼠标穿透开关（低层钩子）

    /// <summary>
    /// 「鼠标穿透」关闭时调用：装/卸 WH_MOUSE_LL 低层鼠标钩子。
    ///
    /// 为什么需要它：窗口用 TransparencyKey 做「背景全透明」，而 Windows 对分层窗口的抠色像素
    /// 不仅视觉透明，**连鼠标一起穿透**——正文控件的底色正是那个抠色，于是按在文字之间的空白处，
    /// 事件直接落到后面的窗口上，只有正好按在文字笔画上才算按到本窗口（实测窗口内 20 个采样点
    /// 只有 5 个命中了正文控件）。直观后果就是「按住拖动」时灵时不灵。
    /// 钩子把窗口矩形内的鼠标事件接管过来（吞掉、不再穿到后面），才能做到「按住任意位置都能拖」。
    /// </summary>
    private void UpdateMouseHook()
    {
        if (!_settings.ClickThrough && _mouseHook == IntPtr.Zero)
        {
            _mouseHookProc = OnLowLevelMouse;
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, _mouseHookProc, NativeMethods.GetModuleHandle(null), 0);

            if (_mouseHook == IntPtr.Zero)
            {
                // 装不上（例如被安全软件/沙箱拦住）：保留用户的开关选择，只把失败写进日志 + 状态栏提示，
                // 绝不悄悄改设置——否则用户看到的勾选状态和实际行为会对不上。
                _mouseHookProc = null;
                LogMouseHook($"安装失败，Win32 错误码 {NativeMethods.LastError()}");
                SetStatusTemp("鼠标接管失败，请查看 data\\mouse-hook.log");
            }
            else
            {
                _hookEventsLogged = 0;
                LogMouseHook($"已安装 hHook={_mouseHook}");
            }
        }
        else if (_settings.ClickThrough && _mouseHook != IntPtr.Zero)
        {
            RemoveMouseHook();
            LogMouseHook("已卸载（切回穿透）");
        }
    }

    /// <summary>鼠标接管状态的诊断日志（与 last-load.log 同类，只用于排查，不参与逻辑）。</summary>
    private static void LogMouseHook(string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Storage.DataDir, "mouse-hook.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志写不进去不影响功能
        }
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero)
            return;

        NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookProc = null;
        _hookPressed = false;
        _hookDragging = false;
    }

    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        try
        {
            NativeMethods.MouseHookData data = NativeMethods.ReadMouseHookData(lParam);

            // 菜单/对话框开着时不能接管（弹出菜单可能压在窗口矩形上，吞掉就点不动菜单项了）；
            // 隐藏中也一律放过去。注入的事件不做区分：程序自己从不注入鼠标事件，统一处理更可预期。
            bool menuOrDialogOpen = _modalOpen
                                    || _contextMenu.Visible
                                    || _tray.ContextMenuStrip?.Visible == true;

            if (_isHidden || !Visible || menuOrDialogOpen)
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            var at = new Point(data.Point.X, data.Point.Y);
            if (!Bounds.Contains(at) && !_hookPressed)
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            // 只记前三条：够证明「钩子确实接到了窗口内的事件」，又不会把日志写爆
            if (_hookEventsLogged < 3)
            {
                _hookEventsLogged++;
                LogMouseHook($"接管事件 {_hookEventsLogged}: msg=0x{(int)wParam:X} @({at.X},{at.Y})");
            }

            switch ((int)wParam)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                    _hookPressed = true;
                    _hookDragging = false;
                    _hookPressScreen = at;
                    _hookPressWindowLocation = Location;
                    return (IntPtr)1;

                case NativeMethods.WM_MOUSEMOVE:
                    if (!_hookPressed)
                        break;

                    int dx = at.X - _hookPressScreen.X;
                    int dy = at.Y - _hookPressScreen.Y;
                    if (!_hookDragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
                        _hookDragging = true;

                    if (_hookDragging)
                        Location = new Point(_hookPressWindowLocation.X + dx, _hookPressWindowLocation.Y + dy);
                    return (IntPtr)1;

                case NativeMethods.WM_LBUTTONUP:
                    if (!_hookPressed)
                        break;

                    _hookPressed = false;
                    if (_hookDragging)
                    {
                        _hookDragging = false;
                        _settings.WindowX = Location.X;
                        _settings.WindowY = Location.Y;
                        _settings.Save();
                    }
                    else if (at.X - Bounds.Left < Bounds.Width / 2)
                    {
                        NextPage();   // 单击左半区：下翻页
                    }
                    else
                    {
                        PrevPage();   // 单击右半区：上翻页
                    }
                    return (IntPtr)1;

                case NativeMethods.WM_RBUTTONDOWN:
                    return (IntPtr)1;   // 与 UP 配对吞掉，否则后面的窗口会收到没有配对的按下

                case NativeMethods.WM_RBUTTONUP:
                    _contextMenu.Show(at);
                    return (IntPtr)1;

                case NativeMethods.WM_MOUSEWHEEL:
                    if (NativeMethods.WheelDelta(data) > 0)
                        PrevPage();
                    else
                        NextPage();
                    return (IntPtr)1;
            }
        }
        catch (Exception)
        {
            // 钩子回调里抛异常会直接把进程带走：吞掉，交给系统默认处理
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void OnTextMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta > 0)
            PrevPage();
        else
            NextPage();
    }

    // ---------------------------------------------------------------- 进度与状态

    private void SaveProgress()
    {
        if (_book is null)
            return;

        _progress.Update(_book.Path, _currentLine, _book.LineCount);
        _progress.Save();
    }

    private void UpdateStatus()
    {
        if (_book is null)
        {
            SetStatus("请自行打开小说！", string.Empty);
            return;
        }

        int page = _book.LineToPage(_currentLine);
        double percent = _book.LineCount > 0 ? _currentLine * 100.0 / _book.LineCount : 0;
        int ci = _book.ChapterAtLine(_currentLine);
        string chapter = ci >= 0 ? _book.Chapters[ci].Title : "（未识别到章节）";

        // 编码名不进状态栏：它只在排查时有用，却会挤掉左侧的书名与章节
        SetStatus($"{_book.Title} | {chapter}",
            $"第 {page + 1}/{_book.TotalPages} 页 {percent:F1}%");
    }

    private void SetStatus(string left, string right)
    {
        _statusLeft.Text = left;
        _statusRight.Text = right;

        // 还没打开书时把提示也写进正文区：状态栏那一行在无边框窗口里太不起眼，
        // 用户打开后会以为程序根本没启动。
        if (_book is null && left.Length > 0)
            _text.Text = $"{left}\r\n\r\n右键菜单 → 打开文件，或直接把 txt 文件拖进来。";
    }

    private async void SetStatusTemp(string message)
    {
        _statusLeft.Text = message;
        await Task.Delay(1500);
        UpdateStatus();
    }

    // ---------------------------------------------------------------- 拖放

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await LoadBookAsync(files[0]);
    }

    // ---------------------------------------------------------------- 旧数据导入

    /// <summary>把本次加载的关键数据落盘，便于验证与排查（不参与阅读逻辑）。</summary>
    private void WriteLoadLog(string path, Book book, long elapsedMs)
    {
        try
        {
            var lines = new List<string>
            {
                $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"文件：{path}",
                $"编码：{book.EncodingName}",
                $"大小：{new FileInfo(path).Length:N0} 字节",
                $"行数：{book.LineCount:N0}",
                $"章节：{book.Chapters.Count}",
                $"耗时：{elapsedMs} ms",
                $"客户端尺寸：{ClientSize.Width}×{ClientSize.Height}",
                $"每页行数：{_rowsPerPage}",
                $"正文渲染字符：{_text.TextLength:N0}",
                $"状态栏：{_statusBar.Width}×{_statusBar.Height}  Visible={_statusBar.Visible}",
                $"状态栏左：{_statusLeft.Text}",
                $"状态栏右：{_statusRight.Text}",
                string.Empty,
                "前 40 个章节：",
            };

            lines.AddRange(book.Chapters.Take(40).Select(c => $"  [{c.Index}] 行 {c.StartLine,-8} {c.Title}"));

            File.WriteAllLines(Path.Combine(Storage.DataDir, "last-load.log"), lines);
        }
        catch (Exception)
        {
            // 日志写不进去不能影响阅读
        }
    }
}
