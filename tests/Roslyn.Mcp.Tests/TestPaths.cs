using System.Reflection;

namespace Roslyn.Mcp.Tests;

internal static class TestPaths
{
    public static string FixturesDir { get; } = ReadMetadataPath("FixturesDir");

    public static string SampleSolutionDir { get; } = Path.Combine(FixturesDir, "SampleSolution");

    public static string SampleSolutionPath { get; } = Path.Combine(SampleSolutionDir, "Sample.slnx");

    public static string ServerExePath { get; } = ReadMetadataPath("ServerExePath");

    public static string CoreFile(string name) => Path.Combine(SampleSolutionDir, "Sample.Core", name);

    private static string ReadMetadataPath(string key) => Path.GetFullPath(
        typeof(TestPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key)
            .Value ?? throw new InvalidOperationException($"{key} assembly metadata has no value."));
}
