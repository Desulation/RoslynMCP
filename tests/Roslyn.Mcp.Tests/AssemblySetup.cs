using Microsoft.Build.Locator;

namespace Roslyn.Mcp.Tests;

[TestClass]
public sealed class AssemblySetup
{
    // Must not mention any Microsoft.CodeAnalysis or Microsoft.Build type other than MSBuildLocator:
    // those assemblies may only load after MSBuild has been registered.
    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        if (!File.Exists(Path.Combine(TestPaths.SampleSolutionDir, "Sample.Core", "obj", "project.assets.json")))
            DotnetCli.Restore(TestPaths.SampleSolutionPath);
    }
}
