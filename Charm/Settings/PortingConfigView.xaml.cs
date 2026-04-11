using System.Windows;
using System.Windows.Controls;
using Tiger;

namespace Charm;

public partial class PortingConfigView : UserControl
{
    public PortingConfigView()
    {
        InitializeComponent();
        _config = TigerInstance.GetSubsystem<ConfigSubsystem>();
    }

    public void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        PopulateS2ConfigPanel();
        PopulateUEConfigPanel();
    }

    private ConfigSubsystem _config;

    private void PopulateS2ConfigPanel()
    {
        S2ConfigPanel.Children.Clear();

        // Enable source 2 asset generation
        ConfigSettingToggleControl cbe = new();
        cbe.SettingName = "Generate S&Box Asset Files";
        cbe.SettingLabel = "Generates shader, material and model files.";
        bool bval2 = _config.GetSBoxExportEnabled();
        cbe.SettingValue = bval2.ToString();
        cbe.ChangeButton.Click += SBoxExportEnabled_OnClick;
        S2ConfigPanel.Children.Add(cbe);
    }

    private void PopulateUEConfigPanel()
    {
        UnrealConfigPanel.Children.Clear();

        // Unreal interop path
        ConfigSettingControl cui = new();
        cui.SettingName = "Unreal Content Path";
        string val = _config.GetUnrealInteropPath();
        cui.SettingValue = val == "" ? "Not set" : val;
        cui.ChangeButton.Click += UnrealInteropPath_OnClick;
        UnrealConfigPanel.Children.Add(cui);

        // Enable UE5 interop
        ConfigSettingToggleControl cii = new();
        cii.SettingName = "Generate Unreal Engine Importing Files";
        bool bval = _config.GetUnrealInteropEnabled();
        cii.SettingValue = bval.ToString();
        cii.ChangeButton.Click += UnrealInteropEnabled_OnClick;
        UnrealConfigPanel.Children.Add(cii);

        // Beta: Environment generation toggles (control what the UE5 import script generates)
        ConfigSettingToggleControl skyboxToggle = new();
        skyboxToggle.SettingName = "[Beta] Import Skybox";
        skyboxToggle.SettingLabel = "Export All: imports sky, skylight, and reflection captures into UE5.";
        skyboxToggle.SettingValue = _config.GetGenerateSkybox().ToString();
        skyboxToggle.ChangeButton.Click += GenerateSkybox_OnClick;
        UnrealConfigPanel.Children.Add(skyboxToggle);

        ConfigSettingToggleControl lightsToggle = new();
        lightsToggle.SettingName = "[Beta] Import Lights";
        lightsToggle.SettingLabel = "Export All: imports point, spot, and area lights into UE5.";
        lightsToggle.SettingValue = _config.GetGenerateLights().ToString();
        lightsToggle.ChangeButton.Click += GenerateLights_OnClick;
        UnrealConfigPanel.Children.Add(lightsToggle);

        ConfigSettingToggleControl fogToggle = new();
        fogToggle.SettingName = "[Beta] Import Fog";
        fogToggle.SettingLabel = "Export All: spawns exponential height fog in the UE5 map.";
        fogToggle.SettingValue = _config.GetGenerateFog().ToString();
        fogToggle.ChangeButton.Click += GenerateFog_OnClick;
        UnrealConfigPanel.Children.Add(fogToggle);

        ConfigSettingToggleControl atmosphereToggle = new();
        atmosphereToggle.SettingName = "[Beta] Import Atmosphere";
        atmosphereToggle.SettingLabel = "Export All: imports atmosphere LUTs and sun direction into UE5.";
        atmosphereToggle.SettingValue = _config.GetGenerateAtmosphere().ToString();
        atmosphereToggle.ChangeButton.Click += GenerateAtmosphere_OnClick;
        UnrealConfigPanel.Children.Add(atmosphereToggle);

        ConfigSettingToggleControl decalsToggle = new();
        decalsToggle.SettingName = "[BROKEN] Import Decals";
        decalsToggle.SettingLabel = "Export All: imports projected decals, road decals, and water decals into UE5.";
        decalsToggle.SettingValue = _config.GetGenerateDecals().ToString();
        decalsToggle.ChangeButton.Click += GenerateDecals_OnClick;
        UnrealConfigPanel.Children.Add(decalsToggle);

        ConfigSettingToggleControl speedTreesToggle = new();
        speedTreesToggle.SettingName = "[Beta] Import SpeedTrees";
        speedTreesToggle.SettingLabel = "Export All: imports SpeedTree foliage and trees into UE5.";
        speedTreesToggle.SettingValue = _config.GetGenerateSpeedTrees().ToString();
        speedTreesToggle.ChangeButton.Click += GenerateSpeedTrees_OnClick;
        UnrealConfigPanel.Children.Add(speedTreesToggle);
    }

    private void Source2Path_OnClick(object sender, RoutedEventArgs e)
    {
        PopulateS2ConfigPanel();
    }

    private void SBoxExportEnabled_OnClick(object sender, RoutedEventArgs e)
    {
        _config.SetSBoxExportEnabled(!_config.GetSBoxExportEnabled());
        if (_config.GetSBoxExportEnabled())
            _config.SetSaveShaderHLSL(true);

        PopulateS2ConfigPanel();
    }

    private void UnrealInteropPath_OnClick(object sender, RoutedEventArgs e)
    {
        OpenUnrealInteropPathDialog();
        PopulateUEConfigPanel();
    }

    public void OpenUnrealInteropPathDialog()
    {
        //ShowUEMessage();
        //return;
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
        {
            bool success = false;
            while (!success)
            {
                dialog.Description = "Select the folder where you want to import to unreal engine (eg Content folder)";
                System.Windows.Forms.DialogResult result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    success = _config.TrySetUnrealInteropPath(dialog.SelectedPath);
                }
                else
                {
                    return;
                }
            }
        }
    }

    private void UnrealInteropEnabled_OnClick(object sender, RoutedEventArgs e)
    {
        if (_config.GetUnrealInteropPath() == "")
        {
            MessageBox.Show("Please set the path to the Unreal Engine content folder first.");
            return;
        }
        _config.SetUnrealInteropEnabled(!_config.GetUnrealInteropEnabled());
        PopulateUEConfigPanel();
    }

    private void GenerateSkybox_OnClick(object sender, RoutedEventArgs e)
    {
        _config.SetGenerateSkybox(!_config.GetGenerateSkybox());
        PopulateUEConfigPanel();
    }

    private void GenerateLights_OnClick(object sender, RoutedEventArgs e)
    {
        _config.SetGenerateLights(!_config.GetGenerateLights());
        PopulateUEConfigPanel();
    }

    private void GenerateFog_OnClick(object sender, RoutedEventArgs e)
    {
        _config.SetGenerateFog(!_config.GetGenerateFog());
        PopulateUEConfigPanel();
    }

    private void GenerateAtmosphere_OnClick(object sender, RoutedEventArgs e)
    {
        _config.SetGenerateAtmosphere(!_config.GetGenerateAtmosphere());
        PopulateUEConfigPanel();
    }

    private void GenerateDecals_OnClick(object sender, RoutedEventArgs e)
    {
        _config.SetGenerateDecals(!_config.GetGenerateDecals());
        PopulateUEConfigPanel();
    }

    private void GenerateSpeedTrees_OnClick(object sender, RoutedEventArgs e)
    {
        _config.SetGenerateSpeedTrees(!_config.GetGenerateSpeedTrees());
        PopulateUEConfigPanel();
    }
}
