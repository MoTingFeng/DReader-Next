using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DReaderNext.Data;

/// <summary>
/// 极简 JSON 读写，只覆盖本工程用到的形状：
///   - 对象（公开「可读可写」属性，只读的计算属性自动跳过）
///   - List&lt;T&gt;、string、bool、整数、float/double/decimal、DateTime/DateTime?、null
///
/// 为什么要自己写而不是用现成的：
///   - System.Text.Json 在 net48 上是 NuGet 包，会额外拖 9 个 DLL（约 1.16 MB），
///     而本机 dotnet 访问不到 nuget.org（SChannel TLS 失败），无法还原；
///   - .NET Framework 自带的 DataContractJsonSerializer 读不了 ISO 8601 日期（实测抛异常）；
///   - JavaScriptSerializer 写日期会变成 \/Date(ms)\/ 且回读丢时区，进而影响「最近阅读」的排序。
///
/// 输出保持「2 空格缩进 + 中文不转义 + ISO 8601 日期」，配置文件仍然肉眼可读、可手改。
/// 读入是宽松的：未知字段忽略、缺失字段保留默认值、解析异常由调用方兜底为默认值。
/// </summary>
internal static class MiniJson
{
    // ------------------------------------------------------------------ 写

    public static string Write(object? value)
    {
        var sb = new StringBuilder(256);
        WriteValue(sb, value, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case DateTime dt:
                WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case decimal m:
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case Enum e:
                WriteString(sb, e.ToString());
                return;
            case IEnumerable items:
                WriteArray(sb, items, depth);
                return;
        }

        WriteObject(sb, value, depth);
    }

    private static void WriteArray(StringBuilder sb, IEnumerable items, int depth)
    {
        sb.Append('[');
        bool empty = true;
        foreach (object? item in items)
        {
            if (!empty)
                sb.Append(',');
            empty = false;
            sb.Append('\n').Append(' ', (depth + 1) * 2);
            WriteValue(sb, item, depth + 1);
        }

        if (!empty)
            sb.Append('\n').Append(' ', depth * 2);
        sb.Append(']');
    }

