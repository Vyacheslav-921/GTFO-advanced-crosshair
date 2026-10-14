using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Builds _ref/ from the profile interop DLLs named in local.props.
// Those assemblies are IL2CPP wrappers: the searchable surface is type and member signatures.

var repo = FindRepoRoot();
var force = args.Contains("--force");
var bepinex = ReadBepInEx(Path.Combine(repo, "local.props"));
var interop = Path.Combine(bepinex, "interop");
if (!Directory.Exists(interop))
{
    Console.Error.WriteLine($"Interop folder not found: {interop}");
    return 1;
}

var dlls = Directory.GetFiles(interop, "*.dll")
    .Select(p => new FileInfo(p))
    .Where(f => f.Length > 0)
    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
    .ToArray();

var outDir = Path.Combine(repo, "_ref");
var manifestPath = Path.Combine(outDir, "manifest.tsv");
var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var dll in dlls)
    hashes[dll.Name] = Sha256(dll.FullName);

if (!force && IsCurrent(manifestPath, hashes))
{
    Console.WriteLine($"_ref is current ({dlls.Length} assemblies). {outDir}");
    return 0;
}

Directory.CreateDirectory(outDir);
var byDir = Path.Combine(outDir, "by-assembly");
if (Directory.Exists(byDir))
    Directory.Delete(byDir, true);
Directory.CreateDirectory(byDir);

var types = new List<string>(capacity: 80_000);
var manifestRows = new List<string>();
var totalMembers = 0;

foreach (var dll in dlls)
{
    AssemblyDump dump;
    try
    {
        dump = DumpAssembly(dll.FullName);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"skip {dll.Name}: {ex.Message}");
        manifestRows.Add($"{dll.Name}\t{dll.Length}\t{hashes[dll.Name]}\t{Category(dll.Name)}\t0\t0");
        continue;
    }

    var category = Category(dll.Name);
    manifestRows.Add($"{dll.Name}\t{dll.Length}\t{hashes[dll.Name]}\t{category}\t{dump.Types.Count}\t{dump.Members}");
    if (dump.Types.Count == 0)
        continue;

    var body = new StringBuilder(capacity: dump.Members + 256);
    body.Append("# ").Append(dll.Name).Append('\t').Append(category).Append("\ttypes=").Append(dump.Types.Count).Append("\tmembers=").Append(dump.Members).Append('\n');
    foreach (var type in dump.Types)
    {
        body.Append('\n').Append(type.Header).Append('\n');
        foreach (var line in type.Members)
            body.Append(line).Append('\n');
        types.Add($"{type.FullName}\t{type.BaseName}\t{dll.Name}\t{category}");
    }

    File.WriteAllText(Path.Combine(byDir, dll.Name + ".txt"), body.ToString());
    totalMembers += dump.Members;
    Console.WriteLine($"{dll.Name}\t{category}\ttypes={dump.Types.Count}\tmembers={dump.Members}");
}

types.Sort(StringComparer.Ordinal);
var typesBody = new StringBuilder(capacity: types.Count * 48);
typesBody.Append("# FullName\tBase\tAssembly\tCategory\n");
foreach (var line in types)
    typesBody.Append(line).Append('\n');
File.WriteAllText(Path.Combine(outDir, "types.tsv"), typesBody.ToString());

var manifest = new StringBuilder();
manifest.Append("# bepinex\t").Append(bepinex).Append('\n');
manifest.Append("# interop\t").Append(interop).Append('\n');
manifest.Append("# generated\t").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append('\n');
manifest.Append("# name\tbytes\tsha256\tcategory\ttypes\tmembers\n");
foreach (var row in manifestRows)
    manifest.Append(row).Append('\n');
File.WriteAllText(manifestPath, manifest.ToString());

File.WriteAllText(Path.Combine(outDir, "HOW.txt"),
    """
    GTFO interop API index. Generated, gitignored, do not edit by hand.
    The interop DLLs are IL2CPP wrappers. This folder is their type and member surface.

    types.tsv          FullName, base type, assembly, category (game | unity | runtime)
    by-assembly/*.txt  one file per DLL
    manifest.tsv       source path, sha256, counts

    Line kinds inside an assembly file:
      TYPE FullName : Base, Interface, ...
      F    field type and name
      P    property type and name
      M    method return type, name, parameters
      E    event type and name

    Compiler-generated types (names containing '<') and Native*InfoPtr plumbing are omitted.
    Some IL2CPP setters are stored as void set_Name() with no parameter. The P line still has the property type.
    Search this folder before opening interop DLLs. Regenerate from the repo root when the game updates:

      dotnet run --project tools/DumpGameRef -c Release

    The tool exits immediately when every interop sha256 still matches manifest.tsv.
    Pass --force to rebuild anyway. Save any one-off decompiled wrapper under _ref/decompiled/.
    """);

