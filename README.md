# SqlXL

## Move data between Excel and SQL Server — safely

Stop manually exporting query results, editing spreadsheets, and hoping nothing breaks.

**SqlXL lets you bulk insert or update SQL Server data from Excel — with full validation using your existing database constraints.**

- No manual scripts
- No fragile copy/paste workflows
- No partial imports or silent data corruption

Built for SQL Server developers and data professionals who rely on Excel but need **production-safe results**.

---

## Why SqlXL exists

If you've ever:

- Exported query results to Excel just to clean them up
- Manually edited data before re-importing it
- Worried about bad data making it into your database
- Written one-off scripts for bulk updates

Then you already know the problem.

**SqlXL replaces that entire workflow with something safe, repeatable, and fast.**

---

**Website:** [runsqlxl.com](https://runsqlxl.com/)

## Install

```bash
dotnet tool install --global SqlXl
```

Requires .NET 10.0 or later. Windows only.

## First-time setup

```bash
sqlxl init --connection "Server=myserver;Database=MyDB;Integrated Security=true;TrustServerCertificate=true;"
```

Installs SqlXL infrastructure into your database and saves the connection as your default profile. You won't need `--connection` on subsequent commands.

**Want to try it before touching your own database?**

```bash
sqlxl demo --connection "Server=localhost;Integrated Security=true;TrustServerCertificate=true;"
sqlxl init --connection "Server=localhost;Database=SqlXlDemo;Integrated Security=true;TrustServerCertificate=true;" --profile demo
sqlxl use demo
sqlxl insert --table dbo.Products
```

**Have a spreadsheet but no destination table yet?**

```bash
sqlxl infer your-data.xlsx --output schema.sql
# review schema.sql, edit if needed, then apply it (sqlcmd, SSMS, etc.)
sqlxl insert --table dbo.YourTable --file your-data.xlsx
```

(Assumes `sqlxl init` has been run against your database. `infer` itself is connectionless — it reads the local xlsx and emits text.)

## Commands

### `sqlxl infer` — bootstrap a table from a spreadsheet

Have a spreadsheet but no destination table yet? `infer` reads the file and emits a `CREATE TABLE` statement with column types inferred from the data. The DDL is for you to review and run yourself — `infer` never executes it.

```bash
# Print DDL to stdout (pipe-friendly)
sqlxl infer products.xlsx --sheet Products --table Products

# Write DDL to a file plus a JSON inference report
sqlxl infer products.xlsx --sheet Products --table Products --output products.sql --report products.json
```

After you apply the DDL (any tool — `sqlcmd`, SSMS, etc.), the same xlsx loads through the validated `sqlxl insert` pipeline below. No reformatting needed.

### `sqlxl insert` — bulk-insert new rows

```bash
# Generate an empty INSERT template
sqlxl insert --table dbo.Products

# Import a filled template
sqlxl insert --table dbo.Products --file Products_insert_20260412.xlsx
```

No configuration needed. SqlXL inspects the table and scaffolds everything automatically on first use.

### `sqlxl update` — bulk-update existing rows

```bash
# Generate a pre-populated UPDATE template (all rows)
sqlxl update --table dbo.Products

# Filter which rows to include
sqlxl update --table dbo.Products --where "CategoryName = 'Electronics'"

# Import a filled template
sqlxl update --table dbo.Products --file Products_update_20260412.xlsx
```

### `sqlxl import` — custom feature (power users)

For workflows that span multiple tables, or require custom validation logic, configure a `BulkOpFeature` in your database and reference it by ID:

```bash
# Generate template driven by the feature config
sqlxl import --feature 7

# Import a filled template through the configured processing sproc
sqlxl import --feature 7 --file data.xlsx
```

### `sqlxl export` — export a query to Excel

```bash
sqlxl export --query "SELECT * FROM dbo.Products WHERE CategoryID = 3"
sqlxl export --query "SELECT * FROM dbo.Orders" --output orders.xlsx
```

### `sqlxl test` — smoke-test a table's features

Auto-generates test data and runs it through all configured features for a table. Useful after scaffolding to verify the pipeline end-to-end.

```bash
sqlxl test --table dbo.Products
sqlxl test --table dbo.Products --rows 5
```

### `sqlxl demo` — spin up a demo database

Creates (or resets) a self-contained demo database with sample tables and data. Safe to run repeatedly — drops and recreates.

```bash
sqlxl demo --connection "Server=localhost;Integrated Security=true;TrustServerCertificate=true;"
```

### `sqlxl llm-context` — machine-readable reference for AI agents

Emits a complete, versioned reference document for the installed binary — commands, flags, workflows, gotchas, and the BulkOpFeature schema — so an AI assistant can operate SqlXL fluently without external lookups.

```bash
# Text (markdown) — human-readable
sqlxl llm-context

# JSON — structured, schema-validated, agent-friendly
sqlxl llm-context --format json

# Include live DB state: active profile, configured features, domain tables
sqlxl llm-context --format json --include-state
```

Pass the JSON output to your AI assistant at the start of a session. No DB connection required unless `--include-state` is used.

## How validation works

Data is bulk-copied to a staging table. SQL Server constraints on that table are the validators. If any row fails a constraint, the entire batch is rolled back and row-level error messages are returned to the terminal. No partial imports.

## Managing connections

SqlXL stores named connection profiles in `~/.sqlxl/config.json`. After `sqlxl init`, no `--connection` flag is needed.

```bash
# Save additional profiles
sqlxl init --connection "Server=prod;Database=ProdDB;..." --profile prod
sqlxl init --connection "Server=staging;Database=StageDB;..." --profile staging

# Switch the active profile
sqlxl use prod

# List all profiles (shows active with *)
sqlxl connections list

# Remove a profile
sqlxl connections remove staging
```

**Per-command overrides:**

```bash
# Use a named profile just for this command (doesn't change the active profile)
sqlxl insert --table dbo.Products --profile staging

# Fully explicit connection string (overrides everything)
sqlxl insert --table dbo.Products --connection "Server=...;Database=...;"
```

**Resolution order** (highest to lowest priority):

1. `--connection` flag
2. `SQLXL_CONNECTION` environment variable
3. `--profile` flag
4. Active profile in `~/.sqlxl/config.json`

**Security:** Windows Auth connection strings (no password) are stored as plain text — there is nothing to protect. SQL Auth connection strings are automatically encrypted with Windows DPAPI, bound to your user account and machine.

## Custom config file location

By default, profiles live in `~/.sqlxl/config.json`. You can override that location for any command — useful for CI/CD pipelines, shared team config files, or running multiple SqlXL configurations side-by-side on the same machine.

```bash
# Per-command override
sqlxl insert --table dbo.Products --config /shared/team-sqlxl-config.json

# Persistent override via environment variable
export SQLXL_CONFIG=/shared/team-sqlxl-config.json
sqlxl insert --table dbo.Products
```

**Resolution order** for the config file location (highest to lowest priority):

1. `--config <path>` flag
2. `SQLXL_CONFIG` environment variable
3. `~/.sqlxl/config.json` (default)

The override applies to both reads and writes — `sqlxl init --config /path/to/file.json` will write the new profile to that file.

## Requirements

- .NET 10.0 or later
- SQL Server 2019 or later
- Windows (DPAPI credential storage is Windows-only)

## License

MIT — see [LICENSE](LICENSE)

Copyright (c) 2026 Chris Hamilton
