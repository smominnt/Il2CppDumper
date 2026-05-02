using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Il2CppDumper
{
    public partial class DummyAssemblyGenerator
    {
        public List<AssemblyDefinition> Assemblies = new();

        private readonly Il2CppExecutor executor;
        private readonly Metadata metadata;
        private readonly Il2Cpp il2Cpp;
        private readonly Dictionary<Il2CppTypeDefinition, TypeDefinition> typeDefinitionDic = new();
        private readonly Dictionary<Il2CppGenericParameter, GenericParameter> genericParameterDic = new();
        private readonly MethodDefinition attributeAttribute;
        private readonly CorLibTypeSignature stringType;
        private readonly Dictionary<int, FieldDefinition> fieldDefinitionDic = new();
        private readonly Dictionary<int, PropertyDefinition> propertyDefinitionDic = new();
        private readonly Dictionary<int, MethodDefinition> methodDefinitionDic = new();


        public DummyAssemblyGenerator(Il2CppExecutor il2CppExecutor, bool addToken)
        {
            executor = il2CppExecutor;
            metadata = il2CppExecutor.metadata;
            il2Cpp   = il2CppExecutor.il2Cpp;

            // Il2CppDummyDll from embedded resource
            var il2CppDummyDll = AssemblyDefinition.FromBytes(Resource1.Il2CppDummyDll);
            Assemblies.Add(il2CppDummyDll);
            var dummyModule = il2CppDummyDll.ManifestModule!;
            var addressAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "AddressAttribute").Methods[0];
            var fieldOffsetAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "FieldOffsetAttribute").Methods[0];
            attributeAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "AttributeAttribute").Methods[0];
            var metadataOffsetAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "MetadataOffsetAttribute").Methods[0];
            var tokenAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "TokenAttribute").Methods[0];
            stringType = dummyModule.CorLibTypeFactory.String;

            var resolver = new MyAssemblyResolver();
            resolver.Register(il2CppDummyDll);

            var parameterDefinitionDic = new Dictionary<int, ParameterDefinition>();
            var eventDefinitionDic = new Dictionary<int, EventDefinition>();

            // Pass 1: create assemblies and all types
            foreach (var imageDef in metadata.imageDefs)
            {
                var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var aname = metadata.assemblyDefs[imageDef.assemblyIndex].aname;
                var assemblyName = metadata.GetStringFromIndex(aname.nameIndex);
                Version vers;
                if (aname.build >= 0)
                    vers = new Version(aname.major, aname.minor, aname.build, aname.revision);
                else
                    vers = new Version(3, 7, 1, 6);

                var assemblyDefinition = new AssemblyDefinition(assemblyName, vers);
                var corLibRef = new AssemblyReference("mscorlib", new Version(4, 0, 0, 0));
                var moduleDefinition = new ModuleDefinition(imageName, corLibRef);
                moduleDefinition.MetadataResolver = new DefaultMetadataResolver(resolver);
                assemblyDefinition.Modules.Add(moduleDefinition);
                resolver.Register(assemblyDefinition);
                Assemblies.Add(assemblyDefinition);

                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    var typeDef = metadata.typeDefs[index];
                    var namespaceName = metadata.GetStringFromIndex(typeDef.namespaceIndex);
                    var typeName = metadata.GetStringFromIndex(typeDef.nameIndex);
                    var typeDefinition = new TypeDefinition(namespaceName, typeName, (TypeAttributes)typeDef.flags);
                    typeDefinitionDic.Add(typeDef, typeDefinition);
                    if (typeDef.declaringTypeIndex == -1)
                        moduleDefinition.TopLevelTypes.Add(typeDefinition);
                }
            }

            // Pass 2: nested types
            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];
                    for (int i = 0; i < typeDef.nested_type_count; i++)
                    {
                        var nestedIndex = metadata.nestedTypeIndices[typeDef.nestedTypesStart + i];
                        var nestedTypeDef = metadata.typeDefs[nestedIndex];
                        var nestedTypeDefinition = typeDefinitionDic[nestedTypeDef];
                        typeDefinition.NestedTypes.Add(nestedTypeDefinition);
                    }
                }
            }

            // Pass 3: generic params, base types, interfaces
            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];

                    if (addToken)
                    {
                        var sig = new CustomAttributeSignature();
                        sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                            CustomAttributeArgumentMemberType.Field, "Token",
                            stringType, StringArg($"0x{typeDef.token:X}")));
                        typeDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                    }

                    if (typeDef.genericContainerIndex >= 0)
                    {
                        var genericContainer = metadata.genericContainers[typeDef.genericContainerIndex];
                        for (int i = 0; i < genericContainer.type_argc; i++)
                        {
                            var genericParameterIndex = genericContainer.genericParameterStart + i;
                            var param = metadata.genericParameters[genericParameterIndex];
                            CreateGenericParameter(param, typeDefinition);
                        }
                    }

                    if (typeDef.parentIndex >= 0)
                    {
                        var parentType = il2Cpp.types[typeDef.parentIndex];
                        typeDefinition.BaseType = GetTypeSignature(typeDefinition, parentType).ToTypeDefOrRef();
                    }

                    for (int i = 0; i < typeDef.interfaces_count; i++)
                    {
                        var interfaceType = il2Cpp.types[metadata.interfaceIndices[typeDef.interfacesStart + i]];
                        var interfaceImpl = new InterfaceImplementation(
                            GetTypeSignature(typeDefinition, interfaceType).ToTypeDefOrRef());
                        typeDefinition.Interfaces.Add(interfaceImpl);
                    }
                }
            }

            // Pass 4: fields, methods, properties, events
            foreach (var imageDef in metadata.imageDefs)
            {
                var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; index++)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];
                    var corLib = typeDefinition.DeclaringModule!.CorLibTypeFactory;

                    // fields
                    var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                    for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                    {
                        var fieldDef = metadata.fieldDefs[i];
                        var fieldType = il2Cpp.types[fieldDef.typeIndex];
                        var fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                        var fieldSig = new FieldSignature(GetTypeSignature(typeDefinition, fieldType));
                        var fieldDefinition = new FieldDefinition(fieldName, (FieldAttributes)fieldType.attrs, fieldSig);
                        typeDefinition.Fields.Add(fieldDefinition);
                        fieldDefinitionDic.Add(i, fieldDefinition);

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                CustomAttributeArgumentMemberType.Field, "Token",
                                stringType, StringArg($"0x{fieldDef.token:X}")));
                            fieldDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                        }

                        if (metadata.GetFieldDefaultValueFromIndex(i, out var fieldDefault) && fieldDefault.dataIndex != -1)
                        {
                            if (executor.TryGetDefaultValue(fieldDefault.typeIndex, fieldDefault.dataIndex, out var value))
                            {
                                fieldDefinition.Constant = new Constant(
                                    GetConstantTypeCode(value), new DataBlobSignature(SerializeConstant(value)));
                            }
                            else
                            {
                                var sig = new CustomAttributeSignature();
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "Offset",
                                    stringType, StringArg($"0x{value:X}")));
                                fieldDefinition.CustomAttributes.Add(new CustomAttribute(metadataOffsetAttribute, sig));
                            }
                        }

                        if (!fieldDefinition.IsLiteral)
                        {
                            var fieldOffset = il2Cpp.GetFieldOffsetFromIndex(index, i - typeDef.fieldStart, i,
                                typeDefinition.IsValueType, fieldDefinition.IsStatic);
                            if (fieldOffset >= 0)
                            {
                                var sig = new CustomAttributeSignature();
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "Offset",
                                    stringType, StringArg($"0x{fieldOffset:X}")));
                                fieldDefinition.CustomAttributes.Add(new CustomAttribute(fieldOffsetAttribute, sig));
                            }
                        }
                    }

                    // methods
                    var methodEnd = typeDef.methodStart + typeDef.method_count;
                    for (var i = typeDef.methodStart; i < methodEnd; ++i)
                    {
                        var methodDef = metadata.methodDefs[i];
                        var methodName = metadata.GetStringFromIndex(methodDef.nameIndex);
                        var methodAttrs = (MethodAttributes)methodDef.flags;
                        var hasThis = (methodAttrs & MethodAttributes.Static) == 0;
                        var isGeneric = methodDef.genericContainerIndex >= 0;
                        var genericCount = isGeneric ? metadata.genericContainers[methodDef.genericContainerIndex].type_argc : 0;

                        var methodSig = hasThis
                            ? MethodSignature.CreateInstance(corLib.Void, genericCount, Enumerable.Empty<TypeSignature>())
                            : MethodSignature.CreateStatic(corLib.Void, genericCount, Enumerable.Empty<TypeSignature>());

                        var methodDefinition = new MethodDefinition(methodName, methodAttrs, methodSig)
                        {
                            ImplAttributes = (MethodImplAttributes)methodDef.iflags
                        };

                        typeDefinition.Methods.Add(methodDefinition);

                        if (isGeneric)
                        {
                            var genericContainer = metadata.genericContainers[methodDef.genericContainerIndex];
                            for (int j = 0; j < genericContainer.type_argc; j++)
                            {
                                var genericParameterIndex = genericContainer.genericParameterStart + j;
                                var param = metadata.genericParameters[genericParameterIndex];
                                CreateGenericParameter(param, methodDefinition);
                            }
                        }

                        var methodReturnType = il2Cpp.types[methodDef.returnType];
                        var returnTypeSig = GetTypeSignatureWithByRef(methodDefinition, methodReturnType);
                        methodDefinition.Signature!.ReturnType = returnTypeSig;

                        // parameters
                        for (var j = 0; j < methodDef.parameterCount; ++j)
                        {
                            var parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                            var parameterName = metadata.GetStringFromIndex(parameterDef.nameIndex);
                            var parameterType = il2Cpp.types[parameterDef.typeIndex];
                            var paramTypeSig = GetTypeSignatureWithByRef(methodDefinition, parameterType);
                            methodDefinition.Signature.ParameterTypes.Add(paramTypeSig);
                            var parameterDefinition = new ParameterDefinition(
                                (ushort)(j + 1), parameterName, (ParameterAttributes)parameterType.attrs);
                            methodDefinition.ParameterDefinitions.Add(parameterDefinition);
                            parameterDefinitionDic.Add(methodDef.parameterStart + j, parameterDefinition);

                            if (metadata.GetParameterDefaultValueFromIndex(methodDef.parameterStart + j,
                                    out var parameterDefault) && parameterDefault.dataIndex != -1)
                            {
                                if (executor.TryGetDefaultValue(parameterDefault.typeIndex,
                                        parameterDefault.dataIndex, out var value))
                                {
                                    parameterDefinition.Constant = new Constant(
                                        GetConstantTypeCode(value), new DataBlobSignature(SerializeConstant(value)));
                                }
                                else
                                {
                                    var sig = new CustomAttributeSignature();
                                    sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                        CustomAttributeArgumentMemberType.Field, "Offset",
                                        stringType, StringArg($"0x{value:X}")));
                                    parameterDefinition.CustomAttributes.Add(new CustomAttribute(metadataOffsetAttribute, sig));
                                }
                            }
                        }

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                CustomAttributeArgumentMemberType.Field, "Token",
                                stringType, StringArg($"0x{methodDef.token:X}")));
                            methodDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                        }

                        // method body
                        var isDelegate = typeDefinition.BaseType?.FullName == "System.MulticastDelegate";
                        if (!methodDefinition.IsAbstract && !methodDefinition.IsPInvokeImpl && !isDelegate)
                        {
                            var body = new CilMethodBody();
                            methodDefinition.CilMethodBody = body;
                            var instructions = body.Instructions;

                            if (returnTypeSig.IsTypeOf("System", "Void"))
                            {
                                instructions.Add(CilOpCodes.Ret);
                            }
                            else if (IsValueType(returnTypeSig))
                            {
                                var local = new CilLocalVariable(returnTypeSig);
                                body.LocalVariables.Add(local);
                                instructions.Add(CilOpCodes.Ldloca_S, local);
                                instructions.Add(CilOpCodes.Initobj, returnTypeSig.ToTypeDefOrRef());
                                instructions.Add(CilOpCodes.Ldloc_0);
                                instructions.Add(CilOpCodes.Ret);
                            }
                            else
                            {
                                instructions.Add(CilOpCodes.Ldnull);
                                instructions.Add(CilOpCodes.Ret);
                            }
                        }

                        methodDefinitionDic.Add(i, methodDefinition);

                        if (!methodDefinition.IsAbstract)
                        {
                            var methodPointer = il2Cpp.GetMethodPointer(imageName, methodDef);
                            if (methodPointer > 0)
                            {
                                var fixedMethodPointer = il2Cpp.GetRVA(methodPointer);
                                var sig = new CustomAttributeSignature();
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "RVA",
                                    stringType, StringArg($"0x{fixedMethodPointer:X}")));
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "Offset",
                                    stringType, StringArg($"0x{il2Cpp.MapVATR(methodPointer):X}")));
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "VA",
                                    stringType, StringArg($"0x{methodPointer:X}")));
                                if (methodDef.slot != ushort.MaxValue)
                                {
                                    sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                        CustomAttributeArgumentMemberType.Field, "Slot",
                                        stringType, StringArg(methodDef.slot.ToString())));
                                }
                                methodDefinition.CustomAttributes.Add(new CustomAttribute(addressAttribute, sig));
                            }
                        }
                    }

                    // properties
                    var propertyEnd = typeDef.propertyStart + typeDef.property_count;
                    for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                    {
                        var propertyDef = metadata.propertyDefs[i];
                        var propertyName = metadata.GetStringFromIndex(propertyDef.nameIndex);
                        TypeSignature? propertyType = null;
                        MethodDefinition? getMethod = null;
                        MethodDefinition? setMethod = null;

                        if (propertyDef.get >= 0)
                        {
                            getMethod = methodDefinitionDic[typeDef.methodStart + propertyDef.get];
                            propertyType = getMethod.Signature!.ReturnType;
                        }
                        if (propertyDef.set >= 0)
                        {
                            setMethod = methodDefinitionDic[typeDef.methodStart + propertyDef.set];
                            propertyType ??= setMethod.Signature!.ParameterTypes[0];
                        }

                        var isInstance = getMethod?.Signature?.HasThis ?? setMethod?.Signature?.HasThis ?? false;
                        var propertySig = new PropertySignature(
                            isInstance ? CallingConventionAttributes.HasThis : 0,
                            propertyType ?? corLib.Void,
                            Enumerable.Empty<TypeSignature>());
                        var propertyDefinition = new PropertyDefinition(propertyName, (PropertyAttributes)propertyDef.attrs, propertySig);
                        if (getMethod != null)
                            propertyDefinition.Semantics.Add(new MethodSemantics(getMethod, MethodSemanticsAttributes.Getter));
                        if (setMethod != null)
                            propertyDefinition.Semantics.Add(new MethodSemantics(setMethod, MethodSemanticsAttributes.Setter));
                        typeDefinition.Properties.Add(propertyDefinition);
                        propertyDefinitionDic.Add(i, propertyDefinition);

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                CustomAttributeArgumentMemberType.Field, "Token",
                                stringType, StringArg($"0x{propertyDef.token:X}")));
                            propertyDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                        }
                    }

                    // events
                    var eventEnd = typeDef.eventStart + typeDef.event_count;
                    for (var i = typeDef.eventStart; i < eventEnd; ++i)
                    {
                        var eventDef = metadata.eventDefs[i];
                        var eventName = metadata.GetStringFromIndex(eventDef.nameIndex);
                        var eventType = il2Cpp.types[eventDef.typeIndex];
                        var eventTypeRef = GetTypeSignature(typeDefinition, eventType).ToTypeDefOrRef();
                        var eventDefinition = new EventDefinition(eventName, (EventAttributes)eventType.attrs, eventTypeRef);
                        if (eventDef.add >= 0)
                            eventDefinition.Semantics.Add(new MethodSemantics(
                                methodDefinitionDic[typeDef.methodStart + eventDef.add], MethodSemanticsAttributes.AddOn));
                        if (eventDef.remove >= 0)
                            eventDefinition.Semantics.Add(new MethodSemantics(
                                methodDefinitionDic[typeDef.methodStart + eventDef.remove], MethodSemanticsAttributes.RemoveOn));
                        if (eventDef.raise >= 0)
                            eventDefinition.Semantics.Add(new MethodSemantics(
                                methodDefinitionDic[typeDef.methodStart + eventDef.raise], MethodSemanticsAttributes.Fire));
                        typeDefinition.Events.Add(eventDefinition);
                        eventDefinitionDic.Add(i, eventDefinition);

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                CustomAttributeArgumentMemberType.Field, "Token",
                                stringType, StringArg($"0x{eventDef.token:X}")));
                            eventDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                        }
                    }
                }
            }

            // Pass 5: custom attributes
            if (il2Cpp.Version > 20)
            {
                foreach (var imageDef in metadata.imageDefs)
                {
                    var typeEnd = imageDef.typeStart + imageDef.typeCount;
                    for (int index = imageDef.typeStart; index < typeEnd; index++)
                    {
                        var typeDef = metadata.typeDefs[index];
                        var typeDefinition = typeDefinitionDic[typeDef];

                        CreateCustomAttribute(imageDef, typeDef.customAttributeIndex, typeDef.token, typeDefinition.CustomAttributes);

                        var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                        for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                        {
                            var fieldDef = metadata.fieldDefs[i];
                            CreateCustomAttribute(imageDef, fieldDef.customAttributeIndex, fieldDef.token, fieldDefinitionDic[i].CustomAttributes);
                        }

                        var methodEnd = typeDef.methodStart + typeDef.method_count;
                        for (var i = typeDef.methodStart; i < methodEnd; ++i)
                        {
                            var methodDef = metadata.methodDefs[i];
                            var methodDefinition = methodDefinitionDic[i];
                            CreateCustomAttribute(imageDef, methodDef.customAttributeIndex, methodDef.token, methodDefinition.CustomAttributes);

                            for (var j = 0; j < methodDef.parameterCount; ++j)
                            {
                                var parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                                CreateCustomAttribute(imageDef, parameterDef.customAttributeIndex, parameterDef.token,
                                    parameterDefinitionDic[methodDef.parameterStart + j].CustomAttributes);
                            }
                        }

                        var propertyEnd = typeDef.propertyStart + typeDef.property_count;
                        for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                        {
                            var propertyDef = metadata.propertyDefs[i];
                            CreateCustomAttribute(imageDef, propertyDef.customAttributeIndex, propertyDef.token, propertyDefinitionDic[i].CustomAttributes);
                        }

                        var eventEnd = typeDef.eventStart + typeDef.event_count;
                        for (var i = typeDef.eventStart; i < eventEnd; ++i)
                        {
                            var eventDef = metadata.eventDefs[i];
                            CreateCustomAttribute(imageDef, eventDef.customAttributeIndex, eventDef.token, eventDefinitionDic[i].CustomAttributes);
                        }
                    }
                }
            }
        }

        private void CreateCustomAttribute(Il2CppImageDefinition imageDef, int customAttributeIndex, uint token, IList<CustomAttribute> customAttributes)
        {
            var attributeIndex = metadata.GetCustomAttributeIndex(imageDef, customAttributeIndex, token);
            if (attributeIndex >= 0)
            {
                try
                {
                    if (il2Cpp.Version < 29)
                    {
                        var attributeTypeRange = metadata.attributeTypeRanges[attributeIndex];
                        for (int i = 0; i < attributeTypeRange.count; i++)
                        {
                            var attributeTypeIndex = metadata.attributeTypes[attributeTypeRange.start + i];
                            var attributeType = il2Cpp.types[attributeTypeIndex];
                            var typeDef = executor.GetTypeDefinitionFromIl2CppType(attributeType);
                            var typeDefinition = typeDefinitionDic[typeDef];
                            if (!TryRestoreCustomAttribute(typeDefinition, customAttributes))
                            {
                                var methodPointer = executor.customAttributeGenerators[attributeIndex];
                                var fixedMethodPointer = il2Cpp.GetRVA(methodPointer);

                                var signature = new CustomAttributeSignature();
                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "Name",
                                    stringType, StringArg(typeDefinition.Name)));
                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "RVA",
                                    stringType, StringArg($"0x{fixedMethodPointer:X}")));
                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, "Offset",
                                    stringType, StringArg($"0x{il2Cpp.MapVATR(methodPointer):X}")));

                                customAttributes.Add(new CustomAttribute(attributeAttribute, signature));
                            }
                        }
                    }
                    else
                    {
                        var startRange = metadata.attributeDataRanges[attributeIndex];
                        var endRange = metadata.attributeDataRanges[attributeIndex + 1];
                        metadata.Position = metadata.header.attributeDataOffset + startRange.startOffset;
                        var buff = metadata.ReadBytes((int)(endRange.startOffset - startRange.startOffset));
                        var reader = new CustomAttributeDataReader(executor, buff);
                        if (reader.Count != 0)
                        {
                            for (var i = 0; i < reader.Count; i++)
                            {
                                var visitor = reader.VisitCustomAttributeData();
                                var methodDefinition = methodDefinitionDic[visitor.CtorIndex];
                                var signature = new CustomAttributeSignature();

                                foreach (var argument in visitor.Arguments)
                                {
                                    var parameter = methodDefinition.Parameters[argument.Index];
                                    var arg = CreateCustomAttributeArgument(parameter.ParameterType, argument.Value, methodDefinition);
                                    signature.FixedArguments.Add(arg);
                                }

                                foreach (var field in visitor.Fields)
                                {
                                    var fieldDefinition = fieldDefinitionDic[field.Index];
                                    var fieldType = fieldDefinition.Signature!.FieldType;
                                    var arg = CreateCustomAttributeArgument(fieldType, field.Value, methodDefinition);
                                    signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                        CustomAttributeArgumentMemberType.Field,
                                        fieldDefinition.Name,
                                        fieldType,
                                        arg));
                                }

                                foreach (var property in visitor.Properties)
                                {
                                    var propertyDefinition = propertyDefinitionDic[property.Index];
                                    var propType = propertyDefinition.Signature!.ReturnType;
                                    var arg = CreateCustomAttributeArgument(propType, property.Value, methodDefinition);
                                    signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                        CustomAttributeArgumentMemberType.Property,
                                        propertyDefinition.Name,
                                        propType,
                                        arg));
                                }

                                customAttributes.Add(new CustomAttribute(methodDefinition, signature));
                            }
                        }
                    }
                }
                catch
                {
                    ExtensionMethods.logger.LogError($"Error while restoring attributeIndex {attributeIndex}");
                }
            }
        }
    }
}
