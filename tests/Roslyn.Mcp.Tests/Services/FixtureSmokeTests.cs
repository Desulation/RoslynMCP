using Microsoft.Extensions.Logging.Abstractions;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class FixtureSmokeTests
{
    [TestMethod]
    public async Task GetSolutionAsync_SampleSolution_LoadsThreeProjects()
    {
        using var service = new CodeAnalysisService(NullLogger<CodeAnalysisService>.Instance);
        var solution = await service.GetSolutionAsync(TestPaths.SampleSolutionPath);
        CollectionAssert.AreEquivalent(
            new[] { "Sample.App", "Sample.Core(net10.0)", "Sample.Core(netstandard2.0)" },
            solution.Projects.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void TestOutput_ContainsNoMicrosoftBuildAssemblies()
    {
        var dir = Path.GetDirectoryName(typeof(FixtureSmokeTests).Assembly.Location)!;
        var offenders = Directory.GetFiles(dir, "Microsoft.Build*.dll").Select(Path.GetFileName).ToArray();
        CollectionAssert.AreEquivalent(new[] { "Microsoft.Build.Locator.dll" }, offenders);
    }
}
