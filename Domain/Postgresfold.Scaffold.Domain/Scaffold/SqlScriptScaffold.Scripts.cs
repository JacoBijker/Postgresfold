using Postgresfold.Scaffold.Model.Sql;
using System.Text;

namespace Postgresfold.Scaffold.Domain.Scaffold
{
    public partial class SqlScriptFileScaffold
    {
        private string[] skipDTDefaults = new string[] { "CreateDT", "UpdateDT" };

        /// <summary>
        /// True upsert: null id creates a new row (generating a uuid PK, or relying on the
        /// column's own identity/serial default for other PK types); a supplied id updates the
        /// row if it exists, or inserts it with that id if it does not. This subsumes what the
        /// original SQL Server tool split into a plain InsUpd proc and a separate '-variant merge'.
        /// </summary>
        public (SqlStoredProcedure Procedure, string Sql) BuildInsertUpdateProcedure(SqlTable table)
        {
            var primaryColumn = GetPrimaryColumn(table);
            var primaryIsUuid = primaryColumn.DataType.Equals("uuid", StringComparison.OrdinalIgnoreCase);
            var hasUpdateDT = table.Columns.Any(c => c.ColumnName.Equals("UpdateDT", StringComparison.OrdinalIgnoreCase));

            var sortedColumns = GetSortedColumnsByNullableDefaultType(table);
            var sortedColumnsNoDt = sortedColumns.Where(s => !skipDTDefaults.Contains(s.ColumnName)).ToList();
            var sortedColumnsNoDtNoPK = sortedColumnsNoDt.Where(s => s.ColumnName != primaryColumn.ColumnName).ToList();

            var procName = $"zgen_{table.TableName}_InsUpd";
            var paramNames = sortedColumnsNoDt.ToDictionary(c => c.ColumnName, c => ToParamName(c.ColumnName));
            var pIdParam = ToParamName(primaryColumn.ColumnName);

            var sb = new StringBuilder();
            sb.AppendLine($"CREATE OR REPLACE FUNCTION {QualifiedName(table.Schema, procName)}(");
            sb.AppendLine(string.Join(",\n", sortedColumnsNoDt.Select(c => $"  {paramNames[c.ColumnName]} {ToPgParamType(c)} DEFAULT NULL")));
            sb.AppendLine($") RETURNS SETOF {QualifiedName(table.Schema, table.TableName)}");
            sb.AppendLine("LANGUAGE plpgsql AS $BODY$");
            sb.AppendLine("BEGIN");

            if (primaryIsUuid)
            {
                sb.AppendLine($"  IF {pIdParam} IS NULL THEN");
                sb.AppendLine($"    {pIdParam} := gen_random_uuid();");
                sb.AppendLine("  END IF;");
                sb.AppendLine();
            }

            sb.AppendLine($"  IF {pIdParam} IS NOT NULL THEN");
            sb.Append($"    UPDATE {QualifiedName(table.Schema, table.TableName)} SET ");
            sb.Append(string.Join(", ", sortedColumnsNoDtNoPK.Select(c => $"{Quote(c.ColumnName)}={paramNames[c.ColumnName]}")));
            if (hasUpdateDT)
                sb.Append($", {Quote("UpdateDT")}=now()");
            sb.AppendLine($" WHERE {Quote(primaryColumn.ColumnName)} = {pIdParam};");
            sb.AppendLine("    IF FOUND THEN");
            sb.AppendLine($"      RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote(primaryColumn.ColumnName)} = {pIdParam};");
            sb.AppendLine("      RETURN;");
            sb.AppendLine("    END IF;");
            sb.AppendLine("  END IF;");
            sb.AppendLine();

            if (primaryIsUuid)
            {
                sb.Append($"  INSERT INTO {QualifiedName(table.Schema, table.TableName)} (");
                sb.Append(string.Join(", ", sortedColumnsNoDt.Select(c => Quote(c.ColumnName))));
                sb.Append(") VALUES (");
                sb.Append(string.Join(", ", sortedColumnsNoDt.Select(c => paramNames[c.ColumnName])));
                sb.AppendLine(");");
            }
            else
            {
                sb.AppendLine($"  IF {pIdParam} IS NULL THEN");
                sb.Append($"    INSERT INTO {QualifiedName(table.Schema, table.TableName)} (");
                sb.Append(string.Join(", ", sortedColumnsNoDtNoPK.Select(c => Quote(c.ColumnName))));
                sb.Append(") VALUES (");
                sb.Append(string.Join(", ", sortedColumnsNoDtNoPK.Select(c => paramNames[c.ColumnName])));
                sb.AppendLine($") RETURNING {Quote(primaryColumn.ColumnName)} INTO {pIdParam};");
                sb.AppendLine("  ELSE");
                sb.Append($"    INSERT INTO {QualifiedName(table.Schema, table.TableName)} (");
                sb.Append(string.Join(", ", sortedColumnsNoDt.Select(c => Quote(c.ColumnName))));
                sb.Append(") VALUES (");
                sb.Append(string.Join(", ", sortedColumnsNoDt.Select(c => paramNames[c.ColumnName])));
                sb.AppendLine(");");
                sb.AppendLine("  END IF;");
            }

            sb.AppendLine();
            sb.AppendLine($"  RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote(primaryColumn.ColumnName)} = {pIdParam};");
            sb.AppendLine("END;");
            sb.AppendLine("$BODY$;");

            var procedure = new SqlStoredProcedure
            {
                TableName = table.TableName,
                Schema = table.Schema,
                StoredProcedureName = procName,
                Parameters = sortedColumnsNoDt.Select(c => CloneParam(c)).ToList()
            };

            return (procedure, sb.ToString());
        }

