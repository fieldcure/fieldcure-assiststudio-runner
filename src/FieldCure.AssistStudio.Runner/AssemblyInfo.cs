using System.Runtime.Versioning;

// Runner depends on Windows Task Scheduler (schtasks) for job registration and
// Windows Credential Manager for credential storage. Declaring the platform at
// the assembly level enables CA1416 analyzer warnings in consumers that build
// on other OSes, while keeping the TargetFramework platform-neutral (net8.0)
// so PackAsTool and dnx continue to work.
[assembly: SupportedOSPlatform("windows")]
