namespace DReaderNext.Data;

/// <summary>
/// 应用配置。字段对应原程序 Ebook.ini 的各个 section，
/// 但颜色改用 #RRGGBB（原程序存 BGR 整数，导入时换算），便于阅读和手改。
/// </summary>
public sealed class AppSettings
{
    // ---- [字体] ----
    public float FontSize { get; set; } = 12f;
    public string FontName { get; set; } = "宋体";
    public string FontColor { get; set; } = "#ECECEC";
    public string BackColor { get; set; } = "#E8ECEC";

    // ---- [窗口] ----
    public int WindowWidth { get; set; } = 330;
    public int WindowHeight { get; set; } = 488;

    /// <summary>窗口左上角坐标；-1 表示尚未记录，由窗口自行居中。</summary>
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;

    /// <summary>窗口置顶（对应原程序的「窗口置顶」）。</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// 抠掉底色做「背景全透明」，即原程序的「设置透明 / 取消透明」。
    /// 原程序那个「自定义底色」就是被抠掉的键色——这也是它字色敢与底色只差 4 的原因。
    /// </summary>
    public bool TransparentBackground { get; set; } = true;

    /// <summary>整体不透明度 0.2–1.0，即原程序的「可调透明度」。</summary>
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>贴屏幕边缘后自动缩进隐藏，鼠标碰到再弹出。</summary>
    public bool SnapToEdge { get; set; } = true;

    /// <summary>
    /// 鼠标离开窗口一段时间后把窗口整个藏起来（摸鱼用，靠老板键或托盘呼出）。
    /// 注意这不是原程序那个「鼠标隐藏」——那个是隐藏鼠标指针，语义不同。
    /// </summary>
    public bool AutoHideOnMouseLeave { get; set; } = true;

    /// <summary>鼠标离开多久后隐藏窗口，毫秒。</summary>
    public int AutoHideDelayMs { get; set; } = 2000;

    /// <summary>鼠标在窗口内静止一段时间后隐藏指针（原程序的「鼠标隐藏」）。</summary>
    public bool HideCursorWhenIdle { get; set; } = true;

    /// <summary>鼠标静止多久后隐藏指针，毫秒。</summary>
    public int CursorIdleDelayMs { get; set; } = 3000;

    /// <summary>把正文文字调暗，更不显眼（原程序的「字体变暗」）。</summary>
    public bool DimFont { get; set; }

    // ---- [热键] ----
    // 不从旧配置迁移：Ebook.ini 里「功能键=4」的确切常量含义已无从考证（交接文档坑 1），
    // 盲目沿用会把键绑错。默认给一组组合键，用户在设置面板里可改。
    public string KeyPrev { get; set; } = "Alt+1";
    public string KeyNext { get; set; } = "Alt+2";
    public string KeyBoss { get; set; } = "Alt+3";

    // ---- [其他] ----
    /// <summary>
    /// 是否使用 Windows 自带的标题栏。默认关——原程序是无边框悬浮窗，它 ini 里的「标题栏=真」
    /// 指的是自己绘制的一条信息栏，不是系统标题栏；开了会多出一个原程序没有的头部。
    /// </summary>
    public bool ShowTitleBar { get; set; } = false;

    /// <summary>
    /// 鼠标穿透。勾选时，窗口的抠色（透明）像素会把鼠标让给后面的窗口——这是分层窗口的固有行为，
    /// 这时只有按在文字笔画上才收得到事件，「按住拖动」基本失效。
    /// 默认关闭：用低层鼠标钩子把窗口矩形内的鼠标接管过来（按住任意位置都能拖、点半区翻页、
    /// 滚轮翻页、右键出菜单），代价是窗口矩形内点不到后面的窗口，想点后面先把窗口移开。
    /// </summary>
    public bool ClickThrough { get; set; } = false;
    public bool EdgeHide { get; set; } = false;
    public bool HideTrayIcon { get; set; } = false;
    public bool AutoOpenLast { get; set; } = true;

    /// <summary>上次打开的书（AutoOpenLast 用）。</summary>
    public string? LastBookPath { get; set; }

    public static AppSettings Load() => Storage.LoadJson<AppSettings>("settings.json") ?? new AppSettings();

    public void Save() => Storage.SaveJson("settings.json", this);
}
