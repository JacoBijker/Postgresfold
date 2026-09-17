using Dapper;
using Npgsql;
using Postgresfold.Scaffold.Model.Enum;
using Postgresfold.Scaffold.Model.Sql;

namespace Postgresfold.Scaffold.Domain.Reader
{
    /// <summary>
    /// Reads table shape (columns, primary/foreign keys, single-column indexes) directly from a live
    /// Postgres connection via information_schema/pg_catalog, replacing the old .sql-file text parsers.
    /// Stored procedures have no independent authored source in this tool - they are always rebuilt
    /// from the current table shape, so there is no equivalent "read an existing proc" reader.
    /// </summary>
    public class PostgresSchemaReader
    {
        public async Task<List<(string Schema, string TableName)>> GetAllTables(NpgsqlConnection connection, string? schemaFilter = null)
        {
            var sql = @"
                SELECT table_schema, table_name
                FROM information_schema.tables
                WHERE table_type = 'BASE TABLE'
                  AND table_schema NOT IN ('pg_catalog', 'information_schema')
                  AND (@schema IS NULL OR table_schema = @schema)
                ORDER BY table_schema, table_name;";

            var rows = await connection.QueryAsync<(string table_schema, string table_name)>(sql, new { schema = schemaFilter });
            return rows.Select(r => (r.table_schema, r.table_name)).ToList();
        }

        public async Task<SqlTable> GetTable(NpgsqlConnection connection, string schema, string tableName)
        {
            var table = new SqlTable { Schema = schema, TableName = tableName };

            var columnSql = @"
                SELECT column_name AS ""ColumnName"",
                       CASE WHEN data_type = 'ARRAY' THEN trim(leading '_' from udt_name) || '[]' ELSE data_type END AS ""DataType"",
                       character_maximum_length::text AS ""DataTypeLength"",
                       column_default AS ""DefaultValue"",
                       (is_nullable = 'YES') AS ""IsNullable""
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = @tableName
                ORDER BY ordinal_position;";

            table.Columns = (await connection.QueryAsync<SqlColumn>(columnSql, new { schema, tableName })).ToList();

            var constraintSql = @"
                SELECT tc.constraint_name AS ""ConstraintName"",
                       tc.constraint_type AS ""RawConstraintType"",
                       kcu.column_name AS ""Column"",
                       ccu.table_schema AS ""RefSchema"",
                       ccu.table_name AS ""RefTable"",
                       ccu.column_name AS ""RefColumn""
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                LEFT JOIN information_schema.constraint_column_usage ccu
                  ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema
                 AND tc.constraint_type = 'FOREIGN KEY'
                WHERE tc.table_schema = @schema AND tc.table_name = @tableName
                  AND tc.constraint_type IN ('PRIMARY KEY', 'FOREIGN KEY')
                ORDER BY tc.constraint_type;";

            var rawConstraints = await connection.QueryAsync<RawConstraint>(constraintSql, new { schema, tableName });
            table.Constraints = rawConstraints.Select(c => new SqlConstraint
            {
                ConstraintName = c.ConstraintName,
                ConstraintType = c.RawConstraintType == "PRIMARY KEY" ? ConstraintType.PrimaryKey : ConstraintType.ForeignKey,
                Column = c.Column,
                RefSchema = c.RefSchema,
                RefTable = c.RefTable,
                RefColumn = c.RefColumn
            }).ToList();

            var indexSql = @"
                SELECT i.relname AS ""IndexName"",
                       ix.indisunique AS ""IsUnique"",
                       a.attname AS ""Column""
                FROM pg_index ix
                JOIN pg_class t ON t.oid = ix.indrelid
                JOIN pg_class i ON i.oid = ix.indexrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
                WHERE n.nspname = @schema AND t.relname = @tableName
                  AND NOT ix.indisprimary
                  AND cardinality(ix.indkey) = 1;";

            table.Indexes = (await connection.QueryAsync<SqlIndex>(indexSql, new { schema, tableName })).ToList();

            return table;
        }

        private class RawConstraint
        {
            public string ConstraintName { get; set; } = string.Empty;
            public string RawConstraintType { get; set; } = string.Empty;
            public string Column { get; set; } = string.Empty;
            public string? RefSchema { get; set; }
            public string? RefTable { get; set; }
            public string? RefColumn { get; set; }
        }
    }
}
