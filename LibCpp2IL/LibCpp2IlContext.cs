using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Logging;
using LibCpp2IL.Metadata;
using LibCpp2IL.Reflection;

namespace LibCpp2IL;

/// <summary>
/// Represents a single initialized IL2CPP application (binary + metadata) and holds state that was historically global/static.
/// </summary>
public sealed class LibCpp2IlContext
{
    public LibCpp2IlMain.LibCpp2IlSettings Settings { get; }

    public bool Il2CppTypeHasNumMods5Bits { get; private set; }

    public Il2CppBinary Binary { get; private set; }
    public Il2CppMetadata Metadata { get; private set; }

    public float MetadataVersion => Metadata.MetadataVersion;

    public Dictionary<ulong, List<Il2CppMethodDefinition>> MethodsByPtr { get; } = new();

    public LibCpp2IlReflectionCache ReflectionCache { get; } = new();

    private LibCpp2IlContext(LibCpp2IlMain.LibCpp2IlSettings settings, Il2CppBinary binary, Il2CppMetadata metadata)
    {
        Settings = settings;
        Binary = binary;
        Metadata = metadata;
    }

    public static LibCpp2IlContext LoadFromFile(string pePath, string metadataPath, UnityVersion unityVersion)
    {
        var metadataBytes = File.ReadAllBytes(metadataPath);
        var peBytes = File.ReadAllBytes(pePath);
        return Initialize(peBytes, metadataBytes, unityVersion);
    }

    public static LibCpp2IlContext Initialize(byte[] binaryBytes, byte[] metadataBytes, UnityVersion unityVersion)
    {
        // Snapshot settings at creation time.
        var settings = new LibCpp2IlMain.LibCpp2IlSettings
        {
            AllowManualMetadataAndCodeRegInput = LibCpp2IlMain.Settings.AllowManualMetadataAndCodeRegInput,
            DisableMethodPointerMapping = LibCpp2IlMain.Settings.DisableMethodPointerMapping,
            DisableGlobalResolving = LibCpp2IlMain.Settings.DisableGlobalResolving,
        };

        var start = DateTime.Now;
        LibLogger.InfoNewline("Initializing Metadata...");

        var metadata = Il2CppMetadata.ReadFrom(metadataBytes, unityVersion);

        var context = new LibCpp2IlContext(settings, binary: null!, metadata);

        context.Il2CppTypeHasNumMods5Bits = metadata.MetadataVersion >= 27.2f;

        LibLogger.InfoNewline($"Initialized Metadata in {(DateTime.Now - start).TotalMilliseconds:F0}ms");

        // Legacy/static API compatibility: some in-binary structures still resolve via LibCpp2IlMain.Binary/TheMetadata
        // during binary initialization, so we must set metadata defaults before initializing the binary.
        LibCpp2IlMain.TheMetadata = metadata;
        LibCpp2IlMain.DefaultContext = context;
        LibCpp2IlMain.Il2CppTypeHasNumMods5Bits = context.Il2CppTypeHasNumMods5Bits;

        var bin = LibCpp2IlBinaryRegistry.CreateAndInit(binaryBytes, metadata);
        context.Binary = bin;

        // Complete legacy/static initialization now that the binary exists.
        LibCpp2IlMain.Binary = bin;

        if (!context.Settings.DisableGlobalResolving && context.MetadataVersion < 27)
        {
            start = DateTime.Now;
            LibLogger.Info("Mapping Globals...");
            LibCpp2IlGlobalMapper.MapGlobalIdentifiers(metadata, bin);
            LibLogger.InfoNewline($"OK ({(DateTime.Now - start).TotalMilliseconds:F0}ms)");
        }

        if (!context.Settings.DisableMethodPointerMapping)
        {
            start = DateTime.Now;
            LibLogger.Info("Mapping pointers to Il2CppMethodDefinitions...");
            foreach (var (method, ptr) in metadata.methodDefs.Select(method => (method, ptr: method.MethodPointer)))
            {
                if (!context.MethodsByPtr.TryGetValue(ptr, out var list))
                    context.MethodsByPtr[ptr] = list = [];

                list.Add(method);
            }

            LibLogger.InfoNewline($"Processed {metadata.methodDefs.Length} OK ({(DateTime.Now - start).TotalMilliseconds:F0}ms)");
        }

        context.ReflectionCache.Init(context);

        return context;
    }

    public List<Il2CppMethodDefinition>? GetManagedMethodImplementationsAtAddress(ulong addr)
        => MethodsByPtr.TryGetValue(addr, out var ret) ? ret : null;

    public MetadataUsage? GetAnyGlobalByAddress(ulong address)
    {
        if (MetadataVersion >= 27f)
            return LibCpp2IlGlobalMapper.CheckForPost27GlobalAt(address);

        var glob = GetLiteralGlobalByAddress(address);
        glob ??= GetMethodGlobalByAddress(address);
        glob ??= GetRawFieldGlobalByAddress(address);
        glob ??= GetRawTypeGlobalByAddress(address);

        return glob;
    }

    public MetadataUsage? GetLiteralGlobalByAddress(ulong address)
        => MetadataVersion < 27f ? LibCpp2IlGlobalMapper.LiteralsByAddress.GetOrDefault(address) : GetAnyGlobalByAddress(address);

    public string? GetLiteralByAddress(ulong address)
    {
        var literal = GetLiteralGlobalByAddress(address);
        if (literal?.Type != MetadataUsageType.StringLiteral)
            return null;

        return literal.AsLiteral();
    }

    public MetadataUsage? GetRawTypeGlobalByAddress(ulong address)
        => MetadataVersion < 27f ? LibCpp2IlGlobalMapper.TypeRefsByAddress.GetOrDefault(address) : GetAnyGlobalByAddress(address);

    public Il2CppTypeReflectionData? GetTypeGlobalByAddress(ulong address)
    {
        var typeGlobal = GetRawTypeGlobalByAddress(address);

        if (typeGlobal?.Type is not (MetadataUsageType.Type or MetadataUsageType.TypeInfo))
            return null;

        return typeGlobal.AsType();
    }

    public MetadataUsage? GetRawFieldGlobalByAddress(ulong address)
        => MetadataVersion < 27f ? LibCpp2IlGlobalMapper.FieldRefsByAddress.GetOrDefault(address) : GetAnyGlobalByAddress(address);

    public Il2CppFieldDefinition? GetFieldGlobalByAddress(ulong address)
        => GetRawFieldGlobalByAddress(address)?.AsField();

    public MetadataUsage? GetMethodGlobalByAddress(ulong address)
        => MetadataVersion < 27f ? LibCpp2IlGlobalMapper.MethodRefsByAddress.GetOrDefault(address) : GetAnyGlobalByAddress(address);

    public Il2CppMethodDefinition? GetMethodDefinitionByGlobalAddress(ulong address)
    {
        var global = GetMethodGlobalByAddress(address);

        if (global?.Type == MetadataUsageType.MethodRef)
            return global.AsGenericMethodRef().BaseMethod;

        return global?.AsMethod();
    }
}
