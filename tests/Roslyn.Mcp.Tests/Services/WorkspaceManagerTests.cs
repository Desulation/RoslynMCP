using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class WorkspaceManagerTests
{
    private const string EditMarker = "// edited";

    // Shared by read-only tests that do not assert LoadCount (its value would depend on test order).
    private static WorkspaceManager s_manager = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        s_manager = CreateManager();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        s_manager.Dispose();
    }

    [TestMethod]
    public void ResolveSolutionPath_SlnxFile_ReturnsFullPath()
    {
        var path = Path.Combine(TestPaths.SampleSolutionDir, "Sample.Core", "..", "Sample.slnx");

        Assert.AreEqual(TestPaths.SampleSolutionPath, WorkspaceManager.ResolveSolutionPath(path));
    }

    [TestMethod]
    public void ResolveSolutionPath_CsprojFile_ReturnsFullPath()
    {
        var path = Path.Combine(TestPaths.SampleSolutionDir, "Sample.App", "..", "Sample.Core", "Sample.Core.csproj");

        Assert.AreEqual(TestPaths.CoreFile("Sample.Core.csproj"), WorkspaceManager.ResolveSolutionPath(path));
    }

    [TestMethod]
    public void ResolveSolutionPath_DirectoryWithOneSolution_ReturnsIt()
    {
        Assert.AreEqual(
            TestPaths.SampleSolutionPath,
            WorkspaceManager.ResolveSolutionPath(TestPaths.SampleSolutionDir));
    }

    [TestMethod]
    public void ResolveSolutionPath_DirectoryWithOnlyProject_ReturnsProject()
    {
        var directory = Path.Combine(TestPaths.SampleSolutionDir, "Sample.Core");

        Assert.AreEqual(TestPaths.CoreFile("Sample.Core.csproj"), WorkspaceManager.ResolveSolutionPath(directory));
    }

    [TestMethod]
    public void ResolveSolutionPath_DirectoryWithTwoSolutions_Throws()
    {
        using var copy = FixtureCopy.Create();
        File.Copy(copy.SolutionPath, Path.Combine(copy.Directory, "Other.slnx"));

        var ex = Assert.ThrowsExactly<ArgumentException>(() => WorkspaceManager.ResolveSolutionPath(copy.Directory));

        StringAssert.Contains(ex.Message, "Sample.slnx");
        StringAssert.Contains(ex.Message, "Other.slnx");
    }

    [TestMethod]
    public void ResolveSolutionPath_EmptyDirectory_Throws()
    {
        var directory = Directory.CreateTempSubdirectory("roslyn-mcp-tests-");
        try
        {
            Assert.ThrowsExactly<ArgumentException>(() => WorkspaceManager.ResolveSolutionPath(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ResolveSolutionPath_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(TestPaths.SampleSolutionDir, "Missing.slnx");

        Assert.ThrowsExactly<FileNotFoundException>(() => WorkspaceManager.ResolveSolutionPath(path));
    }

    [TestMethod]
    public async Task ResolveSolutionPath_TrailingSeparatorAndCase_SameCacheEntry()
    {
        using var manager = CreateManager();
        var withSeparator = TestPaths.SampleSolutionDir + Path.DirectorySeparatorChar;

        Assert.AreEqual(TestPaths.SampleSolutionPath, WorkspaceManager.ResolveSolutionPath(withSeparator));
        await manager.GetSolutionAsync(TestPaths.SampleSolutionDir);
        await manager.GetSolutionAsync(withSeparator);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            await manager.GetSolutionAsync(TestPaths.SampleSolutionDir.ToUpperInvariant());

        Assert.AreEqual(1, manager.LoadCount);
    }

    [TestMethod]
    public async Task GetSolutionAsync_SecondCall_DoesNotReload()
    {
        using var manager = CreateManager();

        await manager.GetSolutionAsync(TestPaths.SampleSolutionPath);
        await manager.GetSolutionAsync(TestPaths.SampleSolutionPath);

        Assert.AreEqual(1, manager.LoadCount);
    }

    [TestMethod]
    public async Task GetSolutionAsync_EditedSourceFile_NewTextVisibleWithoutReload()
    {
        using var copy = FixtureCopy.Create();
        using var manager = CreateManager();
        var orderFile = copy.CoreFile("Order.cs");
        var before = await manager.GetSolutionAsync(copy.SolutionPath);
        var idsBefore = before.GetDocumentIdsWithFilePath(orderFile);

        AppendAndBumpWriteTime(orderFile, EditMarker);
        var after = await manager.GetSolutionAsync(copy.SolutionPath);

        var texts = await DocumentTextsAsync(after, orderFile);
        Assert.IsNotEmpty(texts);
        foreach (var text in texts)
            Assert.IsTrue(text.EndsWith(EditMarker, StringComparison.Ordinal), text);
        Assert.AreEqual(1, manager.LoadCount);
        CollectionAssert.AreEquivalent(idsBefore.ToArray(), after.GetDocumentIdsWithFilePath(orderFile).ToArray());
    }

    [TestMethod]
    public async Task GetSolutionAsync_AddedSourceFile_Reloads()
    {
        using var copy = FixtureCopy.Create();
        using var manager = CreateManager();
        await manager.GetSolutionAsync(copy.SolutionPath);

        File.WriteAllText(copy.CoreFile("Extra.cs"), "namespace Sample.Core;\n\npublic static class Extra { }\n");
        var solution = await manager.GetSolutionAsync(copy.SolutionPath);

        Assert.AreEqual(2, manager.LoadCount);
        Assert.IsTrue(solution.Projects.SelectMany(p => p.Documents).Any(d => d.Name == "Extra.cs"));
    }

    [TestMethod]
    public async Task GetSolutionAsync_ChangedProjectFile_Reloads()
    {
        using var copy = FixtureCopy.Create();
        using var manager = CreateManager();
        await manager.GetSolutionAsync(copy.SolutionPath);

        BumpWriteTime(copy.AppFile("Sample.App.csproj"));
        await manager.GetSolutionAsync(copy.SolutionPath);

        Assert.AreEqual(2, manager.LoadCount);
    }

    [TestMethod]
    public async Task GetSolutionAsync_TouchedAssetsFile_Reloads()
    {
        using var copy = FixtureCopy.Create();
        using var manager = CreateManager();
        await manager.GetSolutionAsync(copy.SolutionPath);

        BumpWriteTime(Path.Combine(copy.Directory, "Sample.Core", "obj", "project.assets.json"));
        await manager.GetSolutionAsync(copy.SolutionPath);

        Assert.AreEqual(2, manager.LoadCount);
    }

    [TestMethod]
    public async Task GetSolutionAsync_NewDirectoryBuildProps_Reloads()
    {
        using var copy = FixtureCopy.Create();
        using var manager = CreateManager();
        await manager.GetSolutionAsync(copy.SolutionPath);

        // Chains to the solution-level props so the reloaded projects still evaluate the same way.
        File.WriteAllText(
            copy.CoreFile("Directory.Build.props"),
            "<Project>\n" +
            "  <Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />\n" +
            "</Project>\n");
        await manager.GetSolutionAsync(copy.SolutionPath);

        Assert.AreEqual(2, manager.LoadCount);
    }

    [TestMethod]
    public async Task GetSolutionAsync_FileLockedDuringRefresh_RetriesNextCall()
    {
        using var copy = FixtureCopy.Create();
        using var manager = CreateManager();
        var orderFile = copy.CoreFile("Order.cs");
        await manager.GetSolutionAsync(copy.SolutionPath);

        AppendAndBumpWriteTime(orderFile, EditMarker);
        using (new FileStream(orderFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await manager.GetSolutionAsync(copy.SolutionPath);

            foreach (var text in await DocumentTextsAsync(locked, orderFile))
                Assert.IsFalse(text.EndsWith(EditMarker, StringComparison.Ordinal), text);
        }

        var unlocked = await manager.GetSolutionAsync(copy.SolutionPath);

        var texts = await DocumentTextsAsync(unlocked, orderFile);
        Assert.IsNotEmpty(texts);
        foreach (var text in texts)
            Assert.IsTrue(text.EndsWith(EditMarker, StringComparison.Ordinal), text);
        Assert.AreEqual(1, manager.LoadCount);
    }

    [TestMethod]
    public async Task GetSolutionAsync_CancelledDuringLoad_ThrowsAndNextCallLoads()
    {
        using var manager = CreateManager();
        using var cts = new CancellationTokenSource();

        // GetSolutionAsync runs synchronously into the MSBuild open before it first yields, so
        // cancelling after it returns cancels the load itself rather than the wait for the gate.
        var cancelled = manager.GetSolutionAsync(TestPaths.SampleSolutionPath, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        Assert.AreEqual(1, manager.LoadCount);

        var solution = await manager.GetSolutionAsync(TestPaths.SampleSolutionPath, CancellationToken.None);

        Assert.AreEqual(2, manager.LoadCount);
        Assert.AreEqual(3, solution.Projects.Count());
    }

    [TestMethod]
    public async Task ReloadAsync_SampleSolution_ListsProjects()
    {
        var summary = await s_manager.ReloadAsync(TestPaths.SampleSolutionPath);

        Assert.AreEqual(3, summary.ProjectCount);
        CollectionAssert.AreEqual(
            new[] { "Sample.App", "Sample.Core(net10.0)", "Sample.Core(netstandard2.0)" },
            summary.Projects);
        Assert.IsGreaterThan(0, summary.DocumentCount);
    }

    [TestMethod]
    public async Task TryTakeLoadHint_CleanSolution_False()
    {
        await s_manager.GetSolutionAsync(TestPaths.SampleSolutionPath);

        Assert.IsFalse(s_manager.TryTakeLoadHint(TestPaths.SampleSolutionPath, out var warningCount));
        Assert.AreEqual(0, warningCount);
    }

    [TestMethod]
    public async Task TryTakeLoadHint_SolutionWithLoadWarnings_TrueOnceThenFalse()
    {
        using var copy = FixtureCopy.Create();
        var appProject = copy.AppFile("Sample.App.csproj");
        const string coreReference = "<ProjectReference Include=\"../Sample.Core/Sample.Core.csproj\" />";
        var projectXml = File.ReadAllText(appProject);
        Assert.Contains(coreReference, projectXml);
        File.WriteAllText(
            appProject,
            projectXml.Replace(
                coreReference,
                coreReference + "\n    <ProjectReference Include=\"../Nope/Nope.csproj\" />",
                StringComparison.Ordinal));
        DotnetCli.Restore(copy.SolutionPath, ignoreFailure: true);
        using var manager = CreateManager();

        await manager.GetSolutionAsync(copy.SolutionPath);

        Assert.IsTrue(manager.TryTakeLoadHint(copy.SolutionPath, out var warningCount));
        Assert.IsGreaterThanOrEqualTo(1, warningCount);
        Assert.IsFalse(manager.TryTakeLoadHint(copy.SolutionPath, out _));

        await manager.ReloadAsync(copy.SolutionPath);

        Assert.IsTrue(manager.TryTakeLoadHint(copy.SolutionPath, out _));
    }

    private static WorkspaceManager CreateManager() => new(
        NullLogger<WorkspaceManager>.Instance,
        new WorkspaceOptions { RefreshInterval = TimeSpan.Zero });

    // Moves the write time explicitly so the change is seen even on file systems with coarse timestamps.
    private static void BumpWriteTime(string path)
    {
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    private static void AppendAndBumpWriteTime(string path, string text)
    {
        var previous = File.GetLastWriteTimeUtc(path);
        File.AppendAllText(path, text);
        File.SetLastWriteTimeUtc(path, previous.AddMinutes(1));
    }

    private static async Task<List<string>> DocumentTextsAsync(Solution solution, string filePath)
    {
        var texts = new List<string>();
        foreach (var documentId in solution.GetDocumentIdsWithFilePath(filePath))
            texts.Add((await solution.GetDocument(documentId)!.GetTextAsync()).ToString());
        return texts;
    }
}