        public (SqlStoredProcedure Procedure, string Sql) BuildGetByIdProcedure(SqlTable table)
        {
            var primaryKeyColumn = GetPrimaryColumn(table);
            var hasIsActive = HasIsActive(table);
            var procName = $"zgen_{table.TableName}_GetById";
            var pId = ToParamName(primaryKeyColumn.ColumnName);
            var pIsActive = "p_isactive";

            var sb = new StringBuilder();
            sb.Append($"CREATE OR REPLACE FUNCTION {QualifiedName(table.Schema, procName)}({pId} {ToPgParamType(primaryKeyColumn)} DEFAULT NULL");
            if (hasIsActive)
                sb.Append($", {pIsActive} boolean DEFAULT NULL");
            sb.AppendLine($") RETURNS SETOF {QualifiedName(table.Schema, table.TableName)}");
            sb.AppendLine("LANGUAGE plpgsql AS $BODY$");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  IF {pId} IS NULL THEN");
            if (hasIsActive)
            {
                sb.AppendLine($"    IF {pIsActive} IS NULL THEN");
                sb.AppendLine($"      RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} ORDER BY {Quote(primaryKeyColumn.ColumnName)} ASC;");
                sb.AppendLine("    ELSE");
                sb.AppendLine($"      RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote("IsActive")} = {pIsActive} ORDER BY {Quote(primaryKeyColumn.ColumnName)} ASC;");
                sb.AppendLine("    END IF;");
            }
            else
                sb.AppendLine($"    RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} ORDER BY {Quote(primaryKeyColumn.ColumnName)} ASC;");
            sb.AppendLine("  ELSE");
            if (hasIsActive)
            {
                sb.AppendLine($"    IF {pIsActive} IS NULL THEN");
                sb.AppendLine($"      RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote(primaryKeyColumn.ColumnName)} = {pId};");
                sb.AppendLine("    ELSE");
                sb.AppendLine($"      RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote(primaryKeyColumn.ColumnName)} = {pId} AND {Quote("IsActive")} = {pIsActive};");
                sb.AppendLine("    END IF;");
            }
            else
                sb.AppendLine($"    RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote(primaryKeyColumn.ColumnName)} = {pId};");
            sb.AppendLine("  END IF;");
            sb.AppendLine("END;");
            sb.AppendLine("$BODY$;");

            var parameters = new List<SqlColumn> { CloneParam(primaryKeyColumn, forceNullable: true) };
            if (hasIsActive)
                parameters.Add(new SqlColumn { ColumnName = "IsActive", DataType = "boolean", IsNullable = true });

            var procedure = new SqlStoredProcedure { TableName = table.TableName, Schema = table.Schema, StoredProcedureName = procName, Parameters = parameters };
            return (procedure, sb.ToString());
        }

