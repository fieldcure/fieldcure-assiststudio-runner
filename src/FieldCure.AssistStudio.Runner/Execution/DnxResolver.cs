namespace FieldCure.AssistStudio.Runner.Execution;

/// <summary>
/// Resolves an absolute path to the <c>dnx</c> launcher. <c>dnx</c> ships with
/// the .NET 10 SDK and on Windows is a <c>.cmd</c> shim; <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// with <c>UseShellExecute=false</c> only searches PATH for <c>.exe</c> by default,
/// so callers that spawn <c>dnx</c> directly (the schtasks command-line builder,
/// the stateless MCP server discovery) need a resolved absolute path.
/// </summary>
internal static class DnxResolver
{
    /// <summary>Cached absolute path to <c>dnx</c>; computed lazily on first access.</summary>
    static readonly Lazy<string?> _path = new(Resolve);

    /// <summary>
    /// Returns the absolute path to <c>dnx</c>, or <see langword="null"/> when the
    /// .NET 10 SDK is not installed (no shim found on PATH).
    /// </summary>
    public static string? Path => _path.Value;

    /// <summary>Walks <c>PATH</c> looking for a <c>dnx</c> launcher with a runnable extension.</summary>
    static string? Resolve()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        string[] extensions = OperatingSystem.IsWindows()
            ? [".cmd", ".exe", ".bat", ".ps1"]
            : [""];

        foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = System.IO.Path.Combine(dir, $"dnx{ext}");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
