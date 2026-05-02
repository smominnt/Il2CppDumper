using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Il2CppDumper
{
    public partial class DummyAssemblyGenerator
    {
        private GenericParameterSignature CreateGenericParameter(Il2CppGenericParameter param, TypeDefinition owner)
        {
            if (!genericParameterDic.TryGetValue(param, out var genericParameter))
            {
                var genericName = metadata.GetStringFromIndex(param.nameIndex);
                genericParameter = new GenericParameter(genericName)
                {
                    Attributes = (GenericParameterAttributes)param.flags
                };
                genericParameterDic.Add(param, genericParameter);
                owner.GenericParameters.Add(genericParameter);
                AddGenericParameterConstraints(genericParameter, param, owner);
            }
            return new GenericParameterSignature(GenericParameterType.Type, genericParameter.Number);
        }

        private GenericParameterSignature CreateGenericParameter(Il2CppGenericParameter param, MethodDefinition owner)
        {
            if (!genericParameterDic.TryGetValue(param, out var genericParameter))
            {
                var genericName = metadata.GetStringFromIndex(param.nameIndex);
                genericParameter = new GenericParameter(genericName)
                {
                    Attributes = (GenericParameterAttributes)param.flags
                };
                genericParameterDic.Add(param, genericParameter);
                owner.GenericParameters.Add(genericParameter);
                AddGenericParameterConstraints(genericParameter, param, owner);
            }
            return new GenericParameterSignature(GenericParameterType.Method, genericParameter.Number);
        }


        private void AddGenericParameterConstraints(GenericParameter genericParameter, Il2CppGenericParameter param, IMetadataMember context)
        {
            for (int i = 0; i < param.constraintsCount; i++)
            {
                var il2CppType = il2Cpp.types[metadata.constraintIndices[param.constraintsStart + i]];
                var constraintSig = GetTypeSignature(context, il2CppType);
                genericParameter.Constraints.Add(new GenericParameterConstraint(constraintSig.ToTypeDefOrRef()));
            }
        }

    }
}