        /// <summary>Lookup by a list of primary key values, e.g. GetByIds when the PK column is 'Id'.</summary>
        public (SqlStoredProcedure Procedure, string Sql) BuildGetByPrimaryKeyIdsProcedure(SqlTable table)
        {
            var primaryKeyColumn = GetPrimaryColumn(table);
            var procName = $"zgen_{table.TableName}_GetBy{primaryKeyColumn.ColumnName}s";
            var pIds = "p_ids";

            var sb = new StringBuilder();
            sb.AppendLine($"CREATE OR REPLACE FUNCTION {QualifiedName(table.Schema, procName)}({pIds} {ToPgScalarType(primaryKeyColumn.DataType)}[])");
            sb.AppendLine($"RETURNS SETOF {QualifiedName(table.Schema, table.TableName)}");
            sb.AppendLine("LANGUAGE sql AS $BODY$");
            sb.AppendLine($"  SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote(primaryKeyColumn.ColumnName)} = ANY({pIds});");
            sb.AppendLine("$BODY$;");

            var parameters = new List<SqlColumn> { new SqlColumn { ColumnName = "Ids", DataType = $"{primaryKeyColumn.DataType}[]", IsNullable = false } };
            var procedure = new SqlStoredProcedure { TableName = table.TableName, Schema = table.Schema, StoredProcedureName = procName, Parameters = parameters };
            return (procedure, sb.ToString());
        }

        public (SqlStoredProcedure Procedure, string Sql) BuildGetByForeignKeysProcedure(SqlTable table)
        {
            var foreignColumns = GetSortedColumnsByNullableDefaultType(table).Where(col => IsForeignKey(col, table)).ToList();
            var hasIsActive = HasIsActive(table);
            var hasCreateDT = table.Columns.Any(c => c.ColumnName.Equals("CreateDT", StringComparison.OrdinalIgnoreCase));
            var procName = $"zgen_{table.TableName}_GetByForeignKeys";
            var pSort = "p_sortdirection";

            var sb = new StringBuilder();
            sb.AppendLine($"CREATE OR REPLACE FUNCTION {QualifiedName(table.Schema, procName)}(");
            var paramDecls = foreignColumns.Select(c => $"  {ToParamName(c.ColumnName)} {ToPgParamType(c)} DEFAULT NULL").ToList();
            if (hasIsActive)
                paramDecls.Add("  p_isactive boolean DEFAULT NULL");
            paramDecls.Add($"  {pSort} text DEFAULT 'ASC'");
            sb.AppendLine(string.Join(",\n", paramDecls));
            sb.AppendLine($") RETURNS SETOF {QualifiedName(table.Schema, table.TableName)}");
            sb.AppendLine("LANGUAGE plpgsql AS $BODY$");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  RETURN QUERY SELECT * FROM {QualifiedName(table.Schema, table.TableName)}");
            var whereClauses = foreignColumns.Select(c => $"({ToParamName(c.ColumnName)} IS NULL OR {Quote(c.ColumnName)} = {ToParamName(c.ColumnName)})").ToList();
            if (hasIsActive)
                whereClauses.Add("(p_isactive IS NULL OR \"IsActive\" = p_isactive)");
            if (whereClauses.Any())
                sb.AppendLine($"  WHERE {string.Join(" AND ", whereClauses)}");
            if (hasCreateDT)
            {
                sb.AppendLine("  ORDER BY");
                sb.AppendLine($"    CASE WHEN {pSort} = 'ASC' THEN {Quote("CreateDT")} END ASC,");
                sb.AppendLine($"    CASE WHEN {pSort} = 'DESC' THEN {Quote("CreateDT")} END DESC;");
            }
            else
                sb.AppendLine("  ;");
            sb.AppendLine("END;");
            sb.AppendLine("$BODY$;");

            var parameters = foreignColumns.Select(c => CloneParam(c, forceNullable: true)).ToList();
            if (hasIsActive)
                parameters.Add(new SqlColumn { ColumnName = "IsActive", DataType = "boolean", IsNullable = true });
            parameters.Add(new SqlColumn { ColumnName = "SortDirection", DataType = "text", IsNullable = true, DefaultValue = "ASC" });

            var procedure = new SqlStoredProcedure { TableName = table.TableName, Schema = table.Schema, StoredProcedureName = procName, Parameters = parameters };
            return (procedure, sb.ToString());
        }

