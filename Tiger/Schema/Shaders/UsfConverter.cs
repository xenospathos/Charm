using System.Text;
using System.Text.RegularExpressions;
using Tiger.Schema.Shaders;

namespace Tiger.Schema;

public class UsfConverter
{
    private struct TextureView
    {
        public string Dimension;
        public string Type;
        public string Variable;
        public int Index;
    }

    private struct Cbuffer
    {
        public string Variable;
        public string Type;
        public int Count;
        public int Index;
    }

    private struct Input
    {
        public string Variable;
        public string Type;
        public int Index;
        public string Semantic;
    }

    private struct Output
    {
        public string Variable;
        public string Type;
        public int Index;
        public string Semantic;
    }

    private StringReader hlsl;
    private StringBuilder usf;
    private bool bOpacityEnabled = false;
    private readonly List<TextureView> textures = new List<TextureView>();
    private readonly List<int> samplers = new List<int>();
    private readonly List<Cbuffer> cbuffers = new List<Cbuffer>();
    private readonly List<Input> inputs = new List<Input>();
    private readonly List<Output> outputs = new List<Output>();

    public string HlslToUsf(Material material, bool bIsVertexShader)
    {
        hlsl = new StringReader(material.Pixel.Shader.Decompile($"ps{material.Pixel.Shader.Hash}"));
        usf = new StringBuilder();
        bOpacityEnabled = false;
        ProcessHlslData();
        if (bOpacityEnabled)
        {
            usf.AppendLine("// masked");
        }
        // WriteTextureComments(material, bIsVertexShader);
        WriteCbuffers(material, bIsVertexShader);
        WriteFunctionDefinition(bIsVertexShader);
        bool success = ConvertInstructions();
        if (!success)
        {
            return "";
        }

        if (!bIsVertexShader)
        {
            AddOutputs();
        }

        WriteFooter(bIsVertexShader);

        if (!bIsVertexShader)
        {
            return PostProcessUsf(usf.ToString());
        }

        return usf.ToString();
    }

    private void ProcessHlslData()
    {
        string line = string.Empty;
        bool bFindOpacity = false;
        do
        {
            line = hlsl.ReadLine();
            if (line != null)
            {
                if (line.Contains("r0,r1")) // at end of function definition
                {
                    bFindOpacity = true;
                }

                if (bFindOpacity)
                {
                    if (line.Contains("discard"))
                    {
                        bOpacityEnabled = true;
                        break;
                    }
                    continue;
                }

                if (line.Contains("Texture"))
                {
                    TextureView texture = new TextureView();
                    texture.Dimension = line.Split("<")[0];
                    texture.Type = line.Split("<")[1].Split(">")[0];
                    texture.Variable = line.Split("> ")[1].Split(" :")[0];
                    texture.Index = Int32.TryParse(new string(texture.Variable.Skip(1).ToArray()), out int index) ? index : -1;
                    textures.Add(texture);
                }
                else if (line.Contains("SamplerState"))
                {
                    samplers.Add(line.Split("(")[1].Split(")")[0].Last() - 48);
                }
                else if (line.Contains("cbuffer"))
                {
                    hlsl.ReadLine();
                    line = hlsl.ReadLine();
                    Cbuffer cbuffer = new Cbuffer();
                    cbuffer.Variable = "cb" + line.Split("cb")[1].Split("[")[0];
                    cbuffer.Index = Int32.TryParse(new string(cbuffer.Variable.Skip(2).ToArray()), out int index) ? index : -1;
                    cbuffer.Count = Int32.TryParse(new string(line.Split("[")[1].Split("]")[0]), out int count) ? count : -1;
                    cbuffer.Type = line.Split("cb")[0].Trim();
                    cbuffers.Add(cbuffer);
                }
                else if (line.Contains(" v") && line.Contains(" : ") && !line.Contains("?"))
                {
                    Input input = new Input();
                    input.Variable = "v" + line.Split("v")[1].Split(" : ")[0];
                    input.Index = Int32.TryParse(new string(input.Variable.Skip(1).ToArray()), out int index) ? index : -1;
                    input.Semantic = line.Split(" : ")[1].Split(",")[0];
                    input.Type = line.Split(" v")[0].Trim();
                    inputs.Add(input);
                }
                else if (line.Contains("out") && line.Contains(" : "))
                {
                    Output output = new Output();
                    output.Variable = "o" + line.Split(" o")[2].Split(" : ")[0];
                    output.Index = Int32.TryParse(new string(output.Variable.Skip(1).ToArray()), out int index) ? index : -1;
                    output.Semantic = line.Split(" : ")[1].Split(",")[0];
                    output.Type = line.Split("out ")[1].Split(" o")[0];
                    outputs.Add(output);
                }
            }

        } while (line != null);
    }

