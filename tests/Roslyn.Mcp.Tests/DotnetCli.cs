using System.Diagnostics;

namespace Roslyn.Mcp.Tests;

internal static class DotnetCli
{
    // Set in this process by MSBuildLocator; a child dotnet must resolve its own SDK instead.
    private static readonly string[] InheritedMSBuildVariables =
    {
        "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath"
    };

    /// <summary>
    /// Runs <c>dotnet restore</c> on a project or solution. Throws with the captured output on a
    /// non-zero exit code unless <paramref name="ignoreFailure"/> is set.
    /// </summary>
    public static void Restore(string projectOrSolution, bool ignoreFailure = false)
    {
        var fullPath = Path.GetFullPath(projectOrSolution);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(fullPath);
        foreach (var name in InheritedMSBuildVariables)
            startInfo.Environment.Remove(name);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'dotnet restore'.");

        // Drain both streams concurrently so neither pipe fills up and blocks the child.
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException(
                $"'dotnet restore {fullPath}' exited with code {process.ExitCode}.{Environment.NewLine}" +
                $"{stdout}{Environment.NewLine}{stderr.GetAwaiter().GetResult()}");
        }
    }
}
