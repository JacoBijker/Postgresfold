using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Config;
using Postgresfold.Scaffold.Model.Enum;
using Postgresfold.Scaffold.Model.Sql;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Postgresfold.Scaffold.Domain.Scaffold
{
    public class SqlModelScaffold
    {
        private readonly CSharpConfig _config;
        private readonly LockingService _lockingService;

        public SqlModelScaffold(CSharpConfig csharpConfig, LockingService lockingService)
        {
            _config = csharpConfig;
            _lockingService = lockingService;
        }

        public async Task<ScaffoldResult> GenerateCode(SqlTable sqlTable)
        {
            var scaffoldingResult = ScaffoldResult.Updated;
            var fileBody = GenerateCSharpModel(sqlTable);
            var fileName = $"{GetClassName(sqlTable)}.#SCHEMA#.Gen.cs".ToSchemaString(sqlTable.Schema);
            var modelPath = Path.Combine(_config.Directories.ModelDirectory.ToSchemaString(sqlTable.Schema), fileName);
            var existingModelContent = string.Empty;

            if (File.Exists(modelPath))
                existingModelContent = FileUtils.SafeReadAllText(modelPath);
            else
                scaffoldingResult = ScaffoldResult.Created;

            try
            {
                await _lockingService.AcquireLockAsync(modelPath);

                if (!existingModelContent.Equals(fileBody))
                {
                    FileUtils.WriteTextAndDirectory(modelPath, fileBody);
                    Logger.LogSuccess($"[Created Model] {modelPath}");
                }
                else
                {
#if DEBUGFORCESCAFFOLD
                    FileUtils.WriteTextAndDirectory(modelPath, fileBody);
                    Logger.LogSuccess($"[Force Created Model] {modelPath}");
#else
                    Logger.LogSkipped($"[Skipped Model] {modelPath}");
                    scaffoldingResult = ScaffoldResult.Skipped;
#endif
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Model Error] {ex.Message}");
            }
            finally
            {
                _lockingService.ReleaseLock(modelPath);
            }

            return scaffoldingResult;
        }

        public async Task<ScaffoldResult> DeleteCode(SqlTable sqlTable)
        {
            var modelPath = GetFilePath(sqlTable);

            if (!File.Exists(modelPath))
                return ScaffoldResult.Skipped;

            await _lockingService.AcquireLockAsync(modelPath);

            File.Delete(modelPath);
            Logger.LogSuccess($"[Deleted Model] {modelPath}");

            _lockingService.ReleaseLock(modelPath);

            return ScaffoldResult.Deleted;
        }

        private string GetFilePath(SqlTable sqlTable)
        {
            var fileName = $"{GetClassName(sqlTable)}.Gen.cs";
            var modelPath = Path.Combine(_config.Directories.ModelDirectory.ToSchemaString(sqlTable.Schema), fileName);
            return modelPath;
        }

        private string GenerateCSharpModel(SqlTable sqlTable)
        {
            var primaryConstraint =
                sqlTable.Constraints.FirstOrDefault(s => s.ConstraintType == ConstraintType.PrimaryKey);

            if (primaryConstraint is null)
                throw new Exception($"No primary constraint found in table {sqlTable.TableName}");

            // Create a class declaration
            var classDeclaration = SyntaxFactory.ClassDeclaration(sqlTable.TableName)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword));

            var totalRowsColumn = new SqlColumn()
            {
                ColumnName = "TotalRows",
                DataType = "INT",
                IsNullable = true,
            };

            var columnsToAdd = sqlTable.Columns.Union([totalRowsColumn]);
            foreach (var column in columnsToAdd)
            {
                if (column.ColumnName == primaryConstraint.Column)
                    column.IsNullable = true;
                
                var typeName = column.ToCSharpTypeString(false);
                 if (!typeName.EndsWith("?") && typeName == "string")
                     typeName += "?";
                
                var property = SyntaxFactory.PropertyDeclaration(
                        SyntaxFactory.ParseTypeName(typeName), column.ColumnName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddAccessorListAccessors(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

                if (column.ColumnName == primaryConstraint.Column)
                    column.IsNullable = false;

                // Make property readonly if required
                if (column.IsReadonly)
                {
                    property = property.WithAccessorList(
                        SyntaxFactory.AccessorList(
                            SyntaxFactory.SingletonList(
                                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                }

                if (column.DataType == "GEOGRAPHY")
                {
                    property = property.AddAttributeLists(
                        SyntaxFactory.AttributeList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Attribute(
                                    SyntaxFactory.IdentifierName("Newtonsoft.Json.JsonIgnore")))));
                }

                classDeclaration = classDeclaration.AddMembers(property);
            }

            // Add properties for foreign keys (based on constraints)
            foreach (var constraint in sqlTable.Constraints.Where(c =>
                         c.ConstraintType == Model.Enum.ConstraintType.ForeignKey))
            {
                //Remove Id from the name to ensure when multiple FK's reference the same column we dont generate duplicates
                var nonIdName = constraint.Column.Substring(0, constraint.Column.Length - 2);

                var fkProperty = SyntaxFactory
                    .PropertyDeclaration(SyntaxFactory.ParseTypeName(constraint.RefTable + "?"), nonIdName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddAccessorListAccessors(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

                classDeclaration = classDeclaration.AddMembers(fkProperty);
            }

            // Wrap the class in the provided namespace
            var namespaceDeclaration = SyntaxFactory
                .NamespaceDeclaration(
                    SyntaxFactory.ParseName(_config.Namespaces.ModelNamespace.ToSchemaString(sqlTable.Schema)))
                .AddMembers(classDeclaration);

            // Create the syntax tree
            var compilationUnit = SyntaxFactory.CompilationUnit()
                .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System")))
                .AddMembers(namespaceDeclaration);

            // Normalize and return the code as a string
            return compilationUnit.NormalizeWhitespace().ToFullString();
        }

        private string GetClassName(SqlTable sqlTable)
        {
            return sqlTable.TableName.ToPascalCase();
        }
    }
}