    private void WriteCbuffers(Material material, bool bIsVertexShader)
    {
        // Try to find matches, pixel shader has Unk2D0 Unk2E0 Unk2F0 Unk300 available
        foreach (var cbuffer in cbuffers)
        {
            if (bIsVertexShader)
                usf.AppendLine($"static {cbuffer.Type} {cbuffer.Variable}[{cbuffer.Count}] = ").AppendLine("{");
            else
                usf.AppendLine($"static {cbuffer.Type} {cbuffer.Variable}[{cbuffer.Count}] = ").AppendLine("{");

            dynamic data = null;
            if (bIsVertexShader)
            {
                data = material.Vertex.GetCBuffer0();
            }
            else
            {
                data = material.Pixel.GetCBuffer0();
            }


            for (int i = 0; i < cbuffer.Count; i++)
            {
                switch (cbuffer.Type)
                {
                    case "float4":
                        if (bIsVertexShader)
                        {
                            if (data == null)
                            {
                                usf.AppendLine("    float4(1.0, 1.0, 1.0, 1.0),");
                            }
                            break;
                        }

                        if (data == null)
                        {
                            usf.AppendLine("    float4(0.0, 0.0, 0.0, 0.0),");
                        }
                        else
                        {
                            try
                            {
                                if (data[i] is Vector4)
                                {
                                    usf.AppendLine($"    float4({data[i].X}, {data[i].Y}, {data[i].Z}, {data[i].W}),");
                                }
                                else
                                {
                                    var x = data[i].Unk00.X; // really bad but required
                                    usf.AppendLine($"    float4({x}, {data[i].Unk00.Y}, {data[i].Unk00.Z}, {data[i].Unk00.W}),");
                                }
                            }
                            catch (Exception e)  // figure out whats up here, taniks breaks it
                            {
                                if (bIsVertexShader)
                                {
                                    usf.AppendLine("    float4(1.0, 1.0, 1.0, 1.0),");
                                }
                                else
                                    usf.AppendLine("    float4(0.0, 0.0, 0.0, 0.0),");
                            }
                        }
                        break;
                    case "float3":
                        if (bIsVertexShader)
                        {
                            if (data == null)
                            {
                                usf.AppendLine("    float3(1.0, 1.0, 1.0),");
                            }
                            break;
                        }
                        if (data == null) usf.AppendLine("    float3(0.0, 0.0, 0.0),");
                        else usf.AppendLine($"    float3({data[i].Unk00.X}, {data[i].Unk00.Y}, {data[i].Unk00.Z}),");
                        break;
                    case "float":
                        if (bIsVertexShader)
                        {
                            if (data == null)
                            {
                                usf.AppendLine("    float(1.0),");
                            }
                            break;
                        }
                        if (data == null) usf.AppendLine("    float(0.0),");
                        else usf.AppendLine($"    float4({data[i].Unk00}),");
                        break;
                    default:
                        throw new NotImplementedException();
                }
            }

            usf.AppendLine("};");
        }
    }

