using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Tiger.Schema.Shaders;

// Post-processes spirv-cross HLSL output (from gcn2hlsl.exe) to make it
// readable for human inspection of the exported PS_<hash>.hlsl files.
//
// Three passes:
//   1. Inject a comment header listing resource bindings (from the .spv.meta.json
//      sidecar), PS-input semantic classification, and a cbuffer summary.
//   2. Reconstruct constant buffers from ByteAddressBuffer ssbo_N.Load(K) sites
//      with literal K: cluster offsets per buffer, emit a cbuffer block with
//      float4 packoffsets, rewrite loads to cb<N>_v<V>.<swizzle>.
//   3. Annotate `static float4 fs_in_attrN;` declarations with the VS-output
//      semantic (Uv / Normal / Tangent / ...) from ClassifySpirvVsOutputs.
//
// The transformed HLSL is written only to disk (export dir). The raw
// spirv-cross text continues to feed HlslToUsfV2_FromSpirvCross via the cached
// Decompile() return, so the USF converter is not affected.
public static class HlslAnnotator
{
    public static string Annotate(string pixelHlsl, FileHash psHash, string vsHlsl)
    {
        if (string.IsNullOrEmpty(pixelHlsl))
            return pixelHlsl;

        var meta = LoadMeta(psHash);
        var vsClass = string.IsNullOrEmpty(vsHlsl)
            ? new Dictionary<int, UsfConverter.VsAttrSemantic>()
            : UsfConverter.ClassifySpirvVsOutputs(vsHlsl);

        // Pass 2 first — it determines which cbuffers exist so the header can
        // describe them. Then inject the header, then annotate fs_in_attrN.
        var cbufferInfo = ReconstructCbuffers(ref pixelHlsl);
        pixelHlsl = AnnotateFragInputs(pixelHlsl, vsClass);
        pixelHlsl = InjectHeader(pixelHlsl, psHash, meta, cbufferInfo, vsClass);

        return pixelHlsl;
    }

    // ─────────────────────────── meta.json loading ───────────────────────────

    private sealed class ShaderMeta
    {
        public List<SlotEntry> Slots { get; init; } = new();
        public List<TableEntry> TableBindings { get; init; } = new();
        public bool Loaded { get; init; }
        public string? SourcePath { get; init; }
    }

    private record SlotEntry(int UsageType, string UsageName, int ApiSlot, int StartRegister, int Flags);
    private record TableEntry(string SpvName, int TableRootSgpr, int TableDwordOffset, int SpvBinding);

    private static ShaderMeta LoadMeta(FileHash psHash)
    {
        string metaPath = $"hlsl_temp/ps{psHash}.spv.meta.json";
        if (!File.Exists(metaPath))
            return new ShaderMeta { Loaded = false };

        try
        {
            var json = JObject.Parse(File.ReadAllText(metaPath));
            var slots = new List<SlotEntry>();
            if (json["slots"] is JArray slotArr)
            {
                foreach (var s in slotArr)
                {
                    slots.Add(new SlotEntry(
                        UsageType: (int?)s["usage_type"] ?? 0,
                        UsageName: (string?)s["usage_name"] ?? "?",
                        ApiSlot: (int?)s["api_slot"] ?? 0,
                        StartRegister: (int?)s["start_register"] ?? 0,
                        Flags: (int?)s["flags"] ?? 0));
                }
            }
            var tables = new List<TableEntry>();
            if (json["table_bindings"] is JArray tbArr)
            {
                foreach (var t in tbArr)
                {
                    tables.Add(new TableEntry(
                        SpvName: (string?)t["spv_name"] ?? "?",
                        TableRootSgpr: (int?)t["table_root_sgpr"] ?? 0,
                        TableDwordOffset: (int?)t["table_dword_offset"] ?? 0,
                        SpvBinding: (int?)t["spv_binding"] ?? 0));
                }
            }
            return new ShaderMeta { Slots = slots, TableBindings = tables, Loaded = true, SourcePath = metaPath };
        }
        catch
        {
            return new ShaderMeta { Loaded = false };
        }
    }

    // ─────────────────────────── cbuffer reconstruction ───────────────────────────

    private sealed class CbufferInfo
    {
        public int Index;
        public int RegisterT;
        public int MaxVecIndex;
        public int LoadSiteCount;
    }

