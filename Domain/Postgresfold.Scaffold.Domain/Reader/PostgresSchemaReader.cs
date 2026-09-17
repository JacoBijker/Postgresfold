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
    /// from the current table shape (or, for deletion, simply discovered by name in pg_proc), so there
    /// is no reader that reconstructs a proc's full parameter list from the catalog.
    /// </summary>
    public class PostgresSchemaReader
    {
        /// <summary>
        /// DbUp's own bookkeeping table(s) - never a real application table, so never scaffolded.
        /// Matches the same "dbup"/"schemaversions" convention the original MSSQL tool skipped.
        /// </summary>
        private static readonly string[] _excludedTableNames = { "dbup", "schemaversions" };

        public async Task<List<(string Schema, string TableName)>> GetAllTables(NpgsqlConnection connection, string? schemaFilter = null)
        {
            var sql = @"
                SELECT table_schema, table_name
                FROM information_schema.tables
                WHERE table_type = 'BASE TABLE'
                  AND table_schema NOT IN ('pg_catalog', 'information_schema')
                  AND lower(table_name) <> ALL(@excludedTableNames)
                  AND (@schema IS NULL OR table_schema = @schema)
                ORDER BY table_schema, table_name;";

            var rows = await connection.QueryAsync<(string table_schema, string table_name)>(sql, new { schema = schemaFilter, excludedTableNames = _excludedTableNames });
            return rows.Select(r => (r.table_schema, r.table_name)).ToList();
        }

        /// <summary>
        /// Finds existing generated ("zgen_") functions by name in pg_proc. Pass <paramref name="tableName"/>
        /// to scope to one table's procs, or leave it null for every generated proc in the schema - this
        /// works even when the owning table has since been dropped, which is what makes it possible to
        /// clean up orphaned generated code/functions for a table that no longer exists.
        /// </summary>
        public async Task<List<string>> GetGeneratedProcedureNames(NpgsqlConnection connection, string schema, string? tableName = null)
        {
            var pattern = tableName is null ? "zgen\\_%" : $"zgen\\_{tableName}\\_%";

            var sql = @"
                SELECT p.proname
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = @schema AND p.proname LIKE @pattern ESCAPE '\'
                ORDER BY p.proname;";

            var rows = await connection.QueryAsync<string>(sql, new { schema, pattern });
            return rows.ToList();
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