    private void WriteFunctionDefinition(bool bIsVertexShader)
    {
        if (!bIsVertexShader)
        {
            foreach (var i in inputs)
            {
                if (i.Type == "float4")
                {
                    usf.AppendLine($"static {i.Type} {i.Variable} = " + "{1, 1, 1, 1};\n");
                }
                else if (i.Type == "float3")
                {
                    usf.AppendLine($"static {i.Type} {i.Variable} = " + "{1, 1, 1};\n");
                }
                else if (i.Type == "uint")
                {
                    usf.AppendLine($"static {i.Type} {i.Variable} = " + "1;\n");
                }
            }
        }
        usf.AppendLine("#define cmp -").AppendLine("struct shader {");
        if (bIsVertexShader)
        {
            foreach (var output in outputs)
            {
                usf.AppendLine($"{output.Type} {output.Variable};");
            }

            usf.AppendLine().AppendLine("void main(");
            foreach (var texture in textures)
            {
                usf.AppendLine($"   {texture.Type} {texture.Variable},");
            }
            for (var i = 0; i < inputs.Count; i++)
            {
                if (i == inputs.Count - 1)
                {
                    usf.AppendLine($"   {inputs[i].Type} {inputs[i].Variable}) // {inputs[i].Semantic}");
                }
                else
                {
                    usf.AppendLine($"   {inputs[i].Type} {inputs[i].Variable}, // {inputs[i].Semantic}");
                }
            }
        }
        else
        {
            usf.AppendLine("FMaterialAttributes main(");
            foreach (var texture in textures)
            {
                usf.AppendLine($"   {texture.Type} {texture.Variable},");
            }

            usf.AppendLine($"   float2 tx,");
            usf.AppendLine($"   float3 viewDir,");
            usf.AppendLine($"   float3 vc,");
            usf.AppendLine($"   float vcw)");

            usf.AppendLine("{").AppendLine("    FMaterialAttributes output;");
            // Output render targets
            usf.AppendLine("    float4 o0,o1,o2;");
            // Map v-registers to actual material inputs
            // D2 pixel shader vertex layout:
            //   v0 = tangent Z (normal up), v1 = tangent X, v2 = tangent Y
            //   v3 = texcoord, v4 = view direction, v5 = vertex color
            foreach (var i in inputs)
            {
                switch (i.Index)
                {
                    case 0 when i.Type == "float4":
                        usf.AppendLine("        float4 v0 = {0,0,1,1};");
                        break;
                    case 1 when i.Type == "float4":
                        usf.AppendLine("        float4 v1 = {1,0,0,1};");
                        break;
                    case 2 when i.Type == "float4":
                        usf.AppendLine("        float4 v2 = {0,1,0,1};");
                        break;
                    case 3 when i.Type == "float4":
                        usf.AppendLine("        float4 v3 = {tx.xy, 1,1};");
                        break;
                    case 4 when i.Type == "float4":
                        usf.AppendLine("        float4 v4 = {viewDir.xyz,1};");
                        break;
                    case 4 when i.Type == "float3":
                        usf.AppendLine("        float3 v4 = viewDir.xyz;");
                        break;
                    case 5 when i.Type == "float4":
                        usf.AppendLine("        float4 v5 = {vc.xyz, vcw};");
                        break;
                    default:
                        if (i.Type == "uint")
                            usf.AppendLine($"    {i.Variable}.x = {i.Variable}.x;");
                        break;
                }
            }
        }
    }

