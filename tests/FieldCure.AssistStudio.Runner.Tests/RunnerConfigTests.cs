using FieldCure.AssistStudio.Runner.Models;

namespace FieldCure.AssistStudio.Runner.Tests;

[TestClass]
public class RunnerConfigTests
{
    [TestMethod]
    public void ResolvePreset_Found()
    {
        var config = new RunnerConfig
        {
            Presets = new()
            {
                ["Claude"] = new PresetConfig
                {
                    ProviderType = "Claude",
                    ModelId = "claude-sonnet-4-20250514",
                    Temperature = 0.5,
                    MaxTokens = 8192,
                }
            }
        };

        var preset = config.ResolvePreset("Claude");
        Assert.IsNotNull(preset);
        Assert.AreEqual("Claude", preset.Name);
        Assert.AreEqual("Claude", preset.ProviderType);
        Assert.AreEqual("claude-sonnet-4-20250514", preset.ModelId);
        Assert.AreEqual(0.5, preset.Temperature);
        Assert.AreEqual(8192, preset.MaxTokens);
    }

    [TestMethod]
    public void ResolvePreset_NotFound_ReturnsNull()
    {
        var config = new RunnerConfig();
        Assert.IsNull(config.ResolvePreset("Unknown"));
    }

    [TestMethod]
    public void ResolvePreset_Null_ReturnsNull()
    {
        var config = new RunnerConfig();
        Assert.IsNull(config.ResolvePreset(null));
    }

    [TestMethod]
    public void DefaultValues()
    {
        var config = new RunnerConfig();
        Assert.AreEqual(30, config.LogRetentionDays);
        Assert.AreEqual(3, config.Retry.MaxAttempts);
        Assert.AreEqual(1000, config.Retry.InitialDelayMs);
        Assert.AreEqual(2.0, config.Retry.BackoffMultiplier);
    }

    [TestMethod]
    public void SaveAndLoad_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"runner_test_{Guid.NewGuid():N}");
        try
        {
            var config = new RunnerConfig
            {
                DefaultPresetName = "Test Preset",
                LogRetentionDays = 7,
                FallbackChannel = "test-alerts",
                Presets = new()
                {
                    ["Test Preset"] = new PresetConfig
                    {
                        ProviderType = "OpenAI",
                        ModelId = "gpt-4o",
                    }
                }
            };

            config.Save(tempDir);
            var loaded = RunnerConfig.Load(tempDir);

            Assert.AreEqual("Test Preset", loaded.DefaultPresetName);
            Assert.AreEqual(7, loaded.LogRetentionDays);
            Assert.AreEqual("test-alerts", loaded.FallbackChannel);
            Assert.IsTrue(loaded.Presets.ContainsKey("Test Preset"));
            Assert.AreEqual("OpenAI", loaded.Presets["Test Preset"].ProviderType);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
