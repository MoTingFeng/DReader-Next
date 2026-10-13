using System.Text.RegularExpressions;

namespace DReaderNext.Core;

/// <summary>章节归类（对应原程序的 目录 / 版权 / 扉页 / 前言 / 简介）。</summary>
public enum ChapterKind
{
    Text,
    Cover,
    Copyright,
    Preface,
    Intro,
    Extra,
}

/// <summary>一个章节：标题所在行在全文中的行号（0 基）。</summary>
public sealed record Chapter(int Index, int StartLine, string Title, ChapterKind Kind)
{
    /// <summary>本章行数（不含下一章标题行），由 Book 建表时回填。</summary>
    public int LineCount { get; set; }
}

/// <summary>
/// 章节识别。正则基线取自交接文档 §5.4，并按真实书籍校准。
///
/// 只提供「逐行判定」入口：由 TextLoader 在流式扫描时对每一行调用。
/// 这样识别章节就不需要把整本书的行都留在内存里。
/// </summary>
public static class ChapterParser
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>疑似标题行的长度上限：超过这个长度的行不可能是章节标题。</summary>
    private const int MaxTitleLength = 60;

    // 主力规则：第X章 / 第X节 / 第X回 / 第X卷 / 第X篇 / 第X集（含中文数字）
    private static readonly Regex RChinese =
        new(@"^第\s*[0-9零一二三四五六七八九十百千两]+\s*[章节回卷篇集]\s*.{0,50}$", Opts);

    // 英文章节
    private static readonly Regex REnglish =
        new(@"^(?:Chapter|CHAPTER|chapter)\s*\d+.{0,50}$", Opts);

    // 特殊篇名
    private static readonly Regex RSpecial =
        new(@"^(?:序章|序言|楔子|前言|后记|尾声|终章|番外|外传|文案|简介|内容简介).{0,30}$", Opts);

    // 弱规则：纯数字序号（"1、" "2." "3．"）。正文里的列表项会中招，
    // 所以只在主力规则几乎没命中时才启用。
    private static readonly Regex RNumber =
        new(@"^[0-9]{1,4}\s*[、.．]\s*\S.{0,40}$", Opts);

    /// <summary>
    /// 判定一行是不是章节标题。
    /// isStrong 表示命中的是主力规则；为 false 说明只命中了「数字序号」这条弱规则，
    /// 调用方据此决定要不要采用（主力规则命中太少时才退而用弱规则的结果）。
    /// </summary>
    public static bool Match(string line, out bool isStrong, out ChapterKind kind)
    {
        isStrong = false;
        kind = ChapterKind.Text;

        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxTitleLength)
            return false;

        if (RChinese.IsMatch(trimmed) || REnglish.IsMatch(trimmed) || RSpecial.IsMatch(trimmed))
        {
            isStrong = true;
            kind = Classify(trimmed);
            return true;
        }

        if (RNumber.IsMatch(trimmed))
        {
            kind = Classify(trimmed);
            return true;
        }

        return false;
    }

    private static ChapterKind Classify(string title)
    {
        if (title.Contains("版权")) return ChapterKind.Copyright;
        if (title.Contains("扉页")) return ChapterKind.Cover;
        if (title.Contains("前言") || title.Contains("序言") || title.Contains("序章")
            || title.Contains("楔子")) return ChapterKind.Preface;
        if (title.Contains("简介") || title.Contains("文案")) return ChapterKind.Intro;
        if (title.Contains("番外") || title.Contains("外传")) return ChapterKind.Extra;
        return ChapterKind.Text;
    }
}
