using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
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
        private readonly Dictionary<int, FieldDefinition> fieldDefinitionDic = new();
        private readonly Dictionary<int, PropertyDefinition> propertyDefinitionDic = new();
        private readonly Dictionary<int, MethodDefinition> methodDefinitionDic = new();


        public DummyAssemblyGenerator(Il2CppExecutor il2CppExecutor, bool addToken)
        {
            this.executor = il2CppExecutor;
            this.metadata = il2CppExecutor.metadata;
            this.il2Cpp = il2CppExecutor.il2Cpp;

            // Il2CppDummyDll from embedded resource
            var il2CppDummyDll = AssemblyDefinition.FromBytes(Resource1.Il2CppDummyDll);
            Assemblies.Add(il2CppDummyDll);
            ModuleDefinition dummyModule = il2CppDummyDll.ManifestModule!;
            MethodDefinition addressAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "AddressAttribute").Methods[0];
            MethodDefinition fieldOffsetAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "FieldOffsetAttribute").Methods[0];
            MethodDefinition metadataOffsetAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "MetadataOffsetAttribute").Methods[0];
            MethodDefinition tokenAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "TokenAttribute").Methods[0];
            this.attributeAttribute = dummyModule.TopLevelTypes.First(x => x.Name == "AttributeAttribute").Methods[0];

            var resolver = new MyAssemblyResolver();
            resolver.Register(il2CppDummyDll);

            var parameterDefinitionDic = new Dictionary<int, ParameterDefinition>();
            var eventDefinitionDic = new Dictionary<int, EventDefinition>();

            // Pass 1: create assemblies and all types
            foreach (var imageDef in metadata.imageDefs)
            {
                string imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                Il2CppAssemblyNameDefinition aname = metadata.assemblyDefs[imageDef.assemblyIndex].aname;
                string assemblyName = metadata.GetStringFromIndex(aname.nameIndex);

                Version vers = aname.build > 0
                    ? new Version(aname.major, aname.minor, aname.build, aname.revision)
                    : new Version(3, 7, 1, 6);

                var assemblyDefinition = new AssemblyDefinition(assemblyName, vers);
                var corLibRef = new AssemblyReference("mscorlib", new Version(4, 0, 0, 0));
                var moduleDefinition = new ModuleDefinition(imageName, corLibRef)
                {
                    MetadataResolver = new DefaultMetadataResolver(resolver)
                };
                assemblyDefinition.Modules.Add(moduleDefinition);
                resolver.Register(assemblyDefinition);
                Assemblies.Add(assemblyDefinition);

                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    Il2CppTypeDefinition typeDef = metadata.typeDefs[index];
                    string namespaceName = metadata.GetStringFromIndex(typeDef.namespaceIndex);
                    string typeName = metadata.GetStringFromIndex(typeDef.nameIndex);
                    var typeDefinition = new TypeDefinition(namespaceName, typeName, (TypeAttributes)typeDef.flags);
                    typeDefinitionDic.Add(typeDef, typeDefinition);

                    if (typeDef.declaringTypeIndex == -1)
                    {
                        moduleDefinition.TopLevelTypes.Add(typeDefinition);
                    }
                }
            }

            // Pass 2: nested types
            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    Il2CppTypeDefinition typeDef = metadata.typeDefs[index];
                    TypeDefinition typeDefinition = typeDefinitionDic[typeDef];
                    for (int i = 0; i < typeDef.nested_type_count; i++)
                    {
                        int nestedIndex = metadata.nestedTypeIndices[typeDef.nestedTypesStart + i];
                        Il2CppTypeDefinition nestedTypeDef = metadata.typeDefs[nestedIndex];
                        TypeDefinition nestedTypeDefinition = typeDefinitionDic[nestedTypeDef];
                        typeDefinition.NestedTypes.Add(nestedTypeDefinition);
                    }
                }
            }

            // Pass 3: generic params, base types, interfaces
            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];

                    if (addToken)
                    {
                        var sig = new CustomAttributeSignature();
                        CorLibTypeSignature moduleString = typeDefinition.DeclaringModule!.CorLibTypeFactory.String;
                        sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                            CustomAttributeArgumentMemberType.Field, 
                            "Token",
                            moduleString, 
                            new CustomAttributeArgument(moduleString, (Utf8String)$"0x{typeDef.token:X}")));
                        typeDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                    }

                    if (typeDef.genericContainerIndex >= 0)
                    {
                        Il2CppGenericContainer genericContainer = metadata.genericContainers[typeDef.genericContainerIndex];
                        for (int i = 0; i < genericContainer.type_argc; i++)
                        {
                            var genericParameterIndex = genericContainer.genericParameterStart + i;
                            Il2CppGenericParameter param = metadata.genericParameters[genericParameterIndex];
                            CreateGenericParameter(param, typeDefinition);
                        }
                    }

                    if (typeDef.parentIndex >= 0)
                    {
                        Il2CppType parentType = il2Cpp.types[typeDef.parentIndex];
                        typeDefinition.BaseType = GetTypeSignature(typeDefinition, parentType).ToTypeDefOrRef();
                    }

                    for (int i = 0; i < typeDef.interfaces_count; i++)
                    {
                        Il2CppType interfaceType = il2Cpp.types[metadata.interfaceIndices[typeDef.interfacesStart + i]];
                        var impl = new InterfaceImplementation(GetTypeSignature(typeDefinition, interfaceType).ToTypeDefOrRef());
                        typeDefinition.Interfaces.Add(impl);
                    }
                }
            }

            // Pass 4: fields, methods, properties, events
            foreach (var imageDef in metadata.imageDefs)
            {
                string imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; index++)
                {
                    Il2CppTypeDefinition typeDef = metadata.typeDefs[index];
                    TypeDefinition typeDefinition = typeDefinitionDic[typeDef];
                    CorLibTypeFactory corLib = typeDefinition.DeclaringModule!.CorLibTypeFactory;
                    CorLibTypeSignature moduleString = corLib.String;

                    // fields
                    var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                    for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                    {
                        Il2CppFieldDefinition fieldDef = metadata.fieldDefs[i];
                        Il2CppType fieldType = il2Cpp.types[fieldDef.typeIndex];

                        var fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                        var fieldSig = new FieldSignature(GetTypeSignature(typeDefinition, fieldType));
                        var fieldDefinition = new FieldDefinition(fieldName, (FieldAttributes)fieldType.attrs, fieldSig);

                        typeDefinition.Fields.Add(fieldDefinition);
                        fieldDefinitionDic.Add(i, fieldDefinition);

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                CustomAttributeArgumentMemberType.Field, 
                                "Token",
                                moduleString,
                                new CustomAttributeArgument(moduleString, (Utf8String)$"0x{fieldDef.token:X}")));
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
                                    CustomAttributeArgumentMemberType.Field, 
                                    "Offset",
                                    moduleString, 
                                    new CustomAttributeArgument(moduleString, (Utf8String)$"0x{value:X}")));
                                fieldDefinition.CustomAttributes.Add(new CustomAttribute(metadataOffsetAttribute, sig));
                            }
                        }

                        if (!fieldDefinition.IsLiteral)
                        {
                            var fieldOffset = il2Cpp.GetFieldOffsetFromIndex(
                                index, 
                                i - typeDef.fieldStart, 
                                i, 
                                typeDefinition.IsValueType, 
                                fieldDefinition.IsStatic);

                            if (fieldOffset >= 0)
                            {
                                var sig = new CustomAttributeSignature();
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field, 
                                    "Offset",
                                    moduleString, 
                                    new CustomAttributeArgument(moduleString, (Utf8String)$"0x{fieldOffset:X}")));
                                fieldDefinition.CustomAttributes.Add(new CustomAttribute(fieldOffsetAttribute, sig));
                            }
                        }
                    }

                    // methods
                    var methodEnd = typeDef.methodStart + typeDef.method_count;
                    for (var i = typeDef.methodStart; i < methodEnd; ++i)
                    {
                        Il2CppMethodDefinition methodDef = metadata.methodDefs[i];
                        var methodName = metadata.GetStringFromIndex(methodDef.nameIndex);
                        var methodAttrs = (MethodAttributes)methodDef.flags;

                        var hasThis = (methodAttrs & MethodAttributes.Static) == 0;
                        var isGeneric = methodDef.genericContainerIndex >= 0;

                        var genericCount = isGeneric 
                            ? metadata.genericContainers[methodDef.genericContainerIndex].type_argc 
                            : 0;

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
                            Il2CppGenericContainer genericContainer = metadata.genericContainers[methodDef.genericContainerIndex];
                            for (int j = 0; j < genericContainer.type_argc; j++)
                            {
                                var genericParameterIndex = genericContainer.genericParameterStart + j;
                                Il2CppGenericParameter param = metadata.genericParameters[genericParameterIndex];
                                CreateGenericParameter(param, methodDefinition);
                            }
                        }

                        Il2CppType methodReturnType = il2Cpp.types[methodDef.returnType];
                        TypeSignature returnTypeSig = GetTypeSignatureWithByRef(methodDefinition, methodReturnType);
                        methodDefinition.Signature!.ReturnType = returnTypeSig;

                        // parameters
                        for (var j = 0; j < methodDef.parameterCount; ++j)
                        {
                            Il2CppParameterDefinition parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                            Il2CppType parameterType = il2Cpp.types[parameterDef.typeIndex];
                            TypeSignature paramTypeSig = GetTypeSignatureWithByRef(methodDefinition, parameterType);
                            methodDefinition.Signature.ParameterTypes.Add(paramTypeSig);

                            var parameterName = metadata.GetStringFromIndex(parameterDef.nameIndex);
                            var parameterDefinition = new ParameterDefinition((ushort)(j + 1), parameterName, (ParameterAttributes)parameterType.attrs);

                            methodDefinition.ParameterDefinitions.Add(parameterDefinition);
                            parameterDefinitionDic.Add(methodDef.parameterStart + j, parameterDefinition);

                            if (metadata.GetParameterDefaultValueFromIndex(methodDef.parameterStart + j, out var parameterDefault) 
                                && parameterDefault.dataIndex != -1)
                            {
                                if (executor.TryGetDefaultValue(parameterDefault.typeIndex, parameterDefault.dataIndex, out var value))
                                {
                                    parameterDefinition.Constant = new Constant(
                                        GetConstantTypeCode(value), 
                                        new DataBlobSignature(SerializeConstant(value)));
                                }
                                else
                                {
                                    var sig = new CustomAttributeSignature();
                                    sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                         CustomAttributeArgumentMemberType.Field, 
                                         "Offset",
                                         moduleString, 
                                         new CustomAttributeArgument(moduleString, (Utf8String)$"0x{value:X}")));
                                    parameterDefinition.CustomAttributes.Add(new CustomAttribute(metadataOffsetAttribute, sig));
                                }
                            }
                        }

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                 CustomAttributeArgumentMemberType.Field,
                                 "Token",
                                 moduleString,
                                 new CustomAttributeArgument(moduleString, (Utf8String)$"0x{methodDef.token:X}")));
                            methodDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                        }

                        // method body
                        var isDelegate = typeDefinition.BaseType?.FullName == "System.MulticastDelegate";
                        if (!methodDefinition.IsAbstract && !methodDefinition.IsPInvokeImpl && !isDelegate)
                        {
                            var body = new CilMethodBody();
                            methodDefinition.CilMethodBody = body;
                            CilInstructionCollection instructions = body.Instructions;

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
                                     CustomAttributeArgumentMemberType.Field,
                                     "RVA",
                                     moduleString,
                                     new CustomAttributeArgument(moduleString, (Utf8String)$"0x{fixedMethodPointer:X}")));
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                     CustomAttributeArgumentMemberType.Field,
                                     "Offset",
                                     moduleString,
                                     new CustomAttributeArgument(moduleString, (Utf8String)$"0x{il2Cpp.MapVATR(methodPointer):X}")));
                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                     CustomAttributeArgumentMemberType.Field,
                                     "VA",
                                     moduleString,
                                     new CustomAttributeArgument(moduleString, (Utf8String)$"0x{methodPointer:X}")));
                                if (methodDef.slot != ushort.MaxValue)
                                {
                                    sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                         CustomAttributeArgumentMemberType.Field,
                                         "Slot",
                                         moduleString,
                                         new CustomAttributeArgument(moduleString, (Utf8String)methodDef.slot.ToString())));
                                }
                                methodDefinition.CustomAttributes.Add(new CustomAttribute(addressAttribute, sig));
                            }
                        }
                    }

                    // properties
                    var propertyEnd = typeDef.propertyStart + typeDef.property_count;
                    for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                    {
                        Il2CppPropertyDefinition propertyDef = metadata.propertyDefs[i];
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
                        {
                            propertyDefinition.Semantics.Add(new MethodSemantics(getMethod, MethodSemanticsAttributes.Getter));
                        }
                        if (setMethod != null)
                        {
                            propertyDefinition.Semantics.Add(new MethodSemantics(setMethod, MethodSemanticsAttributes.Setter));
                        }
                        typeDefinition.Properties.Add(propertyDefinition);
                        propertyDefinitionDic.Add(i, propertyDefinition);

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                CustomAttributeArgumentMemberType.Field,
                                "Token",
                                moduleString,
                                new CustomAttributeArgument(moduleString, (Utf8String)$"0x{propertyDef.token:X}")));
                            propertyDefinition.CustomAttributes.Add(new CustomAttribute(tokenAttribute, sig));
                        }
                    }

                    // events
                    var eventEnd = typeDef.eventStart + typeDef.event_count;
                    for (var i = typeDef.eventStart; i < eventEnd; ++i)
                    {
                        Il2CppEventDefinition eventDef = metadata.eventDefs[i];
                        Il2CppType eventType = il2Cpp.types[eventDef.typeIndex];
                        ITypeDefOrRef eventTypeRef = GetTypeSignature(typeDefinition, eventType).ToTypeDefOrRef();

                        var eventName = metadata.GetStringFromIndex(eventDef.nameIndex);
                        var eventDefinition = new EventDefinition(eventName, (EventAttributes)eventType.attrs, eventTypeRef);

                        if (eventDef.add >= 0)
                        {
                            eventDefinition.Semantics.Add(new MethodSemantics(
                                methodDefinitionDic[typeDef.methodStart + eventDef.add], MethodSemanticsAttributes.AddOn));
                        }
                        if (eventDef.remove >= 0)
                        {
                            eventDefinition.Semantics.Add(new MethodSemantics(
                                methodDefinitionDic[typeDef.methodStart + eventDef.remove], MethodSemanticsAttributes.RemoveOn));
                        }
                        if (eventDef.raise >= 0)
                        {
                            eventDefinition.Semantics.Add(new MethodSemantics(
                                methodDefinitionDic[typeDef.methodStart + eventDef.raise], MethodSemanticsAttributes.Fire));
                        }
                        typeDefinition.Events.Add(eventDefinition);
                        eventDefinitionDic.Add(i, eventDefinition);

                        if (addToken)
                        {
                            var sig = new CustomAttributeSignature();
                            sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                CustomAttributeArgumentMemberType.Field,
                                "Token",
                                moduleString,
                                new CustomAttributeArgument(moduleString, (Utf8String)$"0x{eventDef.token:X}")));
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
                        Il2CppTypeDefinition typeDef = metadata.typeDefs[index];
                        TypeDefinition typeDefinition = typeDefinitionDic[typeDef];
                        Utf8String assemblyName = typeDefinition.DeclaringModule?.Assembly?.Name;

                        CreateCustomAttribute(
                            imageDef, 
                            typeDef.customAttributeIndex, 
                            typeDef.token, 
                            typeDefinition.CustomAttributes, 
                            assemblyName);

                        var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                        for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                        {
                            Il2CppFieldDefinition fieldDef = metadata.fieldDefs[i];
                            CreateCustomAttribute(
                                imageDef, 
                                fieldDef.customAttributeIndex, 
                                fieldDef.token, 
                                fieldDefinitionDic[i].CustomAttributes, 
                                assemblyName);
                        }

                        var methodEnd = typeDef.methodStart + typeDef.method_count;
                        for (var i = typeDef.methodStart; i < methodEnd; ++i)
                        {
                            Il2CppMethodDefinition methodDef = metadata.methodDefs[i];
                            MethodDefinition methodDefinition = methodDefinitionDic[i];
                            CreateCustomAttribute(
                                imageDef, 
                                methodDef.customAttributeIndex, 
                                methodDef.token, 
                                methodDefinition.CustomAttributes, 
                                assemblyName);

                            for (var j = 0; j < methodDef.parameterCount; ++j)
                            {
                                Il2CppParameterDefinition parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                                CreateCustomAttribute(
                                    imageDef, 
                                    parameterDef.customAttributeIndex, 
                                    parameterDef.token,
                                    parameterDefinitionDic[methodDef.parameterStart + j].CustomAttributes, 
                                    assemblyName);
                            }
                        }

                        var propertyEnd = typeDef.propertyStart + typeDef.property_count;
                        for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                        {
                            Il2CppPropertyDefinition propertyDef = metadata.propertyDefs[i];
                            CreateCustomAttribute(
                                imageDef, 
                                propertyDef.customAttributeIndex, 
                                propertyDef.token, 
                                propertyDefinitionDic[i].CustomAttributes, 
                                assemblyName);
                        }

                        var eventEnd = typeDef.eventStart + typeDef.event_count;
                        for (var i = typeDef.eventStart; i < eventEnd; ++i)
                        {
                            Il2CppEventDefinition eventDef = metadata.eventDefs[i];
                            CreateCustomAttribute(
                                imageDef, 
                                eventDef.customAttributeIndex, 
                                eventDef.token, 
                                eventDefinitionDic[i].CustomAttributes, 
                                assemblyName);
                        }
                    }
                }
            }
        }

        private void CreateCustomAttribute(Il2CppImageDefinition imageDef, int customAttributeIndex, uint token, IList<CustomAttribute> customAttributes, string? ownerAssemblyName = null)
        {
            var attributeIndex = metadata.GetCustomAttributeIndex(imageDef, customAttributeIndex, token);
            if (attributeIndex >= 0)
            {
                try
                {
                    if (il2Cpp.Version < 29)
                    {
                        Il2CppCustomAttributeTypeRange attributeTypeRange = metadata.attributeTypeRanges[attributeIndex];
                        for (int i = 0; i < attributeTypeRange.count; i++)
                        {
                            int attributeTypeIndex = metadata.attributeTypes[attributeTypeRange.start + i];
                            Il2CppType attributeType = il2Cpp.types[attributeTypeIndex];
                            Il2CppTypeDefinition typeDef = executor.GetTypeDefinitionFromIl2CppType(attributeType);
                            TypeDefinition typeDefinition = typeDefinitionDic[typeDef];

                            if (!TryRestoreCustomAttribute(typeDefinition, customAttributes))
                            {
                                ulong methodPointer = executor.customAttributeGenerators[attributeIndex];
                                ulong fixedMethodPointer = il2Cpp.GetRVA(methodPointer);

                                CorLibTypeSignature moduleString = typeDefinition.DeclaringModule!.CorLibTypeFactory.String;
                                var sig = new CustomAttributeSignature();

                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field,
                                    "Name",
                                    moduleString,
                                    new CustomAttributeArgument(moduleString, typeDefinition.Name)));

                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field,
                                    "RVA",
                                    moduleString,
                                    new CustomAttributeArgument(moduleString, (Utf8String)$"0x{fixedMethodPointer:X}")));

                                sig.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field,
                                    "Offset",
                                    moduleString,
                                    new CustomAttributeArgument(moduleString, (Utf8String)$"0x{il2Cpp.MapVATR(methodPointer):X}")));

                                customAttributes.Add(new CustomAttribute(attributeAttribute, sig));
                            }
                        }
                    }
                    else
                    {
                        Il2CppCustomAttributeDataRange startRange = metadata.attributeDataRanges[attributeIndex];
                        Il2CppCustomAttributeDataRange endRange = metadata.attributeDataRanges[attributeIndex + 1];

                        var attributeDataOffset = il2Cpp.Version < 38
                            ? metadata.header.attributeDataOffset
                            : metadata.header.attributeData.offset;
                        metadata.Position = attributeDataOffset + startRange.startOffset;
                        byte[] buff = metadata.ReadBytes((int)(endRange.startOffset - startRange.startOffset));
                        CustomAttributeDataReader reader = new CustomAttributeDataReader(executor, buff);

                        if (reader.Count != 0)
                        {
                            for (var i = 0; i < reader.Count; i++)
                            {
                                var iterator = reader.VisitCustomAttributeData();
                                foreach (var visitor in iterator)
                                {
                                    MethodDefinition methodDefinition = methodDefinitionDic[visitor.CtorIndex];
                                    CustomAttributeSignature signature = new CustomAttributeSignature();

                                    foreach (var argument in visitor.Arguments)
                                    {
                                        Parameter parameter = methodDefinition.Parameters[argument.Index];
                                        CustomAttributeArgument arg = CreateCustomAttributeArgument(
                                            parameter.ParameterType,
                                            argument.Value,
                                            methodDefinition);
                                        signature.FixedArguments.Add(arg);
                                    }

                                    foreach (var field in visitor.Fields)
                                    {
                                        FieldDefinition fieldDefinition = fieldDefinitionDic[field.Index];
                                        TypeSignature fieldType = fieldDefinition.Signature!.FieldType;
                                        CustomAttributeArgument arg = CreateCustomAttributeArgument(fieldType, field.Value, methodDefinition);
                                        signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                            CustomAttributeArgumentMemberType.Field,
                                            fieldDefinition.Name,
                                            fieldType,
                                            arg));
                                    }

                                    foreach (var property in visitor.Properties)
                                    {
                                        PropertyDefinition propertyDefinition = propertyDefinitionDic[property.Index];
                                        TypeSignature propType = propertyDefinition.Signature!.ReturnType;
                                        CustomAttributeArgument arg = CreateCustomAttributeArgument(propType, property.Value, methodDefinition);
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
                }
                catch (Exception ex)
                {
                    ExtensionMethods.logger.LogError($"Error while restoring attributeIndex {attributeIndex} | {ex}");
                }
            }
        }
    }
}
