using FieldCure.AssistStudio.Runner.Models;

namespace FieldCure.AssistStudio.Runner.Tests;

[TestClass]
public class RunnerConfigTests
{
    [TestMethod]
    public void ResolveModel_Found()
    {
        var config = new RunnerConfig
        {
            Models = new()
            {
                ["Claude"] = new ModelConfig
                {
                    ProviderType = "Claude",
                    ModelId = "claude-sonnet-4-20250514",
                    Temperature = 0.5,
                    MaxTokens = 8192,
                }
            }
        };

        var providerModel = config.ResolveModel("Claude");
        Assert.IsNotNull(providerModel);
        Assert.AreEqual("Claude", providerModel.Name);
        Assert.AreEqual("Claude", providerModel.ProviderType);
        Assert.AreEqual("claude-sonnet-4-20250514", providerModel.ModelId);
        Assert.AreEqual(0.5, providerModel.Temperature);
        Assert.AreEqual(8192, providerModel.MaxTokens);
    }

    [TestMethod]
    public void ResolveModel_NotFound_ReturnsNull()
    {
        var config = new RunnerConfig();
        Assert.IsNull(config.ResolveModel("Unknown"));
    }

    [TestMethod]
    public void ResolveModel_Null_ReturnsNull()
    {
        var config = new RunnerConfig();
        Assert.IsNull(config.ResolveModel(null));
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
                DefaultModelName = "Test Model",
                LogRetentionDays = 7,
                FallbackChannel = "test-alerts",
                Models = new()
                {
                    ["Test Model"] = new ModelConfig
                    {
                        ProviderType = "OpenAI",
                        ModelId = "gpt-4o",
                    }
                }
            };

            config.Save(tempDir);
            var loaded = RunnerConfig.Load(tempDir);

            Assert.AreEqual("Test Model", loaded.DefaultModelName);
            Assert.AreEqual(7, loaded.LogRetentionDays);
            Assert.AreEqual("test-alerts", loaded.FallbackChannel);
            Assert.IsTrue(loaded.Models.ContainsKey("Test Model"));
            Assert.AreEqual("OpenAI", loaded.Models["Test Model"].ProviderType);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