        public (SqlStoredProcedure Procedure, string Sql) BuildGetByForeignKeysPagingProcedure(SqlTable table)
        {
            var foreignColumns = GetSortedColumnsByNullableDefaultType(table).Where(col => IsForeignKey(col, table)).ToList();
            var hasIsActive = HasIsActive(table);
            var hasCreateDT = table.Columns.Any(c => c.ColumnName.Equals("CreateDT", StringComparison.OrdinalIgnoreCase));
            var procName = $"zgen_{table.TableName}_GetByForeignKeysPaging";
            var pSort = "p_sortdirection";

            var sb = new StringBuilder();
            sb.AppendLine($"CREATE OR REPLACE FUNCTION {QualifiedName(table.Schema, procName)}(");
            var paramDecls = foreignColumns.Select(c => $"  {ToParamName(c.ColumnName)} {ToPgParamType(c)} DEFAULT NULL").ToList();
            if (hasIsActive)
                paramDecls.Add("  p_isactive boolean DEFAULT NULL");
            paramDecls.Add("  p_pagenumber int DEFAULT 1");
            paramDecls.Add("  p_pagesize int DEFAULT 50");
            paramDecls.Add($"  {pSort} text DEFAULT 'ASC'");
            sb.AppendLine(string.Join(",\n", paramDecls));

            var returnColumns = table.Columns.Select(c => $"{Quote(c.ColumnName)} {c.DataType}").ToList();
            returnColumns.Add($"{Quote("TotalRows")} int");
            sb.AppendLine($") RETURNS TABLE({string.Join(", ", returnColumns)})");
            sb.AppendLine("LANGUAGE plpgsql AS $BODY$");
            sb.AppendLine("BEGIN");
            sb.AppendLine("  RETURN QUERY SELECT " + string.Join(", ", table.Columns.Select(c => $"t.{Quote(c.ColumnName)}")) + ", count(*) OVER()::int");
            sb.AppendLine($"  FROM {QualifiedName(table.Schema, table.TableName)} t");
            var whereClauses = foreignColumns.Select(c => $"({ToParamName(c.ColumnName)} IS NULL OR t.{Quote(c.ColumnName)} = {ToParamName(c.ColumnName)})").ToList();
            if (hasIsActive)
                whereClauses.Add("(p_isactive IS NULL OR t.\"IsActive\" = p_isactive)");
            if (whereClauses.Any())
                sb.AppendLine($"  WHERE {string.Join(" AND ", whereClauses)}");
            if (hasCreateDT)
            {
                sb.AppendLine("  ORDER BY");
                sb.AppendLine($"    CASE WHEN {pSort} = 'ASC' THEN t.{Quote("CreateDT")} END ASC,");
                sb.AppendLine($"    CASE WHEN {pSort} = 'DESC' THEN t.{Quote("CreateDT")} END DESC");
            }
            sb.AppendLine("  LIMIT p_pagesize OFFSET p_pagesize * (p_pagenumber - 1);");
            sb.AppendLine("END;");
            sb.AppendLine("$BODY$;");

            var parameters = foreignColumns.Select(c => CloneParam(c, forceNullable: true)).ToList();
            if (hasIsActive)
                parameters.Add(new SqlColumn { ColumnName = "IsActive", DataType = "boolean", IsNullable = true });
            parameters.Add(new SqlColumn { ColumnName = "PageNumber", DataType = "integer", IsNullable = true, DefaultValue = "1" });
            parameters.Add(new SqlColumn { ColumnName = "PageSize", DataType = "integer", IsNullable = true, DefaultValue = "50" });
            parameters.Add(new SqlColumn { ColumnName = "SortDirection", DataType = "text", IsNullable = true, DefaultValue = "ASC" });

            var procedure = new SqlStoredProcedure { TableName = table.TableName, Schema = table.Schema, StoredProcedureName = procName, Parameters = parameters };
            return (procedure, sb.ToString());
        }

