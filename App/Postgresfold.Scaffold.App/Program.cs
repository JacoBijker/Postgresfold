using Postgresfold.Scaffold.App.Worker;
using Postgresfold.Scaffold.Domain.Reader;
using Postgresfold.Scaffold.Domain.Scaffold;
using Postgresfold.Scaffold.Domain.Service;
using Postgresfold.Scaffold.Domain.Util;
using Postgresfold.Scaffold.Model.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            var parsedArgs = ParseArgs(args);
            var configuration = new ConfigurationBuilder()
                .AddCommandLine(parsedArgs, new Dictionary<string, string> {
                { "-dbupproject", "dbupproject" },
                { "-namespace", "namespace" },
                { "-connectionstring", "connectionstring" },
                { "-regen", "regen" },
                { "-delete", "delete" },
                { "-help", "help" },
                })
                .Build();

            if (args.Contains("-help"))
            {
                Console.WriteLine("Available Command-Line Switches:");
                Console.WriteLine("-connectionstring <cs>   : Required. Postgres connection string tables/procs are read from and written to.");
                Console.WriteLine("-dbupproject <path>      : Overrides the DbUp project path instead of letting the application search for it. Used for namespace discovery only.");
                Console.WriteLine("-namespace <name>        : Overrides the namespace for scaffolded code instead of deriving it from the DbUp project.");
                Console.WriteLine("-regen <params>          : Scopes regeneration. Leave empty to regenerate every table in every schema. Can specify a schema 'public', a table 'public.Customer', or an existing generated proc 'public.zgen_Customer_GetById' (its owning table is regenerated). Can send multiple entities with ;");
                Console.WriteLine("-delete <params>         : Deletes previously generated code and functions. Never touches the actual table/data. Leave empty to delete everything found, or specify a schema 'public', a table 'public.Customer' (table model + all its procs; the table need not still exist), or a single proc 'public.zgen_Customer_GetById'. Can send multiple entities with ;");

                return;
            }

            var regenerate = args.Contains("-regen");
            var delete = args.Contains("-delete");
            if (regenerate && delete)
            {
                Console.WriteLine("Only specify one of: -regen, -delete");
                return;
            }

            string overrideDbUpProjectPath = configuration["dbupproject"];
            string overrideNamespace = configuration["namespace"];
            string connectionString = configuration["connectionstring"];

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.WriteLine("-connectionstring is required.");
                return;
            }

            string basePath = Environment.CurrentDirectory;
            if (overrideDbUpProjectPath is null)
                overrideDbUpProjectPath = SearchForDbUpProjectFile(basePath);

            Logger.LogDebug($"Using DbUp Project: {overrideDbUpProjectPath}");
            var csharpConfig = SetupProjectConfiguration(basePath, overrideDbUpProjectPath, overrideNamespace);

            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.AddConfiguration(configuration); // Merge command-line args into DI config
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.None); // Suppress hosting logs
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddMemoryCache();

                    services.AddSingleton(csharpConfig);
                    services.AddSingleton<LockingService>();
                    services.AddSingleton<PostgresSchemaReader>();
                    services.AddSingleton<SqlTableCachingService>();

                    services.AddTransient<SqlDalRepositoryScaffold>();
                    services.AddTransient<SqlScriptFileScaffold>();
                    services.AddTransient<SqlModelScaffold>();
                    services.AddTransient<SqlDalRepositoryInterfaceScaffold>();
                    services.AddTransient<SqlDomainServiceScaffold>();
                    services.AddTransient<SqlDomainServiceInterfaceScaffold>();
                    services.AddTransient<SqlForeignDomainServiceScaffold>();
                    services.AddTransient<SqlForeignDomainServiceInterfaceScaffold>();
                    services.AddTransient<SqlDalRepositoryServiceCollectionExtensionScaffold>();
                    services.AddTransient<SqlDomainServiceServiceCollectionExtensionScaffold>();

                    if (delete)
                        services.AddHostedService<PostgresDeleteWorker>();
                    else
                        services.AddHostedService<PostgresScaffoldWorker>();
                })
                .Build();

            host.Run();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex.Message);
        }
    }

    private static string SearchForDbUpProjectFile(string directory)
    {
        Logger.LogInfo($"Searching for a DbUp project in '{directory}'");
        if (!Directory.Exists(directory))
        {
            Logger.LogError($"Directory does not exist: '{directory}'");
            throw new Exception("Directory does not exist");
        }

        var csprojFiles = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories).ToList();
        var dbUpFiles = csprojFiles.Where(IsDbUpProject).ToList();

        if (!dbUpFiles.Any())
            throw new Exception("No DbUp project found. Aborting");

        var dbUpFile = dbUpFiles.First();
        if (dbUpFiles.Count > 1)
        {
            dbUpFile = dbUpFiles.FirstOrDefault(s => s.Contains("DbUp", StringComparison.OrdinalIgnoreCase)) ?? dbUpFile;
            Logger.LogWarn($"Multiple DbUp projects found, Selected: {dbUpFile}");
        }

        return dbUpFile;
    }

    private static bool IsDbUpProject(string csprojPath)
    {
        if (csprojPath.Contains(".DbUp.", StringComparison.OrdinalIgnoreCase) || csprojPath.EndsWith(".DbUp.csproj", StringComparison.OrdinalIgnoreCase))
            return true;

        var text = FileUtils.SafeReadAllText(csprojPath);
        return text.Contains("dbup-postgresql", StringComparison.OrdinalIgnoreCase) || text.Contains("dbup-core", StringComparison.OrdinalIgnoreCase);
    }

    private static CSharpConfig SetupProjectConfiguration(string rootDirectory, string dbUpProjectFile, string overrideNamespace)
    {
        Logger.LogDebug("Configuring Project");
        string rootNamespace;
        if (string.IsNullOrWhiteSpace(overrideNamespace))
        {
            var projectTxt = FileUtils.SafeReadAllText(dbUpProjectFile);

            var rootNamespaceRx = Regex.Match(projectTxt, @"<RootNamespace>(.*)</RootNamespace>", RegexOptions.Singleline);
            if (rootNamespaceRx.Success)
                rootNamespace = rootNamespaceRx.Groups[1].Value;
            else
            {
                var assemblyNameRx = Regex.Match(projectTxt, @"<AssemblyName>(.*)</AssemblyName>", RegexOptions.Singleline);
                rootNamespace = assemblyNameRx.Success
                    ? assemblyNameRx.Groups[1].Value
                    : Path.GetFileNameWithoutExtension(dbUpProjectFile);
            }

            // Strip the DbUp project's own suffix (e.g. "MyApp.DB.DbUp" or "MyApp.DbUp") to get the app's root namespace
            rootNamespace = Regex.Replace(rootNamespace, @"\.DB\.DbUp$", "", RegexOptions.IgnoreCase);
            rootNamespace = Regex.Replace(rootNamespace, @"\.DbUp$", "", RegexOptions.IgnoreCase);
        }
        else
            rootNamespace = overrideNamespace;

        var csharpConfig = new CSharpConfig(rootDirectory, rootNamespace, dbUpProjectFile);

        return csharpConfig;
    }

    private static string[] ParseArgs(string[] args)
    {
        var parsedArgs = new List<string>();
        string lastKey = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("-")) // It's a key
            {
                if (lastKey != null) // Previous key had no value, treat it as a flag
                {
                    parsedArgs.Add(lastKey);
                    parsedArgs.Add("");
                }
                lastKey = arg;
            }
            else // It's a value
            {
                if (lastKey != null)
                {
                    parsedArgs.Add(lastKey);
                    parsedArgs.Add(arg);
                    lastKey = null;
                }
                else
                {
                    // Unexpected value without a key (ignore or handle error)
                }
            }
        }

        // Handle trailing flag
        if (lastKey != null)
        {
            parsedArgs.Add(lastKey);
            parsedArgs.Add("");
        }

        return parsedArgs.ToArray();
    }
}
