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
    public class SqlDalRepositoryInterfaceScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;

        public SqlDalRepositoryInterfaceScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var dalRepositoryInterfacePath = GetFilePath(sqlStoredProcedure);

            if (!File.Exists(dalRepositoryInterfacePath))
                return ScaffoldResult.Skipped;

            try
            {
                await _lockingService.AcquireLockAsync(dalRepositoryInterfacePath);

                var existingFileContent = FileUtils.SafeReadAllText(dalRepositoryInterfacePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);

                var updatedFileContent = RemoveMethodCall(syntaxTree.GetRoot(), sqlStoredProcedure);
                if (string.IsNullOrEmpty(updatedFileContent))
                {
                    scaffoldingResult = ScaffoldResult.Deleted;
                    File.Delete(dalRepositoryInterfacePath);
                    Logger.LogSuccess($"[Deleted Repository Interface] {dalRepositoryInterfacePath}");
                }
                else
                {
                    FileUtils.WriteTextAndDirectory(dalRepositoryInterfacePath, updatedFileContent);
                    Logger.LogSuccess($"[Updated Repository Interface] {dalRepositoryInterfacePath} removed method {sqlStoredProcedure.StoredProcedureName}");
                }

                return scaffoldingResult;
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lockingService.ReleaseLock(dalRepositoryInterfacePath);
            }
        }

        public async Task<ScaffoldResult> GenerateCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var methodBody = GenerateInterfaceMethod(sqlStoredProcedure);
            var dalInterfacePath = GetFilePath(sqlStoredProcedure);
            var existingFileContent = string.Empty;

            try
            {
                await _lockingService.AcquireLockAsync(dalInterfacePath);

                SyntaxNode syntaxNode;
                if (!File.Exists(dalInterfacePath))
                {
                    scaffoldingResult = ScaffoldResult.Created;
                    Logger.LogWarn($"[File does not exist] Creating {dalInterfacePath}");
                    syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);
                }
                else
                {
                    existingFileContent = FileUtils.SafeReadAllText(dalInterfacePath);
                    var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);
                    syntaxNode = syntaxTree.GetRoot();
                }

                var updatedFileContent = CreateOrUpdateMethod(syntaxNode, sqlStoredProcedure, methodBody);
                if (!existingFileContent.Equals(updatedFileContent))
                {
                    FileUtils.WriteTextAndDirectory(dalInterfacePath, updatedFileContent);
                    Logger.LogSuccess($"[Created Repository Interface] {dalInterfacePath} for method {sqlStoredProcedure.StoredProcedureName}");
                }
                else
                {
#if DEBUGFORCESCAFFOLD
                    FileUtils.WriteTextAndDirectory(dalInterfacePath, updatedFileContent);
                    Logger.LogSuccess($"[Force Repository Interface] {dalInterfacePath} for method {sqlStoredProcedure.StoredProcedureName}");
#else
                    Logger.LogSkipped($"[Skipped Repository] Method {sqlStoredProcedure.StoredProcedureName}");
                    scaffoldingResult = ScaffoldResult.Skipped;
#endif
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Repository Interface] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(dalInterfacePath);
            }

            return scaffoldingResult;
        }

        private string RemoveMethodCall(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure)
        {
            var interfaceName = GetInterfaceName(sqlStoredProcedure);
            var methodName = sqlStoredProcedure.GetMethodName();

            var interfaceDeclaration = root.DescendantNodes()
                                       .OfType<InterfaceDeclarationSyntax>()
                                       .FirstOrDefault(c => c.Identifier.Text == interfaceName);

            if (interfaceDeclaration is null)
                return string.Empty;

            var method = interfaceDeclaration.Members
                                             .OfType<MethodDeclarationSyntax>()
                                             .FirstOrDefault(m => m.Identifier.Text == methodName);

            if (method is null)
                return root.NormalizeWhitespace().ToFullString();

            var updatedRoot = root.RemoveNode(method, SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepTrailingTrivia);

            if (!updatedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Any())
                return string.Empty;

            return updatedRoot.NormalizeWhitespace().ToFullString();
        }

        private string CreateOrUpdateMethod(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure, string methodBody)
        {
            var interfaceName = GetInterfaceName(sqlStoredProcedure);
            var methodName = sqlStoredProcedure.GetMethodName();

            var interfaceDeclaration = root.DescendantNodes()
                                           .OfType<InterfaceDeclarationSyntax>()
                                           .FirstOrDefault(c => c.Identifier.Text == interfaceName);

            if (interfaceDeclaration is null)
            {
                // Add the class if it doesn't exist
                var newInterface = CreateCSharpInterface(sqlStoredProcedure);
                root = ((CompilationUnitSyntax)root).AddMembers(newInterface);
                interfaceDeclaration = root.DescendantNodes()
                                       .OfType<InterfaceDeclarationSyntax>()
                                       .First(c => c.Identifier.Text == interfaceName);
            }

            // Find the method declaration
            var method = interfaceDeclaration.Members
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
                var updatedClass = interfaceDeclaration.AddMembers(updatedMethod);
                updatedRoot = root.ReplaceNode(interfaceDeclaration, updatedClass);
            }

            return updatedRoot.NormalizeWhitespace().ToFullString();
        }

        private SyntaxNode CreateCSharpFileOutline(SqlStoredProcedure sqlStoredProcedure)
        {
            var root = SyntaxFactory.CompilationUnit()
                                    .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetModelNamespace(sqlStoredProcedure))))
                                    .AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(GetDalInterfaceNamespace(sqlStoredProcedure)))
                                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(CreateCSharpInterface(sqlStoredProcedure))));

            return root;
        }

        private InterfaceDeclarationSyntax CreateCSharpInterface(SqlStoredProcedure sqlStoredProcedure)
        {
            return SyntaxFactory.InterfaceDeclaration(GetInterfaceName(sqlStoredProcedure))
                                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword)));
        }

        private string GetFilePath(SqlStoredProcedure sqlStoredProcedure)
        {
            var fileName = $"{GetInterfaceName(sqlStoredProcedure)}.#SCHEMA#.Gen.cs".ToSchemaString(sqlStoredProcedure.Schema);
            var dalInterfacePath = Path.Combine(_config.Directories.DalInterfaceDirectory.ToSchemaString(sqlStoredProcedure.Schema), fileName);
            return dalInterfacePath;
        }

        private string GenerateInterfaceMethod(SqlStoredProcedure sqlStoredProcedure)
        {
            var methodName = sqlStoredProcedure.GetMethodName();
            var returnTypeName = sqlStoredProcedure.GetReturnTypeName();

            bool useSeperateParameters = !methodName.StartsWith("InsUpd");
            bool returnsData = !methodName.StartsWith("Del");

            if (useSeperateParameters)
            {
                var sb = new StringBuilder();
                if (returnsData)
                    sb.Append($"Task<List<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}>> {methodName}(");
                else
                    sb.Append($"Task {methodName}(");

                foreach (var param in sqlStoredProcedure.Parameters)
                    if (param.DataType.EndsWith("[]"))
                        sb.Append($"{param.ToCSharpTypeString(returnsData, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()},");
                    else
                        sb.Append($"{param.ToCSharpTypeString(returnsData, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()} = null,");

                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine(");");
                return sb.ToString();
            }
            else
                return $"Task<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}> {methodName}({GetModelNamespace(sqlStoredProcedure)}.{sqlStoredProcedure.TableName.ToPascalCase()} {sqlStoredProcedure.TableName.ToCSharpSafeKeyword()});";
        }

        private string GetInterfaceName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"I{sqlStoredProcedure.TableName.ToPascalCase()}Repository";
        }

        private string GetModelNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.ModelNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDalInterfaceNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DalInterfaceNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }
    }
}
