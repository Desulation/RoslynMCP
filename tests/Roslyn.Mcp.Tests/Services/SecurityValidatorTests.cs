using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class SecurityValidatorTests
{
    private const string EmptyPatternMessage = "Search pattern must not be empty.";
    private const string LongPatternMessage = "Search pattern must be 256 characters or fewer.";

    private readonly SecurityValidator _validator = new();

    [TestMethod]
    public void ValidateSolutionPath_RelativePath_False()
    {
        var path = Path.Combine("SampleSolution", "Sample.slnx");

        Assert.IsFalse(_validator.ValidateSolutionPath(path));
    }

    [TestMethod]
    public void ValidateSolutionPath_ParentSegment_False()
    {
        // Resolves to the existing Sample.slnx, so only the ".." segment can cause the rejection.
        var path = TestPaths.SampleSolutionDir + Path.DirectorySeparatorChar + "Sample.Core"
            + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "Sample.slnx";

        Assert.IsTrue(Path.IsPathFullyQualified(path));
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(_validator.ValidateSolutionPath(path));
    }

    [TestMethod]
    public void ValidateSolutionPath_WrongExtension_False()
    {
        var path = TestPaths.CoreFile("Order.cs");

        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(_validator.ValidateSolutionPath(path));
    }

    [TestMethod]
    public void ValidateSolutionPath_MissingFile_False()
    {
        var path = Path.Combine(TestPaths.SampleSolutionDir, "Missing.slnx");

        Assert.IsFalse(_validator.ValidateSolutionPath(path));
    }

    [TestMethod]
    public void ValidateSolutionPath_ExistingDirectory_True()
    {
        Assert.IsTrue(_validator.ValidateSolutionPath(TestPaths.SampleSolutionDir));
    }

    [TestMethod]
    public void ValidateSolutionPath_ExistingSlnx_True()
    {
        Assert.IsTrue(_validator.ValidateSolutionPath(TestPaths.SampleSolutionPath));
    }

    [TestMethod]
    public void ValidateSourceFilePath_ExistingCs_True()
    {
        Assert.IsTrue(_validator.ValidateSourceFilePath(TestPaths.CoreFile("Order.cs")));
    }

    [TestMethod]
    public void ValidateSourceFilePath_Csproj_False()
    {
        var path = TestPaths.CoreFile("Sample.Core.csproj");

        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(_validator.ValidateSourceFilePath(path));
    }

    [TestMethod]
    public void ValidateSearchPattern_Empty_ReturnsMessage()
    {
        Assert.AreEqual(EmptyPatternMessage, _validator.ValidateSearchPattern(""));
    }

    [TestMethod]
    public void ValidateSearchPattern_Whitespace_ReturnsMessage()
    {
        Assert.AreEqual(EmptyPatternMessage, _validator.ValidateSearchPattern(" \t "));
    }

    [TestMethod]
    public void ValidateSearchPattern_257Chars_ReturnsMessage()
    {
        Assert.AreEqual(LongPatternMessage, _validator.ValidateSearchPattern(new string('a', 257)));
    }

    [TestMethod]
    public void ValidateSearchPattern_256Chars_Null()
    {
        Assert.IsNull(_validator.ValidateSearchPattern(new string('a', 256)));
    }

    [TestMethod]
    public void ValidateSearchPattern_GenericSyntax_Null()
    {
        Assert.IsNull(_validator.ValidateSearchPattern("List<*>"));
    }

    [TestMethod]
    public void ValidateSearchPattern_Backtick_Null()
    {
        Assert.IsNull(_validator.ValidateSearchPattern("Dictionary`2"));
    }
}
