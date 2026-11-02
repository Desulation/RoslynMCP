using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class ComplexityCalculatorTests
{
    [TestMethod]
    public void Calculate_EmptyMethod_One()
    {
        Assert.AreEqual(1, CalculateFirstMember("void M() { }"));
    }

    [TestMethod]
    public void Calculate_IfElse_Two()
    {
        Assert.AreEqual(2, CalculateFirstMember("void M(bool a) { if (a) { } else { } }"));
    }

    [TestMethod]
    public void Calculate_WhileForForeachDo_Five()
    {
        var complexity = CalculateFirstMember("""
            void M(bool a, int[] xs)
            {
                while (a) { }
                for (var i = 0; i < 1; i++) { }
                foreach (var x in xs) { }
                do { } while (a);
            }
            """);

        Assert.AreEqual(5, complexity);
    }

    [TestMethod]
    public void Calculate_SwitchStatement_CountsCasesNotDefault()
    {
        var complexity = CalculateFirstMember("""
            void M(int x)
            {
                switch (x)
                {
                    case 1: break;
                    case 2: break;
                    default: break;
                }
            }
            """);

        Assert.AreEqual(3, complexity);
    }

    [TestMethod]
    public void Calculate_CaseWithWhen_CountsBoth()
    {
        var complexity = CalculateFirstMember("""
            void M(int x, bool a)
            {
                switch (x)
                {
                    case int n when a: break;
                }
            }
            """);

        Assert.AreEqual(3, complexity);
    }

    [TestMethod]
    public void Calculate_SwitchExpression_CountsArmsExceptDiscard()
    {
        Assert.AreEqual(3, CalculateFirstMember("int M(int x, int a, int b, int c) => x switch { 1 => a, 2 => b, _ => c };"));
    }

    [TestMethod]
    public void Calculate_ConditionalAndCoalesce_Three()
    {
        Assert.AreEqual(3, CalculateFirstMember("string M(bool a, string b, string? c, string d) => a ? b : c ?? d;"));
    }

    [TestMethod]
    public void Calculate_CoalesceAssignment_Two()
    {
        Assert.AreEqual(2, CalculateFirstMember("void M(string? a) { a ??= \"x\"; }"));
    }

    [TestMethod]
    public void Calculate_LogicalOperators_Three()
    {
        Assert.AreEqual(3, CalculateFirstMember("bool M(bool a, bool b, bool c) => a && b || c;"));
    }

    [TestMethod]
    public void Calculate_Patterns_AndOrCount()
    {
        Assert.AreEqual(3, CalculateFirstMember("bool M(int x) => x is > 0 and < 10 or 42;"));
    }

    [TestMethod]
    public void Calculate_Catch_Two()
    {
        Assert.AreEqual(2, CalculateFirstMember("void M() { try { } catch (System.Exception) { } }"));
    }

    [TestMethod]
    public void Calculate_NestedLambdaAndLocalFunction_CountTowardEnclosing()
    {
        var complexity = CalculateFirstMember("""
            void M(bool a)
            {
                System.Action f = () => { if (a) { } };
                void Local() { while (a) { } }
            }
            """);

        Assert.AreEqual(3, complexity);
    }

    [TestMethod]
    public void Calculate_Constructor_Counted()
    {
        var complexity = CalculateFirstMember("C(bool a) { if (a) { } }");

        Assert.AreEqual(2, complexity);
    }

    [TestMethod]
    public void Calculate_ExpressionBodiedAccessor_Counted()
    {
        var member = FirstMemberLike("bool a; int P { get => a ? 1 : 2; }");

        Assert.IsInstanceOfType<AccessorDeclarationSyntax>(member);
        Assert.AreEqual(2, ComplexityCalculator.Calculate(member));
    }

    [TestMethod]
    public void IsMemberLike_ExpressionBodiedProperty_True()
    {
        var root = Parse("bool a; int P => a ? 1 : 2; int this[int i] => a ? i : 0;");

        Assert.IsTrue(ComplexityCalculator.IsMemberLike(root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single()));
        Assert.IsTrue(ComplexityCalculator.IsMemberLike(root.DescendantNodes().OfType<IndexerDeclarationSyntax>().Single()));
    }

    [TestMethod]
    public void Calculate_ExpressionBodiedProperty_Counted()
    {
        var member = FirstMemberLike("bool a; int P => a ? 1 : 2;");

        Assert.IsInstanceOfType<PropertyDeclarationSyntax>(member);
        Assert.AreEqual(2, ComplexityCalculator.Calculate(member));
    }

    [TestMethod]
    public void Calculate_TypeWalk_SkipsExpressionBodiedMembers()
    {
        // Expression-bodied properties and indexers are measured on their own, like methods and accessors.
        var type = Parse("bool a; int P => a ? 1 : 2; int this[int i] => a ? i : 0; void M() { if (a) { } }")
            .DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        Assert.AreEqual(1, ComplexityCalculator.Calculate(type));
    }

    [TestMethod]
    public void IsMemberLike_AutoProperty_False()
    {
        var root = Parse("int P { get; set; }");
        var accessors = root.DescendantNodes().OfType<AccessorDeclarationSyntax>().ToList();

        Assert.HasCount(2, accessors);
        Assert.IsFalse(accessors.Any(ComplexityCalculator.IsMemberLike));
        Assert.IsFalse(root.DescendantNodes().Any(ComplexityCalculator.IsMemberLike));
    }

    private static int CalculateFirstMember(string members)
    {
        return ComplexityCalculator.Calculate(FirstMemberLike(members));
    }

    private static SyntaxNode FirstMemberLike(string members)
    {
        return Parse(members).DescendantNodes().First(ComplexityCalculator.IsMemberLike);
    }

    private static SyntaxNode Parse(string members)
    {
        return CSharpSyntaxTree.ParseText("class C { " + members + " }").GetRoot();
    }
}
