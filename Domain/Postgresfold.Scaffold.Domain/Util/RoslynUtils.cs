using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using System.Data;

namespace Postgresfold.Scaffold.Domain.Util
{
    public static class RoslynUtils
    {
        public static ClassDeclarationSyntax CreateClass(string className, params SyntaxKind[] modifiers)
        {
            return SyntaxFactory.ClassDeclaration(className)
                                .WithModifiers(SyntaxFactory.TokenList(modifiers.Select(s => SyntaxFactory.Token(s))));
        }

        public static ClassDeclarationSyntax CreateClass(string className, string interfaceName, params SyntaxKind[] modifiers)
        {
            return CreateClass(className, modifiers).WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName)))));
        }

        public static FieldDeclarationSyntax CreateField(string fieldName, string fieldType, params SyntaxKind[] modifiers)
        {
            return SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName(fieldType))
                        .WithVariables(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(fieldName))))
                    .WithModifiers(SyntaxFactory.TokenList(modifiers.Select(s => SyntaxFactory.Token(s))));
        }

        public static ParameterSyntax CreateParameter(string parameterName, string parameterType)
        {
            return SyntaxFactory.Parameter(
                    SyntaxFactory.Identifier(parameterName))
                    .WithType(SyntaxFactory.IdentifierName(parameterType));
        }

        public static ExpressionStatementSyntax CreateAssignmentExpression(string parameter, string parameterTwo)
        {
            return SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(parameter),
                        SyntaxFactory.IdentifierName(parameterTwo)));
        }
    }
}
