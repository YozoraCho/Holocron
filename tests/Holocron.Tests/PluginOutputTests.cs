using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Holocron.Tests;

public class PluginOutputTests
{
    private static string Metadata(string key) =>
        typeof(PluginOutputTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string Output(string plugin) =>
        Path.Combine(Metadata("HolocronRoot"), "src", plugin, "bin", Metadata("HolocronConfiguration"), "net10.0");

    [Theory]
    [InlineData("CombatMeter", "Combat Meter")]
    [InlineData("TacticalReadout", "Tactical Readout")]
    public void Build_WritesAManifest_AndLeavesOutWhatAshlaProvides(string plugin, string name)
    {
        var folder = Output(plugin);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var root = manifest.RootElement;

        Assert.Equal(plugin, root.GetProperty("Id").GetString());
        Assert.Equal(name, root.GetProperty("Name").GetString());
        Assert.Equal(1, root.GetProperty("ApiLevel").GetInt32());
        Assert.Equal("https://github.com/YozoraCho/Holocron", root.GetProperty("Repository").GetString());
        foreach (var file in root.GetProperty("Files").EnumerateObject())
        {
            using var stream = File.OpenRead(Path.Combine(folder, file.Name));
            Assert.Equal(file.Value.GetString(), Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
        foreach (var shared in new[] { "Ashla.SDK.dll", "Ashla.UI.dll", "Hexa.NET.ImGui.dll", "HexaGen.Runtime.dll", "cimgui.dll" })
            Assert.Empty(Directory.GetFiles(folder, shared, SearchOption.AllDirectories));
    }

    [Fact]
    public void TacticalReadout_ShipsBarasData_WithItsLicense()
    {
        var folder = Output("TacticalReadout");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var files = manifest.RootElement.GetProperty("Files").EnumerateObject().Select(f => f.Name).ToList();

        Assert.Equal(54, files.Count(f => f.StartsWith("Fights/BARAS/", StringComparison.Ordinal) && f.EndsWith(".toml", StringComparison.Ordinal)));
        Assert.Contains("Fights/BARAS/LICENSE", files);
        Assert.Contains("Tomlyn.dll", files);
        var notices = File.ReadAllText(Path.Combine(folder, "THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("The BARAS Authors", notices);
        Assert.Contains("Alexandre Mutel", notices);
    }
}
