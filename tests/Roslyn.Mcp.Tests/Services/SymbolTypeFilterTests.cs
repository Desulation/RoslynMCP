using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class SymbolTypeFilterTests
{
    private const string Source = """
        namespace N;

        public class PlainClass { public void Run() { } }
        public interface IThing { }
        public struct PlainStruct { }
        public enum Color { Red }
        public delegate void Handler();
        public record RecordClass;
        public record struct RecordStruct;
        """;

    private static Compilation s_compilation = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        s_compilation = CSharpCompilation.Create(
            "FilterTests",
            [CSharpSyntaxTree.ParseText(Source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
    }

    [TestMethod]
    public void Parse_Unknown_ThrowsWithAcceptedList()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => SymbolTypeFilter.Parse("class,widget"));

        Assert.AreEqual(
            "unknown symbol type 'widget'. Accepted: class, interface, struct, enum, record, delegate, method, property, field, event, namespace",
            ex.Message);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(",")]
    public void Parse_Empty_ThrowsWithAcceptedList(string symbolTypes)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => SymbolTypeFilter.Parse(symbolTypes));

        Assert.AreEqual(
            "no symbol types given. Accepted: class, interface, struct, enum, record, delegate, method, property, field, event, namespace",
            ex.Message);
    }

    [TestMethod]
    public void Parse_MixedCaseAndSpaces_Accepts()
    {
        var filter = SymbolTypeFilter.Parse(" Class , METHOD ");

        Assert.IsTrue(filter.Matches(Type("PlainClass")));
        Assert.IsTrue(filter.Matches(Type("PlainClass").GetMembers("Run").Single()));
        Assert.IsFalse(filter.Matches(Type("IThing")));
    }

    [TestMethod]
    public void Matches_Class_ExcludesInterfaceStructEnum()
    {
        var matched = MatchingTypeNames("class");

        Assert.Contains("PlainClass", matched);
        Assert.DoesNotContain("IThing", matched);
        Assert.DoesNotContain("PlainStruct", matched);
        Assert.DoesNotContain("Color", matched);
        Assert.DoesNotContain("Handler", matched);
    }

    [TestMethod]
    public void Matches_Interface_OnlyInterfaces()
    {
        CollectionAssert.AreEquivalent(new[] { "IThing" }, MatchingTypeNames("interface"));
    }

    [TestMethod]
    public void Matches_Record_ClassAndStructRecords()
    {
        CollectionAssert.AreEquivalent(new[] { "RecordClass", "RecordStruct" }, MatchingTypeNames("record"));
    }

    [TestMethod]
    public void Matches_Class_IncludesRecordClass()
    {
        CollectionAssert.AreEquivalent(new[] { "PlainClass", "RecordClass" }, MatchingTypeNames("class"));
    }

    [TestMethod]
    public void Matches_Struct_IncludesRecordStruct()
    {
        CollectionAssert.AreEquivalent(new[] { "PlainStruct", "RecordStruct" }, MatchingTypeNames("struct"));
    }

    [TestMethod]
    public void Matches_Delegate_OnlyDelegates()
    {
        CollectionAssert.AreEquivalent(new[] { "Handler" }, MatchingTypeNames("delegate"));
    }

    private static INamedTypeSymbol Type(string name) =>
        s_compilation.GetTypeByMetadataName("N." + name) ?? throw new InvalidOperationException($"N.{name} not found.");

    private static string[] MatchingTypeNames(string symbolTypes)
    {
        var filter = SymbolTypeFilter.Parse(symbolTypes);
        return s_compilation.Assembly.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "N")
            .GetTypeMembers()
            .Where(filter.Matches)
            .Select(t => t.Name)
            .ToArray();
    }
}
