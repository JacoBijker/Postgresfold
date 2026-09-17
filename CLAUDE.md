# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Postgresfold.Scaffold is a code generation tool that follows a **database-first approach** for PostgreSQL databases. It generates CRUD PL/pgSQL functions and corresponding C# code (Model/Dal.Dapper/Domain/DI layers) using **convention-over-configuration** - one fixed way of doing things, no options to configure it differently.

Table and stored-procedure definitions are read **live from the connected Postgres database** (`information_schema`/`pg_catalog`). There is no local `.sql` source of truth, and generated procs are never written to disk - they exist only as `CREATE OR REPLACE FUNCTION` statements executed directly against the connection. The root namespace is derived from a sibling **DbUp project** (a plain `.csproj` referencing `dbup-postgresql`, e.g. `MyApp.DB.DbUp`) - used only for namespace discovery, nothing is ever written into it.

## Architecture

Three layered projects, referenced App → Domain → Model:

- **Model/Postgresfold.Scaffold.Model** - POCOs (`Sql/SqlTable`, `SqlColumn`, `SqlConstraint`, `SqlIndex`, `SqlStoredProcedure`) and config (`Config/CSharpConfig`, `CSharpNamespaces`, `CSharpDirectories`). No dependencies.
- **Domain/Postgresfold.Scaffold.Domain**
  - `Reader/PostgresSchemaReader.cs` - reads live table shape (columns, PK/FK constraints, single-column indexes) via `information_schema`/`pg_catalog`, and finds existing `zgen_*` functions by name for deletion. Procs have no independent source of truth; they are always rebuilt from the current table shape, so there's no reader that reconstructs a proc's parameter list from the catalog.
  - `Scaffold/SqlScriptScaffold*.cs` - builds the CRUD PL/pgSQL function text and executes it via `NpgsqlConnection.ExecuteAsync` (Dapper). No file I/O.
  - `Scaffold/SqlModelScaffold.cs`, `SqlDalRepositoryScaffold(+Interface).cs`, `SqlDomainServiceScaffold(+Interface).cs`, `SqlForeignDomainServiceScaffold(+Interface).cs`, `*ServiceCollectionExtensionScaffold.cs` - emit the C# Model/Dal.Dapper/Domain/DI layers from the `SqlTable`/`SqlStoredProcedure` POCOs, using Roslyn to add/update/remove individual generated methods in place.
- **App/Postgresfold.Scaffold.App**
  - `Program.cs` - CLI entry point; discovers the DbUp project, derives the namespace, builds `CSharpConfig`, wires up DI.
  - `Worker/PostgresScaffoldWorker.cs` - the `-regen` (and default) mode: reads tables live and regenerates the C# layers + PL/pgSQL functions.
  - `Worker/PostgresDeleteWorker.cs` - the `-delete` mode: removes generated code and drops generated functions. Never touches actual tables/data.

There is no file-watcher mode (nothing to watch - the source is a live database) and no TypeScript/Angular/SQLite generation - this tool does exactly one thing: Postgres table/proc → C#.

## Common Commands

```bash
# Build the solution
dotnet build Postgresfold.Scaffold.sln

# Run from the target solution's root directory (searches for a *.DbUp.csproj there)
dotnet run --project App/Postgresfold.Scaffold.App -- -connectionstring "Host=localhost;Database=mydb;Username=postgres;Password=..."
```

## CLI Usage

Run from the root of the target C# solution (the one containing the DbUp project and where `Model/`, `Dal/`, `Domain/`, `Common/` output folders live).

- `-connectionstring <cs>` - **Required.** Postgres connection string tables/procs are read from and written to.
- `-dbupproject <path>` - Overrides the DbUp project path instead of searching for it (search looks for a `.csproj` whose path contains `.DbUp.` or which references `dbup-postgresql`/`dbup-core`).
- `-namespace <name>` - Overrides the derived namespace.
- `-regen <params>` - Default mode. Leave empty to regenerate every table in every schema. Scope with a schema (`public`), a table (`public.Customer`), or an existing generated proc (`public.zgen_Customer_GetById` - its owning table is regenerated, since procs aren't independently sourced). Multiple entities with `;`.
- `-delete <params>` - Removes generated code + drops generated functions. **Never touches the actual table/data.** Same scoping as `-regen`, except a single proc target (`public.zgen_Customer_GetById`) deletes just that proc, leaving the model and its other procs alone. A table target does not require the table to still exist in Postgres - this is how you clean up after dropping a table.
- `-help` - Show all switches.

`-regen` and `-delete` are mutually exclusive.

## Important Conventions

### Proc naming and shape

Every generated proc follows `zgen_{TableName}_{Operation}`, as a PL/pgSQL **function** (not procedure) returning `SETOF`/`TABLE`:

- `InsUpd` - a true upsert: null id → insert (generating a `uuid` PK via `gen_random_uuid()`, or relying on the column's own identity/serial default); supplied id → update if it exists, otherwise insert with that id.
- `GetById` - by primary key, or all rows when the id is null; filters on `IsActive` too if the table has that column.
- `GetBy{PrimaryKeyColumn}s` (e.g. `GetByIds`) - looks up by an array of PK values (`uuid[]`, `integer[]`, etc.) via `= ANY(...)`.
- `GetByForeignKeys` / `GetByForeignKeysPaging` - filtered/paginated lookup by the table's FK columns.
- `GetBy{IndexedColumn}` - one per single-column index on the table.

There is no delete-proc generation, no variant switches, no table-valued-parameter machinery. Multi-value lookups always use native Postgres arrays.

### Not handled

Full-text search, `geography`/PostGIS columns, and check constraints have no generation path - a table needing those isn't a fit for this tool as-is. It reads tables, PK/FK constraints, and single-column indexes, and generates exactly the proc set above. Nothing else.

### Type mapping

`SqlScaffoldUtils.ToCSharpTypeString` maps Postgres `information_schema.columns.data_type` strings (`uuid`, `integer`, `character varying`, `timestamp without time zone`, etc.) directly to C# types. Array columns (from the reader, or the `Ids` array parameter) use a `{type}[]` pseudo-type that maps to `List<T>`.

### Schema

`CSharpNamespaces.ToSchemaString` treats `public` as the default schema and strips it from generated paths/namespaces; every other schema name is kept.

### Table naming

The `zgen_{Table}_{Op}` → owning-table-name recovery (used by proc-scoped `-regen`/`-delete`) splits on the first underscore. Table names must not contain underscores.
