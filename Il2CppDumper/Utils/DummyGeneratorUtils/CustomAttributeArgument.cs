using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
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
                MethodDefinition methodDefinition = attributeType.Methods[0];
                if (methodDefinition.Name == ".ctor" && methodDefinition.Parameters.Count == 0)
                {
                    customAttributes.Add(new CustomAttribute(methodDefinition));
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
                    TypeSignature innerSig = GetTypeSignature(member, (Il2CppType)val);
                    ModuleDefinition module = ((IModuleProvider)member).ContextModule!;
                    IResolutionScope corLibScope = module.CorLibTypeFactory.CorLibScope;
                    var systemTypeSig = new TypeReference(module, corLibScope, "System", "Type")
                        .ToTypeSignature(isValueType: false);
                    return new CustomAttributeArgument(typeSig, new BoxedArgument(systemTypeSig, innerSig));
                }
                else
                {
                    TypeSignature innerSig = GetBlobValueTypeSignature(blobValue, member);
                    var innerVal = blobValue.Value is string u8blob 
                        ? (Utf8String)u8blob 
                        : blobValue.Value;
                    return new CustomAttributeArgument(typeSig, new BoxedArgument(innerSig, innerVal));
                }
            }
            else if (val == null)
            {
                var argument = typeSig.FullName == "System.String" 
                    ? new CustomAttributeArgument(typeSig, (Utf8String?)null) 
                    : new CustomAttributeArgument(typeSig, (object?)null);
                return argument;
            }
            else if (typeSig is SzArrayTypeSignature szArrayType)
            {
                var arrayVal = (BlobValue[])val;
                TypeSignature elementType = szArrayType.BaseType;
                var elements = arrayVal
                    .Select(v => {
                        var elem = CreateCustomAttributeArgument(elementType, v, member).Element;
                        return elem is string es ? (Utf8String)es : elem;
                    })
                    .ToArray();
                return new CustomAttributeArgument(typeSig, elements);
            }
            else if (typeSig.FullName == "System.Type")
            {
                TypeSignature referencedSig = GetTypeSignature(member, (Il2CppType)val);
                return new CustomAttributeArgument(typeSig, referencedSig);
            }

            var coercedVal = val is string s ? (Utf8String)s : val;
            return new CustomAttributeArgument(typeSig, coercedVal);
        }


        private TypeSignature GetBlobValueTypeSignature(BlobValue blobValue, IMetadataMember member)
        {
            if (blobValue.EnumType != null)
            {
                return GetTypeSignature(member, blobValue.EnumType);
            }
            else
            {
                var il2CppType = new Il2CppType 
                { 
                    type = blobValue.il2CppTypeEnum 
                };
                return GetTypeSignature(member, il2CppType);
            }
        }
    }
}
