using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DReaderNext.Core;

/// <summary>
/// EPUB → 纯文本缓存。
///
/// 为什么不直接走 TextLoader：现有阅读链路是「按行起始字节偏移 seek 原文件」，
/// 而 EPUB 是 zip 容器，正文分散在若干 XHTML 里，没法按字节偏移 seek。
/// 所以这里的做法是：首次打开时把 spine 顺序的正文抽成一份纯文本缓存，
/// 之后 TextLoader.Scan / Book.ReadLines 照旧工作——阅读核心一行都不用改。
///
/// 章节表不走 ChapterParser 的正则识别，而是用 EPUB 自带的目录（EPUB2 的 toc.ncx /
/// EPUB3 的 nav.xhtml），因为这些书的章名常常是「＃私たちは…」「廈門、一九四一年…」这种
/// 正则认不出来的形态；三级兜底依次是 NCX/nav → 正文 h1/h2 → &lt;title&gt;。
///
/// 纯图片页保留为「［插图］」占位（用户选定的行为）；既无文字也无图片的封面/目录页直接跳过。
/// </summary>
public static class EpubConverter
{
    /// <summary>图片页在正文里留下的占位文字。</summary>
    public const string ImagePlaceholder = "［插图］";

    /// <summary>正文短于这个字数、但含图片的 spine 项视为插图页。</summary>
    private const int ImagePageCharLimit = 20;

    private const string CacheDirName = "epub-cache";
    private const string MetaVersion = "v1";

    // ---------------------------------------------------------------- 正则

