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
    public class SqlDomainServiceServiceCollectionExtensionScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;
        private readonly string _methodName = "AddGeneratedServices";
        private readonly string _className = "AddGeneratedServicesServiceCollectionExtension";

        public SqlDomainServiceServiceCollectionExtensionScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var domainServiceScePath = GetFilePath(sqlStoredProcedure);

            if (!File.Exists(domainServiceScePath))
                return ScaffoldResult.Skipped;

            try
            {
                await _lockingService.AcquireLockAsync(domainServiceScePath);

                var existingFileContent = FileUtils.SafeReadAllText(domainServiceScePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);

                var updatedFileContent = RemoveMethodStatement(syntaxTree.GetRoot(), sqlStoredProcedure);
                if (string.IsNullOrEmpty(updatedFileContent))
                {
                    File.Delete(domainServiceScePath);
                    Logger.LogSuccess($"[Deleted SCE Service] {domainServiceScePath}");
                    scaffoldingResult = ScaffoldResult.Deleted;
                }
                else
                {
                    FileUtils.WriteTextAndDirectory(domainServiceScePath, updatedFileContent);
                    Logger.LogSuccess($"[Updated SCE Service] {domainServiceScePath} removed Dependency Injection {sqlStoredProcedure.TableName}");
                }

            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lockingService.ReleaseLock(domainServiceScePath);
            }
            return scaffoldingResult;
        }

        public async Task<ScaffoldResult> GenerateCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var domainServiceScePath = GetFilePath(sqlStoredProcedure);
            var existingFileContent = string.Empty;

            try
            {
                await _lockingService.AcquireLockAsync(domainServiceScePath);

                SyntaxNode syntaxNode;
                if (!File.Exists(domainServiceScePath))
                {
                    scaffoldingResult = ScaffoldResult.Created;
                    Logger.LogWarn($"[File does not exist] Creating {domainServiceScePath}");
                    syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);
                }
                else
                {
                    existingFileContent = FileUtils.SafeReadAllText(domainServiceScePath);
                    var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);
                    syntaxNode = syntaxTree.GetRoot();
                }

                var registrationStatement = SyntaxFactory.ParseStatement(
                    $"services.AddTransient<I{sqlStoredProcedure.TableName}Service, {sqlStoredProcedure.TableName}Service>();"
                );
                var updatedFileContent = UpdateMethodStatements(syntaxNode, sqlStoredProcedure, registrationStatement);
                if (!existingFileContent.Equals(updatedFileContent))
                {
                    FileUtils.WriteTextAndDirectory(domainServiceScePath, updatedFileContent);
                    Logger.LogSuccess($"[Created SCE Service] {domainServiceScePath} for method {sqlStoredProcedure.StoredProcedureName}");
                }
                else
                {
#if DEBUGFORCESCAFFOLD
                    FileUtils.WriteTextAndDirectory(dalRepoScePath, updatedFileContent);
                    Logger.LogSuccess($"[Force Created SCE Service] {dalRepoScePath} for method {sqlStoredProcedure.StoredProcedureName}");
#else
                    Logger.LogSkipped($"[Skipped Service] Method {sqlStoredProcedure.StoredProcedureName}");
                    scaffoldingResult = ScaffoldResult.Skipped;
#endif
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SCE Service] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(domainServiceScePath);
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
                Logger.LogError($"[SCE Service Error] Cannot find method: {_methodName}");
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
                                                      memberAccess.Name is GenericNameSyntax genericName &&
                                                      genericName.Identifier.Text == "AddTransient")
                                                  {
                                                      // Check if this is a registration for the target service
                                                      var typeArgs = genericName.TypeArgumentList.Arguments;
                                                      if (typeArgs.Count >= 2)
                                                      {
                                                          var interfaceType = typeArgs[0].ToString();
                                                          var concreteType = typeArgs[1].ToString();
                                                          return interfaceType == $"I{sqlStoredProcedure.TableName}Service" &&
                                                                 concreteType == $"{sqlStoredProcedure.TableName}Service";
                                                      }
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
                Logger.LogError($"[SCE Service Error] Cannot find method: {_methodName}");
                return root.NormalizeWhitespace().ToFullString();
            }

            bool exists = method.Body.Statements.OfType<ExpressionStatementSyntax>()
                                .Any(stmt =>
                                {
                                    if (stmt.Expression is InvocationExpressionSyntax invocation &&
                                        invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                        memberAccess.Name is GenericNameSyntax genericName &&
                                        genericName.Identifier.Text == "AddTransient")
                                    {
                                        // Check if this is a registration for the target service
                                        var typeArgs = genericName.TypeArgumentList.Arguments;
                                        if (typeArgs.Count >= 2)
                                        {
                                            var interfaceType = typeArgs[0].ToString();
                                            var concreteType = typeArgs[1].ToString();
                                            return interfaceType == $"I{sqlStoredProcedure.TableName}Service" &&
                                                   concreteType == $"{sqlStoredProcedure.TableName}Service";
                                        }
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
            var fileName = $"AddGeneratedServicesServiceCollectionExtension.cs";
            var dalRepoScePath = Path.Combine(_config.Directories.ServiceCollectionExtensionDomainDirectory.ToSchemaString(sqlStoredProcedure.Schema), fileName);
            return dalRepoScePath;
        }

        private SyntaxNode CreateCSharpFileOutline(SqlStoredProcedure sqlStoredProcedure)
        {
            var root = SyntaxFactory.CompilationUnit()
                                    .AddUsings(
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.DependencyInjection")),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDomainInterfaceNamespace(sqlStoredProcedure))),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDomainNamespace(sqlStoredProcedure))))
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
                                                                             .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ThisKeyword))))
                                    .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("services")))));
        }

        

        private string GetDomainNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DomainNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetServiceCollectionExtensionNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.ServiceCollectionExtensionNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDomainInterfaceNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DomainInterfaceNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }
    }
}