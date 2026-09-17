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
    public class SqlDomainServiceInterfaceScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;

        public SqlDomainServiceInterfaceScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var domainServiceInterfacePath = GetFilePath(sqlStoredProcedure);

            if (!File.Exists(domainServiceInterfacePath))
                return ScaffoldResult.Skipped;

            try
            {
                await _lockingService.AcquireLockAsync(domainServiceInterfacePath);

                var existingFileContent = FileUtils.SafeReadAllText(domainServiceInterfacePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);

                var updatedFileContent = RemoveMethodCall(syntaxTree.GetRoot(), sqlStoredProcedure);
                if (string.IsNullOrEmpty(updatedFileContent))
                {
                    File.Delete(domainServiceInterfacePath);
                    Logger.LogSuccess($"[Deleted Repository Interface] {domainServiceInterfacePath}");
                    scaffoldingResult = ScaffoldResult.Deleted;
                }
                else
                {
                    FileUtils.WriteTextAndDirectory(domainServiceInterfacePath, updatedFileContent);
                    Logger.LogSuccess($"[Updated Repository Interface] {domainServiceInterfacePath} removed method {sqlStoredProcedure.StoredProcedureName}");
                }
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lockingService.ReleaseLock(domainServiceInterfacePath);
            }

            return scaffoldingResult;
        }

        public async Task<ScaffoldResult> GenerateCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var methodBody = GenerateInterfaceMethod(sqlStoredProcedure);
            var domainInterfacePath = GetFilePath(sqlStoredProcedure);
            var existingFileContent = string.Empty;

            try
            {
                await _lockingService.AcquireLockAsync(domainInterfacePath);

                SyntaxNode syntaxNode;
                if (!File.Exists(domainInterfacePath))
                {
                    Logger.LogWarn($"[File does not exist] Creating {domainInterfacePath}");
                    syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);
                    scaffoldingResult = ScaffoldResult.Created;
                }
                else
                {
                    existingFileContent = FileUtils.SafeReadAllText(domainInterfacePath);
                    var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);
                    syntaxNode = syntaxTree.GetRoot();
                }

                var updatedFileContent = CreateOrUpdateMethod(syntaxNode, sqlStoredProcedure, methodBody);
                if (!existingFileContent.Equals(updatedFileContent))
                {
                    FileUtils.WriteTextAndDirectory(domainInterfacePath, updatedFileContent);
                    Logger.LogSuccess($"[Created Service Interface] {domainInterfacePath} for method {sqlStoredProcedure.StoredProcedureName}");
                }
                else
                {
#if DEBUGFORCESCAFFOLD
                    FileUtils.WriteTextAndDirectory(domainInterfacePath, updatedFileContent);
                    Logger.LogSuccess($"[Force Created Service Interface] {domainInterfacePath} for method {sqlStoredProcedure.StoredProcedureName}");
#else
                Logger.LogSkipped($"[Skipped Service Interface] Method {sqlStoredProcedure.StoredProcedureName}");
                scaffoldingResult = ScaffoldResult.Skipped;
#endif
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Service Interface] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(domainInterfacePath);
            }

            return scaffoldingResult;
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

        private string GetFilePath(SqlStoredProcedure sqlStoredProcedure)
        {
            var fileName = $"{GetInterfaceName(sqlStoredProcedure)}.#SCHEMA#.Gen.cs".ToSchemaString(sqlStoredProcedure.Schema);
            var domainInterfacePath = Path.Combine(_config.Directories.DomainInterfaceDirectory.ToSchemaString(sqlStoredProcedure.Schema), fileName);
            return domainInterfacePath;
        }

        private SyntaxNode CreateCSharpFileOutline(SqlStoredProcedure sqlStoredProcedure)
        {
            var root = SyntaxFactory.CompilationUnit()
                                    .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetModelNamespace(sqlStoredProcedure))))
                                    .AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(GetDomainInterfaceNamespace(sqlStoredProcedure)))
                                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(CreateCSharpInterface(sqlStoredProcedure))));

            return root;
        }

        private InterfaceDeclarationSyntax CreateCSharpInterface(SqlStoredProcedure sqlStoredProcedure)
        {
            return SyntaxFactory.InterfaceDeclaration(GetInterfaceName(sqlStoredProcedure))
                                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword)));
        }

        private string GenerateInterfaceMethod(SqlStoredProcedure sqlStoredProcedure)
        {
            var methodName = sqlStoredProcedure.GetMethodName();
            var returnTypeName = sqlStoredProcedure.GetReturnTypeName();

            bool returnsData = !methodName.StartsWith("Del");
            bool useSeperateParameters = !methodName.StartsWith("InsUpd");
            if (useSeperateParameters)
            {
                var sb = new StringBuilder();
                if (returnsData)
                    sb.Append($"Task<List<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}>> {methodName}(");
                else
                    sb.Append($"Task {methodName}(");

                foreach (var param in sqlStoredProcedure.Parameters)
                    if (!param.ColumnName.Equals("RetMsg", StringComparison.OrdinalIgnoreCase))
                        if (!string.IsNullOrEmpty(param.DefaultValue))
                            sb.Append($"{param.ToCSharpTypeString(returnsData, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()} = \"{param.DefaultValue}\",");
                        else
                            sb.Append($"{param.ToCSharpTypeString(returnsData, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()},");

                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine(");");
                return sb.ToString();
            }
            else
                return $"Task<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}> {methodName}({GetModelNamespace(sqlStoredProcedure)}.{sqlStoredProcedure.TableName} {sqlStoredProcedure.TableName.ToCSharpSafeKeyword()});";
        }

        private string GetInterfaceName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"I{sqlStoredProcedure.TableName.ToPascalCase()}Service";
        }

        private string GetModelNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.ModelNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDomainInterfaceNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DomainInterfaceNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }
    }
}