Console.WriteLine($"wrote {types.Count} types, {totalMembers} members -> {outDir}");
return 0;

static bool IsCurrent(string manifestPath, Dictionary<string, string> hashes)
{
    if (!File.Exists(manifestPath)
        || !File.Exists(Path.Combine(Path.GetDirectoryName(manifestPath)!, "types.tsv"))
        || !Directory.Exists(Path.Combine(Path.GetDirectoryName(manifestPath)!, "by-assembly")))
        return false;

    var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in File.ReadLines(manifestPath))
    {
        if (raw.Length == 0 || raw[0] == '#')
            continue;
        var parts = raw.Split('\t');
        if (parts.Length < 3)
            continue;
        seen[parts[0]] = parts[2];
    }

    if (seen.Count != hashes.Count)
        return false;
    foreach (var (name, hash) in hashes)
    {
        if (!seen.TryGetValue(name, out var old) || !string.Equals(old, hash, StringComparison.OrdinalIgnoreCase))
            return false;
    }

    return true;
}

static AssemblyDump DumpAssembly(string path)
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    if (!pe.HasMetadata)
        return new AssemblyDump([]);

    var reader = pe.GetMetadataReader();
    var names = new Dictionary<TypeDefinitionHandle, string>();
    foreach (var handle in reader.TypeDefinitions)
    {
        try { names[handle] = TypeDefName(reader, handle, names); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"skip type name in {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    var provider = new SigProvider(reader, names);
    var dumped = new List<DumpedType>();
    foreach (var handle in reader.TypeDefinitions)
    {
        TypeDefinition td;
        string simple;
        try
        {
            td = reader.GetTypeDefinition(handle);
            simple = reader.GetString(td.Name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"skip type in {Path.GetFileName(path)}: {ex.Message}");
            continue;
        }

        if (simple is "<Module>" || simple.Contains('<'))
            continue;

        if (!names.TryGetValue(handle, out var full) || full.Contains('<'))
            continue;

        try
        {
            dumped.Add(DumpType(reader, provider, names, td, full));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"skip {full}: {ex.Message}");
        }
    }

    dumped.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
    return new AssemblyDump(dumped);
}

static DumpedType DumpType(MetadataReader reader, SigProvider provider, Dictionary<TypeDefinitionHandle, string> names, TypeDefinition td, string full)
{
    var ctx = new SigContext(GenericNames(reader, td.GetGenericParameters()));
    var baseName = td.BaseType.IsNil ? "" : TypeFromHandle(reader, provider, names, td.BaseType, ctx);
    var header = new StringBuilder("TYPE ").Append(full);
    if (baseName.Length > 0)
        header.Append(" : ").Append(baseName);

    var ifaces = new List<string>();
    foreach (var ih in td.GetInterfaceImplementations())
    {
        var iface = reader.GetInterfaceImplementation(ih);
        ifaces.Add(TypeFromHandle(reader, provider, names, iface.Interface, ctx));
    }

    ifaces.Sort(StringComparer.Ordinal);
    foreach (var iface in ifaces)
        header.Append(", ").Append(iface);

    var members = new List<string>();
    foreach (var fh in td.GetFields())
    {
        var field = reader.GetFieldDefinition(fh);
        var name = reader.GetString(field.Name);
        if (SkipMember(name))
            continue;
        string typ;
        try { typ = field.DecodeSignature(provider, ctx); }
        catch { typ = "?"; }
        members.Add($"F\t{typ} {name}");
    }

    foreach (var ph in td.GetProperties())
    {
        var prop = reader.GetPropertyDefinition(ph);
        var name = reader.GetString(prop.Name);
        if (SkipMember(name))
            continue;
        members.Add(FormatProperty(reader, provider, prop, name, ctx));
    }

    foreach (var eh in td.GetEvents())
    {
        var ev = reader.GetEventDefinition(eh);
        var name = reader.GetString(ev.Name);
        if (SkipMember(name))
            continue;
        var typ = TypeFromHandle(reader, provider, names, ev.Type, ctx);
        members.Add($"E\t{typ} {name}");
    }

    foreach (var mh in td.GetMethods())
    {
        var method = reader.GetMethodDefinition(mh);
        var name = reader.GetString(method.Name);
        if (SkipMember(name))
            continue;
        members.Add(FormatMethod(reader, provider, method, name, ctx));
    }

    members.Sort(StringComparer.Ordinal);
    return new DumpedType(full, baseName, header.ToString(), members);
}

