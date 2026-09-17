using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Config;
using Postgresfold.Scaffold.Model.Enum;
using Postgresfold.Scaffold.Model.Sql;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Postgresfold.Scaffold.Domain.Scaffold
{
    public class SqlDalRepositoryServiceCollectionExtensionScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;
        private readonly string _methodName = "AddGeneratedRepositories";
        private readonly string _className = "AddGeneratedRepositoriesServiceCollectionExtension";

        public SqlDalRepositoryServiceCollectionExtensionScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var dalRepoScePath = GetFilePath(sqlStoredProcedure);

            if (!File.Exists(dalRepoScePath))
                return ScaffoldResult.Skipped;

            try
            {

                await _lockingService.AcquireLockAsync(dalRepoScePath);

                var existingFileContent = FileUtils.SafeReadAllText(dalRepoScePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);

                var updatedFileContent = RemoveMethodStatement(syntaxTree.GetRoot(), sqlStoredProcedure);
                if (string.IsNullOrEmpty(updatedFileContent))
                {
                    File.Delete(dalRepoScePath);
                    Logger.LogSuccess($"[Deleted SCE Repository] {dalRepoScePath}");
                    scaffoldingResult = ScaffoldResult.Deleted;
                }
                else
                {
                    FileUtils.WriteTextAndDirectory(dalRepoScePath, updatedFileContent);
                    Logger.LogSuccess($"[Updated SCE Repository] {dalRepoScePath} removed Dependency Injection {sqlStoredProcedure.TableName}");
                }

            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lockingService.ReleaseLock(dalRepoScePath);
            }
            return scaffoldingResult;
        }

        public async Task<ScaffoldResult> GenerateCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var dalRepoScePath = GetFilePath(sqlStoredProcedure);
            var existingFileContent = string.Empty;

            try
            {
                await _lockingService.AcquireLockAsync(dalRepoScePath);

                SyntaxNode syntaxNode;
                if (!File.Exists(dalRepoScePath))
                {
                    scaffoldingResult = ScaffoldResult.Created;
                    Logger.LogWarn($"[File does not exist] Creating {dalRepoScePath}");
                    syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);
                }
                else
                {
                    existingFileContent = FileUtils.SafeReadAllText(dalRepoScePath);
                    var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);
                    syntaxNode = syntaxTree.GetRoot();
                }

                var registrationStatement = SyntaxFactory.ParseStatement(
                    $"services.AddTransient<I{sqlStoredProcedure.TableName.ToPascalCase()}Repository, {sqlStoredProcedure.TableName.ToPascalCase()}Repository>(x => new {sqlStoredProcedure.TableName.ToPascalCase()}Repository(connectionString));"
                );
                var updatedFileContent = UpdateMethodStatements(syntaxNode, sqlStoredProcedure, registrationStatement);
                if (!existingFileContent.Equals(updatedFileContent))
                {
                    FileUtils.WriteTextAndDirectory(dalRepoScePath, updatedFileContent);
                    Logger.LogSuccess($"[Created SCE Repository] {dalRepoScePath} for method {sqlStoredProcedure.StoredProcedureName}");
                }
                else
                {
#if DEBUGFORCESCAFFOLD
                    FileUtils.WriteTextAndDirectory(dalRepoScePath, updatedFileContent);
                    Logger.LogSuccess($"[Force Created SCE Repository] {dalRepoScePath} for method {sqlStoredProcedure.StoredProcedureName}");
#else
                    Logger.LogSkipped($"[Skipped Repository] Method {sqlStoredProcedure.StoredProcedureName}");
                    scaffoldingResult = ScaffoldResult.Skipped;
#endif
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SCE Repository] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(dalRepoScePath);
            }

            return scaffoldingResult;
        }

        private (ClassDeclarationSyntax myClass, MethodDeclarationSyntax myMethod) GetClassAndMethod(SyntaxNode root)
        {
            var classDeclaration = root.DescendantNodes()
                                       .OfType<ClassDeclarationSyntax>()
                                       .FirstOrDefault(c => c.Identifier.Text == _className);

            if (classDeclaration is null)
                return (null, null);

            var method = classDeclaration.Members
                                         .OfType<MethodDeclarationSyntax>()
                                         .FirstOrDefault(m => m.Identifier.Text == _methodName);

            return (classDeclaration, method);
        }

        private string RemoveMethodStatement(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure)
        {
            var cnm = GetClassAndMethod(root);
            var method = cnm.myMethod;
            if (method is null)
            {
                Logger.LogError($"[SCE Repository Error] Cannot find method: {_methodName}");
                return root.NormalizeWhitespace().ToFullString();
            }

            // Find the first matching statement
            var statements = method.Body.Statements.ToList();
            var statementToRemove = statements.ToList()
                                              .OfType<ExpressionStatementSyntax>()
                                              .FirstOrDefault(stmt =>
                                              {
                                                  if (stmt.Expression is InvocationExpressionSyntax invocation &&
                                                      invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                                      memberAccess.Name.Identifier.Text == "AddTransient")
                                                  {
                                                      return invocation.ArgumentList.Arguments.Any() &&
                                                             invocation.ArgumentList.Arguments[0].ToString().Equals($"x => new {sqlStoredProcedure.TableName.ToPascalCase()}Repository(connectionString)");
                                                  }
                                                  return false;
                                              });

            if (statementToRemove is not null)
            {
                statements.Remove(statementToRemove);
                if (statements.Count == 1)
                    return string.Empty;    //If we only have the return statement, delete the file.

                var newBody = method.Body.WithStatements(SyntaxFactory.List(statements));
                var updatedMethod = method.WithBody(newBody);
                var updatedClass = cnm.myClass.ReplaceNode(method, updatedMethod);
                var updatedRoot = root.ReplaceNode(cnm.myClass, updatedClass);
                return updatedRoot.NormalizeWhitespace().ToString();
            }

            return root.NormalizeWhitespace().ToString();
        }

        private string UpdateMethodStatements(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure, StatementSyntax statement)
        {
            var cnm = GetClassAndMethod(root);
            var method = cnm.myMethod;
            if (method is null)
            {
                Logger.LogError($"[SCE Repository Error] Cannot find method: {_methodName}");
                return root.NormalizeWhitespace().ToFullString();
            }

            bool exists = method.Body.Statements.OfType<ExpressionStatementSyntax>()
                                .Any(stmt =>
                                {
                                    if (stmt.Expression is InvocationExpressionSyntax invocation)
                                        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                                            // Check if the method being called is "AddTransient"
                                            if (memberAccess.Name.Identifier.Text == "AddTransient")
                                            {
                                                return (invocation.ArgumentList.Arguments.Any() &&
                                                        invocation.ArgumentList.Arguments[0].ToString().Equals($"x => new {sqlStoredProcedure.TableName.ToPascalCase()}Repository(connectionString)"));
                                            }

                                    return false;
                                });

            if (exists)
                return root.NormalizeWhitespace().ToString();

            var updatedStatements = method.Body.Statements.Insert(method.Body.Statements.Count - 1, statement);
            var updatedMethod = method.WithBody(method.Body.WithStatements(updatedStatements));
            var updatedRoot = root.ReplaceNode(method, updatedMethod);

            return updatedRoot.NormalizeWhitespace().ToString();
        }

        private string GetFilePath(SqlStoredProcedure sqlStoredProcedure)
        {
            var fileName = $"AddGeneratedRepositoriesServiceCollectionExtension.cs";
            var dalRepoScePath = Path.Combine(_config.Directories.ServiceCollectionExtensionDalDirectory.ToSchemaString(sqlStoredProcedure.Schema), fileName);
            return dalRepoScePath;
        }

        private SyntaxNode CreateCSharpFileOutline(SqlStoredProcedure sqlStoredProcedure)
        {
            var root = SyntaxFactory.CompilationUnit()
                                    .AddUsings(
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.DependencyInjection")),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDalInterfaceNamespace(sqlStoredProcedure))),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDalNamespace(sqlStoredProcedure))))
                                    .AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(GetServiceCollectionExtensionNamespace(sqlStoredProcedure)))
                                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(CreateCSharpClass(sqlStoredProcedure))));

            return root;
        }

        private ClassDeclarationSyntax CreateCSharpClass(SqlStoredProcedure sqlStoredProcedure)
        {
            return SyntaxFactory.ClassDeclaration(_className)
                                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
                                .AddMembers(
                                    SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("IServiceCollection"), _methodName)
                                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                                    .AddParameterListParameters(
                                        SyntaxFactory.Parameter(SyntaxFactory.Identifier("services"))
                                                                             .WithType(SyntaxFactory.ParseTypeName("IServiceCollection"))
                                                                             .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ThisKeyword))),
                                        SyntaxFactory.Parameter(SyntaxFactory.Identifier("connectionString"))
                                                                             .WithType(SyntaxFactory.ParseTypeName("string")))
                                    .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("services")))));
        }

        private string GetDalNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DalNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetServiceCollectionExtensionNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.ServiceCollectionExtensionNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDalInterfaceNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DalInterfaceNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }
    }
}