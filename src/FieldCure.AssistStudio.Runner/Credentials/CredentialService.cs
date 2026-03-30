using System.Runtime.Versioning;
using Windows.Security.Credentials;

namespace FieldCure.AssistStudio.Runner.Credentials;

/// <summary>
/// Windows PasswordVault based credential service.
/// Uses the same Resource/UserName format as AssistStudio for API key interoperability.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialService : ICredentialService
{
    const string Resource = "FieldCure.AssistStudio";

    public string? GetApiKey(string presetName) =>
        Retrieve(presetName);

    public void SetApiKey(string presetName, string apiKey) =>
        Store(presetName, apiKey);

    public string? GetMcpEnvVar(string serverId, string key) =>
        Retrieve($"McpEnv_{serverId}_{key}");

    public void SetMcpEnvVar(string serverId, string key, string value) =>
        Store($"McpEnv_{serverId}_{key}", value);

    void Store(string userName, string secret)
    {
        var vault = new PasswordVault();

        // Remove existing entry if any
        var existing = FindCredential(vault, userName);
        if (existing is not null)
            vault.Remove(existing);

        if (!string.IsNullOrEmpty(secret))
            vault.Add(new PasswordCredential(Resource, userName, secret));
    }

    string? Retrieve(string userName)
    {
        var vault = new PasswordVault();
        var credential = FindCredential(vault, userName);
        if (credential is null) return null;

        credential.RetrievePassword();
        return credential.Password;
    }

    static PasswordCredential? FindCredential(PasswordVault vault, string userName)
    {
        try
        {
            var results = vault.FindAllByResource(Resource);
            return results.FirstOrDefault(c => c.UserName == userName);
        }
        catch
        {
            // FindAllByResource throws if no entries exist for the resource.
            return null;
        }
    }
}
