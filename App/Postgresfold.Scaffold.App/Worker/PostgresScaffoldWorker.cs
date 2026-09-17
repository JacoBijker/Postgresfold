using Npgsql;
using Postgresfold.Scaffold.Domain.Reader;
using Postgresfold.Scaffold.Domain.Scaffold;
using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Config;
using Postgresfold.Scaffold.Model.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Postgresfold.Scaffold.App.Worker
{
    /// <summary>
    /// Reads table (and, by extension, stored procedure) definitions live from the connected Postgres
    /// database and regenerates the C# Model/Dal/Domain/DI layers plus the CRUD PL/pgSQL functions
    /// themselves. There is no local .sql source of truth to watch or diff against - the database is it.
    /// </summary>
    public class PostgresScaffoldWorker : BackgroundService
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IConfiguration _configuration;

        private readonly CSharpConfig _csharpConfig;
        private readonly PostgresSchemaReader _schemaReader;
        private readonly SqlTableCachingService _sqlTableCachingService;
        private readonly SqlDalRepositoryScaffold _sqlDalRepositoryScaffold;
        private readonly SqlScriptFileScaffold _sqlScriptFileScaffold;
        private readonly SqlModelScaffold _sqlModelScaffold;
        private readonly SqlDalRepositoryInterfaceScaffold _sqlDalRepositoryInterfaceScaffold;
        private readonly SqlDomainServiceScaffold _sqlDomainServiceScaffold;
        private readonly SqlDomainServiceInterfaceScaffold _sqlDomainServiceInterfaceScaffold;
        private readonly SqlForeignDomainServiceScaffold _sqlForeignDomainServiceScaffold;
        private readonly SqlForeignDomainServiceInterfaceScaffold _sqlForeignDomainServiceInterfaceScaffold;
        private readonly SqlDalRepositoryServiceCollectionExtensionScaffold _sqlDalRepositoryServiceCollectionExtensionScaffold;
        private readonly SqlDomainServiceServiceCollectionExtensionScaffold _sqlDomainServiceServiceCollectionExtensionScaffold;

        public PostgresScaffoldWorker(IHostApplicationLifetime lifetime,
                                       IConfiguration configuration,
                                       CSharpConfig csharpConfig,
                                       PostgresSchemaReader schemaReader,
                                       SqlTableCachingService sqlTableCachingService,
                                       SqlDalRepositoryScaffold sqlDalRepositoryScaffold,
                                       SqlScriptFileScaffold sqlScriptFileScaffold,
                                       SqlModelScaffold sqlModelScaffold,
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
            _csharpConfig = csharpConfig;
            _schemaReader = schemaReader;
            _sqlTableCachingService = sqlTableCachingService;
            _sqlDalRepositoryScaffold = sqlDalRepositoryScaffold;
            _sqlScriptFileScaffold = sqlScriptFileScaffold;
            _sqlModelScaffold = sqlModelScaffold;
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

            var regenArgs = _configuration["regen"];
            var allTables = await _schemaReader.GetAllTables(connection);
            var allTableKeys = allTables.Select(t => $"{t.Schema}.{t.TableName}".ToLowerInvariant()).ToHashSet();

            if (string.IsNullOrWhiteSpace(regenArgs))
            {
                Logger.LogInfo("Regenerate all tables and all generated procs");
                foreach (var (schema, tableName) in allTables)
                    await RegenerateTable(connection, schema, tableName);
            }
            else
            {
                var entities = regenArgs.Split(";", StringSplitOptions.RemoveEmptyEntries);
                foreach (var regenEntry in entities)
                {
                    var argSplit = regenEntry.Split(".");
                    var schema = argSplit.Length > 1 ? argSplit[0] : "public";
                    var entityName = argSplit.Length > 1 ? argSplit[1] : argSplit[0];

                    if (argSplit.Length == 1)
                    {
                        // Regenerate every table in a schema (-regen=public)
                        Logger.LogInfo($"Regenerate entire {entityName} schema");
                        foreach (var (tableSchema, tableName) in allTables.Where(t => t.Schema.Equals(entityName, StringComparison.OrdinalIgnoreCase)))
                            await RegenerateTable(connection, tableSchema, tableName);
                    }
                    else if (allTableKeys.Contains($"{schema}.{entityName}".ToLowerInvariant()))
                    {
                        // Regenerate a specific table (-regen=public.Customer)
                        Logger.LogInfo($"Regenerate Table {schema}.{entityName}");
                        await RegenerateTable(connection, schema, entityName);
                    }
                    else if (entityName.StartsWith("zgen_", StringComparison.OrdinalIgnoreCase))
                    {
                        // Regenerate the table that owns a specific generated proc (-regen=public.zgen_Customer_GetById).
                        // Procs have no independent source of their own - they are always rebuilt from their table.
                        var ownerTableName = entityName["zgen_".Length..].Split("_")[0];
                        if (allTableKeys.Contains($"{schema}.{ownerTableName}".ToLowerInvariant()))
                        {
                            Logger.LogInfo($"Regenerate Table {schema}.{ownerTableName} (owner of proc {entityName})");
                            await RegenerateTable(connection, schema, ownerTableName);
                        }
                        else
                        {
                            Logger.LogError($"Could not find the owning table for proc {schema}.{entityName}");
                        }
                    }
                    else
                    {
                        Logger.LogError($"Could not find a table or generated proc {schema}.{entityName}");
                    }
                }
            }

            _lifetime.StopApplication();
        }

        /// <summary>
        /// Regenerates a single table - rebuilds its C# model, its CRUD PL/pgSQL functions (executed
        /// live against the connection), and the Dal/Domain/DI layers for every one of those functions.
        /// </summary>
        private async Task RegenerateTable(NpgsqlConnection connection, string schema, string tableName)
        {
            Logger.LogInfo($"[Regenerating Table] {schema}.{tableName}");

            try
            {
                var sqlTable = await _sqlTableCachingService.GetLatestTableAndCache(connection, schema, tableName);

                await _sqlModelScaffold.GenerateCode(sqlTable);
                var procedures = await _sqlScriptFileScaffold.GenerateCode(connection, sqlTable);

                foreach (var sqlStoredProcedureInfo in procedures)
                    await RegenerateStoredProcedureCode(sqlTable, sqlStoredProcedureInfo);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Error Table] {schema}.{tableName}, {ex.Message}");
            }

            Logger.LogInfo($"[DONE Table] {schema}.{tableName}");
        }

        /// <summary>Regenerates the Dal/Domain/DI C# layers for a single already-applied proc.</summary>
        private async Task RegenerateStoredProcedureCode(SqlTable sqlTable, SqlStoredProcedure sqlStoredProcedureInfo)
        {
            var repoResult = await _sqlDalRepositoryScaffold.GenerateCode(sqlStoredProcedureInfo);
            await _sqlDalRepositoryInterfaceScaffold.GenerateCode(sqlStoredProcedureInfo);
            var domainResult = await _sqlDomainServiceScaffold.GenerateCode(sqlStoredProcedureInfo);
            await _sqlDomainServiceInterfaceScaffold.GenerateCode(sqlStoredProcedureInfo);

            await _sqlForeignDomainServiceScaffold.GenerateCode(sqlTable, sqlStoredProcedureInfo);
            await _sqlForeignDomainServiceInterfaceScaffold.GenerateCode(sqlTable, sqlStoredProcedureInfo);

            if (repoResult == Model.Enum.ScaffoldResult.Created)
                await _sqlDalRepositoryServiceCollectionExtensionScaffold.GenerateCode(sqlStoredProcedureInfo);

            if (domainResult == Model.Enum.ScaffoldResult.Created)
                await _sqlDomainServiceServiceCollectionExtensionScaffold.GenerateCode(sqlStoredProcedureInfo);
        }
    }
}
