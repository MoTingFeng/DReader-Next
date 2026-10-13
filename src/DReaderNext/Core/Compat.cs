using System.IO;

namespace DReaderNext.Core
{
    /// <summary>
    /// net48 缺失的 API 补齐。**只为编译期兼容，不改任何行为**：
    ///   - Math.Clamp         → .NET Core 2.0+ 才有（本工程用到 int / double / decimal 三种）
    ///   - Stream.ReadExactly → .NET 7+ 才有
    /// 调用点由 Math.Clamp(a,b,c) 改成 Compat.Clamp(a,b,c)；
    /// ReadExactly 保持原调用写法（扩展方法，签名一致）。
    /// </summary>
    internal static class Compat
    {
        public static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        public static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);

        public static decimal Clamp(decimal value, decimal min, decimal max)
            => value < min ? min : (value > max ? max : value);

        /// <summary>net7 才有 Stream.ReadExactly：读不满 count 就抛，语义与原来一致。</summary>
        public static void ReadExactly(this Stream stream, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// record 的 init 访问器 / required 成员所需的编译期标记（net48 运行时不带这几个类型）。
    /// 名字与命名空间不能改——编译器是按这些名字找它们的。三者都是纯标记，不参与运行。
    /// </summary>
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }
    }
}
