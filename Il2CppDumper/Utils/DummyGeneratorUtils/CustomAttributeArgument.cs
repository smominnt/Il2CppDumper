using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Il2CppDumper
{
    public partial class DummyAssemblyGenerator
    {
        private static bool TryRestoreCustomAttribute(TypeDefinition attributeType, IList<CustomAttribute> customAttributes)
        {
            if (attributeType.Methods.Count == 1 && attributeType.Name != "CompilerGeneratedAttribute")
            {
                var methodDefinition = attributeType.Methods[0];
                if (methodDefinition.Name == ".ctor" && methodDefinition.Parameters.Count == 0)
                {
                    var customAttribute = new CustomAttribute(methodDefinition);
                    customAttributes.Add(customAttribute);
                    return true;
                }
            }
            return false;
        }

        private CustomAttributeArgument CreateCustomAttributeArgument(TypeSignature typeSig, BlobValue blobValue, IMetadataMember member)
        {
            var val = blobValue.Value;

            if (typeSig.FullName == "System.Object")
            {
                if (blobValue.il2CppTypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX)
                {
                    var innerSig = GetTypeSignature(member, (Il2CppType)val);
                    return new CustomAttributeArgument(typeSig, new BoxedArgument(innerSig, innerSig.ToTypeDefOrRef()));
                }
                else
                {
                    var innerSig = GetBlobValueTypeSignature(blobValue, member);
                    var innerVal = blobValue.Value is string ss ? (Utf8String)ss : blobValue.Value;
                    return new CustomAttributeArgument(typeSig, new BoxedArgument(innerSig, innerVal));
                }
            }

            if (val == null)
                return new CustomAttributeArgument(typeSig, (object?)null);

            if (typeSig is SzArrayTypeSignature szArrayType)
            {
                var arrayVal = (BlobValue[])val;
                var elementType = szArrayType.BaseType;
                var elements = arrayVal
                    .Select(v => {
                        var elem = CreateCustomAttributeArgument(elementType, v, member).Element;
                        return elem is string es ? (object?)(Utf8String)es : elem;
                    })
                    .ToArray();
                return new CustomAttributeArgument(typeSig, elements);
            }

            if (typeSig.FullName == "System.Type")
            {
                var referencedSig = GetTypeSignature(member, (Il2CppType)val);
                return new CustomAttributeArgument(typeSig, referencedSig);
            }

            // AsmResolver requires Utf8String for string values not System.String
            var coercedVal = val is string s ? (Utf8String)s : val;
            Console.WriteLine($"coercedVal type: {val?.GetType().FullName}, typeSig: {typeSig.FullName}");
            return new CustomAttributeArgument(typeSig, coercedVal);
        }


        private CustomAttributeArgument StringArg(string value) => new CustomAttributeArgument(stringType, (Utf8String)value);

        private TypeSignature GetBlobValueTypeSignature(BlobValue blobValue, IMetadataMember member)
        {
            if (blobValue.EnumType != null)
                return GetTypeSignature(member, blobValue.EnumType);

            var il2CppType = new Il2CppType { type = blobValue.il2CppTypeEnum };
            return GetTypeSignature(member, il2CppType);
        }
    }
}
