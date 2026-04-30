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
    public class DummyAssemblyGenerator
    {
        public List<AssemblyDefinition> Assemblies = new();

        private readonly Il2CppExecutor executor;
        private readonly Metadata metadata;
        private readonly Il2Cpp il2Cpp;
        private readonly Dictionary<Il2CppTypeDefinition, TypeDefinition> typeDefinitionDic = new();
        private readonly Dictionary<Il2CppGenericParameter, GenericParameter> genericParameterDic = new();
        private readonly MethodDefinition attributeAttribute;
        private readonly CorLibTypeSignature stringType;
        private readonly CorLibTypeFactory typeSystem;
        private readonly Dictionary<int, FieldDefinition> fieldDefinitionDic = new();
        private readonly Dictionary<int, PropertyDefinition> propertyDefinitionDic = new();
        private readonly Dictionary<int, MethodDefinition> methodDefinitionDic = new();

        public DummyAssemblyGenerator(Il2CppExecutor il2CppExecutor, bool addToken)
        {
            executor = il2CppExecutor;
            metadata = il2CppExecutor.metadata;
            il2Cpp = il2CppExecutor.il2Cpp;

            // Il2CppDummyDll from embedded resource
            AssemblyDefinition il2CppDummyDll = AssemblyDefinition.FromBytes(Resource1.Il2CppDummyDll);
            Assemblies.Add(il2CppDummyDll);

            ModuleDefinition dummyMD = il2CppDummyDll.ManifestModule!;

            MethodDefinition addressAttribute = dummyMD.TopLevelTypes.First(x => x.Name == "AddressAttribute").Methods[0];
            MethodDefinition fieldOffsetAttribute = dummyMD.TopLevelTypes.First(x => x.Name == "FieldOffsetAttribute").Methods[0];
            attributeAttribute = dummyMD.TopLevelTypes.First(x => x.Name == "AttributeAttribute").Methods[0];
            MethodDefinition metadataOffsetAttribute = dummyMD.TopLevelTypes.First(x => x.Name == "MetadataOffsetAttribute").Methods[0];
            MethodDefinition tokenAttribute = dummyMD.TopLevelTypes.First(x => x.Name == "TokenAttribute").Methods[0];

            typeSystem = dummyMD.CorLibTypeFactory;
            stringType = typeSystem.String;

            var parameterDefinitionDic = new Dictionary<int, ParameterDefinition>();
            var eventDefinitionDic = new Dictionary<int, EventDefinition>();


            foreach (var imageDef in metadata.imageDefs)
            {
                // get assembly names
                string imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                Il2CppAssemblyNameDefinition aname = metadata.assemblyDefs[imageDef.assemblyIndex].aname;
                string assemblyName = metadata.GetStringFromIndex(aname.nameIndex);

                // get assembly version
                Version vers;
                if (aname.build >= 0)
                {
                    vers = new Version(aname.major, aname.minor, aname.build, aname.revision);
                }
                else
                {
                    //__Generated
                    vers = new Version(3, 7, 1, 6);
                }

                // container initialization
                AssemblyDefinition assemblyDefinition = new AssemblyDefinition(assemblyName, vers);
                ModuleDefinition moduleDefinition = new ModuleDefinition(imageName);
                assemblyDefinition.Modules.Add(moduleDefinition);
                Assemblies.Add(assemblyDefinition);

                // register types
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; index++)
                {
                    var typeDef = metadata.typeDefs[index];
                    var namespaceName = metadata.GetStringFromIndex(typeDef.namespaceIndex);
                    var typeName = metadata.GetStringFromIndex(typeDef.nameIndex);

                    var typeDefinition = new TypeDefinition(namespaceName, typeName, (TypeAttributes)typeDef.flags);
                    typeDefinitionDic.Add(typeDef, typeDefinition);

                    // append top level classes
                    if (typeDef.declaringTypeIndex == -1)
                    {
                        moduleDefinition.TopLevelTypes.Add(typeDefinition);
                    }
                }
            }


            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];

                    // nested type
                    for (int i = 0; i < typeDef.nested_type_count; i++)
                    {
                        var nestedIndex = metadata.nestedTypeIndices[typeDef.nestedTypesStart + i];
                        var nestedTypeDef = metadata.typeDefs[nestedIndex];
                        typeDefinition.NestedTypes.Add(typeDefinitionDic[nestedTypeDef]);
                    }
                }
            }


            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; index++)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];
                    var module = typeDefinition.DeclaringModule;

                    if (addToken)
                    {
                        var tokenSignature = new CustomAttributeSignature();
                        tokenSignature.FixedArguments.Add(new CustomAttributeArgument(stringType, $"0x{typeDef.token:X}"));
                        typeDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)tokenAttribute, tokenSignature));
                    }

                    // generic parameter
                    if (typeDef.genericContainerIndex >= 0)
                    {
                        var genericContainer = metadata.genericContainers[typeDef.genericContainerIndex];
                        for (int i = 0; i < genericContainer.type_argc; i++)
                        {
                            var genericParameterIndex = genericContainer.genericParameterStart + i;
                            var param = metadata.genericParameters[genericParameterIndex];
                            var genericParameter = CreateGenericParameter(param, module);
                            typeDefinition.GenericParameters.Add(genericParameter);
                        }
                    }

                    // parent
                    if (typeDef.parentIndex >= 0)
                    {
                        var parentType = il2Cpp.types[typeDef.parentIndex];
                        typeDefinition.BaseType = GetTypeDefOrRef(typeDefinition.DeclaringModule, parentType);
                    }

                    // interface
                    for (int i = 0; i < typeDef.interfaces_count; i++)
                    {
                        var interfaceType = il2Cpp.types[metadata.interfaceIndices[typeDef.interfacesStart + i]];
                        typeDefinition.Interfaces.Add(new InterfaceImplementation(GetTypeDefOrRef(typeDefinition.DeclaringModule, interfaceType)));
                    }
                }
            }


            foreach (var imageDef in metadata.imageDefs)
            {
                var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; index++)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];
                    var moduleDefinition = typeDefinition.DeclaringModule;

                    // field
                    var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                    for (var i = typeDef.fieldStart; i < fieldEnd; i++)
                    {
                        var fieldDef = metadata.fieldDefs[i];
                        var fieldType = il2Cpp.types[fieldDef.typeIndex];
                        var fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);

                        var fieldSignature = new FieldSignature(GetTypeSignature(moduleDefinition, fieldType));
                        var fieldDefinition = new FieldDefinition(fieldName, (FieldAttributes)fieldType.attrs, fieldSignature);

                        typeDefinition.Fields.Add(fieldDefinition);
                        fieldDefinitionDic.Add(i, fieldDefinition);

                        if (addToken)
                        {
                            var tokenSignature = new CustomAttributeSignature();
                            tokenSignature.FixedArguments.Add(new CustomAttributeArgument(stringType, $"0x{fieldDef.token:X}"));
                            fieldDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)tokenAttribute, tokenSignature));
                        }

                        // field default
                        if (metadata.GetFieldDefaultValueFromIndex(i, out var fieldDefault) && fieldDefault.dataIndex != -1)
                        {
                            if (executor.TryGetDefaultValue(fieldDefault.typeIndex, fieldDefault.dataIndex, out var value))
                            {
                                var blob = CreateBlobFromObject(value);
                                fieldDefinition.Constant = new Constant(fieldSignature.FieldType.ElementType, blob);
                            }
                            else
                            {
                                var offsetSignature = new CustomAttributeSignature();
                                offsetSignature.FixedArguments.Add(new CustomAttributeArgument(stringType, $"0x{value:X}"));
                                fieldDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)metadataOffsetAttribute, offsetSignature));
                            }
                        }

                        // field offset
                        if (!fieldDefinition.IsLiteral)
                        {
                            var fieldOffset = il2Cpp.GetFieldOffsetFromIndex(index, i - typeDef.fieldStart, i, typeDefinition.IsValueType, fieldDefinition.IsStatic);
                            if (fieldOffset >= 0)
                            {
                                var offsetSignature = new CustomAttributeSignature();
                                offsetSignature.FixedArguments.Add(new CustomAttributeArgument(stringType, $"0x{fieldOffset:X}"));
                                fieldDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)fieldOffsetAttribute, offsetSignature));
                            }
                        }
                    }

                    // method
                    var methodEnd = typeDef.methodStart + typeDef.method_count;
                    for (var i = typeDef.methodStart; i < methodEnd; i++)
                    {
                        var methodDef = metadata.methodDefs[i];
                        var methodName = metadata.GetStringFromIndex(methodDef.nameIndex);
                        var methodReturnType = il2Cpp.types[methodDef.returnType];

                        var returnSignature = GetTypeSignature(moduleDefinition, methodReturnType);
                        var methodSignature = new MethodSignature(
                            (methodDef.flags & (uint)MethodAttributes.Static) != 0 ? CallingConventionAttributes.Default : CallingConventionAttributes.HasThis,
                            returnSignature,
                            new List<TypeSignature>()
                        );

                        var methodDefinition = new MethodDefinition(methodName, (MethodAttributes)methodDef.flags, methodSignature)
                        {
                            ImplAttributes = (MethodImplAttributes)methodDef.iflags
                        };

                        typeDefinition.Methods.Add(methodDefinition);
                        methodDefinitionDic.Add(i, methodDefinition);

                        // generic parameter
                        if (methodDef.genericContainerIndex >= 0)
                        {
                            var genericContainer = metadata.genericContainers[methodDef.genericContainerIndex];
                            for (int j = 0; j < genericContainer.type_argc; j++)
                            {
                                var genericParameterIndex = genericContainer.genericParameterStart + j;
                                var param = metadata.genericParameters[genericParameterIndex];
                                var genericParameter = CreateGenericParameter(param, moduleDefinition);
                                methodDefinition.GenericParameters.Add(genericParameter);
                            }
                        }

                        if (addToken)
                        {
                            var tokenSignature = new CustomAttributeSignature();
                            tokenSignature.FixedArguments.Add(new CustomAttributeArgument(stringType, $"0x{methodDef.token:X}"));
                            methodDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)tokenAttribute, tokenSignature));
                        }

                        // Parameters
                        for (var j = 0; j < methodDef.parameterCount; ++j)
                        {
                            var parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                            var parameterName = metadata.GetStringFromIndex(parameterDef.nameIndex);
                            var parameterType = il2Cpp.types[parameterDef.typeIndex];

                            var paramSignature = GetTypeSignature(moduleDefinition, parameterType);
                            methodSignature.ParameterTypes.Add(paramSignature);

                            var parameterDefinition = new ParameterDefinition((ushort)(j + 1), parameterName, (ParameterAttributes)parameterType.attrs);
                            methodDefinition.ParameterDefinitions.Add(parameterDefinition);
                            parameterDefinitionDic.Add(methodDef.parameterStart + j, parameterDefinition);

                            if (metadata.GetParameterDefaultValueFromIndex(methodDef.parameterStart + j, out var parameterDefault) && parameterDefault.dataIndex != -1)
                            {
                                if (executor.TryGetDefaultValue(parameterDefault.typeIndex, parameterDefault.dataIndex, out var value))
                                {
                                    var blob = CreateBlobFromObject(value);
                                    parameterDefinition.Constant = new Constant(paramSignature.ElementType, blob);
                                }
                                else
                                {
                                    var offsetSignature = new CustomAttributeSignature();
                                    offsetSignature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "Offset", stringType, new CustomAttributeArgument(stringType, $"0x{value:X}")));
                                    parameterDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)metadataOffsetAttribute, offsetSignature));
                                }
                            }

                        }

                        // AsmResolver IL Body Generation
                        if (!methodDefinition.IsAbstract && typeDefinition.BaseType?.FullName != "System.MulticastDelegate")
                        {
                            var body = new CilMethodBody();
                            methodDefinition.CilMethodBody = body;

                            if (returnSignature.ElementType == ElementType.Void)
                            {
                                body.Instructions.Add(CilOpCodes.Ret);
                            }
                            else if (returnSignature.IsValueType)
                            {
                                var local = new CilLocalVariable(returnSignature);
                                body.LocalVariables.Add(local);
                                body.Instructions.Add(CilOpCodes.Ldloca_S, local);
                                body.Instructions.Add(CilOpCodes.Initobj, returnSignature.ToTypeDefOrRef());
                                body.Instructions.Add(CilOpCodes.Ldloc_0);
                                body.Instructions.Add(CilOpCodes.Ret);
                            }
                            else
                            {
                                body.Instructions.Add(CilOpCodes.Ldnull);
                                body.Instructions.Add(CilOpCodes.Ret);
                            }
                        }

                        // method address
                        if (!methodDefinition.IsAbstract)
                        {
                            var methodPointer = il2Cpp.GetMethodPointer(imageName, methodDef);
                            if (methodPointer > 0)
                            {
                                var addressSignature = new CustomAttributeSignature();
                                var fixedMethodPointer = il2Cpp.GetRVA(methodPointer);

                                addressSignature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "RVA", stringType, new CustomAttributeArgument(stringType, $"0x{fixedMethodPointer:X}")));
                                addressSignature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "Offset", stringType, new CustomAttributeArgument(stringType, $"0x{il2Cpp.MapVATR(methodPointer):X}")));
                                addressSignature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "VA", stringType, new CustomAttributeArgument(stringType, $"0x{methodPointer:X}")));

                                if (methodDef.slot != ushort.MaxValue)
                                {
                                    addressSignature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "Slot", stringType, new CustomAttributeArgument(stringType, methodDef.slot.ToString())));
                                }

                                methodDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)addressAttribute, addressSignature));
                            }
                        }
                    }

                    // property
                    var propertyEnd = typeDef.propertyStart + typeDef.property_count;
                    for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                    {
                        var propertyDef = metadata.propertyDefs[i];
                        var propertyName = metadata.GetStringFromIndex(propertyDef.nameIndex);

                        TypeSignature propertyType = null;
                        MethodDefinition getMethod = null;
                        MethodDefinition setMethod = null;

                        if (propertyDef.get >= 0)
                        {
                            getMethod = methodDefinitionDic[typeDef.methodStart + propertyDef.get];
                            propertyType = getMethod.Signature.ReturnType;
                        }
                        if (propertyDef.set >= 0)
                        {
                            setMethod = methodDefinitionDic[typeDef.methodStart + propertyDef.set];
                            propertyType ??= setMethod.Signature.ParameterTypes[0];
                        }

                        var propertySignature = propertyDef.get >= 0
                            ? PropertySignature.CreateInstance(propertyType)
                            : PropertySignature.CreateStatic(propertyType);

                        var propertyDefinition = new PropertyDefinition(propertyName, (PropertyAttributes)propertyDef.attrs, propertySignature);

                        if (getMethod != null)
                            propertyDefinition.Semantics.Add(new MethodSemantics(getMethod, MethodSemanticsAttributes.Getter));
                        if (setMethod != null)
                            propertyDefinition.Semantics.Add(new MethodSemantics(setMethod, MethodSemanticsAttributes.Setter));

                        typeDefinition.Properties.Add(propertyDefinition);
                        propertyDefinitionDic.Add(i, propertyDefinition);

                        if (addToken)
                        {
                            var tokenSignature = new CustomAttributeSignature();
                            tokenSignature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "Token", stringType, new CustomAttributeArgument(stringType, $"0x{propertyDef.token:X}")));
                            propertyDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)tokenAttribute, tokenSignature));
                        }
                    }

                    //event
                    var eventEnd = typeDef.eventStart + typeDef.event_count;
                    for (var i = typeDef.eventStart; i < eventEnd; ++i)
                    {
                        var eventDef = metadata.eventDefs[i];
                        var eventName = metadata.GetStringFromIndex(eventDef.nameIndex);
                        var eventType = il2Cpp.types[eventDef.typeIndex];

                        var eventTypeRef = GetTypeDefOrRef(typeDefinition.DeclaringModule, eventType);
                        var eventDefinition = new EventDefinition(eventName, (EventAttributes)eventType.attrs, eventTypeRef);

                        if (eventDef.add >= 0)
                        {
                            var addMethod = methodDefinitionDic[typeDef.methodStart + eventDef.add];
                            eventDefinition.Semantics.Add(new MethodSemantics(addMethod, MethodSemanticsAttributes.AddOn));
                        }
                        if (eventDef.remove >= 0)
                        {
                            var removeMethod = methodDefinitionDic[typeDef.methodStart + eventDef.remove];
                            eventDefinition.Semantics.Add(new MethodSemantics(removeMethod, MethodSemanticsAttributes.RemoveOn));
                        }
                        if (eventDef.raise >= 0)
                        {
                            var raiseMethod = methodDefinitionDic[typeDef.methodStart + eventDef.raise];
                            eventDefinition.Semantics.Add(new MethodSemantics(raiseMethod, MethodSemanticsAttributes.Fire));
                        }

                        typeDefinition.Events.Add(eventDefinition);
                        eventDefinitionDic.Add(i, eventDefinition);

                        if (addToken)
                        {
                            var tokenSignature = new CustomAttributeSignature();
                            tokenSignature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "Token", stringType, new CustomAttributeArgument(stringType, $"0x{eventDef.token:X}")));
                            eventDefinition.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)tokenAttribute, tokenSignature));
                        }
                    }

                }
            }



            //第三遍，添加CustomAttribute
            if (il2Cpp.Version > 20)
            {
                foreach (var imageDef in metadata.imageDefs)
                {
                    var typeEnd = imageDef.typeStart + imageDef.typeCount;
                    for (int index = imageDef.typeStart; index < typeEnd; index++)
                    {
                        var typeDef = metadata.typeDefs[index];
                        var typeDefinition = typeDefinitionDic[typeDef];
                        //typeAttribute
                        CreateCustomAttribute(imageDef, typeDef.customAttributeIndex, typeDef.token, typeDefinition.DeclaringModule, typeDefinition.CustomAttributes);

                        //field
                        var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                        for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                        {
                            var fieldDef = metadata.fieldDefs[i];
                            var fieldDefinition = fieldDefinitionDic[i];
                            //fieldAttribute
                            CreateCustomAttribute(imageDef, fieldDef.customAttributeIndex, fieldDef.token, typeDefinition.DeclaringModule, fieldDefinition.CustomAttributes);
                        }

                        //method
                        var methodEnd = typeDef.methodStart + typeDef.method_count;
                        for (var i = typeDef.methodStart; i < methodEnd; ++i)
                        {
                            var methodDef = metadata.methodDefs[i];
                            var methodDefinition = methodDefinitionDic[i];
                            //methodAttribute
                            CreateCustomAttribute(imageDef, methodDef.customAttributeIndex, methodDef.token, typeDefinition.DeclaringModule, methodDefinition.CustomAttributes);

                            //method parameter
                            for (var j = 0; j < methodDef.parameterCount; ++j)
                            {
                                var parameterDef = metadata.parameterDefs[methodDef.parameterStart + j];
                                var parameterDefinition = parameterDefinitionDic[methodDef.parameterStart + j];
                                //parameterAttribute
                                CreateCustomAttribute(imageDef, parameterDef.customAttributeIndex, parameterDef.token, typeDefinition.DeclaringModule, parameterDefinition.CustomAttributes);
                            }
                        }

                        //property
                        var propertyEnd = typeDef.propertyStart + typeDef.property_count;
                        for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                        {
                            var propertyDef = metadata.propertyDefs[i];
                            var propertyDefinition = propertyDefinitionDic[i];
                            //propertyAttribute
                            CreateCustomAttribute(imageDef, propertyDef.customAttributeIndex, propertyDef.token, typeDefinition.DeclaringModule, propertyDefinition.CustomAttributes);
                        }

                        //event
                        var eventEnd = typeDef.eventStart + typeDef.event_count;
                        for (var i = typeDef.eventStart; i < eventEnd; ++i)
                        {
                            var eventDef = metadata.eventDefs[i];
                            var eventDefinition = eventDefinitionDic[i];
                            //eventAttribute
                            CreateCustomAttribute(imageDef, eventDef.customAttributeIndex, eventDef.token, typeDefinition.DeclaringModule, eventDefinition.CustomAttributes);
                        }
                    }
                }
            }
        }


        private void CreateCustomAttribute(Il2CppImageDefinition imageDef, int customAttributeIndex, uint token, ModuleDefinition moduleDefinition, IList<CustomAttribute> customAttributes)
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
                            if (!TryRestoreCustomAttribute(typeDefinition, moduleDefinition, customAttributes))
                            {
                                var methodPointer = executor.customAttributeGenerators[attributeIndex];
                                var fixedMethodPointer = il2Cpp.GetRVA(methodPointer);

                                var signature = new CustomAttributeSignature();
                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "Name", stringType, new CustomAttributeArgument(stringType, typeDefinition.Name)));
                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "RVA", stringType, new CustomAttributeArgument(stringType, $"0x{fixedMethodPointer:X}")));
                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Field, "Offset", stringType, new CustomAttributeArgument(stringType, $"0x{il2Cpp.MapVATR(methodPointer):X}")));

                                var customAttribute = new CustomAttribute((ICustomAttributeType)attributeAttribute, signature);
                                customAttributes.Add(customAttribute);
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

                        for (var i = 0; i < reader.Count; i++)
                        {
                            var visitor = reader.VisitCustomAttributeData();
                            var ctorDefinition = methodDefinitionDic[visitor.CtorIndex];

                            var signature = new CustomAttributeSignature();


                            foreach (var argument in visitor.Arguments)
                            {
                                var parameterSig = ctorDefinition.Signature.ParameterTypes[argument.Index];
                                signature.FixedArguments.Add(CreateCustomAttributeArgument(parameterSig, argument.Value, moduleDefinition));
                            }

                            foreach (var field in visitor.Fields)
                            {
                                var fieldDef = fieldDefinitionDic[field.Index];
                                var fieldSig = fieldDef.Signature.FieldType;

                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Field,
                                    fieldDef.Name,
                                    fieldSig,
                                    CreateCustomAttributeArgument(fieldSig, field.Value, moduleDefinition)
                                ));
                            }

                            foreach (var property in visitor.Properties)
                            {
                                var propertyDef = propertyDefinitionDic[property.Index];
                                var propSig = propertyDef.Signature.ReturnType;

                                signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                                    CustomAttributeArgumentMemberType.Property,
                                    propertyDef.Name,
                                    propSig,
                                    CreateCustomAttributeArgument(propSig, property.Value, moduleDefinition)
                                ));
                            }

                            var importedCtor = (ICustomAttributeType)moduleDefinition.DefaultImporter.ImportMethod(ctorDefinition);
                            var customAttribute = new CustomAttribute(importedCtor, signature);

                            customAttributes.Add(customAttribute);
                        }

                    }
                }
                catch
                {
                    ExtensionMethods.logger.LogError($"Error while restoring attributeIndex {attributeIndex}");
                }
            }
        }

        private static bool TryRestoreCustomAttribute(TypeDefinition attributeType, ModuleDefinition moduleDefinition, IList<CustomAttribute> customAttributes)
        {
            if (attributeType.Methods.Count == 1 && attributeType.Name != "CompilerGeneratedAttribute")
            {
                var methodDefinition = attributeType.Methods[0];
                if (methodDefinition.Name == ".ctor" && methodDefinition.ParameterDefinitions.Count == 0)
                {
                    var signature = new CustomAttributeSignature();
                    var importedCtor = (ICustomAttributeType)moduleDefinition.DefaultImporter.ImportMethod(methodDefinition);

                    var customAttribute = new CustomAttribute(importedCtor, signature);
                    customAttributes.Add(customAttribute);
                    return true;
                }
            }
            return false;
        }

        private GenericParameter CreateGenericParameter(Il2CppGenericParameter param, ModuleDefinition module)
        {
            if (!genericParameterDic.TryGetValue(param, out var genericParameter))
            {
                var genericName = metadata.GetStringFromIndex(param.nameIndex);
                genericParameter = new GenericParameter(genericName, (GenericParameterAttributes)param.flags);
                genericParameterDic.Add(param, genericParameter);
                for (int i = 0; i < param.constraintsCount; ++i)
                {
                    var il2CppType = il2Cpp.types[metadata.constraintIndices[param.constraintsStart + i]];
                    var constraintType = GetTypeDefOrRef(module, il2CppType);

                    if (constraintType != null)
                    {
                        genericParameter.Constraints.Add(new GenericParameterConstraint(constraintType));
                    }
                }
            }
            return genericParameter;
        }

        private CustomAttributeArgument CreateCustomAttributeArgument(TypeSignature typeSignature, BlobValue blobValue, ModuleDefinition module)
        {
            var val = blobValue.Value;

            if (typeSignature.FullName == "System.Object")
            {
                if (blobValue.il2CppTypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX)
                {
                    var typeTypeSig = module.CorLibTypeFactory.Object;
                    var typeRef = GetTypeDefOrRef(module, (Il2CppType)val);
                    return new CustomAttributeArgument(typeTypeSig, typeRef);
                }
                else
                {
                    var boxedType = module.CorLibTypeFactory.FromElementType((ElementType)blobValue.il2CppTypeEnum);
                    return new CustomAttributeArgument(boxedType ?? module.CorLibTypeFactory.Object, val);
                }
            }

            if (val == null)
            {
                return new CustomAttributeArgument(typeSignature, null);
            }

            if (typeSignature is SzArrayTypeSignature arrayType)
            {
                var arrayVal = (BlobValue[])val;
                var elements = new List<CustomAttributeArgument>();
                TypeSignature elementSig = arrayType.BaseType;

                for (int i = 0; i < arrayVal.Length; i++)
                {
                    elements.Add(CreateCustomAttributeArgument(elementSig, arrayVal[i], module));
                }
                return new CustomAttributeArgument(typeSignature, elements);
            }

            if (typeSignature.FullName == "System.Type")
            {
                var typeRef = GetTypeDefOrRef(module, (Il2CppType)val);
                return new CustomAttributeArgument(typeSignature, typeRef);
            }

            return new CustomAttributeArgument(typeSignature, val);
        }


        /// <summary>
        /// Used for Type hierarchies (BaseType, Interfaces)
        /// </summary>
        private ITypeDefOrRef GetTypeDefOrRef(ModuleDefinition module, Il2CppType il2CppType)
        {
            var sig = GetTypeSignature(module, il2CppType);
            return sig?.ToTypeDefOrRef();
        }


        /// <summary>
        /// Blob Signatures
        /// </summary>
        private TypeSignature GetTypeSignature(ModuleDefinition module, Il2CppType il2CppType)
        {
            var factory = module.CorLibTypeFactory;

            switch (il2CppType.type)
            {

                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT: return factory.Object;
                case Il2CppTypeEnum.IL2CPP_TYPE_VOID: return factory.Void;
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN: return factory.Boolean;
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR: return factory.Char;
                case Il2CppTypeEnum.IL2CPP_TYPE_I1: return factory.SByte;
                case Il2CppTypeEnum.IL2CPP_TYPE_U1: return factory.Byte;
                case Il2CppTypeEnum.IL2CPP_TYPE_I2: return factory.Int16;
                case Il2CppTypeEnum.IL2CPP_TYPE_U2: return factory.UInt16;
                case Il2CppTypeEnum.IL2CPP_TYPE_I4: return factory.Int32;
                case Il2CppTypeEnum.IL2CPP_TYPE_U4: return factory.UInt32;
                case Il2CppTypeEnum.IL2CPP_TYPE_I: return factory.IntPtr;
                case Il2CppTypeEnum.IL2CPP_TYPE_U: return factory.UIntPtr;
                case Il2CppTypeEnum.IL2CPP_TYPE_I8: return factory.Int64;
                case Il2CppTypeEnum.IL2CPP_TYPE_U8: return factory.UInt64;
                case Il2CppTypeEnum.IL2CPP_TYPE_R4: return factory.Single;
                case Il2CppTypeEnum.IL2CPP_TYPE_R8: return factory.Double;
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING: return factory.String;
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF: return factory.TypedReference;


                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    return GetTypeSignature(module, il2Cpp.GetIl2CppType(il2CppType.data.type)).MakeSzArrayType();

                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    return GetTypeSignature(module, il2Cpp.GetIl2CppType(il2CppType.data.type)).MakePointerType();

                case Il2CppTypeEnum.IL2CPP_TYPE_BYREF:
                    return GetTypeSignature(module, il2Cpp.GetIl2CppType(il2CppType.data.type)).MakeByReferenceType();

                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                    {
                        var arrayType = il2Cpp.MapVATR<Il2CppArrayType>(il2CppType.data.array);
                        var elementSig = GetTypeSignature(module, il2Cpp.GetIl2CppType(arrayType.etype));
                        return elementSig.MakeArrayType(arrayType.rank);
                    }


                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        var typeDefinition = typeDefinitionDic[typeDef];

                        // Import the type into the current module to ensure metadata validity
                        var importedType = module.DefaultImporter.ImportType(typeDefinition);
                        return new TypeDefOrRefSignature(importedType, il2CppType.type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE);
                    }


                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        var genericInst = il2Cpp.MapVATR<Il2CppGenericInst>(genericClass.context.class_inst);

                        Il2CppType baseIl2CppType;
                        if (il2Cpp.Version >= 27)
                        {
                            baseIl2CppType = il2Cpp.GetIl2CppType(genericClass.type);
                        }
                        else
                        {
                            baseIl2CppType = il2Cpp.types[genericClass.typeDefinitionIndex];
                        }

                        // Get the base type
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(baseIl2CppType);
                        var typeDefinition = typeDefinitionDic[typeDef];
                        var importedBase = module.DefaultImporter.ImportType(typeDefinition);

                        var isValueType = il2CppType.type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;
                        var instance = new GenericInstanceTypeSignature(importedBase, isValueType);

                        var pointerList = il2Cpp.MapVATR<ulong>(genericInst.type_argv, (int)genericInst.type_argc);
                        for (int i = 0; i < genericInst.type_argc; i++)
                        {
                            var argType = il2Cpp.GetIl2CppType(pointerList[i]);
                            instance.TypeArguments.Add(GetTypeSignature(module, argType));
                        }
                        return instance;
                    }

                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    return new GenericParameterSignature(module, GenericParameterType.Type, (int)il2CppType.data.genericParameterIndex);

                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    return new GenericParameterSignature(module, GenericParameterType.Method, (int)il2CppType.data.genericParameterIndex);

                default:
                    return factory.Object; // Fallback to avoid crashing the dumper
            }
        }


        public static DataBlobSignature? CreateBlobFromObject(object value)
        {
            return value switch
            {
                null => DataBlobSignature.FromNull(),
                bool b => DataBlobSignature.FromValue(b),
                char c => DataBlobSignature.FromValue(c),
                byte b => DataBlobSignature.FromValue(b),
                sbyte sb => DataBlobSignature.FromValue(sb),
                ushort us => DataBlobSignature.FromValue(us),
                short s => DataBlobSignature.FromValue(s),
                uint ui => DataBlobSignature.FromValue(ui),
                int i => DataBlobSignature.FromValue(i),
                ulong ul => DataBlobSignature.FromValue(ul),
                long l => DataBlobSignature.FromValue(l),
                float f => DataBlobSignature.FromValue(f),
                double d => DataBlobSignature.FromValue(d),
                string s => DataBlobSignature.FromValue(s),
                _ => throw new NotSupportedException($"Unsupported constant type: {value.GetType()}")
            };
        }
    }
}