        public (SqlStoredProcedure Procedure, string Sql) BuildGetByIndexedColumnProcedure(SqlTable table, SqlIndex index)
        {
            var column = table.Columns.First(s => s.ColumnName == index.Column);
            var procName = $"zgen_{table.TableName}_GetBy{index.Column}";
            var pCol = ToParamName(column.ColumnName);

            var sb = new StringBuilder();
            sb.AppendLine($"CREATE OR REPLACE FUNCTION {QualifiedName(table.Schema, procName)}({pCol} {ToPgParamType(column)})");
            sb.AppendLine($"RETURNS SETOF {QualifiedName(table.Schema, table.TableName)}");
            sb.AppendLine("LANGUAGE sql AS $BODY$");
            sb.AppendLine($"  SELECT * FROM {QualifiedName(table.Schema, table.TableName)} WHERE {Quote(column.ColumnName)} = {pCol};");
            sb.AppendLine("$BODY$;");

            var parameters = new List<SqlColumn> { CloneParam(column) };
            var procedure = new SqlStoredProcedure { TableName = table.TableName, Schema = table.Schema, StoredProcedureName = procName, Parameters = parameters };
            return (procedure, sb.ToString());
        }

        #region Private Methods
        private bool IsForeignKey(SqlColumn column, SqlTable table)
        {
            return table.Constraints.Any(c => c.ConstraintType == Model.Enum.ConstraintType.ForeignKey && c.Column == column.ColumnName);
        }

        private bool HasIsActive(SqlTable table)
        {
            return table.Columns.Any(c => c.ColumnName.Equals("IsActive", StringComparison.OrdinalIgnoreCase));
        }

        private SqlColumn GetPrimaryColumn(SqlTable table)
        {
            var primaryKeyColumn = table.Constraints.FirstOrDefault(c => c.ConstraintType == Model.Enum.ConstraintType.PrimaryKey)?.Column;
            if (string.IsNullOrEmpty(primaryKeyColumn))
                throw new InvalidOperationException("No primary key column found for table " + table.TableName);

            return table.Columns.First(c => c.ColumnName == primaryKeyColumn);
        }

        private List<SqlColumn> GetSortedColumnsByNullableDefaultType(SqlTable table)
        {
            Dictionary<string, string> columnDescriptions = new Dictionary<string, string>();
            Dictionary<string, int> columnIndexes = new Dictionary<string, int>();
            var primaryKey = table.Constraints.First(con => con.ConstraintType == Model.Enum.ConstraintType.PrimaryKey);
            int i = 0;
            table.Columns.ForEach(s =>
            {
                columnIndexes[s.ColumnName] = i++;
                columnDescriptions[s.ColumnName] = "3_NULLABLE";

                if (s.ColumnName.Equals(primaryKey.Column))
                    columnDescriptions[s.ColumnName] = "3_NULLABLE";
                else if (!s.IsNullable)
                {
                    if (string.IsNullOrWhiteSpace(s.DefaultValue))
                        columnDescriptions[s.ColumnName] = "1_NOT_NULLABLE_W_DEFAULT";
                    else
                        columnDescriptions[s.ColumnName] = "2_NOT_NULLABLE_NO_DEFAULT";
                }

                if (s.ColumnName == "IsActive")
                    columnDescriptions[s.ColumnName] = "4_LAST";
            });

            return table.Columns.OrderBy(s => columnDescriptions[s.ColumnName])
                                .ThenBy(s => columnIndexes[s.ColumnName])
                                .ToList();
        }

        private SqlColumn CloneParam(SqlColumn column, bool forceNullable = false)
        {
            return new SqlColumn
            {
                ColumnName = column.ColumnName,
                DataType = column.DataType,
                DataTypeLength = column.DataTypeLength,
                DefaultValue = column.DefaultValue,
                IsNullable = forceNullable || column.IsNullable
            };
        }

        private string ToParamName(string columnName) => $"p_{columnName.ToCamelCase()}";

        private string Quote(string identifier) => $"\"{identifier}\"";

        private string QualifiedName(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

        /// <summary>Postgres type text usable directly in a function's parameter/return declaration.</summary>
        private string ToPgParamType(SqlColumn column) => column.DataType;

        private string ToPgScalarType(string dataType) => dataType;
        #endregion
    }
}
