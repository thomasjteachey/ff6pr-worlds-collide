// Dumps every type, field, property and method signature in a .NET assembly
// (here: BepInEx's IL2CPP interop Assembly-CSharp.dll) to a text file.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

var path = args[0];
var output = args[1];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
var sb = new StringBuilder();
var provider = new SigProvider(md);
foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    var ns = md.GetString(t.Namespace);
    var name = md.GetString(t.Name);
    if (name.StartsWith("<")) continue;
    string baseName = "";
    if (!t.BaseType.IsNil) baseName = provider.TypeName(t.BaseType);
    var decl = t.GetDeclaringType();
    var full = (decl.IsNil ? "" : md.GetString(md.GetTypeDefinition(decl).Name) + "+") + name;
    sb.AppendLine($"type {(ns.Length > 0 ? ns + "." : "")}{full} : {baseName}");
    foreach (var fh in t.GetFields())
    {
        var f = md.GetFieldDefinition(fh);
        var fn = md.GetString(f.Name);
        if (fn.StartsWith("NativeFieldInfoPtr") || fn.StartsWith("<")) continue;
        sb.AppendLine($"  field {f.DecodeSignature(provider, null)} {fn}");
    }
    foreach (var ph in t.GetProperties())
    {
        var p = md.GetPropertyDefinition(ph);
        var sig = p.DecodeSignature(provider, null);
        sb.AppendLine($"  prop {sig.ReturnType} {md.GetString(p.Name)}");
    }
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        var mn = md.GetString(m.Name);
        if (mn.StartsWith("get_") || mn.StartsWith("set_") || mn == ".cctor") continue;
        var sig = m.DecodeSignature(provider, null);
        var ps = m.GetParameters().Select(h => md.GetString(md.GetParameter(h).Name)).Where(s => s.Length > 0).ToList();
        var parts = sig.ParameterTypes.Select((pt, i) => pt + " " + (i < ps.Count ? ps[i] : "")).ToList();
        var stat = (m.Attributes & System.Reflection.MethodAttributes.Static) != 0 ? "static " : "";
        sb.AppendLine($"  method {stat}{sig.ReturnType} {mn}({string.Join(", ", parts)})");
    }
}
File.WriteAllText(output, sb.ToString());
Console.WriteLine($"wrote {output}");

class SigProvider : ISignatureTypeProvider<string, object>
{
    readonly MetadataReader md;
    public SigProvider(MetadataReader md) { this.md = md; }
    public string TypeName(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(md, (TypeDefinitionHandle)h, 0),
        HandleKind.TypeReference => GetTypeFromReference(md, (TypeReferenceHandle)h, 0),
        HandleKind.TypeSpecification => "spec",
        _ => "?"
    };
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString().ToLower();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) { var t = r.GetTypeDefinition(h); return r.GetString(t.Name); }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { var t = r.GetTypeReference(h); return r.GetString(t.Name); }
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => "spec";
}
