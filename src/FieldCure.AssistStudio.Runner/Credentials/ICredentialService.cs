namespace FieldCure.AssistStudio.Runner.Credentials;

/// <summary>
/// Credential resolution service for API keys and MCP environment variables.
/// </summary>
public interface ICredentialService
{
    /// <summary>Retrieves the API key for a provider preset.</summary>
    string? GetApiKey(string presetName);

    /// <summary>Stores an API key for a provider preset.</summary>
    void SetApiKey(string presetName, string apiKey);

    /// <summary>Retrieves an MCP server environment variable value.</summary>
    string? GetMcpEnvVar(string serverId, string key);

    /// <summary>Stores an MCP server environment variable value.</summary>
    void SetMcpEnvVar(string serverId, string key, string value);
}
