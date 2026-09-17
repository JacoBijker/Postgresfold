using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Config;
using Postgresfold.Scaffold.Model.Enum;
using Postgresfold.Scaffold.Model.Sql;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace Postgresfold.Scaffold.Domain.Scaffold
{
    public class SqlDalRepositoryScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;

        public SqlDalRepositoryScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var dalRepositoryPath = GetFilePath(sqlStoredProcedure);

            if (!File.Exists(dalRepositoryPath))
                return ScaffoldResult.Skipped;

            try
            {
                await _lockingService.AcquireLockAsync(dalRepositoryPath);

                var existingFileContent = FileUtils.SafeReadAllText(dalRepositoryPath);
                var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);

                var updatedFileContent = RemoveMethodCall(syntaxTree.GetRoot(), sqlStoredProcedure);
                if (string.IsNullOrEmpty(updatedFileContent))
                {
                    File.Delete(dalRepositoryPath);
                    Logger.LogSuccess($"[Deleted Repository] {dalRepositoryPath}");
                    scaffoldingResult = ScaffoldResult.Deleted;
                }
                else
                {
                    FileUtils.WriteTextAndDirectory(dalRepositoryPath, updatedFileContent);
                    Logger.LogSuccess($"[Updated Repository] {dalRepositoryPath} removed method {sqlStoredProcedure.StoredProcedureName}");
                }

                return scaffoldingResult;
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lockingService.ReleaseLock(dalRepositoryPath);
            }
        }

        public async Task<ScaffoldResult> GenerateCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var dalRepositoryPath = GetFilePath(sqlStoredProcedure);
            var methodBody = GenerateStoredProcedureMethod(sqlStoredProcedure);
            var existingFileContent = string.Empty;

            try
            {
                await _lockingService.AcquireLockAsync(dalRepositoryPath);

                SyntaxNode syntaxNode;
                if (!File.Exists(dalRepositoryPath))
                {
                    scaffoldingResult = ScaffoldResult.Created;
                    Logger.LogWarn($"[File does not exist] Creating {dalRepositoryPath}");
                    syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);
                }
                else
                {
                    existingFileContent = FileUtils.SafeReadAllText(dalRepositoryPath);
                    var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);
                    syntaxNode = syntaxTree.GetRoot();
                }

                var updatedFileContent = AddUpdateMethodCall(syntaxNode, sqlStoredProcedure, methodBody);
                if (!existingFileContent.Equals(updatedFileContent))
                {
                    FileUtils.WriteTextAndDirectory(dalRepositoryPath, updatedFileContent);
                    Logger.LogSuccess($"[Created Repository] {dalRepositoryPath} for method {sqlStoredProcedure.StoredProcedureName}");
                }
                else
                {
#if DEBUGFORCESCAFFOLD
                    FileUtils.WriteTextAndDirectory(dalRepositoryPath, updatedFileContent);
                    Logger.LogSuccess($"[Force Created Repository] {dalRepositoryPath} for method {sqlStoredProcedure.StoredProcedureName}");
#else
                    Logger.LogSkipped($"[Skipped Repository] Method {sqlStoredProcedure.StoredProcedureName}");
                    scaffoldingResult = ScaffoldResult.Skipped;
#endif
                }

                if (scaffoldingResult == ScaffoldResult.Created)
                {
                    var dalBasePath = Path.GetDirectoryName(_config.Directories.DalDirectory.ToSchemaString("public"));
                    var baseRepositoryPath = Path.Combine(dalBasePath, "BaseRepository.cs");
                    if (!File.Exists(baseRepositoryPath))
                    {
                        FileUtils.WriteTextAndDirectory(baseRepositoryPath, GetBaseRepositoryFile());
                        Logger.LogSuccess($"[Created Base Repository] {baseRepositoryPath}");
                    }

                    var dapperColumnMapperPath = Path.Combine(dalBasePath, "DapperColumnMapper.cs");
                    if (!File.Exists(dapperColumnMapperPath))
                    {
                        FileUtils.WriteTextAndDirectory(dapperColumnMapperPath, GetDapperColumnMapperFile());
                        Logger.LogSuccess($"[Created Dapper Column Mapper] {dapperColumnMapperPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Repository] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(dalRepositoryPath);
            }

            return scaffoldingResult;
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

        private string AddUpdateMethodCall(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure, string methodBody)
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

        private string GetFilePath(SqlStoredProcedure sqlStoredProcedure)
        {
            var fileName = $"{GetClassName(sqlStoredProcedure)}.#SCHEMA#.Gen.cs".ToSchemaString(sqlStoredProcedure.Schema);
            var dalRepositoryPath = Path.Combine(_config.Directories.DalDirectory.ToSchemaString(sqlStoredProcedure.Schema), fileName);
            return dalRepositoryPath;
        }

        private SyntaxNode CreateCSharpFileOutline(SqlStoredProcedure sqlStoredProcedure)
        {
            var root = SyntaxFactory.CompilationUnit()
                                    .AddUsings(
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDalInterfaceNamespace(sqlStoredProcedure))),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Npgsql")),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Data")),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Dapper")))
                                    .AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(GetDalNamespace(sqlStoredProcedure)))
                                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(CreateCSharpClass(sqlStoredProcedure))));

            return root;
        }

        private ClassDeclarationSyntax CreateCSharpClass(SqlStoredProcedure sqlStoredProcedure)
        {
            return SyntaxFactory.ClassDeclaration(GetClassName(sqlStoredProcedure))
                                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                                .WithBaseList(SyntaxFactory.BaseList(
                                                SyntaxFactory.SeparatedList<BaseTypeSyntax>(new[]
                                                {
                                                    SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("BaseRepository")),
                                                    SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(GetInterfaceName(sqlStoredProcedure)))
                                                })))
                                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                                    SyntaxFactory.ConstructorDeclaration(GetClassName(sqlStoredProcedure))
                                        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                                        .WithParameterList(SyntaxFactory.ParameterList(
                                            SyntaxFactory.SingletonSeparatedList(
                                                SyntaxFactory.Parameter(SyntaxFactory.Identifier("connectionString"))
                                                    .WithType(SyntaxFactory.ParseTypeName("string")))))
                                        .WithInitializer(SyntaxFactory.ConstructorInitializer(
                                            SyntaxKind.BaseConstructorInitializer,
                                            SyntaxFactory.ArgumentList(
                                                SyntaxFactory.SingletonSeparatedList(
                                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("connectionString"))))))
                                        .WithBody(SyntaxFactory.Block(
                                            SyntaxFactory.ParseStatement($"DapperColumnMapper.Register<{GetModelNamespace(sqlStoredProcedure)}.{sqlStoredProcedure.TableName.ToPascalCase()}>();")))));
        }


        private string GenerateStoredProcedureMethod(SqlStoredProcedure sqlStoredProcedure)
        {
            var sb = new StringBuilder();
            var methodName = sqlStoredProcedure.GetMethodName();
            bool useSeperateParameters = !methodName.StartsWith("InsUpd");
            bool returnLists = useSeperateParameters || sqlStoredProcedure.HasCustomReturnType();
            var returnTypeName = sqlStoredProcedure.GetReturnTypeName();

            if (useSeperateParameters)
            {
                sb.Append($"public async Task<List<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}>> {methodName}(");

                foreach (var param in sqlStoredProcedure.Parameters)
                {
                    // Array-typed params (e.g. the GetByIds "Ids" list) are required; every other
                    // param is an optional filter, so it defaults to null - never to the underlying
                    // column's own DB-level default (e.g. gen_random_uuid()), which is meaningless here.
                    if (param.DataType.EndsWith("[]"))
                        sb.Append($"{param.ToCSharpTypeString(true, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()},");
                    else
                        sb.Append($"{param.ToCSharpTypeString(true, GetModelNamespace(sqlStoredProcedure))} {param.ColumnName.ToCamelCase()} = null,");
                }

                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine(")");
            }
            else
                sb.Append($"public async Task<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}> {methodName}({GetModelNamespace(sqlStoredProcedure)}.{sqlStoredProcedure.TableName.ToPascalCase()} {sqlStoredProcedure.TableName.ToCSharpSafeKeyword()})");

            sb.AppendLine("{");

            if (returnLists)
                sb.AppendLine($"    List<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}> ret{returnTypeName} = new List<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}>();");
            else
                sb.AppendLine($"    {GetModelNamespace(sqlStoredProcedure)}.{returnTypeName} ret{returnTypeName};");

            sb.AppendLine("    DynamicParameters dParams = new DynamicParameters();");

            // Append dynamic parameter setup. Every declared function parameter is always bound
            // (even when null) since the generated SQL text below references every @paramName positionally.
            foreach (var param in sqlStoredProcedure.Parameters)
                sb.AppendLine($"    dParams.Add(\"{param.ColumnName}\", {(!useSeperateParameters ? $"{sqlStoredProcedure.TableName.ToCSharpSafeKeyword()}.{param.ColumnName.ToPascalCase()}" : param.ColumnName.ToCamelCase())});");

            sb.AppendLine();
            sb.AppendLine("    using (NpgsqlConnection connection = GetConnection())");
            sb.AppendLine("    {");

            if (returnLists)
                sb.AppendLine($"        ret{returnTypeName} = (await connection.QueryAsync<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}>(\"select * from \\\"{sqlStoredProcedure.Schema}\\\".\\\"{sqlStoredProcedure.StoredProcedureName}\\\"(@{string.Join(", @", sqlStoredProcedure.Parameters.Select(p => p.ColumnName))})\", dParams)).AsList();");
            else
                sb.AppendLine($"        ret{returnTypeName} = (await connection.QueryFirstOrDefaultAsync<{GetModelNamespace(sqlStoredProcedure)}.{returnTypeName}>(\"select * from \\\"{sqlStoredProcedure.Schema}\\\".\\\"{sqlStoredProcedure.StoredProcedureName}\\\"(@{string.Join(", @", sqlStoredProcedure.Parameters.Select(p => p.ColumnName))})\", dParams));");

            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    return ret{returnTypeName};");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private string GetInterfaceName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"I{sqlStoredProcedure.TableName.ToPascalCase()}Repository";
        }

        private string GetClassName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"{sqlStoredProcedure.TableName.ToPascalCase()}Repository";
        }

        private string GetModelNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.ModelNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDalNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DalNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }

        private string GetDalInterfaceNamespace(SqlStoredProcedure sqlStoredProcedure)
        {
            return _config.Namespaces.DalInterfaceNamespace.ToSchemaString(sqlStoredProcedure.Schema);
        }


        private string GetBaseRepositoryFile()
        {
            var ns = _config.Namespaces.DalNamespace.ToSchemaString("public");
            return "using Npgsql;\r\n\r\nnamespace " + ns + "\r\n{\r\n\tpublic partial class BaseRepository\r\n\t{\r\n\t\tprivate string _connectionString;\r\n\t\tpublic BaseRepository(string connectionString)\r\n\t\t{\r\n\t\t\t_connectionString = connectionString;\r\n\t\t}\r\n\t\tprotected NpgsqlConnection GetConnection()\r\n\t\t{\r\n\t\t\treturn new NpgsqlConnection(_connectionString);\r\n\t\t}\r\n\t}\r\n}\r\n";
        }

        /// <summary>
        /// Every generated model property carries a [Column("raw_db_name")] attribute (see
        /// SqlModelScaffold). This registers a Dapper CustomPropertyTypeMap per model type, read
        /// from those attributes, so query results (snake_case columns) materialize onto the
        /// PascalCase C# properties without a blanket process-wide Dapper setting. Each repository's
        /// constructor calls Register&lt;T&gt;() for its own model type; the HashSet makes repeat
        /// calls (one per repository instance) cheap no-ops after the first.
        /// </summary>
        private string GetDapperColumnMapperFile()
        {
            var ns = _config.Namespaces.DalNamespace.ToSchemaString("public");
            return $$"""
using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dapper;

namespace {{ns}}
{
    public static class DapperColumnMapper
    {
        private static readonly HashSet<Type> _registered = new();
        private static readonly object _lock = new();

        public static void Register<T>()
        {
            lock (_lock)
            {
                if (!_registered.Add(typeof(T)))
                    return;

                SqlMapper.SetTypeMap(typeof(T), new CustomPropertyTypeMap(typeof(T), (type, columnName) =>
                    type.GetProperties().FirstOrDefault(p =>
                        string.Equals(p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name, columnName, StringComparison.OrdinalIgnoreCase))));
            }
        }
    }
}
""";
        }
    }
}
