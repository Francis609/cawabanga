using System.Diagnostics;

namespace CawaBanga.Api.Services;

public sealed class ResolveConnector
{
    private readonly IConfiguration _configuration;

    public ResolveConnector(IConfiguration configuration) => _configuration = configuration;

    public object GetStatus()
    {
        var configuredPath = _configuration["Integrations:Resolve:ExecutablePath"];
        var executable = ResolveExecutablePath(configuredPath);
        return new
        {
            status = executable is null ? "not-detected" : "detected",
            executablePath = executable,
            workflow = "Proposal-only; no project is opened or modified without separate user approval."
        };
    }

    private static string? ResolveExecutablePath(string? configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(configuredPath));
        }

        if (OperatingSystem.IsWindows())
        {
            candidates.AddRange(
            [
                @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe"
            ]);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<bool> LaunchProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        var executable = ResolveExecutablePath(_configuration["Integrations:Resolve:ExecutablePath"]);
        if (executable is null || !File.Exists(projectPath))
        {
            return false;
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory
        });
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }
}
