using System.Text;
using System.Text.RegularExpressions;
using SharpDX.Direct3D11;
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

    // V1 DISABLED — regex patterns only used by PostProcessUsf and helpers
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
            // If v4 feeds directly into o0 (base color), viewDir causes rainbow artifacts —
            // zero it out instead (the original v4 is a per-vertex interpolant near zero).
            bool v4IsBaseColor = !bIsTransparent && V4FeedsIntoBaseColor(hlslSource);
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
                        usf.AppendLine(v4IsBaseColor
                            ? "        float4 v4 = float4(0, 0, 0, 1);"
                            : "        float4 v4 = {viewDir.xyz,1};");
                        declaredVRegs.Add(4);
                        break;
                    case 4 when i.Type == "float3":
                        usf.AppendLine(v4IsBaseColor
                            ? "        float3 v4 = float3(0, 0, 0);"
                            : "        float3 v4 = viewDir.xyz;");
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
                    case 4: usf.AppendLine(v4IsBaseColor
                        ? "        float4 v4 = float4(0, 0, 0, 1);"
                        : "        float4 v4 = {viewDir.xyz,1};"); break;
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
        var outputMode = V2_DetermineOutputMode(source, parsed, material);
        var classified = V2_ClassifyTextures(parsed, material);

        // Phase 2: Code Generation
        var sb = new StringBuilder();

        // Blend mode / render state metadata for the Python importer
        V2_EmitMetadataComments(sb, material, outputMode);

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

    private ShaderOutputMode V2_DetermineOutputMode(string source, ParsedShader parsed, Material material)
    {
        // Check scopes first — these are authoritative for transparency
        var scopes = material.EnumerateScopes().ToList();
        if (scopes.Contains(TfxScope.TRANSPARENT) || scopes.Contains(TfxScope.TRANSPARENT_ADVANCED))
            return ShaderOutputMode.Transparent;
        if (scopes.Contains(TfxScope.DECAL)
            && !scopes.Contains(TfxScope.CHUNK_MODEL)
            && !scopes.Contains(TfxScope.RIGID_MODEL)
            && !scopes.Contains(TfxScope.SKINNING))
            return ShaderOutputMode.Transparent;

        // Check render stage
        if (material.RenderStage == TfxRenderStage.WaterReflection)
            return ShaderOutputMode.Transparent;

        // Fallback: HLSL output target analysis
        bool isTransparent = !source.Contains("SV_TARGET2") && source.Contains("SV_TARGET0");
        if (isTransparent) return ShaderOutputMode.Transparent;
        if (parsed.HasDiscard) return ShaderOutputMode.Masked;
        return ShaderOutputMode.Opaque;
    }

    /// <summary>
    /// Map Bungie blend state to a UE5 blend mode string for the Python importer.
    /// </summary>
    private static string MapBlendMode(Material material, ShaderOutputMode outputMode)
    {
        if (outputMode == ShaderOutputMode.Masked)
            return "masked";
        if (outputMode == ShaderOutputMode.Opaque && material.RenderStates.BlendState() == -1)
            return "opaque";

        // Check actual blend desc to distinguish translucent vs additive vs modulate
        var blend = material.RenderStates.Blend;
        if (blend == null || !blend.BlendDesc[0].IsBlendEnabled)
        {
            return outputMode == ShaderOutputMode.Transparent ? "translucent" : "opaque";
        }

        var src = blend.BlendDesc[0].SourceBlend;
        var dst = blend.BlendDesc[0].DestinationBlend;

        // One + One = additive
        if (src == BlendOption.One && dst == BlendOption.One)
            return "additive";
        // Zero + SrcColor = modulate
        if (src == BlendOption.Zero && dst == BlendOption.SourceColor)
            return "modulate";
        // SrcAlpha + InvSrcAlpha = standard translucent
        if (src == BlendOption.SourceAlpha && dst == BlendOption.InverseSourceAlpha)
            return "translucent";
        // One + InvSrcAlpha = premultiplied alpha (use translucent)
        if (src == BlendOption.One && dst == BlendOption.InverseSourceAlpha)
            return "translucent";

        // Default: if blend is enabled, it's translucent
        return "translucent";
    }

    /// <summary>
    /// Emit metadata comments at the top of the .usf file for the Python importer to parse.
    /// </summary>
    private static void V2_EmitMetadataComments(StringBuilder sb, Material material, ShaderOutputMode outputMode)
    {
        string blendMode = MapBlendMode(material, outputMode);
        sb.AppendLine($"// blend_mode: {blendMode}");

        // Two-sided from rasterizer state (CullMode.None = two-sided)
        bool twoSided = false;
        if (material.RenderStates.RasterizerState() != -1)
        {
            var rasterizer = RenderStates.RasterizerStates[material.RenderStates.RasterizerState()];
            twoSided = rasterizer.CullMode == CullMode.None;
        }
        sb.AppendLine($"// two_sided: {twoSided.ToString().ToLower()}");

        // Shading model: unlit for translucent/additive, default_lit for opaque/masked
        string shadingModel = (blendMode == "translucent" || blendMode == "additive" || blendMode == "modulate")
            ? "unlit" : "default_lit";
        sb.AppendLine($"// shading_model: {shadingModel}");

        // Material domain: deferred_decal for decal-scoped materials
        var scopes = material.EnumerateScopes().ToList();
        bool isDecal = scopes.Contains(TfxScope.DECAL)
            && !scopes.Contains(TfxScope.CHUNK_MODEL)
            && !scopes.Contains(TfxScope.RIGID_MODEL)
            && !scopes.Contains(TfxScope.SKINNING);
        if (isDecal || material.RenderStage == TfxRenderStage.Decals || material.RenderStage == TfxRenderStage.DecalsAdditive)
            sb.AppendLine("// material_domain: deferred_decal");

        // Legacy markers for backwards compatibility
        if (outputMode == ShaderOutputMode.Transparent)
            sb.AppendLine("// transparent");
        else if (outputMode == ShaderOutputMode.Masked)
            sb.AppendLine("// masked");
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

    /// <summary>
    /// Check if v4 is added directly into o0 (base color output).
    /// When this happens, mapping v4 to viewDir causes a rainbow artifact
    /// because viewDir sweeps -1..1 across the surface. In these shaders
    /// v4 is likely a per-vertex interpolant (vertex color, ambient, etc.)
    /// that should be near-zero, so we zero it out instead.
    /// </summary>
    private static bool V4FeedsIntoBaseColor(string source)
    {
        using var reader = new StringReader(source);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            // Match lines like: o0.xyz = ... v4.xyz ...  or  o0.xyzw = ... v4 ...
            if (trimmed.StartsWith("o0.") && trimmed.Contains("=") && trimmed.Contains("v4."))
                return true;
        }
        return false;
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
        bool v4IsBaseColor = !isTransparent && V4FeedsIntoBaseColor(source);
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
                    sb.AppendLine(v4IsBaseColor
                        ? "float4 v4 = float4(0, 0, 0, 1);"
                        : "float4 v4 = {viewDir.xyz, 1};");
                    declared.Add(4); break;
                case 4 when i.Type == "float3":
                    sb.AppendLine(v4IsBaseColor
                        ? "float3 v4 = float3(0, 0, 0);"
                        : "float3 v4 = viewDir.xyz;");
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
                case 4: sb.AppendLine(v4IsBaseColor
                    ? "float4 v4 = float4(0, 0, 0, 1);"
                    : "float4 v4 = {viewDir.xyz, 1};"); break;
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

    /// <summary>
    /// Provide a reasonable default for scene-provided textures that we can't bind in UE5.
    /// Matches the S2 converter's approach of providing non-zero stubs to prevent black artifacts.
    /// </summary>
    private static string SceneTextureDefault(int texIndex, string dotAfter)
    {
        // Well-known scene texture slots used by transparent/decal shaders:
        //  10 = depth buffer, 11 = atmosphere far, 13 = atmosphere near,
        //  14 = terrain dyemap, 15 = atmosphere density, 16-18 = volume/3D,
        //  20-21 = modified depth/volumetric, 23 = framebuffer copy, 24 = cubemap
        return texIndex switch
        {
            10 => $"float4(1, 1, 1, 1).{dotAfter} // depth (far plane default)",
            11 or 13 or 15 => $"float4(0, 0, 0, 0).{dotAfter} // atmosphere (no fog)",
            14 => $"float4(0.5, 0.5, 0.5, 1).{dotAfter} // terrain dyemap (neutral)",
            16 or 17 or 18 => $"float4(0.1, 0.1, 0.1, 1).{dotAfter} // volume texture stub",
            20 or 21 => $"float4(0, 0, 0, 1).{dotAfter} // depth/volumetric stub",
            23 => $"float4(0, 0, 0, 0).{dotAfter} // framebuffer (black background)",
            24 => $"float4(0.1, 0.1, 0.1, 0).{dotAfter} // cubemap stub",
            _ => $"float4(0, 0, 0, 0).{dotAfter} // scene texture t{texIndex}"
        };
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
                    sb.AppendLine($"   {equal}= Material_Texture2D_{ct.OutputIndex}.Load(int3({loadArgs})).{dotAfter}");
                }
                else
                {
                    string dotAfter = line.Contains(").") ? line.Split(").")[1] : "xyzw;";
                    sb.AppendLine($"   {equal}= {SceneTextureDefault(texIndex, dotAfter)}");
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
                    // Use Material_Texture2D_N naming — UE5 always pairs these with Material_Texture2D_NSampler
                    sb.AppendLine($"   {equal}= Material_Texture2D_{ct.OutputIndex}.SampleLevel(Material_Texture2D_{ct.OutputIndex}Sampler, {sampleUv}, 0).{dotAfter}");
                }
                else
                {
                    string dotAfter = line.Contains(").") ? line.Split(").")[1] : "xyzw;";
                    sb.AppendLine($"   {equal}= {SceneTextureDefault(texIndex, dotAfter)}");
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

    // ═══════════════════════════════════════════════════════════════════
    // V2 spirv-cross variant — for D1 shaders that go through gcn2hlsl.exe
    // (shadPS4 recompiler → SPIR-V → spirv-cross HLSL).
    //
    // The spirv-cross HLSL dialect is fundamentally different from
    // 3dmigoto's: SSA-form temporaries (`_245`), named texture vars
    // (`fs_img4` rather than `t1`), `cbuffer` with `packoffset`, raw
    // `ByteAddressBuffer ssbo_N` for cbuffers, separate `frag_main()` +
    // `main()` shim. This path parses that dialect and emits USF that
    // UE5 can compile as a Custom Expression. See Discovery 9 in
    // D1_SHADER_PHASE0_STATUS.md for the full incompatibility analysis
    // and the rationale for keeping this as a parallel path rather than
    // unifying with HlslToUsfV2.
    //
    // ═══════════════════════════════════════════════════════════════════

    // Pre-compiled regex for the spirv-cross dialect.
    private static readonly Regex RxSpvTexture = new(
        @"Texture(2D|3D|Cube)<(\w+)>\s+(\w+)\s*:\s*register\(t(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxSpvSampler = new(
        @"SamplerState\s+(\w+)\s*:\s*register\(s(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxSpvSsbo = new(
        @"ByteAddressBuffer\s+(ssbo_\d+)\s*:\s*register\(t(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxSpvFragColor = new(
        @"^\s*static\s+float4\s+(frag_color\d+)\s*;", RegexOptions.Compiled);
    private static readonly Regex RxSpvFragColorAssign = new(
        @"^\s*frag_color(\d+)\.([xyzw]+)\s*=\s*", RegexOptions.Compiled);

    /// <summary>
    /// Convert spirv-cross HLSL output (from gcn2hlsl.exe) to UE5 USF.
    /// Returns null on failure.
    /// </summary>
    public string HlslToUsfV2_FromSpirvCross(Material material, bool bIsVertexShader)
    {
        if (bIsVertexShader)
            return null; // V2 only handles pixel shaders for now

        string source = material.Pixel.Shader.Decompile($"ps{material.Pixel.Shader.Hash}");
        if (string.IsNullOrEmpty(source))
            return null;

        // Phase 1: Parse spirv-cross dialect into the existing ParsedShader struct.
        var parsed = V2_ParseHlsl_Spirv(source);
        if (parsed == null || parsed.Textures.Count == 0 && parsed.Cbuffers.Count == 0)
            return null;

        // Detect MRT count to pick D1 output mode.
        int mrtCount = 0;
        foreach (var line in source.Split('\n'))
        {
            var m = RxSpvFragColor.Match(line);
            if (m.Success)
                mrtCount = Math.Max(mrtCount, int.Parse(m.Groups[1].Value.Substring("frag_color".Length)) + 1);
        }
        if (mrtCount == 0) return null;

        // Two-pass texture mapping using the .meta.json sidecar that
        // gcn2hlsl writes alongside the .spv. spirv-cross names every image
        // variable `fs_img<sharp_idx>` where sharp_idx is the source SGPR
        // for inline ImmResource slots OR a table offset for indirect loads
        // (PtrResourceTable / PtrExtendedUserData paths). The meta file's
        // ImmResource entries give us a direct SGPR → api_slot map, which
        // resolves the inline cases. Indirect loads have no static slot
        // table entry — they're filled by the runtime — so we assign them
        // to whatever material texture slots are left over.
        //
        // Without the meta, V2_ClassifyTextures' index-matching is wrong
        // for D1 because spirv-cross renumbers binding registers from
        // wherever ssbos end. With the meta we get correct semantic
        // bindings: e.g., the diffuse texture goes to Material_Texture2D_0
        // (matching the material's api_slot 0) instead of being shifted
        // off by one.
        var material2D = material.Pixel.EnumerateTextures()
            .Where(t => t.Texture != null && t.Texture.GetDimension() == TextureDimension.D2)
            .OrderBy(t => t.TextureIndex)
            .ToList();
        // Map: api_slot → sequential UE5 Material_Texture2D index
        var apiToSeq = new Dictionary<int, int>();
        for (int i = 0; i < material2D.Count; i++)
            apiToSeq[(int)material2D[i].TextureIndex] = i;

        // Load the meta sidecar (best-effort — fall back to positional
        // pairing if missing).
        var sgprToApi = LoadSpirvMetaImmResources(material.Pixel.Shader.Hash);

        // Load resource-table bindings (Option A): each entry is a
        // distinct `fs_img_t<R>_o<O>` variable emitted by the patched
        // shadPS4 recompiler. Sort by dword offset — the i-th entry in
        // that order corresponds to the i-th logical texture in the
        // resource table, which D1 conventionally lays out in the same
        // order as the material's texture binding list.
        var tableBindings = LoadSpirvMetaTableBindings(material.Pixel.Shader.Hash);
        tableBindings.Sort((a, b) => a.TableDwordOffset.CompareTo(b.TableDwordOffset));
        var tableBindingRank = new Dictionary<string, int>();
        for (int i = 0; i < tableBindings.Count; i++)
            tableBindingRank[tableBindings[i].SpvName] = i;

        var classified = new List<ClassifiedTexture>();
        var sortedShaderTextures = parsed.Textures.OrderBy(t => t.Index).ToList();
        var matchedSeqs = new HashSet<int>();
        var unmatchedTextures = new List<TextureView>();

        // Pass 1: assign each shader image variable to a material slot.
        // Two paths coexist:
        //   a) `fs_img_t<R>_o<O>` — resource-table binding. Material slot
        //      = its offset-sorted rank among table_bindings, skipping any
        //      slot already claimed by an inline ImmResource on this pass.
        //   b) `fs_img<N>` — legacy inline ImmResource. Material slot is
        //      computed by: sharp_idx → meta sgprToApi → material apiToSeq.
        // Unmatched textures fall through to Pass 2 (positional fill).
        foreach (var hlslTex in sortedShaderTextures)
        {
            int seq = -1;
            if (tableBindingRank.TryGetValue(hlslTex.Variable, out int rank))
            {
                // Table-slot image. Find the first unused material slot at
                // or after the table rank. The rank is the natural mapping
                // but we skip any slot an ImmResource has already claimed
                // in a prior iteration (unlikely, but keeps the allocation
                // conflict-free).
                int candidate = rank;
                while (candidate < material2D.Count && matchedSeqs.Contains(candidate))
                    candidate++;
                if (candidate < material2D.Count)
                {
                    seq = candidate;
                    matchedSeqs.Add(seq);
                }
            }
            else
            {
                int sharpIdx = ExtractSharpIdxFromVarName(hlslTex.Variable);
                if (sharpIdx >= 0 && sgprToApi.TryGetValue(sharpIdx, out int apiSlot) &&
                    apiToSeq.TryGetValue(apiSlot, out int s))
                {
                    seq = s;
                    matchedSeqs.Add(seq);
                }
            }

            if (seq >= 0)
            {
                classified.Add(new ClassifiedTexture
                {
                    HlslVariable = hlslTex.Variable,
                    HlslIndex = hlslTex.Index,
                    Dimension = hlslTex.Dimension,
                    DataType = hlslTex.Type,
                    Category = TextureCategory.Material2D,
                    OutputIndex = seq,
                    OutputName = $"t{seq}",
                });
            }
            else
            {
                unmatchedTextures.Add(hlslTex);
            }
        }

        // Pass 2: assign unmatched (indirect-loaded) textures to whatever
        // material slots are still free, in order.
        var freeSeqs = new List<int>();
        for (int i = 0; i < material2D.Count; i++)
            if (!matchedSeqs.Contains(i)) freeSeqs.Add(i);
        for (int i = 0; i < unmatchedTextures.Count; i++)
        {
            var hlslTex = unmatchedTextures[i];
            if (i < freeSeqs.Count)
            {
                int seq = freeSeqs[i];
                classified.Add(new ClassifiedTexture
                {
                    HlslVariable = hlslTex.Variable,
                    HlslIndex = hlslTex.Index,
                    Dimension = hlslTex.Dimension,
                    DataType = hlslTex.Type,
                    Category = TextureCategory.Material2D,
                    OutputIndex = seq,
                    OutputName = $"t{seq}",
                });
            }
            else
            {
                // No free material slot — fall through to unbound stub
                classified.Add(new ClassifiedTexture
                {
                    HlslVariable = hlslTex.Variable,
                    HlslIndex = hlslTex.Index,
                    Dimension = hlslTex.Dimension,
                    DataType = hlslTex.Type,
                    Category = TextureCategory.Scene,
                    OutputIndex = -1,
                });
            }
        }

        // Option B fallback for shaders decompiled by older gcn2hlsl
        // builds that didn't track (root, offset) at the SPIR-V level.
        // Triggers only when the sidecar declares a PtrResourceTable but
        // has no `table_bindings` section AND the body uses more distinct
        // (image, sampler) pairs than images — allocate a slot per pair
        // as a best-effort guess. Modern builds (with table_bindings)
        // take the Pass 1 table-rank path instead.
        Dictionary<(string image, string sampler), int> pairToSlot = null;
        if (tableBindings.Count == 0 && HasSpirvMetaResourceTable(material.Pixel.Shader.Hash))
        {
            var pairs = CollectImageSamplerPairs(source);
            int distinctImages = pairs.Keys.Select(k => k.image).Distinct().Count();
            if (pairs.Count > distinctImages && pairs.Count > 0)
            {
                pairToSlot = pairs;
                classified = new List<ClassifiedTexture>();
                foreach (var kv in pairs.OrderBy(p => p.Value))
                {
                    int seq = kv.Value;
                    if (seq >= material2D.Count)
                    {
                        seq = material2D.Count - 1;
                        if (seq < 0) break;
                    }
                    classified.Add(new ClassifiedTexture
                    {
                        HlslVariable = kv.Key.image,
                        HlslIndex = -1,
                        Dimension = "Texture2D",
                        DataType = "float4",
                        Category = TextureCategory.Material2D,
                        OutputIndex = kv.Value,
                        OutputName = $"t{kv.Value}",
                    });
                }
            }
        }

        // D1 uses 1 or 2 MRTs vs D2's 3. 1-MRT == translucent/unlit
        // (treated as Transparent); 2-MRT == opaque/deferred (D1's
        // smaller GBuffer; mapped with a best-guess base+normal split).
        var outputMode = mrtCount == 1 ? ShaderOutputMode.Transparent : ShaderOutputMode.Opaque;

        var sb = new StringBuilder();

        // Reuse the existing metadata header for the Python importer.
        V2_EmitMetadataComments(sb, material, outputMode);
        sb.AppendLine("// source: gcn2hlsl + spirv-cross (D1)");
        sb.AppendLine("// mrt_count: " + mrtCount);

        // Emit cbuffer data as a static array of u32s. shadPS4 maps every D1
        // cbuffer into a ByteAddressBuffer named ssbo_N; the recompiler
        // accesses each dword as `asfloat(ssbo_N.Load(byteOffset))`. We
        // declare a flat array per ssbo and rewrite Load() calls to indexed
        // reads via a helper.
        EmitSpirvSsboBacking(sb, material, parsed);

        // Emit MRT output declarations matching D2 V2 (`float4 o0;` for
        // 1-MRT, `float4 o0, o1, o2;` for opaque). Body lines have their
        // `frag_colorN` references rewritten to `oN` in TranslateSpirvBodyLine.
        if (mrtCount == 1)
            sb.AppendLine("float4 o0;");
        else
        {
            var names = new List<string>();
            for (int i = 0; i < mrtCount; i++) names.Add("o" + i);
            sb.AppendLine("float4 " + string.Join(", ", names) + ";");
        }

        // Classify PS input attributes by analyzing the matching VS.
        // spirv-cross VS writes `out_attrN = expr` and the pipeline
        // linker maps out_attrN → fs_in_attrN via TEXCOORDN, so a VS
        // def-use trace tells us what semantic each fs_in_attrN carries.
        // Falls back to Unknown (zero) for slots that can't be classified.
        Dictionary<int, VsAttrSemantic> vsClass = null;
        if (material.Vertex.Shader != null && material.Vertex.Shader.Hash.IsValid())
        {
            try
            {
                string vsSource = material.Vertex.Shader.Decompile($"vs{material.Vertex.Shader.Hash}");
                if (!string.IsNullOrEmpty(vsSource))
                    vsClass = ClassifySpirvVsOutputs(vsSource);
            }
            catch { /* VS decompile failure is non-fatal; fall back to Unknown */ }
        }
        EmitSpirvFragInputs(sb, source, vsClass);

        // Phase 2: emit the frag_main() body translated to USF.
        bool ok = EmitSpirvFragMainBody(sb, source, classified, mrtCount, pairToSlot);
        if (!ok) return null;

        // Phase 3: D1-specific output mapping.
        EmitSpirvD1OutputMapping(sb, mrtCount, outputMode);

        return sb.ToString();
    }

    /// <summary>
    /// Extract the sharp_idx from a `fs_img<N>` variable name. shadPS4
    /// names every image variable with `fmt::format("{stage}_img{sharp_idx}")`,
    /// so the trailing digits are the sharp_idx — which equals the source
    /// SGPR for inline ImmResource slots.
    /// Returns -1 if the name doesn't fit the pattern.
    /// </summary>
    private static int ExtractSharpIdxFromVarName(string varName)
    {
        if (string.IsNullOrEmpty(varName)) return -1;
        int i = varName.IndexOf("img");
        if (i < 0) return -1;
        int j = i + 3;
        if (j >= varName.Length || !char.IsDigit(varName[j])) return -1;
        int v = 0;
        while (j < varName.Length && char.IsDigit(varName[j]))
        {
            v = v * 10 + (varName[j] - '0');
            j++;
        }
        return v;
    }

    /// <summary>
    /// Result of parsing a `table_bindings` entry from the meta sidecar.
    /// Each entry corresponds to one `fs_img_t&lt;R&gt;_o&lt;O&gt;` variable
    /// emitted by the patched shadPS4 recompiler — a distinct texture in
    /// a runtime resource table.
    /// </summary>
    private struct SpirvTableBinding
    {
        public string SpvName;          // e.g. "fs_img_t12_o20"
        public int TableRootSgpr;
        public int TableDwordOffset;    // dword offset into the table base
    }

    /// <summary>
    /// Read the `table_bindings` array from the .spv.meta.json sidecar and
    /// return one entry per resource-table-loaded image. Returns an empty
    /// list when the sidecar lacks the section (older gcn2hlsl builds) or
    /// when the shader uses only inline ImmResource bindings.
    ///
    /// Order is preserved as written by the recompiler (which matches
    /// info.images allocation order). Callers typically re-sort by
    /// TableDwordOffset to align with the material's texture slot order.
    /// </summary>
    private static List<SpirvTableBinding> LoadSpirvMetaTableBindings(FileHash psHash)
    {
        var result = new List<SpirvTableBinding>();
        string metaPath = $"hlsl_temp/ps{psHash}.spv.meta.json";
        if (!System.IO.File.Exists(metaPath)) return result;
        try
        {
            string text = System.IO.File.ReadAllText(metaPath);
            var rxEntry = new Regex(
                @"\{\s*""spv_name""\s*:\s*""([^""]+)""\s*,\s*""table_root_sgpr""\s*:\s*(\d+)\s*,\s*""table_dword_offset""\s*:\s*(\d+)",
                RegexOptions.Compiled);
            foreach (Match m in rxEntry.Matches(text))
            {
                result.Add(new SpirvTableBinding
                {
                    SpvName = m.Groups[1].Value,
                    TableRootSgpr = int.Parse(m.Groups[2].Value),
                    TableDwordOffset = int.Parse(m.Groups[3].Value),
                });
            }
        }
        catch { /* empty list signals "fall back to old paths" */ }
        return result;
    }

    /// <summary>
    /// Returns true if the .meta.json sidecar declares any PtrResourceTable
    /// entry (usage_type == 19). When this is set, the GCN code is loading
    /// texture descriptors from a runtime resource table rather than via
    /// inline ImmResource bindings, and shadPS4's recompiler collapses all
    /// of them into a single static `fs_img<N>` variable. The body then
    /// samples that one variable through multiple distinct samplers, one
    /// per "logical texture" in the table. We use this signal to trigger
    /// per-(image, sampler) slot expansion in <see cref="HlslToUsfV2_FromSpirvCross"/>.
    /// </summary>
    private static bool HasSpirvMetaResourceTable(FileHash psHash)
    {
        string metaPath = $"hlsl_temp/ps{psHash}.spv.meta.json";
        if (!System.IO.File.Exists(metaPath)) return false;
        try
        {
            string text = System.IO.File.ReadAllText(metaPath);
            return Regex.IsMatch(text, @"""usage_type""\s*:\s*19\b");
        }
        catch { return false; }
    }

    /// <summary>
    /// Walk the spirv-cross source body and return every distinct
    /// `(image, sampler)` pair used in a sample/load call, in call-site
    /// order. Each pair gets a sequential index starting at 0.
    ///
    /// Used to recover per-table-entry texture identity in the
    /// resource-table-indirection case (Option B). The sampler distinguishes
    /// the call sites because shadPS4 correctly tracks ImmSampler
    /// assignments even when it can't resolve which descriptor a load
    /// targets.
    /// </summary>
    private static Dictionary<(string image, string sampler), int> CollectImageSamplerPairs(string source)
    {
        var result = new Dictionary<(string, string), int>();
        // Match `fs_img<N>.<method>(fs_sampsgpr_<M>` — we don't care about
        // the rest of the args. The sampler name is the literal suffix the
        // recompiler chose (sgpr index), which uniquely tags each call site
        // among the resource-table-loaded textures.
        var rx = new Regex(
            @"\b(fs_img\d+)\.(?:Sample|SampleLevel|SampleGrad|SampleBias|SampleCmp|SampleCmpLevelZero|Gather|GatherRed|GatherGreen|GatherBlue|GatherAlpha)\(\s*(fs_sampsgpr_\d+)\b",
            RegexOptions.Compiled);
        int next = 0;
        foreach (Match m in rx.Matches(source))
        {
            var key = (m.Groups[1].Value, m.Groups[2].Value);
            if (!result.ContainsKey(key))
                result[key] = next++;
        }
        return result;
    }

    /// <summary>
    /// Read the .meta.json sidecar that gcn2hlsl writes alongside the .spv,
    /// extract the SGPR → api_slot mapping for ImmResource entries, and
    /// return it as a dictionary. Returns an empty dict if the meta file
    /// is missing or unreadable.
    ///
    /// The meta file path is `hlsl_temp/ps{Hash}.spv.meta.json` matching
    /// the convention in `ShaderBytecode.Decompile()`.
    /// </summary>
    private static Dictionary<int, int> LoadSpirvMetaImmResources(FileHash psHash)
    {
        var result = new Dictionary<int, int>();
        string metaPath = $"hlsl_temp/ps{psHash}.spv.meta.json";
        if (!System.IO.File.Exists(metaPath)) return result;
        try
        {
            string text = System.IO.File.ReadAllText(metaPath);
            // Match each `{ "usage_type": 0, ... "api_slot": N, "start_register": M, ... }` entry.
            // usage_type 0 == ImmResource.
            var rxSlot = new Regex(
                @"\{\s*""usage_type""\s*:\s*0\s*,[^}]*""api_slot""\s*:\s*(\d+)[^}]*""start_register""\s*:\s*(\d+)",
                RegexOptions.Compiled);
            foreach (Match m in rxSlot.Matches(text))
            {
                int api = int.Parse(m.Groups[1].Value);
                int sgpr = int.Parse(m.Groups[2].Value);
                result[sgpr] = api;
            }
        }
        catch (Exception)
        {
            // Best-effort — leave the dict empty so the caller falls back
            // to positional pairing.
        }
        return result;
    }

    private ParsedShader V2_ParseHlsl_Spirv(string source)
    {
        var p = new ParsedShader { HlslSource = source };
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var t = RxSpvTexture.Match(line);
            if (t.Success)
            {
                p.Textures.Add(new TextureView
                {
                    Dimension = "Texture" + t.Groups[1].Value,
                    Type = t.Groups[2].Value,
                    Variable = t.Groups[3].Value,
                    Index = int.Parse(t.Groups[4].Value),
                });
                continue;
            }
            var s = RxSpvSampler.Match(line);
            if (s.Success)
            {
                p.Samplers.Add(int.Parse(s.Groups[2].Value));
                continue;
            }
            var b = RxSpvSsbo.Match(line);
            if (b.Success)
            {
                // Treat each ssbo as a "cbuffer" for the existing pipeline,
                // even though it's a ByteAddressBuffer in spirv-cross output.
                int ssboIdx = int.Parse(b.Groups[1].Value.Substring("ssbo_".Length));
                p.Cbuffers.Add(new Cbuffer
                {
                    Variable = b.Groups[1].Value,
                    Type = "float4",
                    Count = 256,
                    Index = ssboIdx,
                });
                continue;
            }
        }
        return p;
    }

    private void EmitSpirvSsboBacking(StringBuilder sb, Material material, ParsedShader parsed)
    {
        // Emit cbuffer backing in the SAME shape as the D2 V2 output:
        //   static float4 cbN[Count] = { float4(...), ... };
        //
        // shadPS4 maps the D1 cb0 (the material's per-shader constants) into
        // one of the ssbos — typically the first non-AuxData one. For now we
        // initialize EVERY ssbo from cb0 if available, falling back to zero.
        // The body translation rewrites `ssbo_N.Load(byteOff)` to the
        // equivalent `cbN[byteOff/16].swiz` access pattern.
        dynamic cb0Data = null;
        try { cb0Data = material.Pixel.GetCBuffer0(); } catch { /* not all D1 mats have it */ }

        const int kVec4sPerSsbo = 256; // 256 float4 = 4096 bytes

        foreach (var cb in parsed.Cbuffers)
        {
            // Use ssbo's index as the cbuffer name suffix so body rewrites
            // can find it deterministically.
            sb.AppendLine($"static float4 {cb.Variable}[{kVec4sPerSsbo}] = ");
            sb.AppendLine("{");
            for (int v4 = 0; v4 < kVec4sPerSsbo; v4++)
            {
                float x = 0f, y = 0f, z = 0f, w = 0f;
                if (cb0Data != null && v4 < cb0Data.Count)
                {
                    try
                    {
                        var v = cb0Data[v4];
                        if (v is Vector4)
                        { x = v.X; y = v.Y; z = v.Z; w = v.W; }
                        else
                        { x = v.Unk00.X; y = v.Unk00.Y; z = v.Unk00.Z; w = v.Unk00.W; }
                    }
                    catch { /* leave zero */ }
                }
                sb.Append($"    float4({SanitizeFloat(x)}, {SanitizeFloat(y)}, {SanitizeFloat(z)}, {SanitizeFloat(w)})");
                sb.AppendLine(v4 + 1 < kVec4sPerSsbo ? "," : "");
            }
            sb.AppendLine("};");
        }
    }

    // Semantic classification of a VS `out_attrN` slot. The linker maps
    // out_attrN → fs_in_attrN via TEXCOORDN, so a VS classification tells
    // us what each PS fs_in_attrN actually carries.
    public enum VsAttrSemantic
    {
        Unknown = 0,
        Uv,
        Normal,
        Tangent,
        Bitangent,
        WorldPos,
        VertexColor,
        InstanceData,
    }

    // Regex helpers for the VS analyzer. spirv-cross emits SSA temps as
    // `float _NNN = ...`, `precise float _NNN = ...`, `float4 _NNN = ...`.
    private static readonly Regex RxVsTempDecl = new(
        @"^\s*(?:precise\s+)?(?:float4|float3|float2|float|uint4|uint3|uint2|uint|int4|int3|int2|int|bool)\s+(_\d+)\s*=\s*(.+?);\s*$",
        RegexOptions.Compiled);
    private static readonly Regex RxVsOutAttrAssign = new(
        @"^\s*out_attr(\d+)\.([xyzw]+)\s*=\s*(.+?);\s*$", RegexOptions.Compiled);
    private static readonly Regex RxVsInLeaf = new(@"\bvs_in_attr(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex RxVsInstanceLeaf = new(@"\bvs_instance_attr(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex RxVsTempRef = new(@"\b(_\d+)\b", RegexOptions.Compiled);
    // Functions whose result is a scalar magnitude/normalization helper
    // rather than a vector input. When a temp's RHS is one of these, we
    // do NOT propagate its leaves, because the temp's value semantically
    // represents "a scalar derived from some vector" — it isn't itself
    // the vector. Without this, normalize chains pollute leaf sets:
    // `_319 = rsqrt(_298*_298 + ...)` would otherwise drag vs_in_attr2
    // into every output that multiplies by _319, including the tangent
    // (which shares the normalization scalar with the normal).
    private static readonly Regex RxVsScalarHelper = new(
        @"^\s*(rsqrt|sqrt|length|abs|dot|min|max|saturate|clamp|sign|frac|round|floor|ceil|step|smoothstep|exp|log|pow)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a spirv-cross VS HLSL source and classify each `out_attrN`
    /// slot by its semantic origin. Returns a dict keyed by slot index N.
    ///
    /// Heuristic:
    ///   1. Build a def-use map from SSA temp declarations (_NNN = expr).
    ///   2. For each out_attrN assignment, resolve the leaf inputs by
    ///      walking temps transitively to vs_in_attr*, vs_instance_attr*,
    ///      or ssbo_N references.
    ///   3. Classify based on which vs_in_attr* dominates the leaf set.
    ///      D1 vertex layout (see VertexBuffer.ReadD1VertexData):
    ///        vs_in_attr0 = Position
    ///        vs_in_attr1 = TexCoord0
    ///        vs_in_attr2 = Normal (packed, e.g. quaternion or 4×int16)
    ///        vs_in_attr3 = Tangent (same packing)
    ///        vs_in_attr4 = VertexColor (or secondary data)
    ///   4. Cross-product idiom: if leaves contain BOTH attr2 AND attr3
    ///      the output is the derived Bitangent (N×T).
    /// </summary>
    public static Dictionary<int, VsAttrSemantic> ClassifySpirvVsOutputs(string vsSource)
    {
        var result = new Dictionary<int, VsAttrSemantic>();
        if (string.IsNullOrEmpty(vsSource)) return result;

        // Step 1: build temp def table and out_attr assignment list.
        // spirv-cross SSA is forward-only (lower-numbered temps are
        // defined before higher-numbered ones), so we can expand leaves
        // in a single pass by keeping a leaf set per temp.
        var tempLeaves = new Dictionary<string, HashSet<string>>();
        var outAttrLeaves = new Dictionary<int, HashSet<string>>();

        foreach (var rawLine in vsSource.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var tm = RxVsTempDecl.Match(line);
            if (tm.Success)
            {
                string tempName = tm.Groups[1].Value;
                string rhs = tm.Groups[2].Value;
                // Scalar helpers (rsqrt/sqrt/dot/etc.) collapse vector
                // inputs to a single magnitude. Storing empty leaves
                // prevents downstream multiplications from inheriting
                // the vector input's identity through a normalization
                // scalar that's shared between normal and tangent.
                if (RxVsScalarHelper.IsMatch(rhs))
                    tempLeaves[tempName] = new HashSet<string>();
                else
                    tempLeaves[tempName] = CollectLeaves(rhs, tempLeaves);
                continue;
            }
            var om = RxVsOutAttrAssign.Match(line);
            if (om.Success)
            {
                int slot = int.Parse(om.Groups[1].Value);
                string rhs = om.Groups[3].Value;
                if (!outAttrLeaves.TryGetValue(slot, out var set))
                {
                    set = new HashSet<string>();
                    outAttrLeaves[slot] = set;
                }
                foreach (var leaf in CollectLeaves(rhs, tempLeaves))
                    set.Add(leaf);
                continue;
            }
        }

        // Step 2: classify each out_attrN by its leaf set.
        //
        // Priority is important. Position dominates when present because
        // the common "wind sway" / vertex-displacement pattern writes
        // `worldpos + normal_offset` into an output — the leaf set picks
        // up BOTH vs_in_attr0 and vs_in_attr2, but the resulting value is
        // still semantically a position. Normals and tangents are never
        // summed with a position the other way around, so the inverse
        // ambiguity doesn't exist.
        foreach (var kv in outAttrLeaves)
        {
            var leaves = kv.Value;
            bool hasNormal = leaves.Contains("vs_in_attr2");
            bool hasTangent = leaves.Contains("vs_in_attr3");
            bool hasUv = leaves.Contains("vs_in_attr1");
            bool hasPos = leaves.Contains("vs_in_attr0");
            bool hasColor = leaves.Contains("vs_in_attr4");
            bool hasInstance = leaves.Any(l => l.StartsWith("vs_instance_attr"));

            VsAttrSemantic sem;
            if (hasNormal && hasTangent && !hasPos) sem = VsAttrSemantic.Bitangent;
            else if (hasPos) sem = VsAttrSemantic.WorldPos;
            else if (hasNormal) sem = VsAttrSemantic.Normal;
            else if (hasTangent) sem = VsAttrSemantic.Tangent;
            else if (hasUv) sem = VsAttrSemantic.Uv;
            else if (hasColor) sem = VsAttrSemantic.VertexColor;
            else if (hasInstance) sem = VsAttrSemantic.InstanceData;
            else sem = VsAttrSemantic.Unknown;
            result[kv.Key] = sem;
        }
        return result;
    }

    /// <summary>
    /// Walk an RHS expression and return the set of leaf identifiers:
    /// vs_in_attrN, vs_instance_attrN, and any temp references resolved
    /// transitively via <paramref name="tempLeaves"/>.
    /// </summary>
    private static HashSet<string> CollectLeaves(string rhs, Dictionary<string, HashSet<string>> tempLeaves)
    {
        var leaves = new HashSet<string>();
        foreach (Match m in RxVsInLeaf.Matches(rhs))
            leaves.Add("vs_in_attr" + m.Groups[1].Value);
        foreach (Match m in RxVsInstanceLeaf.Matches(rhs))
            leaves.Add("vs_instance_attr" + m.Groups[1].Value);
        foreach (Match m in RxVsTempRef.Matches(rhs))
        {
            string t = m.Groups[1].Value;
            if (tempLeaves.TryGetValue(t, out var tl))
                foreach (var l in tl) leaves.Add(l);
        }
        return leaves;
    }

    private void EmitSpirvFragInputs(StringBuilder sb, string source,
        Dictionary<int, VsAttrSemantic> vsClass)
    {
        // Route each referenced fs_in_attrN to the UE5 interpolant that
        // matches its VS semantic. The linker maps out_attrN → fs_in_attrN
        // 1:1 via TEXCOORDN, so we index directly into vsClass with N.
        //
        // UE5 interpolants (pin names supplied by import_to_ue5.py):
        //   tx               TexCoord0 (existing)
        //   vc               VertexColor.rgb (existing)
        //   VertexNormalWS   world-space normal (new)
        //   VertexTangentWS  world-space tangent (new)
        //   VertexBitangentWS world-space bitangent = N×T*sign (new)
        //   WorldPosition    absolute world position (new)
        for (int i = 0; i < 32; i++)
        {
            string name = $"fs_in_attr{i}";
            if (!source.Contains(name + ".") && !source.Contains(name + ",") &&
                !source.Contains(name + ")") && !source.Contains(name + ";"))
                continue;

            VsAttrSemantic sem = VsAttrSemantic.Unknown;
            if (vsClass != null) vsClass.TryGetValue(i, out sem);

            string rhs = sem switch
            {
                VsAttrSemantic.Uv           => "float4(tx.xy, 0, 1)",
                VsAttrSemantic.Normal       => "float4(VertexNormalWS, 0)",
                VsAttrSemantic.Tangent      => "float4(VertexTangentWS, 0)",
                VsAttrSemantic.Bitangent    => "float4(VertexBitangentWS, 0)",
                VsAttrSemantic.WorldPos     => "float4(WorldPosition, 1)",
                VsAttrSemantic.VertexColor  => "float4(vc, vcw)",
                VsAttrSemantic.InstanceData => "float4(vc, vcw)",
                _                           => "float4(0, 0, 0, 0)",
            };
            sb.AppendLine($"float4 {name} = {rhs};");
        }
    }

    private bool EmitSpirvFragMainBody(StringBuilder sb, string source,
        List<ClassifiedTexture> classified, int mrtCount,
        Dictionary<(string image, string sampler), int> pairToSlot = null)
    {
        // ReplaceSampleCalls primarily looks up textures by HlslVariable
        // name (the `fs_img...` identifier) by iterating texLookup.Values.
        // The dict's KEY is irrelevant for that path, but it must exist.
        // Legacy ImmResource path uses real HlslIndex values. Option A/B
        // paths use HlslIndex = -1 as a sentinel and would collide with
        // each other under a HlslIndex-keyed dict, so we synthesize
        // unique negative keys for sentinel entries to keep them all in
        // Values without clobbering real-indexed ones.
        var texLookup = new Dictionary<int, ClassifiedTexture>();
        int sentinelKey = -1;
        foreach (var c in classified)
        {
            if (c.HlslIndex >= 0)
            {
                if (!texLookup.ContainsKey(c.HlslIndex))
                    texLookup[c.HlslIndex] = c;
            }
            else
            {
                texLookup[sentinelKey--] = c;
            }
        }

        // UE5 Material Custom Expressions DO NOT ALLOW function definitions
        // inside the body. We CANNOT extract spirv-cross's helper functions
        // (spvBitfieldUExtract, etc.) and emit them as separate functions —
        // every helper call must be substituted inline as an expression at
        // the call site, handled in TranslateSpirvBodyLine.
        //
        // BUT: spirv-cross also emits FILE-SCOPE static variable declarations
        // like `static uint _115;` for SPIR-V private vars or values that span
        // multiple control-flow blocks. These ARE just variable declarations
        // (no function bodies), so we hoist them into the USF body — UE5
        // accepts free `static` declarations.
        HoistSpirvStaticDecls(sb, source);

        // Then extract the body of `void frag_main()` and emit its statements
        // directly. Inline helper substitution happens per-line.
        var reader = new StringReader(source);
        string line;
        bool inBody = false;
        int braceDepth = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (!inBody)
            {
                if (line.TrimStart().StartsWith("void frag_main()"))
                {
                    inBody = true;
                    string next = reader.ReadLine();
                    if (next == null) return false;
                    braceDepth = 1;
                }
                continue;
            }

            foreach (char c in line)
            {
                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
            }
            if (braceDepth <= 0) break;

            string translated = TranslateSpirvBodyLine(line, texLookup, pairToSlot);
            if (translated != null)
                sb.AppendLine(translated);
        }
        return inBody;
    }

    /// <summary>
    /// Scan the spirv-cross source for FILE-SCOPE `static <type> _<id>;`
    /// declarations and emit them at the top of the USF body. spirv-cross
    /// uses these for SPIR-V private variables or values that need to span
    /// multiple control-flow blocks (e.g., a temporary referenced from both
    /// branches of an if-else where the assignment was DCE'd). Without
    /// hoisting, the body references undeclared identifiers.
    ///
    /// Only matches plain `static <type> _<digits>;` (no initializer) so we
    /// don't accidentally pick up the cbuffer arrays we already emitted.
    /// </summary>
    private void HoistSpirvStaticDecls(StringBuilder sb, string source)
    {
        var rxStatic = new Regex(
            @"^\s*static\s+(\w+)\s+(_\d+)\s*;\s*$", RegexOptions.Compiled);
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = rxStatic.Match(line);
            if (!m.Success) continue;
            string type = m.Groups[1].Value;
            string name = m.Groups[2].Value;
            // Initialize to a zero value so the variable is well-defined even
            // if every assignment to it gets stripped by our wave-intrinsic
            // replacements.
            string init = type switch
            {
                "uint" => "0u",
                "int" => "0",
                "float" => "0.0f",
                "bool" => "false",
                _ when type.StartsWith("uint") => $"({type})0",
                _ when type.StartsWith("int") => $"({type})0",
                _ when type.StartsWith("float") => $"({type})0",
                _ => $"({type})0",
            };
            sb.AppendLine($"{type} {name} = {init};");
        }
    }

    private string TranslateSpirvBodyLine(string line, Dictionary<int, ClassifiedTexture> texLookup,
        Dictionary<(string image, string sampler), int> pairToSlot = null)
    {
        // 1. Rewrite ssbo_N.Load(<expr>) → ssbo_N_data[(<expr>)/4]
        //    Bracket-matched so the expression can contain arbitrary commas
        //    inside nested function calls. Also handles asfloat() wrapping
        //    by simply leaving asfloat() in place — the array is float-typed
        //    so asfloat(float) is the identity.
        line = ReplaceSsboLoads(line);

        // 2. Strip SM 6.0 wave / quad intrinsics that UE5 Material Custom
        //    Expressions don't expose. Replace with safe equivalents that
        //    preserve types and value ranges.
        line = ReplaceWaveIntrinsics(line);

        // 3. Rewrite full texture sample calls. Use a bracket-matched
        //    replacement so we consume the entire Sample(samp, uv) call —
        //    a regex on `.Sample\(` alone leaves the trailing args dangling
        //    and produces type mismatches.
        line = ReplaceSampleCalls(line, texLookup, pairToSlot);

        // 4. Drop `precise` qualifier — UE5 Custom Expression doesn't allow it
        line = Regex.Replace(line, @"\bprecise\s+", "");

        // 5. Rename frag_colorN → oN to match D2 V2 output conventions.
        line = Regex.Replace(line, @"\bfrag_color(\d+)", "o$1");

        return line;
    }

    /// <summary>
    /// Replace SM 6.0 wave / quad intrinsics that UE5 Material Custom
    /// Expressions don't expose, plus spirv-cross helper functions (which
    /// would otherwise need to be defined as functions — UE5 doesn't allow
    /// that inside Custom Expression bodies, so they MUST be inlined here).
    /// Each substitution preserves type and is bracket-matched so nested
    /// args with commas don't break.
    /// </summary>
    private string ReplaceWaveIntrinsics(string line)
    {
        // ── spirv-cross helper functions that we MUST inline ─────────
        // (UE5 Custom Expression body cannot contain function definitions.)
        //
        // spvBitfieldUExtract(base, off, count) → bit extraction
        //   ((base >> off) & ((1u << count) - 1u))
        // spvBitfieldSExtract is the signed variant; same expansion is fine
        // for our purposes (we only use the value, not arithmetic semantics).
        line = ReplaceCallNArgs(line, "spvBitfieldUExtract", args =>
        {
            if (args.Count != 3) return null;
            return $"((({args[0]}) >> ({args[1]})) & ((1u << ({args[2]})) - 1u))";
        });
        line = ReplaceCallNArgs(line, "spvBitfieldSExtract", args =>
        {
            if (args.Count != 3) return null;
            // Sign-extending bit extract — for our purposes the unsigned form
            // is close enough (the result is consumed as an index/mask).
            return $"((int)((({args[0]}) >> ({args[1]})) & ((1u << ({args[2]})) - 1u)))";
        });
        line = ReplaceCallNArgs(line, "spvBitfieldInsert", args =>
        {
            if (args.Count != 4) return null;
            // Insert: clear `count` bits at offset `off` in `base`, then OR
            // in the corresponding bits from `insert`.
            return $"((({args[0]}) & (~(((1u << ({args[3]})) - 1u) << ({args[2]})))) | ((({args[1]}) & ((1u << ({args[3]})) - 1u)) << ({args[2]})))";
        });

        // Simple zero-arg wave intrinsics
        line = Regex.Replace(line, @"\bWaveGetLaneIndex\s*\(\s*\)", "0u");
        line = Regex.Replace(line, @"\bWaveGetLaneCount\s*\(\s*\)", "1u");
        line = Regex.Replace(line, @"\bWaveIsFirstLane\s*\(\s*\)", "true");

        // Single-arg "active" reductions: just return the input.
        // Bracket-matched so nested expressions are captured.
        string[] singleArg = {
            "WaveActiveBitOr", "WaveActiveBitAnd", "WaveActiveBitXor",
            "WaveActiveSum", "WaveActiveProduct", "WaveActiveMin", "WaveActiveMax",
            "WaveActiveCountBits", "WaveActiveAllTrue", "WaveActiveAnyTrue",
            "WaveActiveBallot",
            "WavePrefixSum", "WavePrefixProduct", "WavePrefixCountBits",
            "QuadReadAcrossX", "QuadReadAcrossY", "QuadReadAcrossDiagonal",
        };
        foreach (var name in singleArg)
            line = ReplaceCallStripExtraArgs(line, name, keepFirstArg: true);

        // Two-arg `QuadReadLaneAt(value, lane)` — keep first arg only.
        line = ReplaceCallStripExtraArgs(line, "QuadReadLaneAt", keepFirstArg: true);
        line = ReplaceCallStripExtraArgs(line, "WaveReadLaneAt", keepFirstArg: true);
        line = ReplaceCallStripExtraArgs(line, "WaveReadLaneFirst", keepFirstArg: true);

        // CalculateLevelOfDetail / CalculateLevelOfDetailUnclamped — return float 0
        // (different from the float4 texture stub).
        line = StripDotMethod(line, "CalculateLevelOfDetail", "0.0f");
        line = StripDotMethod(line, "CalculateLevelOfDetailUnclamped", "0.0f");

        return line;
    }

    /// <summary>
    /// Find every `Name(arg0, arg1, ...)` call on the line and replace with
    /// the result of calling `transform` on the parsed argument list. If
    /// transform returns null, the original call is left in place.
    /// Bracket-matched so nested args work.
    /// </summary>
    private string ReplaceCallNArgs(string line, string name, Func<List<string>, string> transform)
    {
        var sb = new StringBuilder();
        int i = 0;
        var rxFind = new Regex(@"\b" + Regex.Escape(name) + @"\s*\(", RegexOptions.Compiled);
        while (i < line.Length)
        {
            var m = rxFind.Match(line, i);
            if (!m.Success) { sb.Append(line, i, line.Length - i); break; }
            sb.Append(line, i, m.Index - i);

            int argStart = m.Index + m.Length;
            int depth = 1;
            int j = argStart;
            while (j < line.Length && depth > 0)
            {
                char c = line[j];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth == 0) break;
                j++;
            }
            if (depth != 0) { sb.Append(line, m.Index, line.Length - m.Index); break; }

            string argsRaw = line.Substring(argStart, j - argStart);
            var args = SplitTopLevelArgs(argsRaw);
            string output = transform(args);
            if (output == null)
            {
                // transform refused; emit the original call verbatim
                sb.Append(line, m.Index, j + 1 - m.Index);
            }
            else
            {
                sb.Append(output);
            }
            i = j + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Split a comma-separated argument list at top level (depth 0). Each
    /// element is trimmed of leading/trailing whitespace.
    /// </summary>
    private static List<string> SplitTopLevelArgs(string argsRaw)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < argsRaw.Length; i++)
        {
            char c = argsRaw[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(argsRaw.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        if (start < argsRaw.Length || argsRaw.Length == 0)
            result.Add(argsRaw.Substring(start).Trim());
        return result;
    }

    /// <summary>
    /// Find every `Name(args)` call on the line and replace with either:
    /// - the first top-level argument (if keepFirstArg), or
    /// - a fixed replacement string.
    /// Bracket-matched.
    /// </summary>
    private string ReplaceCallStripExtraArgs(string line, string name,
        bool keepFirstArg, string replacement = null)
    {
        var sb = new StringBuilder();
        int i = 0;
        var rxFind = new Regex(@"\b" + Regex.Escape(name) + @"\s*\(", RegexOptions.Compiled);
        while (i < line.Length)
        {
            var m = rxFind.Match(line, i);
            if (!m.Success) { sb.Append(line, i, line.Length - i); break; }
            sb.Append(line, i, m.Index - i);

            int argStart = m.Index + m.Length;
            int depth = 1;
            int j = argStart;
            while (j < line.Length && depth > 0)
            {
                char c = line[j];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth == 0) break;
                j++;
            }
            if (depth != 0) { sb.Append(line, m.Index, line.Length - m.Index); break; }

            string args = line.Substring(argStart, j - argStart);
            string output;
            if (keepFirstArg)
            {
                int comma = FindTopLevelComma(args);
                output = "(" + (comma < 0 ? args : args.Substring(0, comma)).Trim() + ")";
            }
            else
            {
                output = replacement ?? "0";
            }
            sb.Append(output);
            i = j + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Find every `<obj>.MethodName(args)` call and replace the entire
    /// `<obj>.MethodName(args)` span with `replacement`. Bracket-matched.
    /// </summary>
    private string StripDotMethod(string line, string method, string replacement)
    {
        var sb = new StringBuilder();
        int i = 0;
        var rxFind = new Regex(@"\b\w+\." + Regex.Escape(method) + @"\s*\(", RegexOptions.Compiled);
        while (i < line.Length)
        {
            var m = rxFind.Match(line, i);
            if (!m.Success) { sb.Append(line, i, line.Length - i); break; }
            sb.Append(line, i, m.Index - i);
            int argStart = m.Index + m.Length;
            int depth = 1;
            int j = argStart;
            while (j < line.Length && depth > 0)
            {
                char c = line[j];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth == 0) break;
                j++;
            }
            if (depth != 0) { sb.Append(line, m.Index, line.Length - m.Index); break; }
            sb.Append(replacement);
            i = j + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replace every `ssbo_N.Load(<expr>)` call (with arbitrarily nested
    /// arguments) with `ssbo_N[<expr>/16][<expr>%16/4]` — i.e., index into
    /// the float4 cbuffer array (matching the D2 V2 `static float4 cbN[K]`
    /// declaration format) and dynamically pick the component.
    /// Walks the string with a paren-depth counter so the entire offset
    /// expression — including nested function calls and commas — is
    /// captured correctly.
    /// </summary>
    private string ReplaceSsboLoads(string line)
    {
        var sb = new StringBuilder();
        int i = 0;
        var rxFind = new Regex(
            @"(ssbo_\d+)\.Load(2|3|4)?\(", RegexOptions.Compiled);
        while (i < line.Length)
        {
            var m = rxFind.Match(line, i);
            if (!m.Success)
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            sb.Append(line, i, m.Index - i);
            string ssbo = m.Groups[1].Value;
            string lanes = m.Groups[2].Success ? m.Groups[2].Value : "1";

            int argStart = m.Index + m.Length;
            int depth = 1;
            int j = argStart;
            while (j < line.Length && depth > 0)
            {
                char c = line[j];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth == 0) break;
                j++;
            }
            if (depth != 0)
            {
                sb.Append(line, m.Index, line.Length - m.Index);
                break;
            }

            string offsetExpr = line.Substring(argStart, j - argStart);
            string replacement = MakeSsboLoad(ssbo, offsetExpr, int.Parse(lanes));
            sb.Append(replacement);
            i = j + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a `ssbo_N[v4][component]` access expression for a load at
    /// `byteOff`. For literal integer offsets, emit a static `ssbo_N[K].swiz`
    /// access (HLSL-friendly). For non-literal expressions, fall back to the
    /// dynamic two-step `ssbo_N[(expr)/16][((expr)%16)/4]` form which
    /// requires HLSL's dynamic vector component indexing.
    /// </summary>
    private string MakeSsboLoad(string ssbo, string offsetExpr, int lanes)
    {
        // Try to fold to a literal so we can emit the friendly cbN[K].swiz form
        if (int.TryParse(offsetExpr.Trim(), out int byteOff))
        {
            if (lanes == 1)
            {
                int v4 = byteOff / 16;
                int comp = (byteOff % 16) / 4;
                string swiz = "xyzw"[comp].ToString();
                return $"({ssbo}[{v4}].{swiz})";
            }
            else
            {
                var parts = new List<string>();
                for (int k = 0; k < lanes; k++)
                {
                    int off = byteOff + k * 4;
                    int v4 = off / 16;
                    int comp = (off % 16) / 4;
                    string swiz = "xyzw"[comp].ToString();
                    parts.Add($"{ssbo}[{v4}].{swiz}");
                }
                return $"float{lanes}({string.Join(", ", parts)})";
            }
        }
        // Fall back to runtime indexing for non-literal offsets
        if (lanes == 1)
        {
            return $"({ssbo}[(({offsetExpr})/16)][(({offsetExpr})%16)/4])";
        }
        var parts2 = new List<string>();
        for (int k = 0; k < lanes; k++)
        {
            string off = k == 0 ? offsetExpr : $"({offsetExpr}) + {k * 4}";
            parts2.Add($"{ssbo}[(({off})/16)][(({off})%16)/4]");
        }
        return $"float{lanes}({string.Join(", ", parts2)})";
    }

    /// <summary>
    /// Replace every texture method call on a `fs_imgN` variable
    /// (Sample/SampleLevel/SampleGrad/SampleBias/Load/Gather, with
    /// arbitrarily nested arguments) on a line with a UE5-friendly
    /// substitute. Walks the string with a paren-depth counter so the
    /// replacement consumes EXACTLY the matched call including its closing
    /// paren — no dangling args, no type mismatches.
    ///
    /// We collapse all the variants to a uniform `SampleLevel(samp, uv.xy, 0)`
    /// form for Material2D textures, dropping LOD/grad/face arguments. This
    /// loses some fidelity (mip selection, gradients) but avoids having to
    /// translate AMD-specific intrinsics like `cubeFaceIndexAMD` and SPIR-V
    /// constructs that don't exist in UE5's HLSL dialect.
    /// </summary>
    private string ReplaceSampleCalls(string line, Dictionary<int, ClassifiedTexture> texLookup,
        Dictionary<(string image, string sampler), int> pairToSlot = null)
    {
        var sb = new StringBuilder();
        int i = 0;
        // Match `fs_imgN.<method>(` (legacy ImmResource-style names) OR
        // `fs_img_t<R>_o<O>.<method>(` (new resource-table names emitted by
        // the patched recompiler). We'll handle all of them the same way below.
        var rxFind = new Regex(
            @"(fs_img(?:_t\d+_o[0-9a-fA-F]+|\d+))\.(Sample|SampleLevel|SampleGrad|SampleBias|SampleCmp|SampleCmpLevelZero|Load|Gather|GatherRed|GatherGreen|GatherBlue|GatherAlpha)\(",
            RegexOptions.Compiled);
        while (i < line.Length)
        {
            var m = rxFind.Match(line, i);
            if (!m.Success)
            {
                sb.Append(line, i, line.Length - i);
                break;
            }
            // Copy text before the match
            sb.Append(line, i, m.Index - i);

            string texVar = m.Groups[1].Value;
            string method = m.Groups[2].Value;

            // Walk forward from after the opening paren to find the matching close
            int argStart = m.Index + m.Length;
            int depth = 1;
            int j = argStart;
            while (j < line.Length && depth > 0)
            {
                char c = line[j];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth == 0) break;
                j++;
            }
            if (depth != 0)
            {
                // Unbalanced — bail and leave the rest of the line as-is
                sb.Append(line, m.Index, line.Length - m.Index);
                break;
            }

            // Extract the args between the opening and closing parens
            string argsRaw = line.Substring(argStart, j - argStart);

            // For Sample/SampleLevel/SampleGrad/etc the first arg is the
            // sampler and the second is the uv. For Load the first (and
            // possibly only) arg is the coord. Find the FIRST top-level
            // comma; if absent, treat the whole arg list as the coord.
            string uv;
            string samplerArg = null;
            if (method == "Load")
            {
                uv = argsRaw.Trim();
            }
            else
            {
                int comma1 = FindTopLevelComma(argsRaw);
                if (comma1 < 0)
                {
                    uv = argsRaw.Trim();
                }
                else
                {
                    samplerArg = argsRaw.Substring(0, comma1).Trim();
                    string afterFirst = argsRaw.Substring(comma1 + 1).TrimStart();
                    int comma2 = FindTopLevelComma(afterFirst);
                    uv = comma2 < 0 ? afterFirst : afterFirst.Substring(0, comma2).TrimEnd();
                }
            }

            // Resolve the destination material slot. In Option B expansion
            // mode, the (image, sampler) pair uniquely identifies a logical
            // texture in the resource table. Fall back to per-image lookup
            // for the inline-binding case.
            int? slotIdx = null;
            if (pairToSlot != null && samplerArg != null
                && pairToSlot.TryGetValue((texVar, samplerArg), out int s))
            {
                slotIdx = s;
            }
            else
            {
                var ct = texLookup.Values.FirstOrDefault(c => c.HlslVariable == texVar);
                if (ct != null && ct.Category == TextureCategory.Material2D)
                    slotIdx = ct.OutputIndex;
            }

            string replacement;
            if (slotIdx.HasValue)
            {
                // Truncate uv to xy. `(float2(...)).xy` is the identity for
                // 2-component, and a valid swizzle for 3+ components.
                string uv2 = $"({uv}).xy";
                replacement =
                    $"Material_Texture2D_{slotIdx.Value}.SampleLevel(Material_Texture2D_{slotIdx.Value}Sampler, {uv2}, 0)";
            }
            else
            {
                // Unbound / scene / cube / 3D — neutral grey stub. The caller
                // may append `.swiz` afterwards; float4 supports any swizzle.
                replacement = "float4(0.5, 0.5, 0.5, 1)";
            }
            sb.Append(replacement);
            i = j + 1; // skip past the closing paren
        }
        return sb.ToString();
    }

    /// <summary>
    /// Find the index of the first top-level comma (depth 0) in `s`. Returns
    /// -1 if no top-level comma exists.
    /// </summary>
    private static int FindTopLevelComma(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
    }

    private void EmitSpirvD1OutputMapping(StringBuilder sb, int mrtCount, ShaderOutputMode mode)
    {
        sb.AppendLine();
        sb.AppendLine("FMaterialAttributes output;");
        if (mrtCount == 1)
        {
            // Translucent / unlit — matches the D2 V2 transparent path exactly
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
            // 2-MRT D1 deferred — best-guess channel layout. The actual
            // packing is unverified; iterate after seeing real reconstruction
            // results in UE5.
            sb.AppendLine("// D1 2-MRT mapping is a best guess — verify against real reconstructions.");
            sb.AppendLine("output.BaseColor = saturate(o0.xyz);");
            sb.AppendLine("output.Roughness = saturate(o0.w);");
            sb.AppendLine("float3 n = normalize(o1.xyz * 2 - 1);");
            sb.AppendLine("output.Normal = n;");
            sb.AppendLine("output.Metallic = saturate(o1.w);");
            sb.AppendLine("output.EmissiveColor = float3(0, 0, 0);");
            sb.AppendLine("output.AmbientOcclusion = 1;");
            sb.AppendLine("output.OpacityMask = 1;");
        }
        sb.AppendLine("return output;");
    }
}
