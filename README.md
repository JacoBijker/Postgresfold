# Postgresfold.Scaffold

A database-first code generator for PostgreSQL. Point it at a live Postgres connection and it generates the CRUD PL/pgSQL functions for every table, plus the matching C# (Model, Dapper repository, domain service, DI registration) on top of them.

## This is deliberately opinionated

There is exactly one way this tool does things - one proc set, one naming convention, one type mapping, no config switches to change any of it. That's on purpose, not a limitation to be fixed later.

The point of being this rigid is to give an AI coding assistant a single narrow path to walk instead of a design space to improvise in. A tool with options is a tool an AI can misuse in a dozen plausible-looking ways; a tool with one fixed shape either produces the one correct output or fails loudly. Opinionated, single-path code like this doesn't need much of a test suite to be trusted - there's nothing to hallucinate when there's only one way to do it.

If you've come across this looking for a general-purpose Postgres scaffolding tool: this probably isn't it. There's no attempt here at flexibility, extensibility, or being useful outside the one project it was built for.

## What it does

- Reads table shape and existing generated procs live from the connected database (no `.sql` files, nothing checked in as a source of truth for schema).
- Generates PL/pgSQL functions (`zgen_{Table}_{Operation}`) and executes them directly against the connection.
- Generates the C# layers on disk: model, Dapper repository + interface, domain service + interface, foreign-key-aware service extensions, and DI registration.
- Can also delete everything it generated for a table or a single proc, without touching the actual table or data.

See `CLAUDE.md` for the concrete architecture, CLI switches, and conventions.
