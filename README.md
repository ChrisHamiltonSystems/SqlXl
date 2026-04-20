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

## Install

```bash
dotnet tool install --global SqlXl
```

Requires .NET 8.0 or later. Windows only.

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

## Commands

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

## Requirements

- .NET 8.0 or later
- SQL Server 2019 or later
- Windows (DPAPI credential storage is Windows-only)

## License

MIT — see [LICENSE](LICENSE)
