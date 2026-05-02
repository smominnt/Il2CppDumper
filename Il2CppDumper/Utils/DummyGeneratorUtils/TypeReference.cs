using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using System;
using System.Linq;


namespace Il2CppDumper
{
    public partial class DummyAssemblyGenerator
    {
        private TypeSignature GetTypeSignatureWithByRef(IMetadataMember member, Il2CppType il2CppType)
        {
            var typeSig = GetTypeSignature(member, il2CppType);
            if (il2CppType.byref == 1)
                return typeSig.MakeByReferenceType();
            return typeSig;
        }

        private TypeSignature GetTypeSignature(IMetadataMember member, Il2CppType il2CppType)
        {
            var corLib = member switch
            {
                TypeDefinition td => td.DeclaringModule!.CorLibTypeFactory,
                MethodDefinition md => md.DeclaringModule!.CorLibTypeFactory,
                _ => throw new NotSupportedException($"Unexpected member type: {member.GetType()}")
            };


            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                    return corLib.Object;
                case Il2CppTypeEnum.IL2CPP_TYPE_VOID:
                    return corLib.Void;
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                    return corLib.Boolean;
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                    return corLib.Char;
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                    return corLib.SByte;
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    return corLib.Byte;
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                    return corLib.Int16;
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    return corLib.UInt16;
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                    return corLib.Int32;
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                    return corLib.UInt32;
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                    return corLib.IntPtr;
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    return corLib.UIntPtr;
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                    return corLib.Int64;
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                    return corLib.UInt64;
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    return corLib.Single;
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    return corLib.Double;
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                    return corLib.String;
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF:
                    return corLib.TypedReference;

                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        return typeDefinitionDic[typeDef].ToTypeSignature();
                    }

                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                    {
                        var arrayType = il2Cpp.MapVATR<Il2CppArrayType>(il2CppType.data.array);
                        var oriType = il2Cpp.GetIl2CppType(arrayType.etype);
                        return GetTypeSignature(member, oriType).MakeArrayType(arrayType.rank);
                    }

                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        return GetTypeSignature(member, oriType).MakeSzArrayType();
                    }

                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                        var typeDefinition = typeDefinitionDic[typeDef];
                        var genericInst = il2Cpp.MapVATR<Il2CppGenericInst>(genericClass.context.class_inst);
                        var pointers = il2Cpp.MapVATR<ulong>(genericInst.type_argv, genericInst.type_argc);
                        var typeArgs = pointers
                            .Select(ptr => GetTypeSignature(member, il2Cpp.GetIl2CppType(ptr)))
                            .ToArray();
                        return typeDefinition.MakeGenericInstanceType(typeArgs);
                    }

                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        return GetTypeSignature(member, oriType).MakePointerType();
                    }

                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    {
                        var param = executor.GetGenericParameteFromIl2CppType(il2CppType);
                        if (member is MethodDefinition methodDef)
                            return CreateGenericParameter(param, methodDef.DeclaringType!);
                        return CreateGenericParameter(param, (TypeDefinition)member);
                    }

                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        var param = executor.GetGenericParameteFromIl2CppType(il2CppType);
                        return CreateGenericParameter(param, (MethodDefinition)member);
                    }

                default:
                    throw new NotSupportedException($"Unsupported Il2CppTypeEnum: {il2CppType.type}");
            }
        }

    }
}
