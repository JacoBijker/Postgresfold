using Npgsql;
using Postgresfold.Scaffold.Domain.Reader;
using Postgresfold.Scaffold.Domain.Scaffold;
using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Sql;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Postgresfold.Scaffold.App.Worker
{
    /// <summary>
    /// Removes previously generated code and PL/pgSQL functions for a table (or a single proc).
    /// This never touches the actual table/data in Postgres - it only cleans up what this tool
    /// generates: the C# Model/Dal/Domain/DI layers, and the "zgen_" functions themselves.
    /// </summary>
    public class PostgresDeleteWorker : BackgroundService
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IConfiguration _configuration;

        private readonly PostgresSchemaReader _schemaReader;
        private readonly SqlModelScaffold _sqlModelScaffold;
        private readonly SqlDalRepositoryScaffold _sqlDalRepositoryScaffold;
        private readonly SqlDalRepositoryInterfaceScaffold _sqlDalRepositoryInterfaceScaffold;
        private readonly SqlDomainServiceScaffold _sqlDomainServiceScaffold;
        private readonly SqlDomainServiceInterfaceScaffold _sqlDomainServiceInterfaceScaffold;
        private readonly SqlForeignDomainServiceScaffold _sqlForeignDomainServiceScaffold;
        private readonly SqlForeignDomainServiceInterfaceScaffold _sqlForeignDomainServiceInterfaceScaffold;
        private readonly SqlDalRepositoryServiceCollectionExtensionScaffold _sqlDalRepositoryServiceCollectionExtensionScaffold;
        private readonly SqlDomainServiceServiceCollectionExtensionScaffold _sqlDomainServiceServiceCollectionExtensionScaffold;

        public PostgresDeleteWorker(IHostApplicationLifetime lifetime,
                                     IConfiguration configuration,
                                     PostgresSchemaReader schemaReader,
                                     SqlModelScaffold sqlModelScaffold,
                                     SqlDalRepositoryScaffold sqlDalRepositoryScaffold,
                                     SqlDalRepositoryInterfaceScaffold sqlDalRepositoryInterfaceScaffold,
                                     SqlDomainServiceScaffold sqlDomainServiceScaffold,
                                     SqlDomainServiceInterfaceScaffold sqlDomainServiceInterfaceScaffold,
                                     SqlForeignDomainServiceScaffold sqlForeignDomainServiceScaffold,
                                     SqlForeignDomainServiceInterfaceScaffold sqlForeignDomainServiceInterfaceScaffold,
                                     SqlDalRepositoryServiceCollectionExtensionScaffold sqlDalRepositoryServiceCollectionExtensionScaffold,
                                     SqlDomainServiceServiceCollectionExtensionScaffold sqlDomainServiceServiceCollectionExtensionScaffold)
        {
            _lifetime = lifetime;
            _configuration = configuration;
            _schemaReader = schemaReader;
            _sqlModelScaffold = sqlModelScaffold;
            _sqlDalRepositoryScaffold = sqlDalRepositoryScaffold;
            _sqlDalRepositoryInterfaceScaffold = sqlDalRepositoryInterfaceScaffold;
            _sqlDomainServiceScaffold = sqlDomainServiceScaffold;
            _sqlDomainServiceInterfaceScaffold = sqlDomainServiceInterfaceScaffold;
            _sqlForeignDomainServiceScaffold = sqlForeignDomainServiceScaffold;
            _sqlForeignDomainServiceInterfaceScaffold = sqlForeignDomainServiceInterfaceScaffold;
            _sqlDalRepositoryServiceCollectionExtensionScaffold = sqlDalRepositoryServiceCollectionExtensionScaffold;
            _sqlDomainServiceServiceCollectionExtensionScaffold = sqlDomainServiceServiceCollectionExtensionScaffold;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var connectionString = _configuration["connectionstring"];
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(stoppingToken);

            var deleteArgs = _configuration["delete"];

            if (string.IsNullOrWhiteSpace(deleteArgs))
            {
                Logger.LogWarn("Deleting ALL generated code and functions for every table found in the database");
                foreach (var (schema, tableName) in await _schemaReader.GetAllTables(connection))
                    await DeleteTable(connection, schema, tableName);
            }
            else
            {
                var entities = deleteArgs.Split(";", StringSplitOptions.RemoveEmptyEntries);
                foreach (var deleteEntry in entities)
                {
                    var argSplit = deleteEntry.Split(".");
                    var schema = argSplit.Length > 1 ? argSplit[0] : "public";
                    var entityName = argSplit.Length > 1 ? argSplit[1] : argSplit[0];

                    if (argSplit.Length == 1)
                    {
                        Logger.LogWarn($"Deleting ALL generated code and functions for schema {entityName}");
                        foreach (var (tableSchema, tableName) in await _schemaReader.GetAllTables(connection, entityName))
                            await DeleteTable(connection, tableSchema, tableName);
                    }
                    else if (entityName.StartsWith("zgen_", StringComparison.OrdinalIgnoreCase))
                    {
                        // Delete just this one proc - table model and other procs are left alone.
                        Logger.LogInfo($"Delete Stored Procedure {schema}.{entityName}");
                        await DeleteProcedure(connection, schema, entityName);
                    }
                    else
                    {
                        // Table-scoped delete. Does not require the table to still exist in Postgres -
                        // this is how you clean up generated code/functions left behind after dropping a table.
                        Logger.LogInfo($"Delete Table {schema}.{entityName}");
                        await DeleteTable(connection, schema, entityName);
                    }
                }
            }

            _lifetime.StopApplication();
        }

        /// <summary>Deletes the C# model plus every generated proc (C# + PL/pgSQL function) for a table.</summary>
        private async Task DeleteTable(NpgsqlConnection connection, string schema, string tableName)
        {
            Logger.LogInfo($"[Deleting Table] {schema}.{tableName}");

            try
            {
                var procNames = await _schemaReader.GetGeneratedProcedureNames(connection, schema, tableName);
                foreach (var procName in procNames)
                    await DeleteProcedureCode(connection, schema, tableName, procName);

                await _sqlModelScaffold.DeleteCode(new SqlTable { Schema = schema, TableName = tableName });
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Error Deleting Table] {schema}.{tableName}, {ex.Message}");
            }

            Logger.LogInfo($"[DONE Deleting Table] {schema}.{tableName}");
        }

        /// <summary>Deletes a single named proc without touching the table's model or its other procs.</summary>
        private async Task DeleteProcedure(NpgsqlConnection connection, string schema, string procName)
        {
            // Procs have no independent source of truth - the owning table name is recovered from the
            // "zgen_{Table}_{Suffix}" naming convention, same as everywhere else in this tool.
            var ownerTableName = procName["zgen_".Length..].Split("_")[0];

            try
            {
                await DeleteProcedureCode(connection, schema, ownerTableName, procName);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Error Deleting Procedure] {schema}.{procName}, {ex.Message}");
            }
        }

        private async Task DeleteProcedureCode(NpgsqlConnection connection, string schema, string tableName, string procName)
        {
            var stub = new SqlStoredProcedure { Schema = schema, TableName = tableName, StoredProcedureName = procName };

            var repoResult = await _sqlDalRepositoryScaffold.DeleteCode(stub);
            await _sqlDalRepositoryInterfaceScaffold.DeleteCode(stub);
            var domainResult = await _sqlDomainServiceScaffold.DeleteCode(stub);
            await _sqlDomainServiceInterfaceScaffold.DeleteCode(stub);
            await _sqlForeignDomainServiceScaffold.DeleteCode(stub);
            await _sqlForeignDomainServiceInterfaceScaffold.DeleteCode(stub);

            if (repoResult == Model.Enum.ScaffoldResult.Deleted)
                await _sqlDalRepositoryServiceCollectionExtensionScaffold.DeleteCode(stub);

            if (domainResult == Model.Enum.ScaffoldResult.Deleted)
                await _sqlDomainServiceServiceCollectionExtensionScaffold.DeleteCode(stub);

            await connection.ExecuteAsync($"DROP FUNCTION IF EXISTS \"{schema}\".\"{procName}\";");
            Logger.LogSuccess($"[Dropped Function] {schema}.{procName}");
        }
    }
}
