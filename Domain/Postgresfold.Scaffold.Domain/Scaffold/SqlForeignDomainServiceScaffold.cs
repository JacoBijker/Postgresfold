using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Config;
using Postgresfold.Scaffold.Model.Sql;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Text;
using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Model.Enum;
using System.Data;

namespace Postgresfold.Scaffold.Domain.Scaffold
{
    public class SqlForeignDomainServiceScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;

        public SqlForeignDomainServiceScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlStoredProcedure sqlStoredProcedure)
        {
            var methodName = GetMethodNameWithFK(sqlStoredProcedure);
            if (methodName.StartsWith("InsUpd", StringComparison.OrdinalIgnoreCase) ||
                methodName.StartsWith("Del", StringComparison.OrdinalIgnoreCase))
                return ScaffoldResult.Skipped;

            var domainServicePath = GetFilePath(sqlStoredProcedure);
            if (!File.Exists(domainServicePath))
                return ScaffoldResult.Skipped;

            try
            {
                var scaffoldingResult = ScaffoldResult.Updated;

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

                return scaffoldingResult;
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _lockingService.ReleaseLock(domainServicePath);
            }
        }

        public async Task<ScaffoldResult> GenerateCode(SqlTable sqlTable, SqlStoredProcedure sqlStoredProcedure)
        {
            var methodName = GetMethodNameWithFK(sqlStoredProcedure);
            if (methodName.StartsWith("InsUpd", StringComparison.OrdinalIgnoreCase) ||
                methodName.StartsWith("Del", StringComparison.OrdinalIgnoreCase))
                return ScaffoldResult.Skipped;

            //Do not generate if we dont have any FK constraints
            var foreignConstraints = sqlTable.Constraints.Where(s => s.ConstraintType == Model.Enum.ConstraintType.ForeignKey);
            if (!foreignConstraints.Any())
                return ScaffoldResult.Skipped;

            var scaffoldingResult = ScaffoldResult.Updated;
            var domainServicePath = GetFilePath(sqlStoredProcedure);
            var existingFileContent = string.Empty;

            try
            {
                await _lockingService.AcquireLockAsync(domainServicePath);

                SyntaxNode syntaxNode;
                if (!File.Exists(domainServicePath))
                {
                    Logger.LogWarn($"[File does not exist] Creating {domainServicePath}");
                    syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);
                    scaffoldingResult = ScaffoldResult.Created;
                }
                else
                {
                    existingFileContent = FileUtils.SafeReadAllText(domainServicePath);
                    var syntaxTree = CSharpSyntaxTree.ParseText(existingFileContent);
                    syntaxNode = syntaxTree.GetRoot();
                }

                syntaxNode = UpdateAndGeneratePartialClass(syntaxNode, sqlTable, sqlStoredProcedure);

                var updatedFileContent = syntaxNode.NormalizeWhitespace().ToString();
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
                Logger.LogError($"[Foreign Service] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(domainServicePath);
            }

            return scaffoldingResult;
        }

        private string RemoveMethodCall(SyntaxNode root, SqlStoredProcedure sqlStoredProcedure)
        {
            var className = GetClassName(sqlStoredProcedure);
            var methodName = GetMethodNameWithFK(sqlStoredProcedure);

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

            //If there are no publicly callable methods, return empty string which will delete the file
            if (!updatedRoot.DescendantNodes()
                            .OfType<MethodDeclarationSyntax>()
                            .Where(method => method.Modifiers.Any(SyntaxKind.PublicKeyword)).Any())
                return string.Empty;

            return updatedRoot.NormalizeWhitespace().ToFullString();
        }

        private string GetFilePath(SqlStoredProcedure sqlStoredProcedure)
        {
            var fileName = $"{GetClassName(sqlStoredProcedure)}.#SCHEMA#.Foreign.Gen.cs".ToSchemaString(sqlStoredProcedure.Schema);
            var domainServicePath = Path.Combine(_config.Directories.DomainDirectory.ToSchemaString(sqlStoredProcedure.Schema), fileName);
            return domainServicePath;
        }

        private SyntaxNode CreateCSharpFileOutline(SqlStoredProcedure sqlStoredProcedure)
        {
            var root = SyntaxFactory.CompilationUnit()
                                    .AddUsings(
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDomainInterfaceNamespace(sqlStoredProcedure))),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetDalInterfaceNamespace(sqlStoredProcedure))),
                                        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(GetModelNamespace(sqlStoredProcedure.Schema))))
                                    .AddMembers(SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(GetDomainNamespace(sqlStoredProcedure)))
                                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
                                        RoslynUtils.CreateClass(GetClassName(sqlStoredProcedure), GetInterfaceName(sqlStoredProcedure), SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword))));

            return root;
        }

        public SyntaxNode UpdateAndGeneratePartialClass(SyntaxNode root, SqlTable sqlTable, SqlStoredProcedure sqlStoredProcedure)
        {
            var className = GetClassName(sqlStoredProcedure);
            var classDeclaration = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == className);

            if (classDeclaration == null)
                throw new System.Exception($"Class {className} not found in the provided code.");

            var foreignKeyConstraints = sqlTable.Constraints.Where(c => c.ConstraintType == ConstraintType.ForeignKey).ToList();

            //In case where multiple references are made to the same table, we need to ensure we only create them once.
            var distinctTableRefs = foreignKeyConstraints.Select(s => s.RefTable).Distinct();
            var fields = distinctTableRefs.Select(refTable =>
                RoslynUtils.CreateField($"_{refTable.ToCamelCase()}Repo", $"I{refTable.ToPascalCase()}Repository", SyntaxKind.PrivateKeyword, SyntaxKind.ReadOnlyKeyword));

            var parameters = distinctTableRefs.Select(refTable =>
                RoslynUtils.CreateParameter($"{refTable.ToCamelCase()}Repo", $"I{refTable.ToPascalCase()}Repository")).ToList();

            parameters.Insert(0, RoslynUtils.CreateParameter($"repo", $"I{sqlTable.TableName.ToPascalCase()}Repository"));

            var assignments = distinctTableRefs.Select(refTable =>
                RoslynUtils.CreateAssignmentExpression($"_{refTable.ToCamelCase()}Repo", $"{refTable.ToCamelCase()}Repo")).ToList();

            assignments.Insert(0, RoslynUtils.CreateAssignmentExpression($"_repo", $"repo"));

            var constructor = SyntaxFactory.ConstructorDeclaration(className)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
                .WithBody(SyntaxFactory.Block(assignments));

            var protectedMethods = foreignKeyConstraints.Select(constraint => GenerateAppendMethod(sqlTable, constraint));

            var publicMethods = classDeclaration.Members.OfType<MethodDeclarationSyntax>()
                                                        .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
                                                        .ToList();

            var updatedMethod = SyntaxFactory.ParseMemberDeclaration(GenerateStoredProcedureMethod(sqlTable, sqlStoredProcedure)) as MethodDeclarationSyntax;
            var existingMethodIndex = publicMethods.FindIndex(s => s.Identifier.Text.Equals(updatedMethod.Identifier.Text));

            if (existingMethodIndex >= 0)
                publicMethods[existingMethodIndex] = updatedMethod;
            else
                publicMethods.Add(updatedMethod);

            protectedMethods = protectedMethods.OrderBy(s => s.Identifier.Text).ToList();
            publicMethods = publicMethods.OrderBy(s => s.Identifier.Text).ToList();

            var allTogether = fields.Cast<MemberDeclarationSyntax>()
                                    .Union([constructor])
                                    .Union(publicMethods.Cast<MemberDeclarationSyntax>())
                                    .Union(protectedMethods.Cast<MemberDeclarationSyntax>())
                                    .ToArray();

            var syntaxNode = CreateCSharpFileOutline(sqlStoredProcedure);

            var newClassDeclaration = syntaxNode.DescendantNodes()
                                                .OfType<ClassDeclarationSyntax>()
                                                .FirstOrDefault(c => c.Identifier.Text == className);

            var updatedClassDeclaration = newClassDeclaration.AddMembers(allTogether);

            var updatedRoot = syntaxNode.ReplaceNode(newClassDeclaration, updatedClassDeclaration);
            return updatedRoot;
        }

        private MethodDeclarationSyntax GenerateAppendMethod(SqlTable sqlTable, SqlConstraint constraint)
        {
            var tableName = sqlTable.TableName;
            var refTable = constraint.RefTable.ToPascalCase();
            var columnName = constraint.Column.ToPascalCase();
            var refColumnName = constraint.RefColumn.ToPascalCase();
            var modelNs = GetModelNamespace(sqlTable.Schema);
            var column = sqlTable.Columns.FirstOrDefault(s => s.ColumnName == constraint.Column);
            var safeTableName = tableName.ToCSharpSafeKeyword();

            // Use StringBuilder to construct the method as a string
            var methodBuilder = new StringBuilder();
            var nonIdName = constraint.Column.GetNonIdName();
            methodBuilder.AppendLine($"protected async Task Append{nonIdName}(List<{modelNs}.{tableName.ToPascalCase()}> {tableName.ToCamelCase()}s)");
            methodBuilder.AppendLine("{");

            var hasDefaultValue = !string.IsNullOrWhiteSpace(column.DefaultValue);
            if (column.IsNullable || hasDefaultValue)
                methodBuilder.AppendLine($"    var distinct{columnName}s = {tableName.ToCamelCase()}s.Where(s => s.{columnName}.HasValue).Select(s => s.{columnName}.Value).Distinct().ToList();");
            else
                methodBuilder.AppendLine($"    var distinct{columnName}s = {tableName.ToCamelCase()}s.Select(s => s.{columnName}).Distinct().ToList();");

            // Assumes the referenced table's primary key follows the same generic "Id" convention as
            // every table this tool scaffolds (see BuildGetByPrimaryKeyIdsProcedure) - GetBy{Pk}s becomes GetByIds.
            methodBuilder.AppendLine($"    var distinct{refTable}s = await _{refTable.ToCamelCase()}Repo.Get{refTable}ByIds(distinct{columnName}s);");
            methodBuilder.AppendLine();
            methodBuilder.AppendLine($"    foreach (var {safeTableName} in {tableName.ToCamelCase()}s)");
            methodBuilder.AppendLine("    {");
            methodBuilder.AppendLine($"        {safeTableName}.{nonIdName} = distinct{refTable}s.FirstOrDefault(s => s.{refColumnName} == {safeTableName}.{columnName});");
            methodBuilder.AppendLine("    }");
            methodBuilder.AppendLine("}");

            // Parse the method string into a MethodDeclarationSyntax
            var methodDeclaration = SyntaxFactory.ParseMemberDeclaration(methodBuilder.ToString()) as MethodDeclarationSyntax;

            return methodDeclaration;
        }

        private string GenerateStoredProcedureMethod(SqlTable sqlTable, SqlStoredProcedure sqlStoredProcedure)
        {
            var sb = new StringBuilder();
            var methodName = GetMethodNameWithFK(sqlStoredProcedure);
            var returnTypeName = sqlStoredProcedure.GetReturnTypeName();

            bool useSeperateParameters = !methodName.StartsWith("InsUpd");
            if (useSeperateParameters)
            {
                sb.Append($"public async Task<List<{GetModelNamespace(sqlStoredProcedure.Schema)}.{returnTypeName}>> {GetMethodNameWithFK(sqlStoredProcedure)}(");

                foreach (var param in sqlStoredProcedure.Parameters)
                    if (param.DataType.EndsWith("[]"))
                        sb.Append($"{param.ToCSharpTypeString(true, GetModelNamespace(sqlStoredProcedure.Schema))} {param.ColumnName.ToCamelCase()},");
                    else
                        sb.Append($"{param.ToCSharpTypeString(true, GetModelNamespace(sqlStoredProcedure.Schema))} {param.ColumnName.ToCamelCase()} = null,");

                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine(")");
                sb.AppendLine("{");

                sb.Append($"    var ret{returnTypeName} = await _repo.{sqlStoredProcedure.GetMethodName()}(");
                foreach (var param in sqlStoredProcedure.Parameters)
                    if (!param.ColumnName.Equals("RetMsg", StringComparison.OrdinalIgnoreCase))
                        sb.Append($"{param.ColumnName.ToCamelCase()},");

                sb.Remove(sb.Length - 1, 1);
                sb.AppendLine($");");

                foreach (var constraint in sqlTable.Constraints.Where(s => s.ConstraintType == ConstraintType.ForeignKey))
                {
                    //Remove Id from the name to ensure when multiple FK's reference the same column we dont generate duplicates
                    var nonIdName = constraint.Column.GetNonIdName();
                    sb.AppendLine($"    await Append{nonIdName}(ret{returnTypeName});");
                }

                sb.AppendLine($"    return ret{returnTypeName};");
                sb.AppendLine("}");

                return sb.ToString();
            }
            else
            {
                sb.AppendLine($"public async Task<{GetModelNamespace(sqlStoredProcedure.Schema)}.{returnTypeName}> {methodName}({GetModelNamespace(sqlStoredProcedure.Schema)}.{sqlStoredProcedure.TableName.ToPascalCase()} {sqlStoredProcedure.TableName.ToCamelCase()})");
                sb.AppendLine("{");
                sb.AppendLine($"    return await _repo.{GetMethodNameWithFK(sqlStoredProcedure)}({sqlStoredProcedure.TableName.ToCamelCase()});");
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private string GetMethodNameWithFK(SqlStoredProcedure sqlStoredProcedure)
        {
            return sqlStoredProcedure.GetMethodName() + "IncludeForeignKeys";
        }

        private string GetInterfaceName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"I{sqlStoredProcedure.TableName.ToPascalCase()}Service";
        }

        private string GetClassName(SqlStoredProcedure sqlStoredProcedure)
        {
            return $"{sqlStoredProcedure.TableName.ToPascalCase()}Service";
        }

        private string GetModelNamespace(string schema)
        {
            return _config.Namespaces.ModelNamespace.ToSchemaString(schema);
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
