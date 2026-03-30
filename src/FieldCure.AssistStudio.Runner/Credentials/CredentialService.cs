using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FieldCure.AssistStudio.Runner.Credentials;

/// <summary>
/// Windows PasswordVault (Credential Manager) based credential service.
/// Shares the same resource name as AssistStudio for API key interoperability.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialService : ICredentialService
{
    const string Resource = "FieldCure.AssistStudio";
    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;

    public string? GetApiKey(string presetName) =>
        Retrieve($"{Resource}:{presetName}");

    public void SetApiKey(string presetName, string apiKey) =>
        Store($"{Resource}:{presetName}", apiKey);

    public string? GetMcpEnvVar(string serverId, string key) =>
        Retrieve($"{Resource}:McpEnv_{serverId}_{key}");

    public void SetMcpEnvVar(string serverId, string key, string value) =>
        Store($"{Resource}:McpEnv_{serverId}_{key}", value);

    void Store(string credentialName, string secret)
    {
        var secretBytes = Encoding.Unicode.GetBytes(secret);

        var credential = new CREDENTIAL
        {
            Type = CredTypeGeneric,
            TargetName = credentialName,
            CredentialBlobSize = (uint)secretBytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(secretBytes.Length),
            Persist = CredPersistLocalMachine,
        };

        try
        {
            Marshal.Copy(secretBytes, 0, credential.CredentialBlob, secretBytes.Length);

            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException(
                    $"Failed to write credential '{credentialName}'. Error: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    string? Retrieve(string credentialName)
    {
        if (!CredRead(credentialName, CredTypeGeneric, 0, out var credentialPtr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return null;

            var secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, (int)credential.CredentialBlobSize);
            return Encoding.Unicode.GetString(secretBytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    #pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CredDelete(string target, int type, int flags);

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
}
