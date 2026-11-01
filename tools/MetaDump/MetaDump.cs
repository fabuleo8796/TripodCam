// Lists classes, fields, properties and methods from .NET assemblies (here: BepInEx's IL2CPP interop
// stubs), like `ilspycmd -t` but built from .NET's own System.Reflection.Metadata, so nothing has to be
// downloaded. Output is game API shape only; keep it in the git-ignored _private/ folder.
//
// dotnet MetaDump.dll <dll> types [text]        every type whose full name contains text
// dotnet MetaDump.dll <dll> type <text>         full listing of the types whose full name contains text
// dotnet MetaDump.dll <dll> uses <text>         types with a field, property or method mentioning text
//                                               (e.g. "Cinemachine", "InputAction", "FMOD")
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

static class MetaDump
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("MetaDump <dll> types|type|uses [text]");
            return 1;
        }
        using var pe = new PEReader(File.OpenRead(args[0]));
        var md = pe.GetMetadataReader();
        string mode = args[1], text = args.Length > 2 ? args[2] : "";
        var names = new Names(md);
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            string full = names.Full(h);
            if (full.Contains("<") || full.StartsWith("Il2Cpp") && full.Contains("NativeMethodInfoPtr")) continue;
            switch (mode)
            {
                case "types":
                    if (Has(full, text)) Console.WriteLine($"{full}  : {names.Base(t)}");
                    break;
                case "type":
                    if (Has(full, text)) Print(md, names, h);
                    break;
                case "uses":
                    var hits = Members(md, names, h).Where(m => Has(m, text)).ToList();
                    if (hits.Count > 0)
                    {
                        Console.WriteLine($"== {full}  : {names.Base(t)}");
                        foreach (var m in hits) Console.WriteLine("   " + m);
                    }
                    break;
            }
        }
        return 0;
    }

    static bool Has(string s, string text) => text.Length == 0 || s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

    static void Print(MetadataReader md, Names names, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        Console.WriteLine($"== {names.Full(h)}  : {names.Base(t)}");
        foreach (var m in Members(md, names, h)) Console.WriteLine("   " + m);
    }

    // Fields, properties and methods, skipping the interop's plumbing (NativeFieldInfoPtr_ etc.).
    static System.Collections.Generic.IEnumerable<string> Members(MetadataReader md, Names names, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var provider = new Sig(names);
        foreach (var fh in t.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            string n = md.GetString(f.Name);
            if (n.StartsWith("NativeFieldInfoPtr_") || n.StartsWith("NativeMethodInfoPtr_") || n.StartsWith("<")) continue;
            string st = (f.Attributes & FieldAttributes.Static) != 0 ? "static " : "";
            yield return $"field {st}{f.DecodeSignature(provider, null)} {n}";
        }
        foreach (var ph in t.GetProperties())
        {
            var p = md.GetPropertyDefinition(ph);
            var sig = p.DecodeSignature(provider, null);
            yield return $"prop {sig.ReturnType} {md.GetString(p.Name)}";
        }
        foreach (var mh in t.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            string n = md.GetString(m.Name);
            if (n.StartsWith("get_") || n.StartsWith("set_") || n == ".cctor") continue;
            var sig = m.DecodeSignature(provider, null);
            var pn = m.GetParameters().Select(ph2 => md.GetString(md.GetParameter(ph2).Name)).ToList();
            var ps = sig.ParameterTypes.Select((pt, i) => pt + " " + (i < pn.Count ? pn[i] : "")).ToList();
            string st = (m.Attributes & MethodAttributes.Static) != 0 ? "static " : "";
            yield return $"method {st}{sig.ReturnType} {n}({string.Join(", ", ps)})";
        }
    }

    sealed class Names
    {
        readonly MetadataReader md;
        public Names(MetadataReader md) => this.md = md;

        public string Full(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            string name = md.GetString(t.Name);
            var outer = t.GetDeclaringType();
            if (!outer.IsNil) return Full(outer) + "+" + name;
            string ns = md.GetString(t.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        public string Ref(TypeReferenceHandle h)
        {
            var r = md.GetTypeReference(h);
            string ns = md.GetString(r.Namespace), name = md.GetString(r.Name);
            if (r.ResolutionScope.Kind == HandleKind.TypeReference) return Ref((TypeReferenceHandle)r.ResolutionScope) + "+" + name;
            return ns.Length > 0 ? ns + "." + name : name;
        }

        public string Any(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => Full((TypeDefinitionHandle)h),
            HandleKind.TypeReference => Ref((TypeReferenceHandle)h),
            HandleKind.TypeSpecification => "spec",
            _ => "?",
        };

        public string Base(TypeDefinition t) => t.BaseType.IsNil ? "-" : Any(t.BaseType);
    }

    sealed class Sig : ISignatureTypeProvider<string, object>
    {
        readonly Names names;
        public Sig(Names names) => this.names = names;
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString().ToLowerInvariant();
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Short(names.Full(h));
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Short(names.Ref(h));
        public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => "spec";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[,]";
        public string GetByReferenceType(string e) => "ref " + e;
        public string GetPointerType(string e) => e + "*";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(", ", a) + ">";
        public string GetGenericTypeParameter(object c, int i) => "T" + i;
        public string GetGenericMethodParameter(object c, int i) => "M" + i;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetModifiedType(string m, string u, bool req) => u;
        public string GetPinnedType(string e) => e;

        // Keep namespaces only where they tell something (Cinemachine, FMOD, input, URP...).
        static string Short(string full)
        {
            foreach (var keep in new[] { "Cinemachine", "FMOD", "InputSystem", "Rendering", "EHS", "FishNet", "TMPro" })
                if (full.Contains(keep)) return full;
            int dot = full.LastIndexOf('.');
            return dot >= 0 ? full.Substring(dot + 1) : full;
        }
    }
}
