using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Brovan.SourceGen
{
    /// <summary>
    /// Generates AOT/trimming-safe (no reflection) marshallers for every managed struct that flows through
    /// <c>StructSerializer.WriteStruct/ParseStruct/GetStructSize&lt;T&gt;</c>, plus the structs they nest.
    /// The emitted code mirrors the byte layout of the original reflection-based serializer exactly:
    /// sequential fields written/read consecutively, explicit fields at their <c>[FieldOffset]</c>, the same
    /// <c>[EmulatedInline]</c> byte[]/string/array handling, and the same enum widths.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class StructMarshalGenerator : IIncrementalGenerator
    {
        private const string StructSerializerFqn = "Brovan.Core.Emulation.OS.StructSerializer";
        private const string InlineAttr = "Brovan.Core.Emulation.OS.EmulatedInlineAttribute";
        private const string PointerAttr = "Brovan.Core.Emulation.OS.EmulatedPointerAttribute";
        private const string FieldOffsetAttr = "System.Runtime.InteropServices.FieldOffsetAttribute";
        private const string StructLayoutAttr = "System.Runtime.InteropServices.StructLayoutAttribute";

        private const string WSR = "global::Brovan.Core.Emulation.OS.WriteStructResult";
        private const string WSE = "global::Brovan.Core.Emulation.OS.WriteStructError";

        private static readonly DiagnosticDescriptor UnsupportedField = new(
            id: "BRVGEN001",
            title: "Unsupported struct field for AOT marshalling",
            messageFormat: "Field '{0}' of type '{1}' uses a shape the Brovan struct marshaller generator does not support ({2}). Serialization of '{3}' will be unavailable under AOT.",
            category: "Brovan.SourceGen",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterSourceOutput(context.CompilationProvider, static (spc, comp) => Execute(spc, comp));
        }

        private static void Execute(SourceProductionContext spc, Compilation comp)
        {
            INamedTypeSymbol? serializer = comp.GetTypeByMetadataName(StructSerializerFqn);
            if (serializer is null)
                return;

            // 1) Seed set: every struct used as a type argument of StructSerializer.WriteStruct/ParseStruct/GetStructSize.
            var seeds = new List<INamedTypeSymbol>();
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (SyntaxTree tree in comp.SyntaxTrees)
            {
                SyntaxNode root = tree.GetRoot();
                SemanticModel? model = null;

                foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    // Match by the simple method name, whether or not the type argument is written
                    // explicitly: WriteStruct<T>/ParseStruct<T> usually infer T from the value/out argument,
                    // so the call site has no GenericNameSyntax. The resolved symbol still carries the
                    // (inferred) type argument, which is what we read below.
                    string? name = InvokedName(inv.Expression);
                    if (name != "WriteStruct" && name != "ParseStruct" && name != "GetStructSize")
                        continue;

                    model ??= comp.GetSemanticModel(tree);
                    IMethodSymbol? ms = model.GetSymbolInfo(inv).Symbol as IMethodSymbol
                                        ?? model.GetSymbolInfo(inv).CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (ms is null)
                        continue;
                    if (!SymbolEqualityComparer.Default.Equals(ms.ContainingType, serializer))
                        continue;
                    if (ms.TypeArguments.Length < 1 || ms.TypeArguments[0] is not INamedTypeSymbol nt)
                        continue;
                    if (nt.TypeKind != TypeKind.Struct || !SymbolEqualityComparer.Default.Equals(nt.ContainingAssembly, comp.Assembly))
                        continue;

                    if (seen.Add(nt))
                        seeds.Add(nt);
                }
            }

            // 2) Transitive closure over nested struct fields (by-value structs + inline struct array elements).
            var all = new List<INamedTypeSymbol>();
            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<INamedTypeSymbol>(seeds);
            while (queue.Count > 0)
            {
                INamedTypeSymbol t = queue.Dequeue();
                if (!visited.Add(t))
                    continue;
                all.Add(t);

                foreach (IFieldSymbol f in InstanceFields(t))
                {
                    if (IsUserStruct(f.Type, comp))
                        queue.Enqueue((INamedTypeSymbol)f.Type);
                    else if (f.Type is IArrayTypeSymbol arr && IsUserStruct(arr.ElementType, comp))
                        queue.Enqueue((INamedTypeSymbol)arr.ElementType);
                }
            }

            all.Sort(static (a, b) => string.CompareOrdinal(Fq(a), Fq(b)));

            // 3) Emit.
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable disable");
            sb.AppendLine("namespace Brovan.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    internal static class BrovanGeneratedMarshal");
            sb.AppendLine("    {");

            foreach (INamedTypeSymbol s in all)
                EmitType(sb, s, comp, spc);

            EmitDispatch(sb, all);

            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource("Brovan.Generated.StructMarshal.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        // ---- per-type emission ----------------------------------------------------------------

        private static void EmitType(StringBuilder sb, INamedTypeSymbol s, Compilation comp, SourceProductionContext spc)
        {
            string fq = Fq(s);
            string mangle = Mangle(s);
            bool isExplicit = LayoutKind(s) == 2;
            var fields = InstanceFields(s).ToList();
            if (!isExplicit && fields.Any(HasFieldOffset))
                isExplicit = true;

            var ordered = isExplicit
                ? fields.OrderBy(f => GetFieldOffset(f) ?? 0).ToList()
                : fields;

            var gens = ordered.Select(f => Classify(f, comp, spc, s)).ToList();

            // Size_X(is64)
            sb.AppendLine($"        internal static int Size_{mangle}(bool is64)");
            sb.AppendLine("        {");
            if (isExplicit)
            {
                sb.AppendLine("            int s = 0;");
                for (int i = 0; i < ordered.Count; i++)
                {
                    int off = GetFieldOffset(ordered[i]) ?? 0;
                    sb.AppendLine($"            s = global::System.Math.Max(s, {off} + ({gens[i].SizeExpr}));");
                }
                sb.AppendLine("            return s;");
            }
            else
            {
                sb.Append("            return 0");
                foreach (FieldGen g in gens)
                    sb.Append($" + ({g.SizeExpr})");
                sb.AppendLine(";");
            }
            sb.AppendLine("        }");

            // WriteFields_X
            sb.AppendLine($"        internal static {WSR} WriteFields_{mangle}(global::System.IO.BinaryWriter bw, in {fq} v, bool is64)");
            sb.AppendLine("        {");
            sb.AppendLine("            long __start = bw.BaseStream.Position;");
            for (int i = 0; i < ordered.Count; i++)
            {
                if (isExplicit)
                {
                    int off = GetFieldOffset(ordered[i]) ?? 0;
                    sb.AppendLine($"            if (__start + {off} < bw.BaseStream.Position) return {WSR}.Fail({WSE}.InvalidStructLayout, \"Field {ordered[i].Name} offset {off} is behind the write cursor in {s.Name}\");");
                    sb.AppendLine($"            while (bw.BaseStream.Position < __start + {off}) bw.Write((byte)0);");
                }
                sb.Append(gens[i].WriteCode);
            }
            if (isExplicit)
                sb.AppendLine($"            while (bw.BaseStream.Position < __start + Size_{mangle}(is64)) bw.Write((byte)0);");
            sb.AppendLine($"            return {WSR}.Ok;");
            sb.AppendLine("        }");

            // ReadFields_X
            sb.AppendLine($"        internal static bool ReadFields_{mangle}(byte[] raw, int @base, ref {fq} v, bool is64)");
            sb.AppendLine("        {");
            sb.AppendLine("            int __c = @base;");
            for (int i = 0; i < ordered.Count; i++)
            {
                int off = GetFieldOffset(ordered[i]) ?? 0;
                string offVar = $"__o{i}";
                sb.AppendLine(isExplicit
                    ? $"            int {offVar} = @base + {off};"
                    : $"            int {offVar} = __c;");
                sb.Append(gens[i].ReadCode.Replace("%OFF%", offVar));
                sb.AppendLine($"            __c = {offVar} + ({gens[i].SizeExpr});");
            }
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
        }

        private static void EmitDispatch(StringBuilder sb, List<INamedTypeSymbol> all)
        {
            sb.AppendLine("        internal static bool TryGetSize(global::System.Type t, bool is64, out int size)");
            sb.AppendLine("        {");
            foreach (INamedTypeSymbol s in all)
                sb.AppendLine($"            if (t == typeof({Fq(s)})) {{ size = Size_{Mangle(s)}(is64); return true; }}");
            sb.AppendLine("            size = 0; return false;");
            sb.AppendLine("        }");

            sb.AppendLine($"        internal static bool TryWriteFields(global::System.IO.BinaryWriter bw, object value, global::System.Type t, bool is64, out {WSR} result)");
            sb.AppendLine("        {");
            foreach (INamedTypeSymbol s in all)
                sb.AppendLine($"            if (t == typeof({Fq(s)})) {{ result = WriteFields_{Mangle(s)}(bw, ({Fq(s)})value, is64); return true; }}");
            sb.AppendLine($"            result = {WSR}.Ok; return false;");
            sb.AppendLine("        }");

            sb.AppendLine("        internal static bool TryReadFields(byte[] raw, int @base, ref object value, global::System.Type t, bool is64, out bool ok)");
            sb.AppendLine("        {");
            foreach (INamedTypeSymbol s in all)
            {
                string fq = Fq(s);
                sb.AppendLine($"            if (t == typeof({fq})) {{ {fq} __v = value is {fq} __cv ? __cv : default; ok = ReadFields_{Mangle(s)}(raw, @base, ref __v, is64); value = __v; return true; }}");
            }
            sb.AppendLine("            ok = false; return false;");
            sb.AppendLine("        }");
        }

        // ---- field classification -------------------------------------------------------------

        private sealed class FieldGen
        {
            public string SizeExpr = "0";
            public string WriteCode = "";
            public string ReadCode = ""; // uses %OFF% placeholder for the offset variable
        }

        private static FieldGen Classify(IFieldSymbol f, Compilation comp, SourceProductionContext spc, INamedTypeSymbol owner)
        {
            string name = f.Name;
            ITypeSymbol type = f.Type;
            InlineInfo? inline = GetInline(f);

            if (HasPointer(f))
                return Unsupported(f, owner, spc, "[EmulatedPointer] is not generated (unused in this codebase); add explicit handling if required");

            // Primitive / bool
            if (TryPrimitive(type, out int psize, out string pkw))
            {
                var g = new FieldGen { SizeExpr = psize.ToString() };
                g.WriteCode = "            " + WriteScalar(pkw, $"v.{name}") + "\n";
                g.ReadCode = $"            v.{name} = {ReadScalar(pkw, "raw", "%OFF%")};\n";
                return g;
            }

            // Enum
            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol en && en.EnumUnderlyingType is { } ut && TryPrimitive(ut, out int esize, out string ekw))
            {
                var g = new FieldGen { SizeExpr = esize.ToString() };
                g.WriteCode = $"            bw.Write(({ekw})v.{name});\n";
                g.ReadCode = $"            v.{name} = ({Fq(type)})({ReadScalar(ekw, "raw", "%OFF%")});\n";
                return g;
            }

            // Nested struct by value
            if (IsUserStruct(type, comp))
            {
                string m = Mangle(type);
                var g = new FieldGen { SizeExpr = $"Size_{m}(is64)" };
                g.WriteCode =
                    $"            {{ var __r = WriteFields_{m}(bw, in v.{name}, is64); if (!__r.Success) return __r; }}\n";
                g.ReadCode =
                    $"            {{ {Fq(type)} __n = default; if (!ReadFields_{m}(raw, %OFF%, ref __n, is64)) return false; v.{name} = __n; }}\n";
                return g;
            }

            // string
            if (type.SpecialType == SpecialType.System_String)
            {
                if (inline is { } inf)
                {
                    string enc = inf.Ascii ? "global::System.Text.Encoding.ASCII" : "global::System.Text.Encoding.Unicode";
                    int step = inf.Ascii ? 1 : 2;
                    var g = new FieldGen { SizeExpr = inf.Size.ToString() };
                    g.WriteCode =
                        $"            {{ var __s = v.{name}; byte[] __e = __s != null ? {enc}.GetBytes(__s) : global::System.Array.Empty<byte>(); byte[] __t = new byte[{inf.Size}]; global::System.Buffer.BlockCopy(__e, 0, __t, 0, global::System.Math.Min(__e.Length, {inf.Size})); bw.Write(__t); }}\n";
                    string secondZero = step == 2 ? $" && raw[%OFF% + __len - {step} + 1] == 0" : "";
                    g.ReadCode =
                        $"            {{ int __len = {inf.Size}; while (__len >= {step} && raw[%OFF% + __len - {step}] == 0{secondZero}) __len -= {step}; v.{name} = {enc}.GetString(raw, %OFF%, __len); }}\n";
                    return g;
                }
                // bare string (no attribute): the original serializer writes a null (zero) pointer and reads null.
                var bg = new FieldGen { SizeExpr = "(is64 ? 8 : 4)" };
                bg.WriteCode = $"            if (is64) bw.Write((ulong)0); else bw.Write((uint)0);\n";
                bg.ReadCode = $"            v.{name} = null;\n";
                return bg;
            }

            // Arrays
            if (type is IArrayTypeSymbol arr)
            {
                if (inline is not { } ai)
                    return Unsupported(f, owner, spc, "array without [EmulatedInline] (runtime pointer allocation is not generated)");

                ITypeSymbol et = arr.ElementType;

                // Inline byte[] / sbyte[] -> fixed-size raw bytes.
                if (et.SpecialType == SpecialType.System_Byte || et.SpecialType == SpecialType.System_SByte)
                {
                    var g = new FieldGen { SizeExpr = ai.Size.ToString() };
                    g.WriteCode =
                        $"            {{ byte[] __t = new byte[{ai.Size}]; var __src = v.{name}; if (__src != null && __src.Length > 0) global::System.Buffer.BlockCopy(__src, 0, __t, 0, global::System.Math.Min(__src.Length, {ai.Size})); bw.Write(__t); }}\n";
                    g.ReadCode =
                        $"            {{ byte[] __t = new byte[{ai.Size}]; global::System.Buffer.BlockCopy(raw, %OFF%, __t, 0, {ai.Size}); v.{name} = __t; }}\n";
                    return g;
                }

                // Inline array of structs / primitives / enums.
                string elemSizeExpr;
                string writeElem; // operates on __a[__i]
                string readElem;  // assigns __arr[__i] from raw at __eo
                string elemFq = Fq(et);

                if (IsUserStruct(et, comp))
                {
                    string m = Mangle(et);
                    elemSizeExpr = $"Size_{m}(is64)";
                    writeElem = $"var __r = WriteFields_{m}(bw, in __a[__i], is64); if (!__r.Success) return __r;";
                    readElem = $"{elemFq} __e = default; if (!ReadFields_{m}(raw, __eo, ref __e, is64)) return false; __arr[__i] = __e;";
                }
                else if (et.TypeKind == TypeKind.Enum && et is INamedTypeSymbol een && een.EnumUnderlyingType is { } eut && TryPrimitive(eut, out int elsz, out string elkw))
                {
                    elemSizeExpr = elsz.ToString();
                    writeElem = $"bw.Write(({elkw})__a[__i]);";
                    readElem = $"__arr[__i] = ({elemFq})({ReadScalar(elkw, "raw", "__eo")});";
                }
                else if (TryPrimitive(et, out int elsz2, out string elkw2))
                {
                    elemSizeExpr = elsz2.ToString();
                    writeElem = WriteScalar(elkw2, "__a[__i]");
                    readElem = $"__arr[__i] = {ReadScalar(elkw2, "raw", "__eo")};";
                }
                else
                {
                    return Unsupported(f, owner, spc, $"inline array of unsupported element type '{elemFq}'");
                }

                var ag = new FieldGen { SizeExpr = $"({elemSizeExpr}) * {ai.Size}" };
                ag.WriteCode =
                    $"            {{ var __a = v.{name}; for (int __i = 0; __i < {ai.Size}; __i++) {{ if (__a != null && __i < __a.Length) {{ {writeElem} }} else {{ for (int __z = 0; __z < ({elemSizeExpr}); __z++) bw.Write((byte)0); }} }} }}\n";
                ag.ReadCode =
                    $"            {{ var __arr = new {elemFq}[{ai.Size}]; for (int __i = 0; __i < {ai.Size}; __i++) {{ int __eo = %OFF% + __i * ({elemSizeExpr}); {readElem} }} v.{name} = __arr; }}\n";
                return ag;
            }

            return Unsupported(f, owner, spc, $"unsupported field type '{Fq(type)}'");
        }

        private static FieldGen Unsupported(IFieldSymbol f, INamedTypeSymbol owner, SourceProductionContext spc, string reason)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                UnsupportedField,
                f.Locations.FirstOrDefault(),
                f.Name, f.Type.ToDisplayString(), reason, owner.Name));
            // Emit code that is a no-op but keeps the file compilable (size 0, no bytes).
            return new FieldGen { SizeExpr = "0", WriteCode = "", ReadCode = "" };
        }

        // ---- scalar helpers -------------------------------------------------------------------

        private static bool TryPrimitive(ITypeSymbol t, out int size, out string keyword)
        {
            switch (t.SpecialType)
            {
                case SpecialType.System_Boolean: size = 1; keyword = "bool"; return true;
                case SpecialType.System_Byte: size = 1; keyword = "byte"; return true;
                case SpecialType.System_SByte: size = 1; keyword = "sbyte"; return true;
                case SpecialType.System_Int16: size = 2; keyword = "short"; return true;
                case SpecialType.System_UInt16: size = 2; keyword = "ushort"; return true;
                case SpecialType.System_Int32: size = 4; keyword = "int"; return true;
                case SpecialType.System_UInt32: size = 4; keyword = "uint"; return true;
                case SpecialType.System_Int64: size = 8; keyword = "long"; return true;
                case SpecialType.System_UInt64: size = 8; keyword = "ulong"; return true;
                case SpecialType.System_Single: size = 4; keyword = "float"; return true;
                case SpecialType.System_Double: size = 8; keyword = "double"; return true;
                default: size = 0; keyword = ""; return false;
            }
        }

        private static string WriteScalar(string kw, string valExpr)
        {
            if (kw == "bool")
                return $"bw.Write((byte)({valExpr} ? 1 : 0));";
            return $"bw.Write({valExpr});";
        }

        private static string ReadScalar(string kw, string rawVar, string offExpr)
        {
            switch (kw)
            {
                case "bool": return $"({rawVar}[{offExpr}] != 0)";
                case "byte": return $"{rawVar}[{offExpr}]";
                case "sbyte": return $"(sbyte){rawVar}[{offExpr}]";
                case "short": return $"global::System.BitConverter.ToInt16({rawVar}, {offExpr})";
                case "ushort": return $"global::System.BitConverter.ToUInt16({rawVar}, {offExpr})";
                case "int": return $"global::System.BitConverter.ToInt32({rawVar}, {offExpr})";
                case "uint": return $"global::System.BitConverter.ToUInt32({rawVar}, {offExpr})";
                case "long": return $"global::System.BitConverter.ToInt64({rawVar}, {offExpr})";
                case "ulong": return $"global::System.BitConverter.ToUInt64({rawVar}, {offExpr})";
                case "float": return $"global::System.BitConverter.ToSingle({rawVar}, {offExpr})";
                case "double": return $"global::System.BitConverter.ToDouble({rawVar}, {offExpr})";
                default: return "default";
            }
        }

        // ---- symbol helpers -------------------------------------------------------------------

        private static IEnumerable<IFieldSymbol> InstanceFields(INamedTypeSymbol t)
            => t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared);

        private static bool IsUserStruct(ITypeSymbol t, Compilation comp)
            => t.TypeKind == TypeKind.Struct
               && t.SpecialType == SpecialType.None
               && t is INamedTypeSymbol
               && SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, comp.Assembly);

        private static int LayoutKind(INamedTypeSymbol t)
        {
            foreach (AttributeData a in t.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() == StructLayoutAttr && a.ConstructorArguments.Length >= 1)
                    return Convert.ToInt32(a.ConstructorArguments[0].Value);
            }
            return 0; // Sequential
        }

        private static bool HasFieldOffset(IFieldSymbol f) => GetFieldOffset(f).HasValue;

        private static int? GetFieldOffset(IFieldSymbol f)
        {
            foreach (AttributeData a in f.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() == FieldOffsetAttr && a.ConstructorArguments.Length >= 1)
                    return Convert.ToInt32(a.ConstructorArguments[0].Value);
            }
            return null;
        }

        private sealed class InlineInfo
        {
            public int Size;
            public bool Ascii;
        }

        private static InlineInfo? GetInline(IFieldSymbol f)
        {
            foreach (AttributeData a in f.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() != InlineAttr)
                    continue;
                var info = new InlineInfo();
                if (a.ConstructorArguments.Length >= 1)
                    info.Size = Convert.ToInt32(a.ConstructorArguments[0].Value);
                foreach (KeyValuePair<string, TypedConstant> na in a.NamedArguments)
                    if (na.Key == "Ascii" && na.Value.Value is bool b)
                        info.Ascii = b;
                return info;
            }
            return null;
        }

        private static bool HasPointer(IFieldSymbol f)
            => f.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PointerAttr);

        private static string? InvokedName(ExpressionSyntax expr)
        {
            // Handles both StructSerializer.Method(...) and Method(...) and explicit Method<T>(...).
            return expr switch
            {
                MemberAccessExpressionSyntax m => m.Name is GenericNameSyntax mg ? mg.Identifier.Text : (m.Name as IdentifierNameSyntax)?.Identifier.Text,
                GenericNameSyntax g => g.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null,
            };
        }

        private static readonly SymbolDisplayFormat FqFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        private static string Fq(ITypeSymbol t) => t.ToDisplayString(FqFormat);

        private static string Mangle(ITypeSymbol t)
        {
            string s = t.ToDisplayString(FqFormat);
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
