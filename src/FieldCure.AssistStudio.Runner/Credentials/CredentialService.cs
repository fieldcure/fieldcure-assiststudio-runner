using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FieldCure.AssistStudio.Runner.Credentials;

/// <summary>
/// Windows Credential Manager based credential service.
/// Uses CredEnumerate P/Invoke to read credentials stored by AssistStudio's UWP PasswordVault
/// (Resource = "FieldCure.AssistStudio", UserName = provider type or env key).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialService : ICredentialService
{
    /// <summary>Windows Credential Manager resource/target prefix.</summary>
    const string Resource = "FieldCure.AssistStudio";

    /// <summary>CRED_TYPE_GENERIC value for Windows Credential Manager.</summary>
    const int CredTypeGeneric = 1;

    /// <summary>CRED_PERSIST_LOCAL_MACHINE value for Windows Credential Manager.</summary>
    const int CredPersistLocalMachine = 2;

    /// <inheritdoc />
    public string? GetApiKey(string presetName) =>
        RetrieveByUserName(presetName);

    /// <inheritdoc />
    public void SetApiKey(string presetName, string apiKey) =>
        Store(presetName, apiKey);

    /// <inheritdoc />
    public string? GetMcpEnvVar(string serverId, string key) =>
        RetrieveByUserName($"McpEnv_{serverId}_{key}");

    /// <inheritdoc />
    public void SetMcpEnvVar(string serverId, string key, string value) =>
        Store($"McpEnv_{serverId}_{key}", value);

    /// <summary>
    /// Checks whether a credential with the given userName exists under the resource.
    /// </summary>
    internal bool HasCredential(string userName) =>
        RetrieveByUserName(userName) is not null;

    /// <summary>
    /// Enumerates all userNames stored under the resource.
    /// </summary>
    internal IReadOnlyList<string> EnumerateUserNames()
    {
        var result = new List<string>();
        if (!CredEnumerate($"{Resource}*", 0, out var count, out var credArrayPtr))
            return result;

        try
        {
            for (int i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(credArrayPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                if (cred.Type == CredTypeGeneric && !string.IsNullOrEmpty(cred.UserName))
                    result.Add(cred.UserName);
            }
        }
        finally
        {
            CredFree(credArrayPtr);
        }

        return result;
    }

    /// <summary>
    /// Retrieves a credential by matching UserName across all entries under the resource.
    /// PasswordVault stores Resource as part of TargetName and UserName separately;
    /// CredEnumerate with a resource prefix filter finds the matching entry.
    /// </summary>
    string? RetrieveByUserName(string userName)
    {
        if (!CredEnumerate($"{Resource}*", 0, out var count, out var credArrayPtr))
            return null;

        try
        {
            for (int i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(credArrayPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);

                if (cred.Type == CredTypeGeneric
                    && string.Equals(cred.UserName, userName, StringComparison.Ordinal)
                    && cred.CredentialBlobSize > 0
                    && cred.CredentialBlob != IntPtr.Zero)
                {
                    var bytes = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                    return Encoding.Unicode.GetString(bytes);
                }
            }
        }
        finally
        {
            CredFree(credArrayPtr);
        }

        return null;
    }

    /// <summary>
    /// Stores a credential using CredWrite with the same TargetName/UserName format
    /// as PasswordVault for credential sharing with AssistStudio.
    /// </summary>
    void Store(string userName, string secret)
    {
        var targetName = $"{Resource}/{userName}";
        var secretBytes = Encoding.Unicode.GetBytes(secret);

        var credential = new CREDENTIAL
        {
            Type = CredTypeGeneric,
            TargetName = targetName,
            UserName = userName,
            CredentialBlobSize = (uint)secretBytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(secretBytes.Length),
            Persist = CredPersistLocalMachine,
        };

        try
        {
            Marshal.Copy(secretBytes, 0, credential.CredentialBlob, secretBytes.Length);

            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException(
                    $"Failed to write credential '{targetName}'. Error: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    #region P/Invoke

#pragma warning disable SYSLIB1054
    /// <summary>Writes a credential to Windows Credential Manager.</summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    /// <summary>Enumerates credentials matching a filter in Windows Credential Manager.</summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentials);

    /// <summary>Frees a credential buffer allocated by the Credential Manager.</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern void CredFree(IntPtr buffer);
#pragma warning restore SYSLIB1054

    /// <summary>Interop structure matching the native CREDENTIAL layout.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        /// <summary>Reserved flags.</summary>
        public uint Flags;
        /// <summary>Credential type (e.g., CRED_TYPE_GENERIC).</summary>
        public int Type;
        /// <summary>Target name identifying the credential entry.</summary>
        public string TargetName;
        /// <summary>Optional comment associated with the credential.</summary>
        public string Comment;
        /// <summary>Timestamp of last modification (FILETIME).</summary>
        public long LastWritten;
        /// <summary>Size of the credential blob in bytes.</summary>
        public uint CredentialBlobSize;
        /// <summary>Pointer to the credential secret data.</summary>
        public IntPtr CredentialBlob;
        /// <summary>Persistence scope (e.g., CRED_PERSIST_LOCAL_MACHINE).</summary>
        public int Persist;
        /// <summary>Number of extended attributes.</summary>
        public uint AttributeCount;
        /// <summary>Pointer to extended attribute array.</summary>
        public IntPtr Attributes;
        /// <summary>Alias for the target name.</summary>
        public string TargetAlias;
        /// <summary>User name associated with the credential.</summary>
        public string UserName;
    }

    #endregion
}