    private static readonly Regex RxSsboDecl = new(
        @"^\s*ByteAddressBuffer\s+ssbo_(\d+)\s*:\s*register\(t(\d+)(?:,\s*space\d+)?\)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Match `asfloat( ssbo_N.Load( <literal> ) )` — all single-dword, literal
    // offset, with optional whitespace. Non-literal offsets are left untouched.
    private static readonly Regex RxAsfloatSsboLoad = new(
        @"asfloat\s*\(\s*ssbo_(\d+)\s*\.\s*Load\s*\(\s*(\d+)\s*\)\s*\)",
        RegexOptions.Compiled);

    // Bare `ssbo_N.Load(<literal>)` — used when the GCN code consumed the
    // dword as uint directly (no asfloat wrap). Rewrite with asuint().
    private static readonly Regex RxBareSsboLoad = new(
        @"\bssbo_(\d+)\s*\.\s*Load\s*\(\s*(\d+)\s*\)",
        RegexOptions.Compiled);

    private static List<CbufferInfo> ReconstructCbuffers(ref string hlsl)
    {
        var declMatches = RxSsboDecl.Matches(hlsl);
        if (declMatches.Count == 0)
            return new List<CbufferInfo>();

        var infos = new Dictionary<int, CbufferInfo>();
        foreach (Match m in declMatches)
        {
            int ssboIdx = int.Parse(m.Groups[1].Value);
            int regT = int.Parse(m.Groups[2].Value);
            infos[ssboIdx] = new CbufferInfo { Index = ssboIdx, RegisterT = regT, MaxVecIndex = -1 };
        }

        // Scan all literal-offset loads (asfloat-wrapped and bare). Track the
        // highest vec4 index referenced per ssbo to size the cbuffer block.
        foreach (Match m in RxAsfloatSsboLoad.Matches(hlsl))
            TrackLoad(infos, m.Groups[1].Value, m.Groups[2].Value);
        foreach (Match m in RxBareSsboLoad.Matches(hlsl))
            TrackLoad(infos, m.Groups[1].Value, m.Groups[2].Value);

        // Rewrite asfloat-wrapped loads: the wrapper becomes part of the
        // substitution because cb<N>_v<V>.<c> is already float-typed.
        hlsl = RxAsfloatSsboLoad.Replace(hlsl, m =>
        {
            int idx = int.Parse(m.Groups[1].Value);
            int off = int.Parse(m.Groups[2].Value);
            if (!infos.ContainsKey(idx)) return m.Value;
            return FormatCbRef(idx, off);
        });
        // Rewrite bare loads with an asuint() wrapper (the cb block is float4).
        hlsl = RxBareSsboLoad.Replace(hlsl, m =>
        {
            int idx = int.Parse(m.Groups[1].Value);
            int off = int.Parse(m.Groups[2].Value);
            if (!infos.ContainsKey(idx)) return m.Value;
            return $"asuint({FormatCbRef(idx, off)})";
        });

        // Replace each `ByteAddressBuffer ssbo_N : register(tN, space0);`
        // declaration with a reconstructed cbuffer block. Buffers with no
        // literal-offset loads get size 1 (preserves declaration without
        // over-padding).
        hlsl = RxSsboDecl.Replace(hlsl, m =>
        {
            int idx = int.Parse(m.Groups[1].Value);
            int regT = int.Parse(m.Groups[2].Value);
            if (!infos.TryGetValue(idx, out var info)) return m.Value;
            int vecCount = Math.Max(1, info.MaxVecIndex + 1);
            return BuildCbufferBlock(idx, regT, vecCount);
        });

        // The cbuffer info list — sorted by ssbo index for stable header output.
        var ordered = new List<CbufferInfo>();
        foreach (var kv in infos)
            ordered.Add(kv.Value);
        ordered.Sort((a, b) => a.Index.CompareTo(b.Index));
        return ordered;
    }

    private static void TrackLoad(Dictionary<int, CbufferInfo> infos, string idxStr, string offStr)
    {
        int idx = int.Parse(idxStr);
        int off = int.Parse(offStr);
        if (!infos.TryGetValue(idx, out var info)) return;
        int vec = off / 16;
        if (vec > info.MaxVecIndex) info.MaxVecIndex = vec;
        info.LoadSiteCount++;
    }

    private static string FormatCbRef(int ssboIdx, int byteOffset)
    {
        int vec = byteOffset / 16;
        int comp = (byteOffset % 16) / 4;
        char swiz = "xyzw"[comp];
        return $"cb{ssboIdx}_v{vec}.{swiz}";
    }

    private static string BuildCbufferBlock(int ssboIdx, int registerT, int vecCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"// Reconstructed from ByteAddressBuffer ssbo_{ssboIdx} — {vecCount} float4 slot(s) referenced");
        sb.AppendLine($"cbuffer cb{ssboIdx} : register(b{ssboIdx})");
        sb.AppendLine("{");
        for (int v = 0; v < vecCount; v++)
            sb.AppendLine($"    float4 cb{ssboIdx}_v{v} : packoffset(c{v});");
        sb.Append("};");
        return sb.ToString();
    }

    // ─────────────────────────── fs_in_attr semantic comments ───────────────────────────

