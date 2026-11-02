using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Roslyn.Mcp.Services
{
    /// <summary>
    /// Cyclomatic complexity of a method-like declaration (spec §5.7): base 1, plus 1 per decision point.
    /// Lambdas and local functions count toward the declaration that contains them.
    /// </summary>
    public static class ComplexityCalculator
    {
        /// <summary>
        /// True for methods, constructors, destructors, operators and accessors that have a body.
        /// </summary>
        public static bool IsMemberLike(SyntaxNode node)
        {
            return node switch
            {
                BaseMethodDeclarationSyntax method => method.Body != null || method.ExpressionBody != null,
                AccessorDeclarationSyntax accessor => accessor.Body != null || accessor.ExpressionBody != null,
                _ => false,
            };
        }

        public static int Calculate(SyntaxNode memberDeclaration)
        {
            var complexity = 1;

            var nodes = memberDeclaration.DescendantNodes(descendIntoChildren: n =>
                n == memberDeclaration ||
                n is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or BaseTypeDeclarationSyntax));

            foreach (var node in nodes)
            {
                if (IsDecisionPoint(node))
                    complexity++;
            }

            return complexity;
        }

        private static bool IsDecisionPoint(SyntaxNode node)
        {
            return node switch
            {
                IfStatementSyntax or
                WhileStatementSyntax or
                ForStatementSyntax or
                CommonForEachStatementSyntax or
                DoStatementSyntax or
                CaseSwitchLabelSyntax or
                CasePatternSwitchLabelSyntax or
                CatchClauseSyntax or
                ConditionalExpressionSyntax or
                BinaryPatternSyntax or
                WhenClauseSyntax => true,
                BinaryExpressionSyntax binary => binary.Kind() is
                    SyntaxKind.CoalesceExpression or
                    SyntaxKind.LogicalAndExpression or
                    SyntaxKind.LogicalOrExpression,
                AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression),
                SwitchExpressionArmSyntax arm => arm.Pattern is not DiscardPatternSyntax,
                _ => false,
            };
        }
    }
}
