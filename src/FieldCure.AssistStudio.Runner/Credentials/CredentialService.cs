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
    const string Resource = "FieldCure.AssistStudio";
    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;

    public string? GetApiKey(string presetName) =>
        RetrieveByUserName(presetName);

    public void SetApiKey(string presetName, string apiKey) =>
        Store(presetName, apiKey);

    public string? GetMcpEnvVar(string serverId, string key) =>
        RetrieveByUserName($"McpEnv_{serverId}_{key}");

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
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern void CredFree(IntPtr buffer);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    #endregion
}