    private static readonly Regex RxFragInputDecl = new(
        @"^(?<indent>\s*)static\s+float4\s+fs_in_attr(?<idx>\d+)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string AnnotateFragInputs(string hlsl, Dictionary<int, UsfConverter.VsAttrSemantic> vsClass)
    {
        if (vsClass == null || vsClass.Count == 0)
            return hlsl;

        return RxFragInputDecl.Replace(hlsl, m =>
        {
            int idx = int.Parse(m.Groups["idx"].Value);
            if (!vsClass.TryGetValue(idx, out var sem) || sem == UsfConverter.VsAttrSemantic.Unknown)
                return m.Value;
            string label = SemanticLabel(sem);
            return $"{m.Groups["indent"].Value}static float4 fs_in_attr{idx}; // TEXCOORD{idx} → {label}";
        });
    }

    private static string SemanticLabel(UsfConverter.VsAttrSemantic sem) => sem switch
    {
        UsfConverter.VsAttrSemantic.Uv => "TexCoord (UV)",
        UsfConverter.VsAttrSemantic.Normal => "Normal",
        UsfConverter.VsAttrSemantic.Tangent => "Tangent",
        UsfConverter.VsAttrSemantic.Bitangent => "Bitangent (N×T)",
        UsfConverter.VsAttrSemantic.WorldPos => "WorldPosition",
        UsfConverter.VsAttrSemantic.VertexColor => "VertexColor",
        UsfConverter.VsAttrSemantic.InstanceData => "InstanceData",
        _ => "unknown",
    };

    // ─────────────────────────── header injection ───────────────────────────

    private static string InjectHeader(string hlsl, FileHash psHash, ShaderMeta meta,
        List<CbufferInfo> cbuffers, Dictionary<int, UsfConverter.VsAttrSemantic> vsClass)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ═════════════════════════════════════════════════════════════════════");
        sb.AppendLine($"// Annotated by Charm HlslAnnotator for D1 pixel shader PS_{psHash}");
        sb.AppendLine("// Source: spirv-cross over shadPS4-recompiled PS4 GCN bytecode.");
        sb.AppendLine("// ═════════════════════════════════════════════════════════════════════");

        // Resource bindings from meta.slots (ImmResource / ImmSampler / ImmConstBuffer / Ptr*).
        if (meta.Loaded && meta.Slots.Count > 0)
        {
            sb.AppendLine("//");
            sb.AppendLine("// Resource bindings (OrbShdr input_usage_slots):");
            foreach (var slot in meta.Slots)
            {
                string kind = slot.UsageName;
                sb.AppendLine(string.Format(
                    "//   [{0,-20}] api_slot={1,-3} sgpr={2,-3} flags=0x{3:X2}",
                    kind, slot.ApiSlot, slot.StartRegister, slot.Flags));
            }
        }

        // Table bindings (resource-table indirection — each fs_img_t<R>_o<O>).
        if (meta.Loaded && meta.TableBindings.Count > 0)
        {
            sb.AppendLine("//");
            sb.AppendLine("// Resource-table textures (each maps to a distinct HLSL image):");
            var ordered = meta.TableBindings
                .OrderBy(t => t.TableDwordOffset)
                .ToList();
            foreach (var t in ordered)
            {
                sb.AppendLine(string.Format(
                    "//   {0,-24} table_sgpr={1,-3} dword_off={2,-4} spv_binding={3}",
                    t.SpvName, t.TableRootSgpr, t.TableDwordOffset, t.SpvBinding));
            }
        }

        if (!meta.Loaded)
        {
            sb.AppendLine("//");
            sb.AppendLine("// (no .spv.meta.json sidecar found — resource binding details unavailable)");
        }

        // PS input semantics from VS classification.
        sb.AppendLine("//");
        if (vsClass != null && vsClass.Count > 0)
        {
            sb.AppendLine("// PS input semantics (derived from paired VS output classification):");
            var classified = vsClass
                .Where(kv => kv.Value != UsfConverter.VsAttrSemantic.Unknown)
                .OrderBy(kv => kv.Key)
                .ToList();
            if (classified.Count == 0)
            {
                sb.AppendLine("//   (VS present but no slots classified — shader may not use interpolants)");
            }
            else
            {
                foreach (var kv in classified)
                    sb.AppendLine($"//   fs_in_attr{kv.Key,-2} = TEXCOORD{kv.Key,-2} → {SemanticLabel(kv.Value)}");
            }
        }
        else
        {
            sb.AppendLine("// PS input semantics: (no VS HLSL available — attributes un-classified)");
        }

        // Reconstructed cbuffer summary.
        if (cbuffers.Count > 0)
        {
            sb.AppendLine("//");
            sb.AppendLine("// Reconstructed cbuffers (inferred from ssbo_N.Load(K) literal offsets):");
            foreach (var cb in cbuffers)
            {
                if (cb.LoadSiteCount == 0)
                {
                    sb.AppendLine($"//   cb{cb.Index} : register(b{cb.Index})  (no literal loads — size=1, declaration only)");
                }
                else
                {
                    int vecs = cb.MaxVecIndex + 1;
                    sb.AppendLine($"//   cb{cb.Index} : register(b{cb.Index})  {vecs,3} vec4s ({vecs * 16,4} bytes), {cb.LoadSiteCount} load site(s)");
                }
            }
        }

        sb.AppendLine("// ═════════════════════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.Append(hlsl);
        return sb.ToString();
    }
}
