using Dapper;
using Npgsql;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Sql;

namespace Postgresfold.Scaffold.Domain.Scaffold
{
    /// <summary>
    /// Builds the CRUD PL/pgSQL functions for a table and executes them directly against the live
    /// Postgres connection (CREATE OR REPLACE FUNCTION). Nothing is ever written to disk here -
    /// the database is the source of truth for generated procs, not a project file.
    /// </summary>
    public partial class SqlScriptFileScaffold
    {
        public async Task<List<SqlStoredProcedure>> GenerateCode(NpgsqlConnection connection, SqlTable sqlTable)
        {
            var procedures = new List<SqlStoredProcedure>();

            procedures.Add(await Apply(connection, BuildInsertUpdateProcedure(sqlTable)));
            procedures.Add(await Apply(connection, BuildGetByIdProcedure(sqlTable)));
            procedures.Add(await Apply(connection, BuildGetByPrimaryKeyIdsProcedure(sqlTable)));

            if (sqlTable.Constraints.Any(s => s.ConstraintType == Model.Enum.ConstraintType.ForeignKey))
            {
                procedures.Add(await Apply(connection, BuildGetByForeignKeysProcedure(sqlTable)));
                procedures.Add(await Apply(connection, BuildGetByForeignKeysPagingProcedure(sqlTable)));
            }

            foreach (var index in sqlTable.Indexes)
                procedures.Add(await Apply(connection, BuildGetByIndexedColumnProcedure(sqlTable, index)));

            return procedures;
        }

        private async Task<SqlStoredProcedure> Apply(NpgsqlConnection connection, (SqlStoredProcedure Procedure, string Sql) built)
        {
            await connection.ExecuteAsync(built.Sql);
            Logger.LogSuccess($"[Applied Function] {built.Procedure.Schema}.{built.Procedure.StoredProcedureName}");
            return built.Procedure;
        }
    }
}