    private bool ConvertInstructions()
    {
        Dictionary<int, TextureView> texDict = new();
        foreach (var texture in textures)
        {
            texDict.Add(texture.Index, texture);
        }
        List<int> sortedIndices = texDict.Keys.OrderBy(x => x).ToList();
        string line = hlsl.ReadLine();
        if (line == null)
        {
            // its a broken pixel shader that uses some kind of memory textures
            return false;
        }
        while (!line.Contains("SV_TARGET2"))
        {
            line = hlsl.ReadLine();
            if (line == null)
            {
                // its a broken pixel shader that uses some kind of memory textures
                return false;
            }
        }
        hlsl.ReadLine();
        do
        {
            line = hlsl.ReadLine();
            if (line != null)
            {
                if (line.Contains("return;"))
                {
                    break;
                }
                if (line.Contains("Sample"))
                {
                    var equal = line.Split("=")[0];
                    var texIndex = Int32.Parse(line.Split(".Sample")[0].Split("t")[1]);
                    var sampleIndex = Int32.Parse(line.Split("(s")[1].Split("_s,")[0]);
                    var sampleUv = line.Split(", ")[1].Split(")")[0];
                    var dotAfter = line.Split(").")[1];
                    // todo add dimension
                    usf.AppendLine($"   {equal}= Material_Texture2D_{sortedIndices.IndexOf(texIndex)}.SampleLevel(Material_Texture2D_{sampleIndex - 1}Sampler, {sampleUv}, 0).{dotAfter}");
                }
                // todo add load, levelofdetail, o0.w
                else if (line.Contains("discard"))
                {
                    // Skip discard lines entirely — opacity is handled via o0.w in AddOutputs()
                }
                else
                {
                    usf.AppendLine(line);
                }
            }
        } while (line != null);

        return true;
    }

    private void AddOutputs()
    {
        string opacityMask = bOpacityEnabled ? "output.OpacityMask = o0.w;" : "output.OpacityMask = 1;";

        string outputString = $@"
        ///RT0
        output.BaseColor = o0.xyz; // Albedo

        ///RT1

        // Normal
        float3 biased_normal = o1.xyz - float3(0.5, 0.5, 0.5);
        float normal_length = length(biased_normal);
        float3 normal_in_world_space = biased_normal / normal_length;
        normal_in_world_space.z = sqrt(1.0 - saturate(dot(normal_in_world_space.xy, normal_in_world_space.xy)));
        output.Normal = normalize((normal_in_world_space * 2 - 1.35)*0.5 + 0.5);

        // Roughness
        float smoothness = saturate(8 * (normal_length - 0.375));
        output.Roughness = 1 - smoothness;

        ///RT2
        output.Metallic = saturate(o2.x);
        output.EmissiveColor = clamp((o2.y - 0.5) * 2 * 5 * output.BaseColor, 0, 100);  // the *5 is a scale to make it look good
        output.AmbientOcclusion = saturate(o2.y * 2); // Texture AO

        {opacityMask}

        return output;
        ";
        usf.AppendLine(outputString);
    }

    private void WriteFooter(bool bIsVertexShader)
    {
        usf.AppendLine("}").AppendLine("};");
        if (!bIsVertexShader)
        {
            usf.AppendLine("shader s;").AppendLine($"return s.main({String.Join(',', textures.Select(x => x.Variable))},tx,viewDir,vc,vcw);");
        }
    }

    // ─── Post-processing: strip LOD textures, prune signature ────────────