    private static void WriteObject(StringBuilder sb, object obj, int depth)
    {
        sb.Append('{');
        bool empty = true;
        foreach (PropertyInfo prop in Properties(obj.GetType()))
        {
            if (!empty)
                sb.Append(',');
            empty = false;
            sb.Append('\n').Append(' ', (depth + 1) * 2);
            WriteString(sb, prop.Name);
            sb.Append(": ");
            WriteValue(sb, prop.GetValue(obj, null), depth + 1);
        }

        if (!empty)
            sb.Append('\n').Append(' ', depth * 2);
        sb.Append('}');
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < ' ')
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);   // 中文不转义，配置文件保持可读
                    break;
            }
        }
        sb.Append('"');
    }

    // ------------------------------------------------------------------ 读

    /// <summary>解析并映射成 <paramref name="type"/> 的实例；失败抛异常，由调用方兜底。</summary>
    public static object? Parse(string text, Type type) => Bind(ReadValue(new Cursor(text)), type);

    private static object? Bind(object? value, Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return value is null ? null : Bind(value, underlying);

        if (value is null)
            return null;

        if (type == typeof(string))
            return value as string ?? throw new FormatException("期望字符串");
        if (type == typeof(bool))
            return value as bool? ?? throw new FormatException("期望布尔值");
        if (type.IsEnum)
            return Enum.Parse(type, (string)value);
        if (type == typeof(DateTime))
            return DateTime.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(int))
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (type == typeof(long))
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (type == typeof(double))
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (type == typeof(float))
            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        if (type == typeof(decimal))
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            Type itemType = type.GetGenericArguments()[0];
            foreach (object? item in (List<object?>)value!)
                list.Add(Bind(item, itemType));
            return list;
        }

        if (value is Dictionary<string, object?> map)
        {
            object instance = Activator.CreateInstance(type)
                ?? throw new FormatException($"无法创建 {type.Name}");
            foreach (PropertyInfo prop in Properties(type))
                if (map.TryGetValue(prop.Name, out object? raw))
                    prop.SetValue(instance, Bind(raw, prop.PropertyType), null);
            return instance;
        }

        throw new NotSupportedException($"JSON 反序列化不支持 {type.FullName}");
    }

    private static object? ReadValue(Cursor c)
    {
        SkipWhitespace(c);
        if (c.Pos >= c.Text.Length)
            throw new FormatException("JSON 意外结束");

        switch (c.Text[c.Pos])
        {
            case '{': return ReadObject(c);
            case '[': return ReadArray(c);
            case '"': return ReadString(c);
            case 't': Expect(c, "true"); return true;
            case 'f': Expect(c, "false"); return false;
            case 'n': Expect(c, "null"); return null;
            default: return ReadNumber(c);
        }
    }

    private static Dictionary<string, object?> ReadObject(Cursor c)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        c.Pos++;   // {
        SkipWhitespace(c);
        if (c.Peek() == '}')
        {
            c.Pos++;
            return map;
        }

        while (true)
        {
            SkipWhitespace(c);
            string name = ReadString(c);
            SkipWhitespace(c);
            if (c.Peek() != ':')
                throw new FormatException("对象里缺少 ':'");
            c.Pos++;
            map[name] = ReadValue(c);

            SkipWhitespace(c);
            char ch = c.Peek();
            if (ch == ',')
            {
                c.Pos++;
                continue;
            }
            if (ch == '}')
            {
                c.Pos++;
                return map;
            }
            throw new FormatException("对象里缺少 ',' 或 '}'");
        }
    }

    private static List<object?> ReadArray(Cursor c)
    {
        var list = new List<object?>();
        c.Pos++;   // [
        SkipWhitespace(c);
        if (c.Peek() == ']')
        {
            c.Pos++;
            return list;
        }

        while (true)
        {
            list.Add(ReadValue(c));
            SkipWhitespace(c);
            char ch = c.Peek();
            if (ch == ',')
            {
                c.Pos++;
                continue;
            }
            if (ch == ']')
            {
                c.Pos++;
                return list;
            }
            throw new FormatException("数组里缺少 ',' 或 ']'");
        }
    }

    private static string ReadString(Cursor c)
    {
        if (c.Peek() != '"')
            throw new FormatException("期望字符串");

        c.Pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (c.Pos >= c.Text.Length)
                throw new FormatException("字符串未闭合");

            char ch = c.Text[c.Pos++];
            if (ch == '"')
                return sb.ToString();

            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }

            if (c.Pos >= c.Text.Length)
                throw new FormatException("转义序列未闭合");

            char esc = c.Text[c.Pos++];
            switch (esc)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (c.Pos + 4 > c.Text.Length)
                        throw new FormatException("\\u 转义不完整");
                    sb.Append((char)ushort.Parse(c.Text.Substring(c.Pos, 4),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    c.Pos += 4;
                    break;
                default:
                    throw new FormatException($"未知转义 \\{esc}");
            }
        }
    }

    private static double ReadNumber(Cursor c)
    {
        int start = c.Pos;
        while (c.Pos < c.Text.Length && "+-.eE0123456789".IndexOf(c.Text[c.Pos]) >= 0)
            c.Pos++;

        if (c.Pos == start)
            throw new FormatException($"无法识别的字符 '{c.Text[c.Pos]}'");

        return double.Parse(c.Text.Substring(start, c.Pos - start),
            NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static void Expect(Cursor c, string literal)
    {
        if (c.Pos + literal.Length > c.Text.Length ||
            string.CompareOrdinal(c.Text, c.Pos, literal, 0, literal.Length) != 0)
            throw new FormatException($"期望 {literal}");

        c.Pos += literal.Length;
    }

    private static void SkipWhitespace(Cursor c)
    {
        while (c.Pos < c.Text.Length)
        {
            char ch = c.Text[c.Pos];
            // \uFEFF：文件带 BOM 时首字符会带出来
            if (char.IsWhiteSpace(ch) || ch == '\uFEFF')
                c.Pos++;
            else
                break;
        }
    }

    /// <summary>参与读写的属性：公开、可读可写、非索引器。只读计算属性（如 DisplayTitle）会被跳过。</summary>
    private static PropertyInfo[] Properties(Type type)
    {
        PropertyInfo[] all = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var usable = new List<PropertyInfo>(all.Length);
        foreach (PropertyInfo prop in all)
        {
            if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                usable.Add(prop);
        }
        return usable.ToArray();
    }

    private sealed class Cursor
    {
        public Cursor(string text) => Text = text;

        public string Text { get; }

        public int Pos { get; set; }

        public char Peek() => Pos < Text.Length ? Text[Pos] : '\0';
    }
}
