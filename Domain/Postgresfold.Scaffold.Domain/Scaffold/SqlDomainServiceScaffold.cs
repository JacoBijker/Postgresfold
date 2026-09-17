using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Config;
using Postgresfold.Scaffold.Model.Sql;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Text;
using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Model.Enum;

namespace Postgresfold.Scaffold.Domain.Scaffold
{
    public class SqlDomainServiceScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;

        public SqlDomainServiceScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var domainServicePath = GetFilePath(sqlStoredProcedure);

            if (!File.Exists(domainServicePath))
                return ScaffoldResult.Skipped;

            try
            {

                await _lockingService.AcquireLockAsync(domainServicePath);

                var existingFileContent = FileUtils.SafeReadAllText(domainServicePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);

                var updatedFileContent = RemoveMethodCall(syntaxTree.GetRoot(), sqlStoredProcedure);
                if (string.IsNullOrEmpty(updatedFileContent))
                {
                    File.Delete(domainServicePath);
                    Logger.LogSuccess($"[Deleted Service] {domainServicePath}");
                    scaffoldingResult = ScaffoldResult.Deleted;
                }
                else
                {
                    FileUtils.WriteTextAndDirectory(domainServicePath, updatedFileContent);
                    Logger.LogSuccess($"[Updated Service] {domainServicePath} removed method {sqlStoredProcedure.StoredProcedureName}");
                }

            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lockingService.ReleaseLock(domainServicePath);
            }
            return scaffoldingResult;
        }

        public async Task<ScaffoldResult> GenerateCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var methodBody = GenerateStoredProcedureMethod(sqlStoredProcedure);
            var domainServicePath = GetFilePath(sqlStoredProcedure);
            var existingFileContent = string.Empty;

            try
            {
                await _lockingService.AcquireLockAsync(domainServicePath);

                SyntaxNode syntaxNode;
                if (!File.Exists(domainServicePath))
                {
                    scaffoldingResult = ScaffoldResult.Created;
                    Logger.LogWarn($"[File does not exist] Creating {domainServicePath}");
                    syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);
                }
                else
                {
                    existingFileContent = FileUtils.SafeReadAllText(domainServicePath);
                    var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);
                    syntaxNode = syntaxTree.GetRoot();
                }

                var updatedFileContent = CreateOrUpdateMethod(syntaxNode, sqlStoredProcedure, methodBody);
                if (!existingFileContent.Equals(updatedFileContent))
                {
                    FileUtils.WriteTextAndDirectory(domainServicePath, updatedFileContent);
                    Logger.LogSuccess($"[Created Service] {domainServicePath} for method {sqlStoredProcedure.StoredProcedureName}");
                }
                else
                {
#if DEBUGFORCESCAFFOLD
                    FileUtils.WriteTextAndDirectory(domainServicePath, updatedFileContent);
                    Logger.LogSuccess($"[Force Created Service] {domainServicePath} for method {sqlStoredProcedure.StoredProcedureName}");
#else
                    Logger.LogSkipped($"[Skipped Service] Method {sqlStoredProcedure.StoredProcedureName}");
                    scaffoldingResult = ScaffoldResult.Skipped;
#endif
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Service] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(domainServicePath);
            }

            return scaffoldingResult;
        }

        private string CreateOrUpdateMethod(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure, string methodBody)
        {
            var className = GetClassName(sqlStoredProcedure);
            var methodName = sqlStoredProcedure.GetMethodName();

            var classDeclaration = root.DescendantNodes()
                                       .OfType<ClassDeclarationSyntax>()
                                       .FirstOrDefault(c => c.Identifier.Text == className);

            if (classDeclaration is null)
            {
                // Add the class if it doesn't exist
                var newClass = CreateCSharpClass(sqlStoredProcedure);

                root = ((CompilationUnitSyntax)root).AddMembers(newClass);
                classDeclaration = root.DescendantNodes()
                                       .OfType<ClassDeclarationSyntax>()
                                       .First(c => c.Identifier.Text == className);
            }

            // Find the method declaration
            var method = classDeclaration.Members
                                          .OfType<MethodDeclarationSyntax>()
                                          .FirstOrDefault(m => m.Identifier.Text == methodName);


            var updatedMethod = SyntaxFactory.ParseMemberDeclaration(methodBody);

            SyntaxNode updatedRoot;
            if (method is not null)
            {
                updatedRoot = root.ReplaceNode(method, updatedMethod);
            }
            else
            {
                var updatedClass = classDeclaration.AddMembers(updatedMethod);
                updatedRoot = root.ReplaceNode(classDeclaration, updatedClass);
            }

            return updatedRoot.NormalizeWhitespace().ToFullString();
        }

        private string RemoveMethodCall(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure)
        {
            var className = GetClassName(sqlStoredProcedure);
            var methodName = sqlStoredProcedure.GetMethodName();

            var classDeclaration = root.DescendantNodes()
                                       .OfType<ClassDeclarationSyntax>()
                                       .FirstOrDefault(c => c.Identifier.Text == className);

            if (classDeclaration is null)
                return string.Empty;

            var method = classDeclaration.Members
                                         .OfType<MethodDeclarationSyntax>()
                                         .FirstOrDefault(m => m.Identifier.Text == methodName);

            if (method is null)
                return root.NormalizeWhitespace().ToFullString();

            var updatedRoot = root.RemoveNode(method, SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepTrailingTrivia);

            if (!updatedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Any())
                return string.Empty;

            return updatedRoot.NormalizeWhitespace().ToFullString();
        }

        private string GetFilePath(SqlStoredProcedure sqlStoredProcedure)
        {
            var fileName = $"{GetClassName(sqlStoredProcedure)}.#SCHEMA#.Gen.cs".ToSchemaString(sqlStoredProcedure.Schema);
            var domainServicePath = Path.Combine(_config.Directories.DomainDirectory.ToSchemaString(sqlStoredProcedure.Schema), fileName);
            return domainServicePath;
        }

        private SyntaxNode CreateCSharpFileOutline(SqlStoredProcedure sqlStoredProcedure)
        {
            var root = SyntaxFactory.CompilationUnit()
                                    .AddUsings(
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDomainInterfaceNamespace(sqlStoredProcedure))),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDalInterfaceNamespace(sqlStoredProcedure))),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetModelNamespace(sqlStoredProcedure))))
                                    .AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(GetDomainNamespace(sqlStoredProcedure)))
                                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(CreateCSharpClass(sqlStoredProcedure))));

            return root;
        }

        private MemberDeclarationSyntax CreateCSharpClass(SqlStoredProcedure sqlStoredProcedure)
        {
            var className = GetClassName(sqlStoredProcedure);
            var interfaceName = $"I{sqlStoredProcedure.TableName.ToPascalCase()}Repository";

            // Private readonly field
            var field = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(interfaceName))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("_repo"))))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

            // Constructor
            var constructor = SyntaxFactory.ConstructorDeclaration(className)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("repo"))
                        .WithType(SyntaxFactory.ParseTypeName(interfaceName)))))
                .WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(
                    SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName($"_repo"),
                        SyntaxFactory.IdentifierName("repo"))))));

            // Create the class
            return SyntaxFactory.ClassDeclaration(className)
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                .WithMembers(SyntaxFactory.List(new MemberDeclarationSyntax[] { field, constructor }))
                .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(GetInterfaceName(sqlStoredProcedure))))));
        }

        private string GenerateStoredProcedureMethod(SqlStoredProcedure sqlStoredProcedure)
        {
            var sb = new StringBuilder();
            var methodName = sqlStoredProcedure.GetMethodName();
            var returnTypeName = sqlStoredProcedure.GetReturnTypeName();

            bool useSeperateParameters = !methodName.StartsWith("InsUpd");
            bool returnsData = !methodName.StartsWith("Del");

            if (useSeperateParameters)
            {
                if (returnsData)
                    sb.Append($"public async Task<List<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}>> {methodName}(");
                else
                    sb.Append($"public async Task {methodName}(");

                foreach (var param in sqlStoredProcedure.Parameters)
                    if (param.DataType.EndsWith("[]"))
                        sb.Append($"{param.ToCSharpTypeString(returnsData, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()},");
                    else
                        sb.Append($"{param.ToCSharpTypeString(returnsData, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()} = null,");

                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine(")");
                sb.AppendLine("{");

                if (returnsData)
                    sb.Append($"    return await _repo.{methodName}(");
                else
                    sb.Append($"    await _repo.{methodName}(");

                foreach (var param in sqlStoredProcedure.Parameters)
                    if (!param.ColumnName.Equals("RetMsg", StringComparison.OrdinalIgnoreCase))
                        sb.Append($"{param.ColumnName.ToCamelCase()},");

                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine($");");
                sb.AppendLine("}");

                return sb.ToString();
            }
            else
            {
                sb.AppendLine($"public async Task<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}> {methodName}({GetModelNamespace(sqlStoredProcedure)}.{sqlStoredProcedure.TableName.ToPascalCase()} {sqlStoredProcedure.TableName.ToCSharpSafeKeyword()})");
                sb.AppendLine("{");
                sb.AppendLine($"    return await _repo.{methodName}({sqlStoredProcedure.TableName.ToCSharpSafeKeyword()});");
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private string GetInterfaceName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"I{sqlStoredProcedure.TableName.ToPascalCase()}Service";
        }

        private string GetClassName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"{sqlStoredProcedure.TableName.ToPascalCase()}Service";
        }

        private string GetModelNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.ModelNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDomainNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DomainNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDalInterfaceNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DalInterfaceNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDomainInterfaceNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DomainInterfaceNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }
    }
}
