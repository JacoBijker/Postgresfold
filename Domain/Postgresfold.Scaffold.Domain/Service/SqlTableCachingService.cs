using Npgsql;
using Postgresfold.Scaffold.Domain.Reader;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Sql;
using Microsoft.Extensions.Caching.Memory;

namespace Postgresfold.Scaffold.Domain.Service
{
    public class SqlTableCachingService
    {
        private readonly IMemoryCache _memoryCache;
        private readonly PostgresSchemaReader _schemaReader;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);  // Lock for thread safety

        public SqlTableCachingService(IMemoryCache memoryCache, PostgresSchemaReader schemaReader)
        {
            _memoryCache = memoryCache;
            _schemaReader = schemaReader;
        }

        public async Task<SqlTable> GetCachedTable(NpgsqlConnection connection, string schema, string tableName)
        {
            var cacheKey = $"{schema}.{tableName}";
            var cachedData = _memoryCache.Get<SqlTable>(cacheKey);
            if (cachedData is not null)
            {
                Logger.LogDebug($"Cache hit [{cacheKey}]");
                return cachedData;
            }

            Logger.LogDebug($"Cache miss [{cacheKey}]");
            return await GetLatestTableAndCache(connection, schema, tableName);
        }

        public async Task<SqlTable> GetLatestTableAndCache(NpgsqlConnection connection, string schema, string tableName)
        {
            var cacheKey = $"{schema}.{tableName}";

            // Using lock to ensure only one thread populates the cache at a time
            await _cacheLock.WaitAsync();
            try
            {
                var sqlTable = await _schemaReader.GetTable(connection, schema, tableName);
                Logger.LogDebug($"Read [{cacheKey}]");

                var expirationTime = DateTimeOffset.Now.AddMinutes(10);
                _memoryCache.Set(cacheKey, sqlTable, expirationTime);

                return sqlTable;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error reading '{cacheKey}'\r\n{ex.Message}");
                throw;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public void RemoveCached(string schema, string tableName)
        {
            _memoryCache.Remove($"{schema}.{tableName}");
        }
    }
}