    /// <summary>XML 声明里的编码（单双引号都可能出现，日文轻小说实测见过单引号）。</summary>
    private static readonly Regex ReXmlEncoding =
        new(@"encoding\s*=\s*[""']([\w\-.]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReComment =
        new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ReHead =
        new(@"<head\b[^>]*>.*?</head>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReScript =
        new(@"<(script|style|svg|math)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>ruby 注音：<rt>/<rp> 是多出来的读音，留着会把标题和正文搅成「廈 モイ 門」。</summary>
    private static readonly Regex ReRubyAnnotation =
        new(@"<(rt|rp)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>块级标签在纯文本里用一个换行表示。</summary>
    private static readonly Regex ReBlockTags = new(
        @"</?(?:p|div|br|hr|h[1-6]|li|ul|ol|dl|dt|dd|tr|td|th|table|thead|tbody|tfoot|section|article|aside|blockquote|figure|figcaption|header|footer|nav|pre)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReAnyTag = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>正文里是否嵌了图片（img/image 两种写法）。</summary>
    private static readonly Regex ReImage =
        new(@"<(?:img|image)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReTitleElement =
        new(@"<title\b[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReDoctype =
        new(@"<!DOCTYPE[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 形如 c0 / c3D / c1F2 的 &lt;title&gt;：这是排版工具的内部文件名，不是章节名。
    /// 日文轻小说里实测整本书的 title 都是这种，直接当标题会得到「c9」「c13」这种东西。
    /// </summary>
    private static readonly Regex ReInternalTitle =
        new("^[A-Za-z][A-Za-z0-9]{0,7}$", RegexOptions.Compiled);

    // ---------------------------------------------------------------- 对外入口

    /// <summary>一次打开 EPUB 的结果：缓存 txt 的路径、书名、可交给 Book 的扫描结果。</summary>
    public sealed class OpenResult
    {
        /// <summary>转出的纯文本缓存路径（Book 的读取源）。</summary>
        public required string CachePath { get; init; }

        /// <summary>书名：优先 OPF 的 dc:title，取不到才用文件名。</summary>
        public required string Title { get; init; }

        /// <summary>缓存 txt 的扫描结果（章节表已换成 EPUB 自带目录）。</summary>
        public required TextScanResult Scan { get; init; }

        /// <summary>本次是否直接命中已有缓存（排查与性能观察用）。</summary>
        public bool FromCache { get; init; }
    }

    public static bool IsEpub(string path) =>
        string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 打开 EPUB：缓存命中就直接用，否则转换一次再写缓存。
    /// 缓存失效条件只看「源文件大小 + 修改时间」——书被替换/重下都会重转。
    /// </summary>
    public static OpenResult Open(string epubPath, string dataDir)
    {
        string fullPath = Path.GetFullPath(epubPath);
        var source = new FileInfo(fullPath);
        if (!source.Exists)
            throw new FileNotFoundException("找不到文件：" + epubPath, fullPath);

        string cacheDir = Path.Combine(dataDir, CacheDirName);
        Directory.CreateDirectory(cacheDir);

        string key = CacheKey(fullPath);
        string txtPath = Path.Combine(cacheDir, key + ".txt");
        string metaPath = Path.Combine(cacheDir, key + ".meta");

        if (File.Exists(txtPath) && TryReadMeta(metaPath, source, out string cachedTitle, out List<Chapter>? cachedChapters))
        {
            TextScanResult cached = TextLoader.Scan(txtPath);
            return new OpenResult
            {
                CachePath = txtPath,
                Title = cachedTitle,
                Scan = WithChapters(cached, cachedChapters!),
                FromCache = true,
            };
        }

        (string title, List<Chapter> chapters) = ConvertToText(fullPath, txtPath);

        try
        {
            WriteMeta(metaPath, source, title, chapters);
        }
        catch (Exception)
        {
            // 缓存索引写不进去最多下次重转，不能因此打不开书
        }

        TextScanResult scan = TextLoader.Scan(txtPath);
        return new OpenResult
        {
            CachePath = txtPath,
            Title = title,
            Scan = WithChapters(scan, chapters),
            FromCache = false,
        };
    }

    /// <summary>把 TextLoader 的结果换上 EPUB 的章节表与编码名。</summary>
    private static TextScanResult WithChapters(TextScanResult scan, List<Chapter> chapters) =>
        new()
        {
            LineOffsets = scan.LineOffsets,
            Encoding = scan.Encoding,
            EncodingName = "EPUB",
            Chapters = chapters,
        };

    // ---------------------------------------------------------------- 缓存

    /// <summary>FNV-1a 64：只为得到稳定的缓存文件名，不做安全用途。</summary>
    private static string CacheKey(string path)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char ch in path.ToLowerInvariant())
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16");
    }

    private static bool TryReadMeta(string metaPath, FileInfo source, out string title, out List<Chapter>? chapters)
    {
        title = string.Empty;
        chapters = null;
        if (!File.Exists(metaPath))
            return false;

        try
        {
            string[] lines = File.ReadAllLines(metaPath, Encoding.UTF8);
            if (lines.Length < 3 || lines[0] != MetaVersion)
                return false;
            if (lines[1] != "size=" + source.Length || lines[2] != "mtime=" + source.LastWriteTimeUtc.Ticks)
                return false;

            string? bookTitle = null;
            var list = new List<Chapter>();
            for (int i = 3; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("title=", StringComparison.Ordinal))
                {
                    bookTitle = line.Substring("title=".Length);
                }
                else if (line.StartsWith("chapter=", StringComparison.Ordinal))
                {
                    string payload = line.Substring("chapter=".Length);
                    int tab = payload.IndexOf('\t');
                    if (tab <= 0 || !int.TryParse(payload.Substring(0, tab), out int startLine))
                        continue;
                    string chapterTitle = payload.Substring(tab + 1);
                    list.Add(BuildChapter(list.Count, startLine, chapterTitle));
                }
            }

            if (bookTitle is null || list.Count == 0)
                return false;

            title = bookTitle;
            chapters = list;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteMeta(string metaPath, FileInfo source, string title, List<Chapter> chapters)
    {
        var lines = new List<string>
        {
            MetaVersion,
            "size=" + source.Length,
            "mtime=" + source.LastWriteTimeUtc.Ticks,
            "title=" + OneLine(title),
        };
        foreach (Chapter chapter in chapters)
            lines.Add("chapter=" + chapter.StartLine + "\t" + OneLine(chapter.Title));

        File.WriteAllLines(metaPath, lines, new UTF8Encoding(false));
    }

    private static string OneLine(string text)
    {
        string replaced = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return replaced.Trim();
    }

    // ---------------------------------------------------------------- 转换主流程

    /// <summary>解 EPUB、抽正文、写缓存 txt，返回书名与章节表。</summary>
    private static (string Title, List<Chapter> Chapters) ConvertToText(string epubPath, string txtPath)
    {
        using var stream = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive zip = OpenArchive(stream);

        string opfPath = FindOpfPath(zip);
        XmlDocument opf = LoadXml(zip, opfPath);
        string baseDir = PosixDirectoryName(opfPath);

        var manifest = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (XmlElement item in Elements(opf, "item"))
        {
            string id = Attr(item, "id");
            if (id.Length == 0)
                continue;
            manifest[id] = new ManifestItem(Attr(item, "href"), Attr(item, "media-type"), Attr(item, "properties"));
        }

        string bookTitle = ReadMetadata(opf, "title");
        if (bookTitle.Length == 0)
            bookTitle = Path.GetFileNameWithoutExtension(epubPath);

        // 目录：NCX（EPUB2）优先，取不到再看 EPUB3 的 nav
        var toc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XmlElement? spine = opf.SelectSingleNode("//*[local-name()='spine']") as XmlElement;

        string? ncxPath = null;
        string? ncxId = spine == null ? null : Attr(spine, "toc");
        if (!string.IsNullOrEmpty(ncxId) && manifest.TryGetValue(ncxId!, out ManifestItem? ncxItem))
            ncxPath = ResolveEntryName(baseDir, ncxItem.Href);
        if (ncxPath == null)
        {
            foreach (KeyValuePair<string, ManifestItem> pair in manifest)
            {
                if (string.Equals(pair.Value.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase))
                {
                    ncxPath = ResolveEntryName(baseDir, pair.Value.Href);
                    break;
                }
            }
        }
        if (ncxPath != null)
            ReadNcx(zip, ncxPath, toc);

        if (toc.Count == 0)
        {
            foreach (KeyValuePair<string, ManifestItem> pair in manifest)
            {
                ManifestItem item = pair.Value;
                if (!IsHtml(item.MediaType))
                    continue;
                bool isNav = item.Properties.IndexOf("nav", StringComparison.OrdinalIgnoreCase) >= 0
                             || Path.GetFileName(item.Href).StartsWith("nav", StringComparison.OrdinalIgnoreCase);
                if (!isNav)
                    continue;
                ReadNav(zip, ResolveEntryName(baseDir, item.Href), toc);
                break;
            }
        }

        // spine 顺序 = 正文顺序
        var lines = new List<string>();
        var chapters = new List<Chapter>();
        int section = 0;
        int realTextLines = 0;

        if (spine != null)
        {
            foreach (XmlElement itemref in Elements(spine, "itemref"))
            {
                string idref = Attr(itemref, "idref");
                if (idref.Length == 0 || !manifest.TryGetValue(idref, out ManifestItem? item))
                    continue;
                if (!IsHtml(item.MediaType))
                    continue;

                string entryName = ResolveEntryName(baseDir, item.Href);
                ZipArchiveEntry? entry = FindEntry(zip, entryName);
                if (entry == null)
                    continue;

                string doc = ReadEntryText(zip, entry.FullName);
                if (doc.Length == 0)
                    continue;

                string body = ExtractBody(doc);
                bool hasImage = ReImage.IsMatch(body);
                List<string> bodyLines = SplitLines(HtmlToText(body));
                int textChars = 0;
                foreach (string line in bodyLines)
                    textChars += line.Length;

                // 既没文字也没图：封面/版权/空页，不进目录
                if (textChars == 0 && !hasImage)
                    continue;

                // 纯图页：占位，让翻页时不至于整页空白
                bool imagePage = hasImage && textChars < ImagePageCharLimit;
                if (imagePage)
                {
                    bodyLines.Clear();
                    bodyLines.Add(ImagePlaceholder);
                }
                else
                {
                    realTextLines += bodyLines.Count;
                }

                string? title = ResolveTitle(toc, entryName, body, doc, bookTitle);
                if (title == null)
                    title = imagePage ? ImagePlaceholder : "第 " + (section + 1) + " 节";

                chapters.Add(BuildChapter(chapters.Count, lines.Count, title));
                lines.Add(title);
                lines.AddRange(bodyLines);
                section++;
            }
        }

        if (lines.Count == 0)
            throw new InvalidDataException("EPUB 里没有找到可阅读的正文（spine 为空或全部是空白页）。");

        // 只有图片、没有一个字的 epub 不该当成一本书打开——那会是个只有「［插图］」的空书架
        if (realTextLines == 0)
            throw new InvalidDataException("EPUB 里没有找到可阅读的正文（全部是图片页）。");

        using (var writer = new StreamWriter(txtPath, append: false, new UTF8Encoding(false)))
        {
            foreach (string line in lines)
                writer.WriteLine(line);
        }

        return (bookTitle, chapters);
    }

    // ---------------------------------------------------------------- EPUB 结构

    private sealed class ManifestItem
    {
        public ManifestItem(string href, string mediaType, string properties)
        {
            Href = href;
            MediaType = mediaType;
            Properties = properties;
        }

        public string Href { get; }
        public string MediaType { get; }
        public string Properties { get; }
    }

    private static bool IsHtml(string mediaType) =>
        mediaType.Length == 0
        || mediaType.IndexOf("xhtml", StringComparison.OrdinalIgnoreCase) >= 0
        || mediaType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>打开 zip。坏文件/冒牌 .epub 在这里换成一句能看懂的话。</summary>
    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("不是有效的 EPUB（文件损坏，或者它根本不是 zip 压缩包）。", ex);
        }
    }

    private static string FindOpfPath(ZipArchive zip)
    {
        if (FindEntry(zip, "META-INF/container.xml") is null)
            throw new InvalidDataException("不是有效的 EPUB：缺少 META-INF/container.xml。");

        XmlDocument container = LoadXml(zip, "META-INF/container.xml");
        XmlElement? rootfile = container.SelectSingleNode("//*[local-name()='rootfile']") as XmlElement;
        string opfPath = rootfile == null ? string.Empty : Attr(rootfile, "full-path");
        if (opfPath.Length == 0)
            throw new InvalidDataException("不是有效的 EPUB：container.xml 里没有 rootfile。");
        return opfPath.Replace('\\', '/');
    }

    /// <summary>
    /// 读 XML 文件。先去 XML 声明再 LoadXml——声明里的 encoding 可能与已经解码好的字符串不符，
    /// 留着会被 XmlDocument 当成「实际编码与声明不符」报错。
    /// </summary>
    private static XmlDocument LoadXml(ZipArchive zip, string entryName)
    {
        string text = ReadEntryText(zip, entryName);
        text = Regex.Replace(text, @"^\s*<\?xml[^>]*\?>", string.Empty);
        text = ReDoctype.Replace(text, string.Empty);

        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            doc.LoadXml(text);
        }
        catch (XmlException ex)
        {
            // 裸抛 XmlException 会把「以下元素未封闭」这种英文细节弹给用户，换成能看懂的话
            throw new InvalidDataException("不是有效的 EPUB：" + entryName + " 解析失败（文件可能损坏）。", ex);
        }
        return doc;
    }

    private static string ReadEntryText(ZipArchive zip, string entryName)
    {
        ZipArchiveEntry? entry = FindEntry(zip, entryName);
        if (entry == null)
            throw new FileNotFoundException("EPUB 内部缺少文件：" + entryName, entryName);

        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return DecodeText(buffer.ToArray());
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 无 BOM 的 UTF-16：XML 开头是 "<?"，在 UTF-16 里表现为 3C 00 / 00 3C。
        // 不能只靠严格 UTF-8 校验——它会把 3C 00 3F 00 当合法 UTF-8 静默解成乱码。
        if (bytes.Length >= 2 && bytes[0] == 0x3C && bytes[1] == 0x00)
            return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0x00 && bytes[1] == 0x3C)
            return Encoding.BigEndianUnicode.GetString(bytes);

        string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(300, bytes.Length));
        Match declared = ReXmlEncoding.Match(head);
        if (declared.Success)
        {
            string name = declared.Groups[1].Value.ToLowerInvariant();
            if (name.Contains("utf-16"))
                return Encoding.Unicode.GetString(bytes);
            if (name.Contains("1252") || name.Contains("8859"))
                return Encoding.GetEncoding(1252).GetString(bytes);
            if (name.Contains("gb"))
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            if (name.Contains("932") || name.Contains("sjis"))
                return Encoding.GetEncoding(932).GetString(bytes);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }

        try
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        catch (ArgumentException)
        {
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string entryName)
    {
        ZipArchiveEntry? exact = zip.GetEntry(entryName);
        if (exact != null)
            return exact;

        // href 与 entry 名大小写不一致的书见过，退化成忽略大小写再找一次
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    /// <summary>把 OPF 里的相对 href 归一化成 zip entry 名（处理 ./ ../ 与 #锚点）。</summary>
    private static string ResolveEntryName(string baseDir, string href)
    {
        string clean = StripFragment(href);
        if (clean.Length == 0)
            return baseDir;

        clean = Uri.UnescapeDataString(clean);
        string combined = baseDir.Length == 0 ? clean : baseDir + "/" + clean;

        var parts = new List<string>();
        foreach (string part in combined.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join("/", parts);
    }

    private static string StripFragment(string href)
    {
        int hash = href.IndexOf('#');
        return hash < 0 ? href : href.Substring(0, hash);
    }

    private static string PosixDirectoryName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? string.Empty : path.Substring(0, slash);
    }

    private static IEnumerable<XmlElement> Elements(XmlNode node, string localName)
    {
        XmlNodeList list = node.SelectNodes(".//*[local-name()='" + localName + "']");
        if (list == null)
            yield break;
        foreach (XmlNode child in list)
        {
            if (child is XmlElement element)
                yield return element;
        }
    }

    private static string Attr(XmlElement element, string name) =>
        element.HasAttribute(name) ? element.GetAttribute(name) : string.Empty;

    private static string ReadMetadata(XmlDocument opf, string localName)
    {
        XmlNodeList list = opf.SelectNodes(".//*[local-name()='metadata']/*[local-name()='" + localName + "']");
        if (list == null)
            return string.Empty;
        foreach (XmlNode node in list)
        {
            string text = NormalizeSpace(WebUtility.HtmlDecode(node.InnerText ?? string.Empty));
            if (text.Length > 0)
                return text;
        }
        return string.Empty;
    }

    private static void ReadNcx(ZipArchive zip, string ncxPath, Dictionary<string, string> toc)
    {
        XmlDocument ncx;
        try
        {
            ncx = LoadXml(zip, ncxPath);
        }
        catch (Exception)
        {
            return;
        }

        XmlElement? navMap = ncx.SelectSingleNode("//*[local-name()='navMap']") as XmlElement;
        if (navMap == null)
            return;

        string ncxDir = PosixDirectoryName(ncxPath);
        foreach (XmlElement navPoint in navMap.ChildNodes.OfType<XmlElement>())
        {
            if (IsLocal(navPoint, "navPoint"))
                CollectNavPoint(navPoint, ncxDir, toc);
        }
    }

    /// <summary>递归收集目录项：父节点（卷/部）不产标题，只取叶子。</summary>
    private static void CollectNavPoint(XmlElement navPoint, string ncxDir, Dictionary<string, string> toc)
    {
        var children = new List<XmlElement>();
        foreach (XmlNode child in navPoint.SelectNodes(".//*[local-name()='navPoint']"))
        {
            if (child is XmlElement element)
                children.Add(element);
        }

        if (children.Count > 0)
        {
            foreach (XmlElement child in children)
                CollectNavPoint(child, ncxDir, toc);
            return;
        }

        string src = string.Empty;
        XmlElement? content = navPoint.SelectSingleNode("./*[local-name()='content']") as XmlElement;
        if (content != null)
            src = Attr(content, "src");
        if (src.Length == 0)
            return;

        XmlElement? label = navPoint.SelectSingleNode("./*[local-name()='navLabel']/*[local-name()='text']") as XmlElement;
        string title = label == null ? string.Empty : NormalizeSpace(label.InnerText ?? string.Empty);
        if (title.Length == 0)
            return;

        string entryName = ResolveEntryName(ncxDir, src);
        if (!toc.ContainsKey(entryName))
            toc[entryName] = title;
    }

    private static void ReadNav(ZipArchive zip, string navPath, Dictionary<string, string> toc)
    {
        XmlDocument nav;
        try
        {
            nav = LoadXml(zip, navPath);
        }
        catch (Exception)
        {
            return;
        }

        string navDir = PosixDirectoryName(navPath);
        XmlNodeList links = nav.SelectNodes("//*[local-name()='nav']//*[local-name()='a']");
        if (links == null)
            return;

        foreach (XmlNode node in links)
        {
            if (node is not XmlElement link)
                continue;
            string href = Attr(link, "href");
            if (href.Length == 0)
                continue;
            string title = NormalizeSpace(link.InnerText ?? string.Empty);
            if (title.Length == 0)
                continue;

            string entryName = ResolveEntryName(navDir, href);
            if (!toc.ContainsKey(entryName))
                toc[entryName] = title;
        }
    }

    private static bool IsLocal(XmlNode node, string localName) =>
        string.Equals(node.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 章节标题

    private static string? ResolveTitle(
        Dictionary<string, string> toc, string entryName, string body, string doc, string bookTitle)
    {
        if (toc.TryGetValue(entryName, out string? fromToc) && fromToc.Length > 0)
            return fromToc;

        foreach (string tag in new[] { "h1", "h2", "h3" })
        {
            var match = Regex.Match(body,
                "<" + tag + @"\b[^>]*>(.*?)</" + tag + ">",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;
            string text = CleanInline(match.Groups[1].Value);
            if (text.Length > 0)
                return text;
        }

        Match titleElement = ReTitleElement.Match(doc);
        if (titleElement.Success)
        {
            string text = CleanInline(titleElement.Groups[1].Value);
            // <title> 常常是书名或内部文件名（c3D 这种），这两种都不能当章名
            if (text.Length > 0
                && !string.Equals(text, bookTitle, StringComparison.OrdinalIgnoreCase)
                && !ReInternalTitle.IsMatch(text))
                return text;
        }

        // 最后兜底：正文首行。前置页（版权/扉页/「集英社e文库」这类）只有它能当标题；
        // 首行就是长句时说明这是正文段落而不是标题，宁可不猜。
        return FirstBodyLine(body);
    }

    private static string? FirstBodyLine(string body)
    {
        foreach (string line in SplitLines(HtmlToText(body)))
        {
            if (line.Length > 40)
                return null;
            // 标题里不能带段首缩进：全角空格在这里当空白折叠掉
            string cleaned = NormalizeSpace(line);
            if (cleaned.Length >= 2)
                return cleaned;
        }
        return null;
    }

    private static Chapter BuildChapter(int index, int startLine, string title) =>
        new(index, startLine, title, ClassifyKind(title));

    private static ChapterKind ClassifyKind(string title)
    {
        if (title.Contains("版权")) return ChapterKind.Copyright;
        if (title.Contains("封面") || title.Contains("扉页")) return ChapterKind.Cover;
        if (title.Contains("前言") || title.Contains("序言") || title.Contains("序章")
            || title.Contains("楔子")) return ChapterKind.Preface;
        if (title.Contains("简介") || title.Contains("文案")) return ChapterKind.Intro;
        if (title.Contains("番外") || title.Contains("外传")) return ChapterKind.Extra;
        return ChapterKind.Text;
    }

    // ---------------------------------------------------------------- HTML → 文本

    private static string ExtractBody(string doc)
    {
        Match match = Regex.Match(doc, @"<body\b[^>]*>(.*?)</body>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : doc;
    }

    private static string HtmlToText(string body)
    {
        string text = ReComment.Replace(body, string.Empty);
        text = ReHead.Replace(text, "\n");
        text = ReScript.Replace(text, "\n");
        text = ReRubyAnnotation.Replace(text, string.Empty);
        text = ReBlockTags.Replace(text, "\n");
        text = ReAnyTag.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00A0', ' ').Replace("\u200B", string.Empty);
        return text;
    }

    /// <summary>行内文字清理（章节标题用）：去注音、去标签、还原实体、压空白。</summary>
    private static string CleanInline(string html)
    {
        string text = ReComment.Replace(html, string.Empty);
        text = ReRubyAnnotation.Replace(text, string.Empty);
        text = ReAnyTag.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00A0', ' ').Replace("\u200B", string.Empty);
        return NormalizeSpace(text);
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = TrimAscii(raw);
            if (line.Length > 0)
                lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// 只裁 ASCII 空白。不能直接用 string.Trim()：U+3000 全角空格在 .NET 里算空白，
    /// 会把中文段首缩进整段吃掉。
    /// </summary>
    private static string TrimAscii(string text)
    {
        int start = 0;
        int end = text.Length;
        while (start < end && IsAsciiSpace(text[start]))
            start++;
        while (end > start && IsAsciiSpace(text[end - 1]))
            end--;
        return text.Substring(start, end - start);
    }

    private static bool IsAsciiSpace(char ch) =>
        ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\f' || ch == '\v';

    private static string NormalizeSpace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool lastSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace && builder.Length > 0)
                    builder.Append(' ');
                lastSpace = true;
                continue;
            }
            builder.Append(ch);
            lastSpace = false;
        }
        return builder.ToString().TrimEnd();
    }
}
