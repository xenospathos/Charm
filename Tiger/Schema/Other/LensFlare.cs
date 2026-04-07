using Tiger.Exporters;
using Tiger.Schema.Shaders;

namespace Tiger.Schema;

public class LensFlare : Tag<SLensFlare>
{
    public MapTransform Transform { get; set; }
    public List<FileHash> Materials { get; set; }
    public TfxFeatureRenderer FeatureType = TfxFeatureRenderer.LensFlares;

    public LensFlare(FileHash hash) : base(hash)
    {
    }

    public void LoadIntoExporter(ExporterScene scene) // Not ideal
    {
        Exporter.Get().GetGlobalScene().AddToGlobalScene(this);
        Materials = new();
        using TigerReader reader = GetReader();
        for (int i = 0; i < _tag.Entries.Count; i++)
        {
            SLensFlareEntry entry = _tag.Entries.ElementAt(reader, i);
            if (entry.Material == null) continue;
            entry.Material.RenderStage = TfxRenderStage.LensFlares;
            scene.Materials.Add(new ExportMaterial(entry.Material));
            Materials.Add(entry.Material.Hash);
        }
    }
}

/// <summary>
/// Light Lens Flares
/// </summary>
[SchemaStruct(TigerStrategy.DESTINY2_SHADOWKEEP_2601, "BF6C8080", 0x18)]
[SchemaStruct(TigerStrategy.DESTINY2_BEYONDLIGHT_3402, "B5678080", 0x1C)]
public struct SMapLensFlareResource
{
    [SchemaField(0x10)]
    public LensFlare LensFlare; // S786A8080
}

/// <summary>
/// Unk data resource.
/// </summary>
[SchemaStruct(TigerStrategy.DESTINY2_SHADOWKEEP_2601, "686F8080", 0x38)]
[SchemaStruct(TigerStrategy.DESTINY2_BEYONDLIGHT_3402, "786A8080", 0x38)]
public struct SLensFlare
{
    public ulong FileSize;
    [SchemaField(0x18)]
    public Tag<SA16D8080> Unk18;
    [SchemaField(0x20)]
    public DynamicArrayUnloaded<SLensFlareEntry> Entries;
    public TigerHash Unk30;
}

/// <summary>
/// Unk data resource.
/// </summary>
[SchemaStruct(TigerStrategy.DESTINY2_SHADOWKEEP_2601, "6D6F8080", 0xC)]
[SchemaStruct(TigerStrategy.DESTINY2_BEYONDLIGHT_3402, "7D6A8080", 0xC)]
public struct SLensFlareEntry
{
    public Material Material;
    public Tag<SA16D8080> Unk04;
    public int Unk08;
}
