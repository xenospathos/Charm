using System.Text;
using System.Text.RegularExpressions;
using Tiger.Schema.Shaders;

namespace Tiger.Schema;

public class UsfConverter
{
    internal struct TextureView
    {
        public string Dimension;
        public string Type;
        public string Variable;
        public int Index;
    }

    internal struct Cbuffer
    {
        public string Variable;
        public string Type;
        public int Count;
        public int Index;
    }

    internal struct Input
    {
        public string Variable;
        public string Type;
        public int Index;
        public string Semantic;
    }

    internal struct Output
    {
        public string Variable;
        public string Type;
        public int Index;
        public string Semantic;
    }

    // ─── Pre-compiled regex patterns (V2 only uses RxReturnVoid, RxTexIndex, RxUvSwizzle) ───
    private static readonly Regex RxReturnVoid = new(@"^return\s*;", RegexOptions.Compiled);
    private static readonly Regex RxUvSwizzle = new(@"^(r\d+\.)(\w+)$", RegexOptions.Compiled);
    private static readonly Regex RxTexIndex = new(@"t(\d+)", RegexOptions.Compiled);

    private static readonly Regex RxMtdDot = new(@"Material_Texture2D_(\d+)\.", RegexOptions.Compiled);
    private static readonly Regex RxMtdDotOrSampler = new(@"Material_Texture2D_(\d+)(?:\.|Sampler)", RegexOptions.Compiled);
    private static readonly Regex RxIfStart = new(@"^if\s*\(", RegexOptions.Compiled);
    private static readonly Regex RxElseBrace = new(@"^\}\s*else\s*\{", RegexOptions.Compiled);
    private static readonly Regex RxCmpAssign = new(@"=\s*cmp\(", RegexOptions.Compiled);
    private static readonly Regex RxCmpRegister = new(@"^(r\d+\.\w+)\s*=\s*cmp\(", RegexOptions.Compiled);
    private static readonly Regex RxRegAssign = new(@"^(r\d+\.\w+)\s*=", RegexOptions.Compiled);
    private static readonly Regex RxRegToReg = new(@"^r\d+\.\w+\s*=\s*r\d+\.\w+\s*;", RegexOptions.Compiled);
    private static readonly Regex RxFloat4T = new(@"float\d?\s+t(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxTParam = new(@"^t(\d+)$", RegexOptions.Compiled);
    private static readonly Regex RxReturnMain = new(@"return s\.main\((.*?)\);", RegexOptions.Compiled);
    private static readonly Regex RxMainSig = new(@"main\(([\s\S]*)\)", RegexOptions.Compiled);
    private static readonly Regex RxMtdFull = new(@"Material_Texture2D_(\d+)", RegexOptions.Compiled);

    private string hlslSource;
    private StringReader hlsl;
    private StringBuilder usf;
    private bool bOpacityEnabled = false;
    private bool bIsTransparent = false;
    private readonly List<TextureView> textures = new();
    private readonly List<int> samplers = new();
    private readonly List<Cbuffer> cbuffers = new();
    private readonly List<Input> inputs = new();
    private readonly List<Output> outputs = new();

    // ═══════════════════════════════════════════════════════════════════
    // V1 Pipeline — DISABLED. All shaders now go through HlslToUsfV2.
    // Kept for reference until V2 is fully validated.
    // ═══════════════════════════════════════════════════════════════════
    /*
    public string HlslToUsf(Material material, bool bIsVertexShader)
    {
        hlslSource = material.Pixel.Shader.Decompile($"ps{material.Pixel.Shader.Hash}");
        hlsl = new StringReader(hlslSource);
        usf = new StringBuilder();
        bOpacityEnabled = false;
        bIsTransparent = !hlslSource.Contains("SV_TARGET2") && hlslSource.Contains("SV_TARGET0");
        ProcessHlslData();
        // Reset the reader so ConvertInstructions can read from the start
        hlsl = new StringReader(hlslSource);
        if (bIsTransparent)
        {
            usf.AppendLine("// transparent");
        }
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
            if (bIsTransparent)
                AddTransparentOutputs();
            else
                AddOutputs();
        }

        WriteFooter(bIsVertexShader);

        if (!bIsVertexShader)
        {
            return PostProcessUsf(usf.ToString());
        }

        return usf.ToString();
    }
    */

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
                    TextureView texture = new();
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
                    Cbuffer cbuffer = new();
                    cbuffer.Variable = "cb" + line.Split("cb")[1].Split("[")[0];
                    cbuffer.Index = Int32.TryParse(new string(cbuffer.Variable.Skip(2).ToArray()), out int index) ? index : -1;
                    cbuffer.Count = Int32.TryParse(new string(line.Split("[")[1].Split("]")[0]), out int count) ? count : -1;
                    cbuffer.Type = line.Split("cb")[0].Trim();
                    cbuffers.Add(cbuffer);
                }
                else if (line.Contains(" v") && line.Contains(" : ") && !line.Contains("?"))
                {
                    Input input = new();
                    input.Variable = "v" + line.Split("v")[1].Split(" : ")[0];
                    input.Index = Int32.TryParse(new string(input.Variable.Skip(1).ToArray()), out int index) ? index : -1;
                    input.Semantic = line.Split(" : ")[1].Split(",")[0];
                    input.Type = line.Split(" v")[0].Trim();
                    inputs.Add(input);
                }
                else if (line.Contains("out") && line.Contains(" : "))
                {
                    Output output = new();
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
        foreach (Cbuffer cbuffer in cbuffers)
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
                                    dynamic x = data[i].Unk00.X; // really bad but required
                                    usf.AppendLine($"    float4({x}, {data[i].Unk00.Y}, {data[i].Unk00.Z}, {data[i].Unk00.W}),");
                                }
                            }
                            catch (Exception)  // figure out whats up here, taniks breaks it
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
            foreach (Input i in inputs)
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
            foreach (Output output in outputs)
            {
                usf.AppendLine($"{output.Type} {output.Variable};");
            }

            usf.AppendLine().AppendLine("void main(");
            foreach (TextureView texture in textures)
            {
                usf.AppendLine($"   {texture.Type} {texture.Variable},");
            }
            for (int i = 0; i < inputs.Count; i++)
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
            foreach (TextureView texture in textures)
            {
                // Skip non-2D textures — they can't be Material_Texture2D inputs
                if (!texture.Dimension.Contains("Texture2D"))
                    continue;
                usf.AppendLine($"   {texture.Type} {texture.Variable},");
            }

            if (bIsTransparent)
            {
                usf.AppendLine($"   float2 screenPos,");
                usf.AppendLine($"   float twoSidedSign,");
            }
            usf.AppendLine($"   float2 tx,");
            usf.AppendLine($"   float3 viewDir,");
            usf.AppendLine($"   float3 vc,");
            usf.AppendLine($"   float vcw)");

            usf.AppendLine("{").AppendLine("    FMaterialAttributes output;");
            // Output render targets
            if (bIsTransparent)
                usf.AppendLine("    float4 o0;");
            else
                usf.AppendLine("    float4 o0,o1,o2;");
            // Map v-registers to actual material inputs
            // D2 pixel shader vertex layout:
            //   v0 = tangent Z (normal up), v1 = tangent X, v2 = tangent Y
            //   v3 = texcoord, v4 = view direction, v5 = vertex color
            HashSet<int> declaredVRegs = new();
            foreach (Input i in inputs)
            {
                switch (i.Index)
                {
                    case 0 when i.Type == "float4":
                        usf.AppendLine("        float4 v0 = {tx.xy, 1,1};");
                        declaredVRegs.Add(0);
                        break;
                    case 1 when i.Type == "float4":
                        usf.AppendLine("        float4 v1 = {1,0,0,1};");
                        declaredVRegs.Add(1);
                        break;
                    case 2 when i.Type == "float4":
                        usf.AppendLine("        float4 v2 = {0,1,0,1};");
                        declaredVRegs.Add(2);
                        break;
                    case 3 when i.Type == "float4":
                        usf.AppendLine("        float4 v3 = {tx.xy, 1,1};");
                        declaredVRegs.Add(3);
                        break;
                    case 4 when i.Type == "float4":
                        usf.AppendLine("        float4 v4 = {viewDir.xyz,1};");
                        declaredVRegs.Add(4);
                        break;
                    case 4 when i.Type == "float3":
                        usf.AppendLine("        float3 v4 = viewDir.xyz;");
                        declaredVRegs.Add(4);
                        break;
                    case 5 when i.Type == "float4" && bIsTransparent:
                        usf.AppendLine("        float4 v5 = float4(screenPos, 0, 1);");
                        declaredVRegs.Add(5);
                        break;
                    case 5 when i.Type == "float4":
                        usf.AppendLine("        float4 v5 = {vc.xyz, vcw};");
                        declaredVRegs.Add(5);
                        break;
                    default:
                        if (i.Type == "uint" && bIsTransparent)
                            usf.AppendLine($"    {i.Variable} = twoSidedSign > 0 ? 1u : 0u;");
                        else if (i.Type == "uint")
                            usf.AppendLine($"    {i.Variable}.x = {i.Variable}.x;");
                        break;
                }
            }

            // Emit default declarations for any v-registers used in the shader body
            // but not declared via the input signature (common in transparent shaders)
            for (int vi = 0; vi <= 5; vi++)
            {
                if (declaredVRegs.Contains(vi))
                    continue;
                if (!hlslSource.Contains($"v{vi}."))
                    continue;
                switch (vi)
                {
                    case 0: usf.AppendLine("        float4 v0 = {tx.xy, 1,1};"); break;
                    case 1: usf.AppendLine("        float4 v1 = {1,0,0,1};"); break;
                    case 2: usf.AppendLine("        float4 v2 = {0,1,0,1};"); break;
                    case 3: usf.AppendLine("        float4 v3 = {tx.xy, 1,1};"); break;
                    case 4: usf.AppendLine("        float4 v4 = {viewDir.xyz,1};"); break;
                    case 5:
                        if (bIsTransparent)
                            usf.AppendLine("        float4 v5 = float4(screenPos, 0, 1);");
                        else
                            usf.AppendLine("        float4 v5 = {vc.xyz, vcw};");
                        break;
                }
            }
        }
    }

    private bool ConvertInstructions()
    {
        Dictionary<int, TextureView> texDict = new();
        foreach (TextureView texture in textures)
        {
            texDict.Add(texture.Index, texture);
        }
        // Only 2D textures get Material_Texture2D_N slots — non-2D are stripped
        List<int> sortedIndices = texDict.Where(kv => kv.Value.Dimension.Contains("Texture2D"))
                                         .Select(kv => kv.Key).OrderBy(x => x).ToList();
        string targetMarker = bIsTransparent ? "SV_TARGET0" : "SV_TARGET2";
        string line = hlsl.ReadLine();
        if (line == null)
        {
            // its a broken pixel shader that uses some kind of memory textures
            return false;
        }
        while (!line.Contains(targetMarker))
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
                if (RxReturnVoid.IsMatch(line.Trim()))
                {
                    break;
                }
                if (line.Contains(".Load"))
                {
                    string equal = line.Split("=")[0];
                    int texIndex = int.Parse(RxTexIndex.Match(line.Split(".Load")[0]).Groups[1].Value);
                    // Skip non-2D textures (cubemaps, 3D textures) — not yet supported in UE5 custom expressions
                    if (texDict.ContainsKey(texIndex) && !texDict[texIndex].Dimension.Contains("Texture2D"))
                    {
                        usf.AppendLine($"   {equal}= 0; // stripped non-2D texture sample");
                        continue;
                    }
                    string loadArgs = line.Split(".Load(")[1].Split(")")[0];
                    string dotAfter = line.Split(").")[1];
                    int mtdIdx = sortedIndices.IndexOf(texIndex);
                    usf.AppendLine($"   {equal}= Material_Texture2D_{mtdIdx}.Load(int3({loadArgs})).{dotAfter}");
                }
                else if (line.Contains("Sample"))
                {
                    int texIndex = int.Parse(RxTexIndex.Match(line.Split(".Sample")[0]).Groups[1].Value);
                    // Skip non-2D textures (cubemaps, 3D textures) — not yet supported in UE5 custom expressions
                    if (texDict.ContainsKey(texIndex) && !texDict[texIndex].Dimension.Contains("Texture2D"))
                    {
                        string equal = line.Split("=")[0];
                        usf.AppendLine($"   {equal}= 0; // stripped non-2D texture sample");
                        continue;
                    }
                    var (equal2, mtdIdx, samplerIdx, uv) = ParseTextureOp(line, ".Sample", texDict, sortedIndices);
                    var dotAfter = line.Split(").")[1];
                    // Use mtdIdx for sampler — UE5 pairs each Material_Texture2D_N with Material_Texture2D_NSampler
                    usf.AppendLine($"   {equal2}= Material_Texture2D_{mtdIdx}.SampleLevel(Material_Texture2D_{mtdIdx}Sampler, {uv}, 0).{dotAfter}");
                }
                else if (line.Contains("CalculateLevelOfDetail"))
                {
                    int texIndex = int.Parse(RxTexIndex.Match(line.Split(".CalculateLevelOfDetail")[0]).Groups[1].Value);
                    // Skip non-2D textures
                    if (texDict.ContainsKey(texIndex) && !texDict[texIndex].Dimension.Contains("Texture2D"))
                    {
                        string equal = line.Split("=")[0];
                        usf.AppendLine($"   {equal}= 0; // stripped non-2D texture LOD calc");
                        continue;
                    }
                    var (equal2, mtdIdx, samplerIdx, uv) = ParseTextureOp(line, ".CalculateLevelOfDetail", texDict, sortedIndices);
                    usf.AppendLine($"   {equal2}= Material_Texture2D_{mtdIdx}.CalculateLevelOfDetail(Material_Texture2D_{mtdIdx}Sampler, {uv});");
                }
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

    /// <summary>
    /// Shared parser for texture Sample and CalculateLevelOfDetail lines.
    /// Returns (equalPart, mtdIndex, samplerIndex, truncatedUv).
    /// </summary>
    private static (string equal, int mtdIdx, int samplerIdx, string uv) ParseTextureOp(
        string line, string opName, Dictionary<int, TextureView> texDict, List<int> sortedIndices)
    {
        string equal = line.Split("=")[0];
        int texIndex = int.Parse(RxTexIndex.Match(line.Split(opName)[0]).Groups[1].Value);
        int sampleIndex = int.Parse(line.Split("(s")[1].Split("_s,")[0]);
        string sampleUv = line.Split(", ")[1].Split(")")[0];

        if (texDict.ContainsKey(texIndex) && texDict[texIndex].Dimension.Contains("Texture2D"))
        {
            var uvMatch = RxUvSwizzle.Match(sampleUv);
            if (uvMatch.Success && uvMatch.Groups[2].Value.Length > 2)
                sampleUv = uvMatch.Groups[1].Value + uvMatch.Groups[2].Value.Substring(0, 2);
        }

        return (equal, sortedIndices.IndexOf(texIndex), sampleIndex - 1, sampleUv);
    }

    private void AddTransparentOutputs()
    {
        string outputString = @"
        output.EmissiveColor = o0.xyz;
        output.Opacity = o0.w;
        output.BaseColor = float3(0, 0, 0);
        output.Metallic = 0;
        output.Roughness = 1;
        output.Normal = float3(0, 0, 1);
        output.OpacityMask = 1;
        output.AmbientOcclusion = 1;

        return output;
        ";
        usf.AppendLine(outputString);
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
            string texArgs = String.Join(',', textures.Where(x => x.Dimension.Contains("Texture2D")).Select(x => x.Variable));
            if (bIsTransparent)
                usf.AppendLine("shader s;").AppendLine($"return s.main({texArgs},screenPos,twoSidedSign,tx,viewDir,vc,vcw);");
            else
                usf.AppendLine("shader s;").AppendLine($"return s.main({texArgs},tx,viewDir,vc,vcw);");
        }
    }

    // ─── Post-processing: strip LOD textures, prune signature ────────────

    internal string PostProcessUsf(string usfText)
    {
        var lines = usfText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var allNums = new SortedSet<int>();
        foreach (Match m in RxMtdDot.Matches(usfText))
            allNums.Add(int.Parse(m.Groups[1].Value));

        var tParamOrder = ExtractTParamOrder(lines);

        if (allNums.Count > 0)
        {
            var (maxBase, lodOnlyTextures, virtualTextures) = ClassifyTextures(lines);

            if (lodOnlyTextures.Count > 0)
            {
                lines = RemoveLodBlocks(lines, maxBase);

                var vtSet = new HashSet<int>(virtualTextures);
                lines = lines.Where(l =>
                {
                    var m = RxMtdDot.Match(l);
                    return !m.Success || !vtSet.Contains(int.Parse(m.Groups[1].Value));
                }).ToList();

                string remaining = string.Join("\n", lines);
                var mtdUsed = new HashSet<int>();
                foreach (Match m in RxMtdDot.Matches(remaining))
                    mtdUsed.Add(int.Parse(m.Groups[1].Value));

                var tKeep = MtdKeepToTKeep(tParamOrder, mtdUsed);
                var renameMap = ComputeRenameMap(tParamOrder, tKeep);

                lines = RemoveOrphanedCmps(lines);
                lines = UpdateSignature(lines, tKeep, renameMap);
                lines = UpdateReturn(lines, tKeep, renameMap);
            }
        }

        // ── Single-pass final sync: renumber body MTDs to be sequential,
        //    then match signature and return to the body. ──

        // 1. Renumber body MTD references to 0..N-1 (fills gaps from LOD removal)
        lines = RenumberMtdReferences(lines);

        // 2. Find which MTD indices the body actually uses (should be 0..N-1 now)
        string bodyText = string.Join("\n", lines);
        var mtdUsedFinal = new SortedSet<int>();
        foreach (Match m in RxMtdDotOrSampler.Matches(bodyText))
            mtdUsedFinal.Add(int.Parse(m.Groups[1].Value));

        int requiredTextureCount = mtdUsedFinal.Count > 0 ? mtdUsedFinal.Max + 1 : 0;

        // 3. Cap body: if body references more MTDs than the signature has t-params,
        //    strip the excess lines (can happen when LOD removal is partial)
        var tOrder = ExtractTParamOrder(lines);
        if (requiredTextureCount > tOrder.Count)
        {
            int cap = tOrder.Count;
            lines = lines.Where(l =>
            {
                foreach (Match mx in RxMtdFull.Matches(l))
                    if (int.Parse(mx.Groups[1].Value) >= cap) return false;
                return true;
            }).ToList();
            // Recount after stripping
            string stripped = string.Join("\n", lines);
            mtdUsedFinal = new SortedSet<int>();
            foreach (Match m in RxMtdDotOrSampler.Matches(stripped))
                mtdUsedFinal.Add(int.Parse(m.Groups[1].Value));
            requiredTextureCount = mtdUsedFinal.Count > 0 ? mtdUsedFinal.Max + 1 : 0;
        }

        // 4. Rebuild signature and return to have exactly requiredTextureCount
        //    sequential t-params (t(N-1)..t0). Always run — even when counts match,
        //    the raw HLSL register names (t21, t10, etc.) must be renamed to t0..tN-1.
        var tKeepFinal = new HashSet<int>(tOrder.Take(requiredTextureCount));
        var renameMapFinal = ComputeRenameMap(tOrder, tKeepFinal);
        lines = UpdateSignature(lines, tKeepFinal, renameMapFinal);
        lines = UpdateReturn(lines, tKeepFinal, renameMapFinal);

        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Measures the length (in lines) of an if-block starting at the given index.
    /// Returns null if the line at start is not an if-with-brace pattern.
    /// </summary>
    private static int? CollectIfBlockLength(List<string> lines, int start)
    {
        string stripped = lines[start].Trim();
        if (!RxIfStart.IsMatch(stripped) || !stripped.Contains("{"))
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
            if (RxElseBrace.IsMatch(s))
                continue;
            if (RxIfStart.IsMatch(s))
                continue;
            if (s.Contains(".SampleLevel("))
                continue;
            if (s.Contains(".CalculateLevelOfDetail("))
                continue;
            if (RxCmpAssign.IsMatch(s))
                continue;
            if (RxRegToReg.IsMatch(s))
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
        // Find all LOD-only if-block ranges and pre-compute which lines are inside them
        var lodLines = new HashSet<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (RxIfStart.IsMatch(lines[i].Trim()) && lines[i].Contains("{"))
            {
                int? length = CollectIfBlockLength(lines, i);
                if (length != null && IsLodOnlyBlock(lines.GetRange(i, length.Value)))
                {
                    for (int j = i; j < i + length.Value; j++)
                        lodLines.Add(j);
                }
            }
        }

        var outsideTextures = new HashSet<int>();
        var lodOnlyTextures = new HashSet<int>();

        for (int i = 0; i < lines.Count; i++)
        {
            foreach (Match m in RxMtdDot.Matches(lines[i]))
            {
                int idx = int.Parse(m.Groups[1].Value);
                if (lodLines.Contains(i))
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
                if (RxIfStart.IsMatch(stripped) && stripped.Contains("{"))
                {
                    int? length = CollectIfBlockLength(lines, i);
                    if (length != null)
                    {
                        var blockLines = lines.GetRange(i, length.Value);
                        string blockText = string.Join("\n", blockLines);
                        var texIndices = new HashSet<int>();
                        foreach (Match m in RxMtdDot.Matches(blockText))
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
                foreach (Match m in RxFloat4T.Matches(line))
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
                var mSig = RxMainSig.Match(full);
                if (mSig.Success)
                {
                    var parameters = mSig.Groups[1].Value.Split(',')
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();

                    var kept = new List<string>();
                    foreach (string p in parameters)
                    {
                        var tm = RxFloat4T.Match(p);
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
            var m = RxReturnMain.Match(line);
            if (m.Success)
            {
                var args = m.Groups[1].Value.Split(',')
                    .Select(a => a.Trim())
                    .Where(a => !string.IsNullOrEmpty(a))
                    .ToList();

                var kept = new List<string>();
                foreach (string a in args)
                {
                    var tm = RxTParam.Match(a);
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
    /// Renumbers Material_Texture2D_N references in the shader body so they are sequential (0..count-1).
    /// After LOD texture removal, gaps in indices cause UE5 "undeclared identifier" errors because
    /// the engine only provides Material_Texture2D_0..N-1 based on the number of connected texture inputs.
    /// </summary>
    private static List<string> RenumberMtdReferences(List<string> lines)
    {
        string text = string.Join("\n", lines);
        var usedIndices = new SortedSet<int>();
        foreach (Match m in RxMtdFull.Matches(text))
            usedIndices.Add(int.Parse(m.Groups[1].Value));

        if (usedIndices.Count == 0)
            return lines;

        // Check if already sequential from 0
        bool needsRenumber = false;
        int expected = 0;
        foreach (int idx in usedIndices)
        {
            if (idx != expected) { needsRenumber = true; break; }
            expected++;
        }
        if (!needsRenumber)
            return lines;

        // Build mapping: old index -> new sequential index
        var mtdRename = new Dictionary<int, int>();
        int seq = 0;
        foreach (int idx in usedIndices)
        {
            if (idx != seq)
                mtdRename[idx] = seq;
            seq++;
        }

        // Apply renaming using regex to avoid partial matches (e.g. _6 matching _60)
        var result = new List<string>();
        foreach (string line in lines)
        {
            string l = RxMtdFull.Replace(line, m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return mtdRename.TryGetValue(idx, out int newIdx)
                    ? $"Material_Texture2D_{newIdx}"
                    : m.Value;
            });
            result.Add(l);
        }
        return result;
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
            if (output.Count > 0 && RxCmpAssign.IsMatch(stripped))
            {
                string prevStripped = output[^1].Trim();
                var mCurr = RxCmpRegister.Match(stripped);
                var mPrev = RxRegAssign.Match(prevStripped);
                if (mCurr.Success && mPrev.Success && mCurr.Groups[1].Value == mPrev.Groups[1].Value)
                    continue;
            }
            output.Add(line);
        }
        return output;
    }

    // ═══════════════════════════════════════════════════════════════════
    // V2 Pipeline — flat code, Texture2DSampleLevel, no regex post-processing
    // ═══════════════════════════════════════════════════════════════════

    public enum TextureCategory { Material2D, Material3D, MaterialCube, Scene, LodOnly }
    public enum ShaderOutputMode { Opaque, Transparent, Masked }

    public class ClassifiedTexture
    {
        public string HlslVariable;    // "t3"
        public int HlslIndex;          // 3
        public string Dimension;       // "Texture2D", "Texture3D", "TextureCube"
        public string DataType;        // "float4"
        public TextureCategory Category;
        public int OutputIndex = -1;   // sequential: 0, 1, 2... (-1 for non-material)
        public string OutputName;      // "t0", "t1"... (null for non-material)
    }

    internal class ParsedShader
    {
        public List<TextureView> Textures = new();
        public List<int> Samplers = new();
        public List<Cbuffer> Cbuffers = new();
        public List<Input> Inputs = new();
        public List<Output> Outputs = new();
        public bool HasDiscard;
        public string HlslSource;
    }

    /// <summary>
    /// V2 entry point. Returns null on failure (caller should fall back to V1).
    /// </summary>
    public string HlslToUsfV2(Material material, bool bIsVertexShader)
    {
        if (bIsVertexShader)
            return null; // V2 only handles pixel shaders for now

        string source = material.Pixel.Shader.Decompile($"ps{material.Pixel.Shader.Hash}");
        if (string.IsNullOrEmpty(source))
            return null;

        // Phase 1: Analysis
        var parsed = V2_ParseHlsl(source);
        var outputMode = V2_DetermineOutputMode(source, parsed);
        var classified = V2_ClassifyTextures(parsed, material);

        // Phase 2: Code Generation
        var sb = new StringBuilder();

        // Comments
        if (outputMode == ShaderOutputMode.Transparent)
            sb.AppendLine("// transparent");
        else if (outputMode == ShaderOutputMode.Masked)
            sb.AppendLine("// masked");

        V2_EmitCbuffers(sb, material, parsed);
        sb.AppendLine("#define cmp -");
        V2_EmitVRegisters(sb, parsed, outputMode, source);
        bool success = V2_EmitInstructions(sb, source, parsed, classified, outputMode);
        if (!success)
            return null;

        // Phase 3: Output Mapping
        V2_EmitOutputMapping(sb, outputMode);

        return sb.ToString();
    }

    // ─── Phase 1: Analysis ──────────────────────────────────────────

    private ParsedShader V2_ParseHlsl(string source)
    {
        var p = new ParsedShader { HlslSource = source };
        var reader = new StringReader(source);
        bool bFindOpacity = false;
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains("r0,r1"))
                bFindOpacity = true;

            if (bFindOpacity)
            {
                if (line.Contains("discard"))
                {
                    p.HasDiscard = true;
                    break;
                }
                continue;
            }

            if (line.Contains("Texture"))
            {
                var tex = new TextureView
                {
                    Dimension = line.Split("<")[0].Trim(),
                    Type = line.Split("<")[1].Split(">")[0],
                    Variable = line.Split("> ")[1].Split(" :")[0],
                    Index = int.TryParse(new string(line.Split("> ")[1].Split(" :")[0].Skip(1).ToArray()), out int idx) ? idx : -1
                };
                p.Textures.Add(tex);
            }
            else if (line.Contains("SamplerState"))
            {
                p.Samplers.Add(line.Split("(")[1].Split(")")[0].Last() - 48);
            }
            else if (line.Contains("cbuffer"))
            {
                reader.ReadLine();
                line = reader.ReadLine();
                var cb = new Cbuffer
                {
                    Variable = "cb" + line.Split("cb")[1].Split("[")[0],
                    Count = int.TryParse(line.Split("[")[1].Split("]")[0], out int cnt) ? cnt : -1,
                    Type = line.Split("cb")[0].Trim()
                };
                cb.Index = int.TryParse(new string(cb.Variable.Skip(2).ToArray()), out int cbi) ? cbi : -1;
                p.Cbuffers.Add(cb);
            }
            else if (line.Contains(" v") && line.Contains(" : ") && !line.Contains("?"))
            {
                var inp = new Input
                {
                    Variable = "v" + line.Split("v")[1].Split(" : ")[0],
                    Semantic = line.Split(" : ")[1].Split(",")[0],
                    Type = line.Split(" v")[0].Trim()
                };
                inp.Index = int.TryParse(new string(inp.Variable.Skip(1).ToArray()), out int ii) ? ii : -1;
                p.Inputs.Add(inp);
            }
            else if (line.Contains("out") && line.Contains(" : "))
            {
                var outp = new Output
                {
                    Variable = "o" + line.Split(" o")[2].Split(" : ")[0],
                    Semantic = line.Split(" : ")[1].Split(",")[0],
                    Type = line.Split("out ")[1].Split(" o")[0]
                };
                outp.Index = int.TryParse(new string(outp.Variable.Skip(1).ToArray()), out int oi) ? oi : -1;
                p.Outputs.Add(outp);
            }
        }
        return p;
    }

    private ShaderOutputMode V2_DetermineOutputMode(string source, ParsedShader parsed)
    {
        bool isTransparent = !source.Contains("SV_TARGET2") && source.Contains("SV_TARGET0");
        if (isTransparent) return ShaderOutputMode.Transparent;
        if (parsed.HasDiscard) return ShaderOutputMode.Masked;
        return ShaderOutputMode.Opaque;
    }

    private List<ClassifiedTexture> V2_ClassifyTextures(ParsedShader parsed, Material material)
    {
        // Build material texture index → dimension map
        var materialTextures = new Dictionary<int, TextureDimension>();
        foreach (STextureTag tex in material.Pixel.EnumerateTextures())
        {
            if (tex.Texture != null)
                materialTextures[(int)tex.TextureIndex] = tex.Texture.GetDimension();
        }

        var result = new List<ClassifiedTexture>();
        int sequentialIndex = 0;

        foreach (var hlslTex in parsed.Textures.OrderBy(t => t.Index))
        {
            var ct = new ClassifiedTexture
            {
                HlslVariable = hlslTex.Variable,
                HlslIndex = hlslTex.Index,
                Dimension = hlslTex.Dimension,
                DataType = hlslTex.Type
            };

            if (!materialTextures.ContainsKey(hlslTex.Index))
            {
                // Not in material JSON = scene-provided texture
                ct.Category = TextureCategory.Scene;
            }
            else
            {
                var dim = materialTextures[hlslTex.Index];
                ct.Category = dim switch
                {
                    TextureDimension.D2 => TextureCategory.Material2D,
                    TextureDimension.D3 => TextureCategory.Material3D,
                    TextureDimension.CUBE => TextureCategory.MaterialCube,
                    _ => TextureCategory.Scene
                };
            }

            // Only Material2D textures get output slots (for now)
            if (ct.Category == TextureCategory.Material2D)
            {
                ct.OutputIndex = sequentialIndex;
                ct.OutputName = $"t{sequentialIndex}";
                sequentialIndex++;
            }

            result.Add(ct);
        }

        return result;
    }

    // ─── Phase 2: Code Generation ───────────────────────────────────

    private static string SanitizeFloat(float v)
    {
        if (float.IsNaN(v)) return "0";
        if (float.IsPositiveInfinity(v)) return "3.402823e+38";
        if (float.IsNegativeInfinity(v)) return "-3.402823e+38";
        return v.ToString();
    }

    private void V2_EmitCbuffers(StringBuilder sb, Material material, ParsedShader parsed)
    {
        dynamic cb0Data = material.Pixel.GetCBuffer0();

        foreach (var cbuffer in parsed.Cbuffers)
        {
            sb.AppendLine($"static {cbuffer.Type} {cbuffer.Variable}[{cbuffer.Count}] = ");
            sb.AppendLine("{");

            for (int i = 0; i < cbuffer.Count; i++)
            {
                // Only cb0 has material-specific data; other cbuffers (cb2, cb12, cb13) are scope-provided
                if (cbuffer.Index == 0 && cb0Data != null && i < cb0Data.Count)
                {
                    try
                    {
                        float x, y, z, w;
                        if (cb0Data[i] is Vector4)
                        {
                            x = cb0Data[i].X; y = cb0Data[i].Y; z = cb0Data[i].Z; w = cb0Data[i].W;
                        }
                        else
                        {
                            x = cb0Data[i].Unk00.X; y = cb0Data[i].Unk00.Y; z = cb0Data[i].Unk00.Z; w = cb0Data[i].Unk00.W;
                        }
                        sb.AppendLine($"    float4({SanitizeFloat(x)}, {SanitizeFloat(y)}, {SanitizeFloat(z)}, {SanitizeFloat(w)}),");
                    }
                    catch
                    {
                        sb.AppendLine("    float4(0, 0, 0, 0),");
                    }
                }
                else
                {
                    sb.AppendLine("    float4(0, 0, 0, 0),");
                }
            }
            sb.AppendLine("};");
        }
    }

    private void V2_EmitVRegisters(StringBuilder sb, ParsedShader parsed, ShaderOutputMode outputMode, string source)
    {
        // Output render targets
        if (outputMode == ShaderOutputMode.Transparent)
            sb.AppendLine("float4 o0;");
        else
            sb.AppendLine("float4 o0, o1, o2;");

        // Map v-registers to UE5 Custom Expression input params
        bool isTransparent = outputMode == ShaderOutputMode.Transparent;
        var declared = new HashSet<int>();

        foreach (var i in parsed.Inputs)
        {
            switch (i.Index)
            {
                case 0 when i.Type == "float4":
                    sb.AppendLine("float4 v0 = {tx.xy, 1, 1};");
                    declared.Add(0); break;
                case 1 when i.Type == "float4":
                    sb.AppendLine("float4 v1 = {1, 0, 0, 1};");
                    declared.Add(1); break;
                case 2 when i.Type == "float4":
                    sb.AppendLine("float4 v2 = {0, 1, 0, 1};");
                    declared.Add(2); break;
                case 3 when i.Type == "float4":
                    sb.AppendLine("float4 v3 = {tx.xy, 1, 1};");
                    declared.Add(3); break;
                case 4 when i.Type == "float4":
                    sb.AppendLine("float4 v4 = {viewDir.xyz, 1};");
                    declared.Add(4); break;
                case 4 when i.Type == "float3":
                    sb.AppendLine("float3 v4 = viewDir.xyz;");
                    declared.Add(4); break;
                case 5 when i.Type == "float4" && isTransparent:
                    sb.AppendLine("float4 v5 = float4(screenPos, 0, 1);");
                    declared.Add(5); break;
                case 5 when i.Type == "float4":
                    sb.AppendLine("float4 v5 = {vc.xyz, vcw};");
                    declared.Add(5); break;
                default:
                    if (i.Type == "uint" && isTransparent)
                    {
                        sb.AppendLine($"uint {i.Variable} = twoSidedSign > 0 ? 1u : 0u;");
                        declared.Add(i.Index);
                    }
                    else if (i.Type == "uint")
                    {
                        sb.AppendLine($"uint {i.Variable} = 1;");
                        declared.Add(i.Index);
                    }
                    break;
            }
        }

        // Fill in any v-registers used in the body but not declared via inputs
        for (int vi = 0; vi <= 5; vi++)
        {
            if (declared.Contains(vi) || !source.Contains($"v{vi}."))
                continue;
            switch (vi)
            {
                case 0: sb.AppendLine("float4 v0 = {tx.xy, 1, 1};"); break;
                case 1: sb.AppendLine("float4 v1 = {1, 0, 0, 1};"); break;
                case 2: sb.AppendLine("float4 v2 = {0, 1, 0, 1};"); break;
                case 3: sb.AppendLine("float4 v3 = {tx.xy, 1, 1};"); break;
                case 4: sb.AppendLine("float4 v4 = {viewDir.xyz, 1};"); break;
                case 5:
                    sb.AppendLine(isTransparent
                        ? "float4 v5 = float4(screenPos, 0, 1);"
                        : "float4 v5 = {vc.xyz, vcw};");
                    break;
            }
        }

        // Handle higher v-registers (v6+) using their actual parsed type
        // These can be SV_POSITION (float4), SV_isFrontFace (uint), etc.
        foreach (var i in parsed.Inputs)
        {
            if (i.Index < 6 || declared.Contains(i.Index))
                continue;
            if (!source.Contains($"v{i.Index}.") && !source.Contains($"v{i.Index} ") && !source.Contains($"v{i.Index};"))
                continue;

            if (i.Type == "float4")
            {
                if (i.Semantic.Contains("SV_POSITION") && isTransparent)
                    sb.AppendLine($"float4 v{i.Index} = float4(screenPos, 0, 1);");
                else
                    sb.AppendLine($"float4 v{i.Index} = float4(0, 0, 0, 0);");
            }
            else if (i.Type == "uint")
            {
                if (isTransparent)
                    sb.AppendLine($"uint v{i.Index} = twoSidedSign > 0 ? 1u : 0u;");
                else
                    sb.AppendLine($"uint v{i.Index} = 1;");
            }
            else
            {
                sb.AppendLine($"{i.Type} v{i.Index} = ({i.Type})0;");
            }
            declared.Add(i.Index);
        }
    }

    private bool V2_EmitInstructions(StringBuilder sb, string source, ParsedShader parsed,
        List<ClassifiedTexture> classified, ShaderOutputMode outputMode)
    {
        // Build lookup: HLSL texture index → ClassifiedTexture
        var texLookup = classified.ToDictionary(c => c.HlslIndex);

        // Find the instruction body start marker
        string targetMarker = outputMode == ShaderOutputMode.Transparent ? "SV_TARGET0" : "SV_TARGET2";
        var reader = new StringReader(source);
        string line = reader.ReadLine();
        if (line == null) return false;

        while (!line.Contains(targetMarker))
        {
            line = reader.ReadLine();
            if (line == null) return false;
        }
        reader.ReadLine(); // skip opening brace

        // Convert instructions line by line
        while ((line = reader.ReadLine()) != null)
        {
            if (RxReturnVoid.IsMatch(line.Trim()))
                break;

            if (line.Contains(".Load"))
            {
                string equal = line.Split("=")[0];
                int texIndex = int.Parse(RxTexIndex.Match(line.Split(".Load")[0]).Groups[1].Value);

                if (texLookup.TryGetValue(texIndex, out var ct) && ct.Category == TextureCategory.Material2D)
                {
                    string loadArgs = line.Split(".Load(")[1].Split(")")[0];
                    string dotAfter = line.Split(").")[1];
                    sb.AppendLine($"   {equal}= {ct.OutputName}.Load(int3({loadArgs})).{dotAfter}");
                }
                else
                {
                    sb.AppendLine($"   {equal}= 0; // scene/non-2D texture .Load");
                }
            }
            else if (line.Contains("Sample"))
            {
                string equal = line.Split("=")[0];
                int texIndex = int.Parse(RxTexIndex.Match(line.Split(".Sample")[0]).Groups[1].Value);

                if (texLookup.TryGetValue(texIndex, out var ct) && ct.Category == TextureCategory.Material2D)
                {
                    string sampleUv = line.Split(", ")[1].Split(")")[0];

                    // Truncate UV to 2 components for Texture2D
                    var uvMatch = RxUvSwizzle.Match(sampleUv);
                    if (uvMatch.Success && uvMatch.Groups[2].Value.Length > 2)
                        sampleUv = uvMatch.Groups[1].Value + uvMatch.Groups[2].Value.Substring(0, 2);

                    string dotAfter = line.Split(").")[1];
                    sb.AppendLine($"   {equal}= Texture2DSampleLevel({ct.OutputName}, {ct.OutputName}Sampler, {sampleUv}, 0).{dotAfter}");
                }
                else
                {
                    sb.AppendLine($"   {equal}= 0; // scene/non-2D texture");
                }
            }
            else if (line.Contains("CalculateLevelOfDetail"))
            {
                string equal = line.Split("=")[0];
                sb.AppendLine($"   {equal}= 0; // CalculateLevelOfDetail stripped");
            }
            else if (line.Contains("discard"))
            {
                // Handled via OpacityMask in output mapping
            }
            else
            {
                sb.AppendLine(line);
            }
        }

        return true;
    }

    // ─── Phase 3: Output Mapping ────────────────────────────────────

    private void V2_EmitOutputMapping(StringBuilder sb, ShaderOutputMode outputMode)
    {
        sb.AppendLine();
        sb.AppendLine("FMaterialAttributes output;");

        if (outputMode == ShaderOutputMode.Transparent)
        {
            sb.AppendLine("output.EmissiveColor = o0.xyz;");
            sb.AppendLine("output.Opacity = o0.w;");
            sb.AppendLine("output.BaseColor = float3(0, 0, 0);");
            sb.AppendLine("output.Metallic = 0;");
            sb.AppendLine("output.Roughness = 1;");
            sb.AppendLine("output.Normal = float3(0, 0, 1);");
            sb.AppendLine("output.OpacityMask = 1;");
            sb.AppendLine("output.AmbientOcclusion = 1;");
        }
        else
        {
            sb.AppendLine("output.BaseColor = o0.xyz;");
            sb.AppendLine();
            sb.AppendLine("float3 biased_normal = o1.xyz - float3(0.5, 0.5, 0.5);");
            sb.AppendLine("float normal_length = length(biased_normal);");
            sb.AppendLine("float3 normal_in_world_space = biased_normal / normal_length;");
            sb.AppendLine("normal_in_world_space.z = sqrt(1.0 - saturate(dot(normal_in_world_space.xy, normal_in_world_space.xy)));");
            sb.AppendLine("output.Normal = normalize((normal_in_world_space * 2 - 1.35)*0.5 + 0.5);");
            sb.AppendLine();
            sb.AppendLine("float smoothness = saturate(8 * (normal_length - 0.375));");
            sb.AppendLine("output.Roughness = 1 - smoothness;");
            sb.AppendLine();
            sb.AppendLine("output.Metallic = saturate(o2.x);");
            sb.AppendLine("output.EmissiveColor = clamp((o2.y - 0.5) * 2 * 5 * output.BaseColor, 0, 100);");
            sb.AppendLine("output.AmbientOcclusion = saturate(o2.y * 2);");

            if (outputMode == ShaderOutputMode.Masked)
                sb.AppendLine("output.OpacityMask = o0.w;");
            else
                sb.AppendLine("output.OpacityMask = 1;");
        }

        sb.AppendLine("return output;");
    }
}