    private string PostProcessUsf(string usfText)
    {
        var lines = usfText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var allNums = new SortedSet<int>();
        foreach (Match m in Regex.Matches(usfText, @"Material_Texture2D_(\d+)\."))
            allNums.Add(int.Parse(m.Groups[1].Value));

        var tParamOrder = ExtractTParamOrder(lines);

        if (allNums.Count > 0)
        {
            var (maxBase, lodOnlyTextures, virtualTextures) = ClassifyTextures(lines);

            if (lodOnlyTextures.Count > 0)
            {
                lines = RemoveLodBlocks(lines, maxBase);

                foreach (int vt in virtualTextures)
                {
                    var pat = new Regex($@"Material_Texture2D_{vt}\.");
                    lines = lines.Where(l => !pat.IsMatch(l)).ToList();
                }

                string remaining = string.Join("\n", lines);
                var mtdUsed = new HashSet<int>();
                foreach (Match m in Regex.Matches(remaining, @"Material_Texture2D_(\d+)\."))
                    mtdUsed.Add(int.Parse(m.Groups[1].Value));

                var tKeep = MtdKeepToTKeep(tParamOrder, mtdUsed);
                var renameMap = ComputeRenameMap(tParamOrder, tKeep);

                lines = RemoveOrphanedCmps(lines);
                lines = UpdateSignature(lines, tKeep, renameMap);
                lines = UpdateReturn(lines, tKeep, renameMap);
            }
        }

        // Final cleanup: ensure signature/return t-params match actual MTD usage
        {
            string remainingText = string.Join("\n", lines);
            var mtdFinal = new HashSet<int>();
            foreach (Match m in Regex.Matches(remainingText, @"Material_Texture2D_(\d+)(?:\.|Sampler)"))
                mtdFinal.Add(int.Parse(m.Groups[1].Value));

            var tOrderFinal = ExtractTParamOrder(lines);
            var tKeepFinal = new HashSet<int>();
            for (int pos = 0; pos < tOrderFinal.Count; pos++)
            {
                if (mtdFinal.Contains(pos))
                    tKeepFinal.Add(tOrderFinal[pos]);
            }

            var renameFinal = ComputeRenameMap(tOrderFinal, tKeepFinal);
            if (!tKeepFinal.SetEquals(new HashSet<int>(tOrderFinal)))
            {
                lines = UpdateSignature(lines, tKeepFinal, renameFinal);
            }
            lines = UpdateReturn(lines, tKeepFinal, renameFinal);
        }

        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Measures the length (in lines) of an if-block starting at the given index.
    /// Returns null if the line at start is not an if-with-brace pattern.
    /// </summary>
    private static int? CollectIfBlockLength(List<string> lines, int start)
    {
        string stripped = lines[start].Trim();
        if (!Regex.IsMatch(stripped, @"^if\s*\(") || !stripped.Contains("{"))
            return null;

        int depth = 0;
        for (int i = start; i < lines.Count; i++)
        {
            depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            if (depth == 0)
                return i - start + 1;
        }
        return null;
    }

    /// <summary>
    /// Returns true if a block contains only LOD-related instructions:
    /// SampleLevel calls, cmp comparisons, register-to-register moves,
    /// if/else braces, and whitespace.
    /// </summary>
    private static bool IsLodOnlyBlock(List<string> blockLines)
    {
        foreach (string line in blockLines)
        {
            string s = line.Trim();
            if (s == "" || s == "{" || s == "}")
                continue;
            if (Regex.IsMatch(s, @"^\}\s*else\s*\{"))
                continue;
            if (Regex.IsMatch(s, @"^if\s*\("))
                continue;
            if (s.Contains(".SampleLevel("))
                continue;
            if (Regex.IsMatch(s, @"=\s*cmp\("))
                continue;
            if (Regex.IsMatch(s, @"^r\d+\.\w+\s*=\s*r\d+\.\w+\s*;"))
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Classifies Material_Texture2D indices as base (needed), LOD-only (removable),
    /// or virtual (referenced after LOD range, also removable).
    /// Returns (maxBaseIndex, lodOnlySet, virtualTexturesList).
    /// maxBaseIndex is -1 if no LOD textures found.
    /// </summary>
    private static (int maxBase, HashSet<int> lodOnly, List<int> virtualTextures) ClassifyTextures(List<string> lines)
    {
        // Find all LOD-only if-block ranges
        var lodRanges = new List<(int start, int end)>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i].Trim(), @"^if\s*\(") && lines[i].Contains("{"))
            {
                int? length = CollectIfBlockLength(lines, i);
                if (length != null && IsLodOnlyBlock(lines.GetRange(i, length.Value)))
                {
                    lodRanges.Add((i, i + length.Value));
                }
            }
        }

        var outsideTextures = new HashSet<int>();
        var lodOnlyTextures = new HashSet<int>();

        for (int i = 0; i < lines.Count; i++)
        {
            foreach (Match m in Regex.Matches(lines[i], @"Material_Texture2D_(\d+)\."))
            {
                int idx = int.Parse(m.Groups[1].Value);
                bool insideLod = lodRanges.Any(r => i >= r.start && i < r.end);
                if (insideLod)
                {
                    if (!outsideTextures.Contains(idx))
                        lodOnlyTextures.Add(idx);
                }
                else
                {
                    outsideTextures.Add(idx);
                    lodOnlyTextures.Remove(idx);
                }
            }
        }

