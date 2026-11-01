// Tells which methods of an IL2CPP interop type call the game's real compiled code and which ones Il2CppInterop
// rebuilt ("unstripped") because the game build stripped them. Rebuilt bodies of Unity 6 bindings that pass arrays or
// strings can crash the game; prefer the real ones.
// dotnet NativeCheck.dll <interop dll> <full type name> [method name filter]
// dotnet NativeCheck.dll scan <mod dll> <interop folder>    every interop method the mod calls that was rebuilt
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

static class NativeCheck
{
    static int Main(string[] args)
    {
        if (args[0] == "scan") return Scan(args[1], args[2]);
        using var pe = new PEReader(File.OpenRead(args[0]));
        var md = pe.GetMetadataReader();
        string filter = args.Length > 2 ? args[2] : "";
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            string full = md.GetString(t.Namespace) + "." + md.GetString(t.Name);
            if (full != args[1]) continue;
            var natives = t.GetFields().Select(f => md.GetString(md.GetFieldDefinition(f).Name))
                .Where(n => n.StartsWith("NativeMethodInfoPtr_")).ToList();
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                string name = md.GetString(m.Name);
                if (filter.Length > 0 && !name.Contains(filter)) continue;
                string key = "NativeMethodInfoPtr_" + (name == ".ctor" ? "_ctor" : name) + "_";
                var sig = m.DecodeSignature(new Sig(md), null);
                Console.WriteLine($"{(IsReal(md, pe, m) ? "real   " : "REBUILT")} {name}({string.Join(", ", sig.ParameterTypes)}){Calls(md, pe, m)}");
            }
            Console.WriteLine("native pointers: " + string.Join(" ", natives.Where(n => filter.Length == 0 || n.Contains(filter))));
        }
        return 0;
    }

    // What a rebuilt body calls (call / callvirt targets), to follow it down to the game's real code.
    static string Calls(MetadataReader md, PEReader pe, MethodDefinition m)
    {
        if (m.RelativeVirtualAddress == 0 || IsReal(md, pe, m)) return "";
        var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
        var calls = new System.Collections.Generic.List<string>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue;
            int token = BitConverter.ToInt32(il, i + 1), row = token & 0xFFFFFF;
            if ((token >> 24) == 0x06 && row >= 1 && row <= md.GetTableRowCount(TableIndex.MethodDef))
                calls.Add(md.GetString(md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row)).Name));
            else if ((token >> 24) == 0x0A && row >= 1 && row <= md.GetTableRowCount(TableIndex.MemberRef))
                calls.Add(md.GetString(md.GetMemberReference(MetadataTokens.MemberReferenceHandle(row)).Name));
        }
        return calls.Count > 0 ? "   -> " + string.Join(" ", calls.Distinct()) : "";
    }

    // Real interop methods load their NativeMethodInfoPtr_ field (ldsfld) to call into the game; rebuilt ones don't.
    static bool IsReal(MetadataReader md, PEReader pe, MethodDefinition m)
    {
        if (m.RelativeVirtualAddress == 0) return true; // no body (extern / abstract): not ours to judge
        var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x7E) continue;
            int token = BitConverter.ToInt32(il, i + 1), row = token & 0xFFFFFF;
            string name = null;
            if ((token >> 24) == 0x04 && row >= 1 && row <= md.GetTableRowCount(TableIndex.Field))
                name = md.GetString(md.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(row)).Name);
            else if ((token >> 24) == 0x0A && row >= 1 && row <= md.GetTableRowCount(TableIndex.MemberRef))
                name = md.GetString(md.GetMemberReference(MetadataTokens.MemberReferenceHandle(row)).Name);
            if (name != null && name.StartsWith("NativeMethodInfoPtr_")) return true;
        }
        return false;
    }

    static int Scan(string modPath, string interop)
    {
        using var modPe = new PEReader(File.OpenRead(modPath));
        var mod = modPe.GetMetadataReader();
        var cache = new System.Collections.Generic.Dictionary<string, (PEReader, MetadataReader)>();
        var seen = new System.Collections.Generic.HashSet<string>();
        int rebuilt = 0, real = 0;
        foreach (var rh in mod.MemberReferences)
        {
            var r = mod.GetMemberReference(rh);
            if (r.GetKind() != MemberReferenceKind.Method || r.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = mod.GetTypeReference((TypeReferenceHandle)r.Parent);
            if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            string asm = mod.GetString(mod.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
            string file = Path.Combine(interop, asm + ".dll");
            if (!File.Exists(file)) continue;
            if (!cache.TryGetValue(asm, out var lib)) { var pe = new PEReader(File.OpenRead(file)); lib = (pe, pe.GetMetadataReader()); cache[asm] = lib; }
            var (lpe, md) = lib;
            string ns = mod.GetString(tr.Namespace), tn = mod.GetString(tr.Name), name = mod.GetString(r.Name);
            var want = r.DecodeMethodSignature(new Sig(mod), null);
            string wantSig = string.Join(",", want.ParameterTypes);
            string label = $"{ns}.{tn}.{name}({wantSig})";
            if (!seen.Add(label)) continue;
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if (md.GetString(t.Name) != tn || md.GetString(t.Namespace) != ns) continue;
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (md.GetString(m.Name) != name) continue;
                    if (string.Join(",", m.DecodeSignature(new Sig(md), null).ParameterTypes) != wantSig) continue;
                    if (IsReal(md, lpe, m)) real++;
                    else { rebuilt++; Console.WriteLine("REBUILT " + label); }
                }
            }
        }
        Console.WriteLine($"{real} real, {rebuilt} rebuilt");
        return 0;
    }

    sealed class Sig : ISignatureTypeProvider<string, object>
    {
        readonly MetadataReader md;
        public Sig(MetadataReader md) { this.md = md; }
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => "spec";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[,]";
        public string GetByReferenceType(string e) => "ref " + e;
        public string GetPointerType(string e) => e + "*";
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
        public string GetGenericTypeParameter(object c, int i) => "T" + i;
        public string GetGenericMethodParameter(object c, int i) => "M" + i;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
    }
}
