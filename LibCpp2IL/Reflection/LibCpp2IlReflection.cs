using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace LibCpp2IL.Reflection;

public static class LibCpp2IlReflection
{
    private static readonly ConcurrentDictionary<(string, string?), Il2CppTypeDefinition?> CachedTypes = new();
    private static readonly ConcurrentDictionary<string, Il2CppTypeDefinition?> CachedTypesByFullName = new();

    private static readonly Dictionary<Il2CppTypeDefinition, Il2CppVariableWidthIndex<Il2CppTypeDefinition>> TypeIndices = new();
    private static readonly Dictionary<Il2CppMethodDefinition, Il2CppVariableWidthIndex<Il2CppMethodDefinition>> MethodIndices = new();
    private static readonly Dictionary<Il2CppFieldDefinition, Il2CppVariableWidthIndex<Il2CppFieldDefinition>> FieldIndices = new();
    private static readonly Dictionary<Il2CppPropertyDefinition, int> PropertyIndices = new();

    private static readonly Dictionary<Il2CppFieldDefinition, Il2CppTypeDefinition> FieldDeclaringTypes = new();

    private static readonly Dictionary<Il2CppTypeEnum, Il2CppType> PrimitiveTypeCache = new();
    public static readonly Dictionary<Il2CppTypeEnum, Il2CppTypeDefinition> PrimitiveTypeDefinitions = new();
    private static readonly Dictionary<Il2CppVariableWidthIndex<Il2CppTypeDefinition>, Il2CppType> Il2CppTypeCache = new();

    internal static void ResetCaches()
    {
        CachedTypes.Clear();
        CachedTypesByFullName.Clear();

        lock (TypeIndices)
            TypeIndices.Clear();

        MethodIndices.Clear();
        FieldIndices.Clear();
        PropertyIndices.Clear();
        FieldDeclaringTypes.Clear();
        PrimitiveTypeCache.Clear();
        PrimitiveTypeDefinitions.Clear();
        Il2CppTypeCache.Clear();
    }

    internal static void InitCaches()
    {
        for (var e = Il2CppTypeEnum.IL2CPP_TYPE_VOID; e <= Il2CppTypeEnum.IL2CPP_TYPE_STRING; e++)
        {
            PrimitiveTypeCache[e] = LibCpp2IlMain.Binary!.AllTypes.First(t => t.Type == e && t.Byref == 0);
        }

        PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF] = LibCpp2IlMain.Binary!.AllTypes.FirstOrDefault(t => t.Type == Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF && t.Byref == 0)
            ?? LibCpp2IlMain.TheMetadata!.typeDefs.First(t => t.DeclaringAssembly?.Name is "mscorlib.dll" && t.Namespace is "System" && t.Name is "TypedReference").RawType;
        // Sometimes, TypedReference does not have IL2CPP_TYPE_TYPEDBYREF as its type, but instead has IL2CPP_TYPE_VALUETYPE.
        // In this case, we need to get the type from the metadata instead of the binary.
        // https://github.com/SamboyCoding/Cpp2IL/issues/445

        for (var e = Il2CppTypeEnum.IL2CPP_TYPE_I; e <= Il2CppTypeEnum.IL2CPP_TYPE_U; e++)
        {
            PrimitiveTypeCache[e] = LibCpp2IlMain.Binary!.AllTypes.First(t => t.Type == e && t.Byref == 0);
        }

        PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_OBJECT] = LibCpp2IlMain.Binary!.AllTypes.First(t => t.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT && t.Byref == 0);

        for (var i = 0; i < LibCpp2IlMain.TheMetadata!.TypeDefinitionCount; i++)
        {
            var typeDefinition = LibCpp2IlMain.TheMetadata.typeDefs[i];

            TypeIndices[typeDefinition] = Il2CppVariableWidthIndex<Il2CppTypeDefinition>.MakeTemporaryForFixedWidthUsage(i); //DynWidth: i is computed, not read from metadata, so temp usage is ok

            var type = typeDefinition.RawType;

            if (type.Type.IsIl2CppPrimitive())
                PrimitiveTypeDefinitions[type.Type] = typeDefinition;
        }

        foreach (var type in LibCpp2IlMain.Binary!.AllTypes)
        {
            if (type.Type is not Il2CppTypeEnum.IL2CPP_TYPE_CLASS and not Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
                continue;

            if (type.Byref == 0)
            {
                Il2CppTypeCache[type.Data.ClassIndex] = type;
            }
        }
    }

    public static Il2CppTypeDefinition? GetType(string name, string? @namespace = null)
    {
        if (LibCpp2IlMain.TheMetadata == null) return null;

        var key = (name, @namespace);
        if (!CachedTypes.ContainsKey(key))
        {
            var typeDef = LibCpp2IlMain.TheMetadata.typeDefs.FirstOrDefault(td =>
                td.Name == name &&
                (@namespace == null || @namespace == td.Namespace)
            );
            CachedTypes[key] = typeDef;
        }

        return CachedTypes[key];
    }

    public static Il2CppTypeDefinition? GetTypeByFullName(string fullName)
    {
        if (LibCpp2IlMain.TheMetadata == null) return null;

        if (!CachedTypesByFullName.ContainsKey(fullName))
        {
            var typeDef = LibCpp2IlMain.TheMetadata.typeDefs.FirstOrDefault(td =>
                td.FullName == fullName
            );
            CachedTypesByFullName[fullName] = typeDef;
        }

        return CachedTypesByFullName[fullName];
    }


    public static Il2CppTypeDefinition? GetTypeDefinitionByTypeIndex(Il2CppVariableWidthIndex<Il2CppType> index)
    {
        if (LibCpp2IlMain.TheMetadata == null || LibCpp2IlMain.Binary == null) return null;

        if (index.IsNull) return null;

        var type = LibCpp2IlMain.Binary.GetType(index);

        return type.CoerceToUnderlyingTypeDefinition();
    }

    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
    public static Il2CppVariableWidthIndex<Il2CppTypeDefinition> GetTypeIndexFromType(Il2CppTypeDefinition typeDefinition)
    {
        if (LibCpp2IlMain.TheMetadata == null) return Il2CppVariableWidthIndex<Il2CppTypeDefinition>.Null;

        return TypeIndices.GetOrDefault(typeDefinition, Il2CppVariableWidthIndex<Il2CppTypeDefinition>.Null);
    }

    // ReSharper disable InconsistentlySynchronizedField
    public static Il2CppVariableWidthIndex<Il2CppMethodDefinition> GetMethodIndexFromMethod(Il2CppMethodDefinition methodDefinition)
    {
        if (LibCpp2IlMain.TheMetadata == null) return Il2CppVariableWidthIndex<Il2CppMethodDefinition>.Null;

        if (MethodIndices.Count == 0)
        {
            lock (MethodIndices)
            {
                if (MethodIndices.Count == 0)
                {
                    //Check again inside lock
                    for (var i = 0; i < LibCpp2IlMain.TheMetadata.MethodDefinitionCount; i++)
                    {
                        var def = LibCpp2IlMain.TheMetadata.methodDefs[i];
                        MethodIndices[def] = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(i); //DynWidth: i is computed, not read from metadata, so temp usage is ok
                    }
                }
            }
        }

        return MethodIndices.GetOrDefault(methodDefinition, Il2CppVariableWidthIndex<Il2CppMethodDefinition>.Null);
    }

    // ReSharper disable InconsistentlySynchronizedField
    public static Il2CppVariableWidthIndex<Il2CppFieldDefinition> GetFieldIndexFromField(Il2CppFieldDefinition fieldDefinition)
    {
        if (LibCpp2IlMain.TheMetadata == null) return Il2CppVariableWidthIndex<Il2CppFieldDefinition>.Null;

        if (FieldIndices.Count == 0)
        {
            lock (FieldIndices)
            {
                if (FieldIndices.Count == 0)
                {
                    for (var i = 0; i < LibCpp2IlMain.TheMetadata.fieldDefs.Length; i++)
                    {
                        var def = LibCpp2IlMain.TheMetadata.fieldDefs[i];
                        FieldIndices[def] = Il2CppVariableWidthIndex<Il2CppFieldDefinition>.MakeTemporaryForFixedWidthUsage(i); //DynWidth: i is computed, not read from metadata, so temp usage is ok
                    }
                }
            }
        }

        return FieldIndices[fieldDefinition];
    }

    public static int GetPropertyIndexFromProperty(Il2CppPropertyDefinition propertyDefinition)
    {
        if (LibCpp2IlMain.TheMetadata == null) return -1;

        if (PropertyIndices.Count == 0)
        {
            lock (PropertyIndices)
            {
                if (PropertyIndices.Count == 0)
                {
                    for (var i = 0; i < LibCpp2IlMain.TheMetadata.propertyDefs.Length; i++)
                    {
                        var def = LibCpp2IlMain.TheMetadata.propertyDefs[i];
                        PropertyIndices[def] = i;
                    }
                }
            }
        }

        return PropertyIndices[propertyDefinition];
    }

    public static Il2CppTypeDefinition GetDeclaringTypeFromField(Il2CppFieldDefinition fieldDefinition)
    {
        if (LibCpp2IlMain.TheMetadata == null) return null!;

        if (FieldDeclaringTypes.Count == 0)
        {
            lock (FieldDeclaringTypes)
            {
                if (FieldDeclaringTypes.Count == 0)
                {
                    foreach (var declaringType in LibCpp2IlMain.TheMetadata.typeDefs)
                    {
                        foreach (var field in declaringType.Fields ?? [])
                        {
                            FieldDeclaringTypes[field] = declaringType;
                        }
                    }
                }
            }
        }

        return FieldDeclaringTypes[fieldDefinition];
    }

    public static Il2CppType? GetTypeFromDefinition(Il2CppTypeDefinition definition)
    {
        if (LibCpp2IlMain.Binary == null)
            return null;

        switch (definition.FullName)
        {
            case "System.SByte":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_I1];
            case "System.Int16":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_I2];
            case "System.Int32":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_I4];
            case "System.Int64":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_I8];
            case "System.Byte":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_U1];
            case "System.UInt16":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_U2];
            case "System.UInt32":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_U4];
            case "System.UInt64":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_U8];
            case "System.IntPtr":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_I];
            case "System.UIntPtr":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_U];
            case "System.Single":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_R4];
            case "System.Double":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_R8];
            case "System.Boolean":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN];
            case "System.Char":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_CHAR];
            case "System.String":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_STRING];
            case "System.Void":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_VOID];
            case "System.TypedReference":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF];
            case "System.Object":
                return PrimitiveTypeCache[Il2CppTypeEnum.IL2CPP_TYPE_OBJECT];
        }

        var index = definition.TypeIndex;

        if (Il2CppTypeCache.TryGetValue(index, out var cachedType))
        {
            return cachedType;
        }

        foreach (var type in LibCpp2IlMain.Binary.AllTypes)
        {
            if (type.Type is not Il2CppTypeEnum.IL2CPP_TYPE_CLASS and not Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
                continue;

            if (type.Data.ClassIndex.Value == index.Value && type.Byref == 0)
            {
                lock (Il2CppTypeCache)
                {
                    Il2CppTypeCache[index] = type;
                }

                return type;
            }
        }

        return null;
    }
}