static string FormatMethod(MetadataReader reader, SigProvider provider, MethodDefinition method, string name, SigContext typeCtx)
{
    var methodArgs = GenericNames(reader, method.GetGenericParameters());
    if (methodArgs.Length > 0)
        name += "<" + string.Join(", ", methodArgs) + ">";

    var ctx = new SigContext(typeCtx.TypeArgs, methodArgs);
    MethodSignature<string> sig;
    try { sig = method.DecodeSignature(provider, ctx); }
    catch { return $"M\t? {name}()"; }

    var named = new Dictionary<int, string>();
    var maxSeq = 0;
    foreach (var ph in method.GetParameters())
    {
        var p = reader.GetParameter(ph);
        if (p.SequenceNumber <= 0)
            continue;
        maxSeq = Math.Max(maxSeq, p.SequenceNumber);
        named[p.SequenceNumber] = reader.GetString(p.Name);
    }

    var count = Math.Max(sig.ParameterTypes.Length, maxSeq);
    var parts = new List<string>(count);
    for (var i = 1; i <= count; i++)
    {
        var ptype = i - 1 < sig.ParameterTypes.Length ? sig.ParameterTypes[i - 1] : "?";
        if (named.TryGetValue(i, out var pname) && pname.Length > 0)
            parts.Add(ptype + " " + pname);
        else
            parts.Add(ptype);
    }

    return $"M\t{sig.ReturnType} {name}({string.Join(", ", parts)})";
}

static string FormatProperty(MetadataReader reader, SigProvider provider, PropertyDefinition prop, string name, SigContext ctx)
{
    MethodSignature<string> sig;
    try { sig = prop.DecodeSignature(provider, ctx); }
    catch { return $"P\t? {name}"; }

    var access = new List<string>(2);
    var accessors = prop.GetAccessors();
    if (!accessors.Getter.IsNil)
        access.Add("get");
    if (!accessors.Setter.IsNil)
        access.Add("set");

    var suffix = access.Count == 0 ? "" : " { " + string.Join("; ", access) + "; }";
    if (sig.ParameterTypes.Length == 0)
        return $"P\t{sig.ReturnType} {name}{suffix}";
    return $"P\t{sig.ReturnType} {name}[{string.Join(", ", sig.ParameterTypes)}]{suffix}";
}

static bool SkipMember(string name) =>
    name.Contains('<')
    || name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal)
    || name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)
    || name.StartsWith("NativePropertyInfoPtr_", StringComparison.Ordinal);

static string[] GenericNames(MetadataReader reader, GenericParameterHandleCollection handles)
{
    if (handles.Count == 0)
        return [];
    var names = new string[handles.Count];
    foreach (var handle in handles)
    {
        var gp = reader.GetGenericParameter(handle);
        names[gp.Index] = reader.GetString(gp.Name);
    }
    return names;
}

static string TypeDefName(MetadataReader reader, TypeDefinitionHandle handle, Dictionary<TypeDefinitionHandle, string> cache)
{
    if (cache.TryGetValue(handle, out var cached))
        return cached;

    var td = reader.GetTypeDefinition(handle);
    var name = reader.GetString(td.Name);
    string full;
    if (td.IsNested)
        full = TypeDefName(reader, td.GetDeclaringType(), cache) + "+" + name;
    else
    {
        var ns = reader.GetString(td.Namespace);
        full = ns.Length == 0 ? name : ns + "." + name;
    }

    cache[handle] = full;
    return full;
}

