using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using System.IO;
using System.Text;

namespace Il2CppDumper
{
    public partial class DummyAssemblyGenerator
    {
        private static ElementType GetConstantTypeCode(object? value) => value switch
        {
            bool => ElementType.Boolean,
            byte => ElementType.U1,
            sbyte => ElementType.I1,
            short => ElementType.I2,
            ushort => ElementType.U2,
            int => ElementType.I4,
            uint => ElementType.U4,
            long => ElementType.I8,
            ulong => ElementType.U8,
            float => ElementType.R4,
            double => ElementType.R8,
            char => ElementType.Char,
            string => ElementType.String,
            null => ElementType.Class,
            _ => ElementType.Object
        };

        private static byte[] SerializeConstant(object? value)
        {
            if (value is null) return new byte[4];
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            switch (value)
            {
                case bool v: bw.Write(v); break;
                case byte v: bw.Write(v); break;
                case sbyte v: bw.Write(v); break;
                case short v: bw.Write(v); break;
                case ushort v: bw.Write(v); break;
                case int v: bw.Write(v); break;
                case uint v: bw.Write(v); break;
                case long v: bw.Write(v); break;
                case ulong v: bw.Write(v); break;
                case float v: bw.Write(v); break;
                case double v: bw.Write(v); break;
                case char v: bw.Write((ushort)v); break;
                case string v:
                    var bytes = Encoding.Unicode.GetBytes(v);
                    bw.Write(bytes);
                    break;
            }
            return ms.ToArray();
        }

        private static bool IsValueType(TypeSignature sig) =>
            sig is not (ByReferenceTypeSignature or PointerTypeSignature or SzArrayTypeSignature
                or ArrayTypeSignature or GenericParameterSignature)
            && sig.IsValueType;

    }

}
