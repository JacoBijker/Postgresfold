using Postgresfold.Scaffold.Model.Sql;

namespace Postgresfold.Scaffold.Domain.Util
{
    public static class SqlScaffoldUtils
    {
        public static string GetMethodName(this SqlStoredProcedure sqlStoredProcedure)
        {
            return sqlStoredProcedure.StoredProcedureName.Replace("zgen_", "")
                                                         .Replace($"{sqlStoredProcedure.TableName}_", "", StringComparison.OrdinalIgnoreCase)
                                                         .Replace("GetBy", $"Get{sqlStoredProcedure.TableName.ToPascalCase()}By")
                                                         .Replace("InsUpd", $"InsUpd{sqlStoredProcedure.TableName.ToPascalCase()}")
                                                         .ToPascalCase();

        }

        public static string ToSchemaString(this string declaration, string schema)
        {
            if (schema.Equals("public", StringComparison.OrdinalIgnoreCase))
                return declaration.Replace(".#SCHEMA#", string.Empty)
                                  .Replace($"/#SCHEMA#", string.Empty)
                                  .Replace($"\\#SCHEMA#", string.Empty);

            return declaration.Replace("#SCHEMA#", schema.ToUpper());
        }

        public static string ToCSharpTypeString(this SqlColumn column, bool forceNullable, string? modelNamespace = null)
        {
            string csharpType = ToScalarOrArrayCSharpTypeString(column.DataType);

            var hasDefaultValue = !string.IsNullOrWhiteSpace(column.DefaultValue);
            if (column.IsNullable || hasDefaultValue || forceNullable)
                if (csharpType != "string" && !csharpType.StartsWith("List<"))
                    return $"{csharpType}?";

            return csharpType;
        }

        public static string ToCSharpSafeKeyword(this string tableName)
        {
            if (tableName.ToCamelCase() == "event") //We cant use c# keywords like event
                return "evt";

            return tableName.ToCamelCase();
        }

        /// <summary>
        /// PascalCases a foreign-key column name and strips a trailing "Id" (e.g. "user_id" ->
        /// "User"), for the FK's navigation-property name. Shared by SqlModelScaffold and
        /// SqlForeignDomainServiceScaffold so both always compute the exact same name.
        /// </summary>
        public static string GetNonIdName(this string rawColumnName)
        {
            var pascal = rawColumnName.ToPascalCase();
            return pascal.EndsWith("Id", StringComparison.Ordinal) ? pascal[..^2] : pascal;
        }

        public static string GetReturnTypeName(this SqlStoredProcedure sqlStoredProcedure)
        {
            return string.IsNullOrWhiteSpace(sqlStoredProcedure.CustomReturnType)
                ? sqlStoredProcedure.TableName.ToPascalCase()
                : sqlStoredProcedure.CustomReturnType;
        }

        public static bool HasCustomReturnType(this SqlStoredProcedure sqlStoredProcedure)
        {
            return !string.IsNullOrWhiteSpace(sqlStoredProcedure.CustomReturnType);
        }

        private static string ToScalarOrArrayCSharpTypeString(string sqlType)
        {
            if (sqlType.EndsWith("[]"))
                return $"List<{ToScalarCSharpTypeString(sqlType[..^2])}>";

            return ToScalarCSharpTypeString(sqlType);
        }

        private static string ToScalarCSharpTypeString(string sqlType)
        {
            return sqlType.ToLower() switch
            {
                "integer" => "int",
                "int" => "int",
                "int4" => "int",
                "smallint" => "short",
                "int2" => "short",
                "bigint" => "long",
                "int8" => "long",
                "boolean" => "bool",
                "bool" => "bool",
                "character varying" => "string",
                "varchar" => "string",
                "character" => "string",
                "char" => "string",
                "text" => "string",
                "citext" => "string",
                "numeric" => "decimal",
                "decimal" => "decimal",
                "real" => "float",
                "float4" => "float",
                "double precision" => "double",
                "float8" => "double",
                "date" => "DateTime",
                "timestamp without time zone" => "DateTime",
                "timestamp" => "DateTime",
                "timestamp with time zone" => "DateTimeOffset",
                "timestamptz" => "DateTimeOffset",
                "time without time zone" => "TimeSpan",
                "time" => "TimeSpan",
                "uuid" => "Guid",
                "bytea" => "byte[]",
                "json" => "string",
                "jsonb" => "string",
                _ => throw new Exception($"ToCSharpTypeString lookup exception: {sqlType}")
            };
        }
    }
}
