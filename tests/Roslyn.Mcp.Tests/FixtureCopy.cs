namespace Roslyn.Mcp.Tests;

/// <summary>
/// A restored copy of the SampleSolution fixture in a fresh temporary directory, for tests that
/// modify files. The checked-in fixture is never touched.
/// </summary>
internal sealed class FixtureCopy : IDisposable
{
    private FixtureCopy(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public string SolutionPath => Path.Combine(Directory, "Sample.slnx");

    public string CoreFile(string name) => Path.Combine(Directory, "Sample.Core", name);

    public string AppFile(string name) => Path.Combine(Directory, "Sample.App", name);

    public static FixtureCopy Create()
    {
        var copy = new FixtureCopy(
            Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests", Guid.NewGuid().ToString("N")));
        try
        {
            CopyDirectory(TestPaths.SampleSolutionDir, copy.Directory);
            DotnetCli.Restore(copy.SolutionPath);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file still held open (for example by a build node) leaves the directory behind in temp.
        }
    }

    // Copies everything except bin/ output; obj/ comes along so the copy starts from a restored state.
    private static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);

        foreach (var file in System.IO.Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));

        foreach (var child in System.IO.Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(child);
            if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase))
                CopyDirectory(child, Path.Combine(destination, name));
        }
    }
}