static string TypeRefName(MetadataReader reader, TypeReferenceHandle handle)
{
    var tr = reader.GetTypeReference(handle);
    var name = reader.GetString(tr.Name);
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        return TypeRefName(reader, (TypeReferenceHandle)tr.ResolutionScope) + "." + name;

    var ns = reader.GetString(tr.Namespace);
    return ns.Length == 0 ? name : ns + "." + name;
}

static string TypeFromHandle(MetadataReader reader, SigProvider provider, Dictionary<TypeDefinitionHandle, string> names, EntityHandle handle, SigContext ctx) =>
    handle.Kind switch
    {
        HandleKind.TypeDefinition => names[(TypeDefinitionHandle)handle],
        HandleKind.TypeReference => TypeRefName(reader, (TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => Spec(reader, provider, (TypeSpecificationHandle)handle, ctx),
        _ => handle.Kind.ToString()
    };

static string Spec(MetadataReader reader, SigProvider provider, TypeSpecificationHandle handle, SigContext ctx)
{
    try { return reader.GetTypeSpecification(handle).DecodeSignature(provider, ctx); }
    catch { return "?"; }
}

static string Category(string fileName)
{
    if (fileName.StartsWith("UnityEngine", StringComparison.Ordinal) || fileName.StartsWith("Unity.", StringComparison.Ordinal))
        return "unity";
    if (fileName.StartsWith("Il2Cpp", StringComparison.Ordinal))
        return "runtime";
    return "game";
}

static string ReadBepInEx(string propsPath)
{
    if (!File.Exists(propsPath))
        throw new FileNotFoundException("Copy local.props.example to local.props and set BepInEx.", propsPath);

    var text = File.ReadAllText(propsPath);
    var match = Regex.Match(text, @"<BepInEx>\s*(.*?)\s*</BepInEx>");
    if (!match.Success || match.Groups[1].Value.Length == 0)
        throw new InvalidOperationException($"No <BepInEx> value in {propsPath}");
    return match.Groups[1].Value;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "gtfo-advanced-crosshair.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException("Run this from the gtfo-advanced-crosshair repo.");
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

sealed class SigContext(string[] typeArgs, string[]? methodArgs = null)
{
    public string[] TypeArgs { get; } = typeArgs;
    public string[] MethodArgs { get; } = methodArgs ?? [];
}

sealed class SigProvider(MetadataReader reader, Dictionary<TypeDefinitionHandle, string> typeNames) : ISignatureTypeProvider<string, SigContext>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.TypedReference => "typedref",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Object => "object",
        _ => typeCode.ToString()
    };

    public string GetTypeFromDefinition(MetadataReader _, TypeDefinitionHandle handle, byte _raw) =>
        typeNames.TryGetValue(handle, out var name) ? name : "?";

    public string GetTypeFromReference(MetadataReader _, TypeReferenceHandle handle, byte _raw) =>
        RefName(handle);

    string RefName(TypeReferenceHandle handle)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return RefName((TypeReferenceHandle)tr.ResolutionScope) + "." + name;

        var ns = reader.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
    {
        var ranks = new string[shape.Rank];
        for (var i = 0; i < shape.Rank; i++)
            ranks[i] = i < shape.LowerBounds.Length ? shape.LowerBounds[i] + "..." : "";
        return elementType + "[" + string.Join(",", ranks) + "]";
    }

    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(", ", typeArguments) + ">";

    public string GetGenericMethodParameter(SigContext ctx, int index) =>
        index < ctx.MethodArgs.Length ? ctx.MethodArgs[index] : "!!" + index;

    public string GetGenericTypeParameter(SigContext ctx, int index) =>
        index < ctx.TypeArgs.Length ? ctx.TypeArgs[index] : "!" + index;

    public string GetModifiedType(string _modifier, string unmodifiedType, bool _isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;

    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "delegate* " + signature.ReturnType;

    public string GetTypeFromSpecification(MetadataReader metadataReader, SigContext ctx, TypeSpecificationHandle handle, byte _raw)
    {
        try { return metadataReader.GetTypeSpecification(handle).DecodeSignature(this, ctx); }
        catch { return "?"; }
    }
}

sealed record DumpedType(string FullName, string BaseName, string Header, List<string> Members);

sealed class AssemblyDump
{
    public AssemblyDump(List<DumpedType> types) => Types = types;
    public List<DumpedType> Types { get; }
    public int Members => Types.Sum(t => t.Members.Count);
}