        if (lodOnlyTextures.Count == 0)
            return (-1, lodOnlyTextures, new List<int>());

        int lodMax = lodOnlyTextures.Max();
        int maxBase = lodOnlyTextures.Min() - 1;

        var virtualTextures = outsideTextures.Where(idx => idx > lodMax).OrderBy(x => x).ToList();

        return (maxBase, lodOnlyTextures, virtualTextures);
    }

    /// <summary>
    /// Removes if-blocks whose texture references are all above maxBase
    /// and that contain only LOD-related instructions. Repeats until stable.
    /// </summary>
    private static List<string> RemoveLodBlocks(List<string> lines, int maxBase)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            var output = new List<string>();
            int i = 0;
            while (i < lines.Count)
            {
                string stripped = lines[i].Trim();
                if (Regex.IsMatch(stripped, @"^if\s*\(") && stripped.Contains("{"))
                {
                    int? length = CollectIfBlockLength(lines, i);
                    if (length != null)
                    {
                        var blockLines = lines.GetRange(i, length.Value);
                        string blockText = string.Join("\n", blockLines);
                        var texIndices = new HashSet<int>();
                        foreach (Match m in Regex.Matches(blockText, @"Material_Texture2D_(\d+)\."))
                            texIndices.Add(int.Parse(m.Groups[1].Value));

                        if (texIndices.Count > 0 && texIndices.All(idx => idx > maxBase) && IsLodOnlyBlock(blockLines))
                        {
                            i += length.Value;
                            changed = true;
                            continue;
                        }
                    }
                }
                output.Add(lines[i]);
                i++;
            }
            lines = output;
        }
        return lines;
    }

    /// <summary>
    /// Extracts the ordered list of t-param indices from the function signature.
    /// e.g. main(float4 t3, float4 t2, float4 t0, ...) -> [3, 2, 0]
    /// </summary>
    private static List<int> ExtractTParamOrder(List<string> lines)
    {
        bool inSig = false;
        var tIndices = new List<int>();
        foreach (string line in lines)
        {
            if (line.Contains("FMaterialAttributes main("))
                inSig = true;
            if (inSig)
            {
                foreach (Match m in Regex.Matches(line, @"float4\s+t(\d+)"))
                    tIndices.Add(int.Parse(m.Groups[1].Value));
                if (line.Contains(")"))
                    break;
            }
        }
        return tIndices;
    }

    /// <summary>
    /// After LOD removal, computes how to renumber remaining t-params sequentially.
    /// Kept params get sequential indices: t(N-1), t(N-2), ..., t0 in signature order,
    /// so they match the engine's outer-scope variable names.
    /// </summary>
    private static Dictionary<int, int> ComputeRenameMap(List<int> tParamOrder, HashSet<int> tKeep)
    {
        var keptOrder = tParamOrder.Where(idx => tKeep.Contains(idx)).ToList();
        if (keptOrder.Count == 0)
            return new Dictionary<int, int>();

        var rename = new Dictionary<int, int>();
        int count = keptOrder.Count;
        for (int i = 0; i < keptOrder.Count; i++)
        {
            int newIdx = count - 1 - i;
            if (keptOrder[i] != newIdx)
                rename[keptOrder[i]] = newIdx;
        }
        return rename;
    }

    /// <summary>
    /// Converts a Material_Texture2D keep-set to a t-param keep-set using positional mapping.
    /// Position 0 in the signature -> Material_Texture2D_0, position 1 -> MTD_1, etc.
    /// The t-param INDEX (t2, t5, etc.) is the GPU register, not the MTD index.
    /// </summary>
    private static HashSet<int> MtdKeepToTKeep(List<int> tParamOrder, HashSet<int> mtdKeepSet)
    {
        var tKeep = new HashSet<int>();
        for (int pos = 0; pos < tParamOrder.Count; pos++)
        {
            if (mtdKeepSet.Contains(pos))
                tKeep.Add(tParamOrder[pos]);
        }
        return tKeep;
    }

    /// <summary>
    /// Updates the FMaterialAttributes main(...) signature to only include kept t-params,
    /// applying renames where needed.
    /// </summary>
    private static List<string> UpdateSignature(List<string> lines, HashSet<int> keepSet, Dictionary<int, int> renameMap)
    {
        var result = new List<string>();
        int i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Contains("FMaterialAttributes main("))
            {
                var sigLines = new List<string>();
                while (i < lines.Count)
                {
                    sigLines.Add(lines[i]);
                    if (lines[i].Contains(")"))
                    {
                        i++;
                        break;
                    }
                    i++;
                }

                string full = string.Join("\n", sigLines);
                var mSig = Regex.Match(full, @"main\(([\s\S]*)\)");
                if (mSig.Success)
                {
                    var parameters = mSig.Groups[1].Value.Split(',')
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();

                    var kept = new List<string>();
                    foreach (string p in parameters)
                    {
                        var tm = Regex.Match(p, @"float4\s+t(\d+)");
                        if (tm.Success)
                        {
                            int idx = int.Parse(tm.Groups[1].Value);
                            if (keepSet.Contains(idx))
                            {
                                string param = p;
                                if (renameMap != null && renameMap.ContainsKey(idx))
                                    param = param.Replace($"t{idx}", $"t{renameMap[idx]}");
                                kept.Add(param);
                            }
                        }
                        else
                        {
                            kept.Add(p);
                        }
                    }

                    result.Add("FMaterialAttributes main(");
                    for (int j = 0; j < kept.Count; j++)
                    {
                        string suffix = j < kept.Count - 1 ? "," : ")";
                        result.Add("   " + kept[j] + suffix);
                    }
                }
                else
                {
                    result.AddRange(sigLines);
                }
                continue;
            }
            result.Add(lines[i]);
            i++;
        }
        return result;
    }

    /// <summary>
    /// Updates the return s.main(...) statement to only include kept t-params,
    /// applying renames where needed.
    /// </summary>
    private static List<string> UpdateReturn(List<string> lines, HashSet<int> keepSet, Dictionary<int, int> renameMap)
    {
        var output = new List<string>();
        foreach (string line in lines)
        {
            var m = Regex.Match(line, @"return s\.main\((.*?)\);");
            if (m.Success)
            {
                var args = m.Groups[1].Value.Split(',')
                    .Select(a => a.Trim())
                    .Where(a => !string.IsNullOrEmpty(a))
                    .ToList();

                var kept = new List<string>();
                foreach (string a in args)
                {
                    var tm = Regex.Match(a, @"^t(\d+)$");
                    if (tm.Success)
                    {
                        int idx = int.Parse(tm.Groups[1].Value);
                        if (keepSet.Contains(idx))
                        {
                            if (renameMap != null && renameMap.ContainsKey(idx))
                                kept.Add($"t{renameMap[idx]}");
                            else
                                kept.Add(a);
                        }
                    }
                    else
                    {
                        kept.Add(a);
                    }
                }
                output.Add($"return s.main({string.Join(",", kept)});");
            }
            else
            {
                output.Add(line);
            }
        }
        return output;
    }

    /// <summary>
    /// After LOD block removal, consecutive same-register cmp writes indicate
    /// orphaned conditions whose if-blocks were removed. Remove the later one.
    /// </summary>
    private static List<string> RemoveOrphanedCmps(List<string> lines)
    {
        var output = new List<string>();
        foreach (string line in lines)
        {
            string stripped = line.Trim();
            if (output.Count > 0 && Regex.IsMatch(stripped, @"=\s*cmp\("))
            {
                string prevStripped = output[^1].Trim();
                var mCurr = Regex.Match(stripped, @"^(r\d+\.\w+)\s*=\s*cmp\(");
                var mPrev = Regex.Match(prevStripped, @"^(r\d+\.\w+)\s*=");
                if (mCurr.Success && mPrev.Success && mCurr.Groups[1].Value == mPrev.Groups[1].Value)
                    continue;
            }
            output.Add(line);
        }
        return output;
    }
}
