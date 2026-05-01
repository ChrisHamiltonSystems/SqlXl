# SqlXL - Context for LLMs

## What is SqlXL?

SqlXL is a **lightweight .NET global tool** for SQL Server data professionals to bulk-insert, bulk-update, and export data between SQL Server and Excel files — no web server required.

**Target users:** DBAs, data analysts, SQL developers in enterprise environments who need a fast, scriptable way to move data in and out of SQL Server via Excel.

**Installed via NuGet:**
```bash
dotnet tool install --global SqlXl
```

## Core Value Proposition

**"Excel ↔ SQL Server, done right — no web server required!"**

- Simple CLI — `sqlxl insert`, `sqlxl update`, `sqlxl import`
- The database schema IS the validator — staging table constraints enforce data quality
- No infrastructure beyond SQL Server — runs directly on the user's workstation like SSMS
- Self-installing — `sqlxl init` creates all required SqlXL schema objects in the target database
- Three tiers of power: zero-config table commands → fully custom staging/sproc workflows

## The Commands

### Setup commands
| Command | Description |
|---------|-------------|
| `sqlxl init --connection "..."` | Install SqlXL infrastructure in target DB + save connection profile |
| `sqlxl demo --connection "..."` | Create SqlXlDemo database with sample data (idempotent) |
| `sqlxl use <profile>` | Set the active connection profile |
| `sqlxl connections list` | List saved connection profiles |
| `sqlxl connections remove <profile>` | Remove a saved connection profile |

### Data commands
| Command | Description |
|---------|-------------|
| `sqlxl insert --table dbo.T` | Generate empty INSERT template (no `--file`) or import filled template (`--file`) |
| `sqlxl update --table dbo.T` | Generate pre-populated UPDATE template or import filled template |
| `sqlxl import --feature N` | Tier 3: custom staging table / processing sproc workflow |
| `sqlxl export --query "SELECT ..."` | Export query results to Excel (read-only) |
| `sqlxl test --table dbo.T` | Auto-generate test rows and validate them against all configured features |
| `sqlxl infer products.xlsx` | Infer SQL Server `CREATE TABLE` DDL from an Excel file (no DB connection needed) |

### Global flag
`--config <path>` or `SQLXL_CONFIG` env var — override the default config file location (`~/.sqlxl/config.json`). Useful for CI/CD or side-by-side profile sets.

## Tier Model

**Tier 1 / 2 — `insert` / `update`** (zero config)
- No BulkOpFeature row required
- Scaffolds a staging table and processing sproc automatically on first use
- Re-scaffold is manual today; `sqlxl refresh --table` is a planned post-v1.0 feature

**Tier 3 — `import --feature N`** (fully configured)
- Requires a manually configured `SqlXl.BulkOpFeatures` row
- Requires a custom staging table and processing sproc designed by the SQL pro
- Supports complex multi-table writes, denormalized input, arbitrary business rules

## Technology Stack

**Distribution:** .NET 10 Global Tool via NuGet

**Runtime:**
- .NET 10.0 (Windows only — DPAPI credential storage)
- C# 12, Spectre.Console.Cli (CLI framework)
- SQL Server 2019+ (direct connection, no ORM)
- Dapper (SQL queries), SqlBulkCopy (bulk inserts)
- ClosedXML 0.102.2 (Excel read/write — MIT licensed)
- Microsoft.Data.SqlClient 6.1.4

**No web stack.** No ASP.NET Core, no IIS, no Kestrel, no HTTP.

## Project Structure

```
SqlXlRepo/
├── src/
│   └── SqlXl/
│       ├── Commands/              # One file per command (12 files)
│       ├── Config/                # Config file loading + connection resolution
│       │   ├── ConfigLocator.cs   # --config flag / SQLXL_CONFIG env var logic
│       │   ├── ConnectionResolver.cs
│       │   └── SqlXlConfig.cs     # JSON config model, DPAPI encryption
│       ├── Core/                  # Business logic
│       │   ├── BulkOpsHelper.cs   # Core staging-table validation engine (~70 KB)
│       │   ├── DataService.cs     # SQL Server access layer (Dapper)
│       │   ├── ExcelTemplateGenerator.cs
│       │   ├── ExcelImporter.cs
│       │   └── SchemaInference/   # sqlxl infer engine (pure, no I/O)
│       ├── Helpers/
│       │   └── SqlScriptExecutor.cs
│       ├── Models/
│       │   └── BulkOpFeature.cs
│       ├── sql/                   # Embedded SQL scripts
│       │   ├── CreateInfrastructure.sql  # SqlXl schema: tables, functions, sprocs (166 KB)
│       │   ├── CreateDemoDatabase.sql
│       │   └── CreateDemoFeatures.sql
│       ├── Program.cs
│       └── SqlXl.csproj
├── smoke-test.ps1                 # 12-step end-to-end smoke test
├── CLI_DESIGN.md                  # Authoritative design doc (read this for deep context)
├── claude.md                      # This file
├── README.md
└── .gitignore
```

## Current Status (as of 2026-05-01)

**Version: 1.2.0 — published to NuGet**

All commands build and work. The tool is production-ready.

### Recently shipped (v1.2.0)
- Upgraded from .NET 8 to .NET 10 (breaking change — requires .NET 10 runtime)
- Fixed `sqlxl init` idempotency: re-running init on an already-configured database now works correctly in all cases (constraint drop/re-add for `TableExists`, `SprocExists`, `ColumnExists` functions)
- Added `smoke-test.ps1` — 12-step end-to-end regression script

### Known limitations
- **`sqlxl test` unique constraint collision** — test data uses fixed sample values; collides with existing data on unique columns. Workaround: `sqlxl demo --yes` to reset. Fix: append GUID/timestamp to sample string values.
- **Schema drift** — no `sqlxl refresh --table` yet; if a domain table's columns change after initial scaffold, the staging table/sproc must be manually updated. Spec at `SPEC_SCHEMA_REFRESH_IDEA.md`.
- **Bulk update concurrency** — last-write-wins on concurrent UPDATE workflows. Optimistic concurrency via `rowversion` is designed but not yet implemented.

### Post-v1.2.0 backlog
See `CLI_DESIGN.md → Post-v1.0 backlog` for the full list.

## Database Infrastructure

SqlXL installs its own schema (`SqlXl`) into the target database via `sqlxl init`. Users do **not** need to run any SQL scripts manually.

**What gets installed:**
- `SqlXl` schema with tables: `BulkOpFeatures`, `Meta_Columns`, `ColumnUIConfigurations`
- Helper functions: `TableExists`, `SprocExists`, `ColumnExists`, `PascalCaseToLabel`, and ~20 others
- Scaffold sprocs: `ScaffoldAn_INSERT_Feature`, `ScaffoldAn_UPDATE_Feature`
- Per-table scaffold artifacts: staging tables + processing sprocs (created on first `insert`/`update`)

**Connection profiles** are stored in `~/.sqlxl/config.json`. Passwords are DPAPI-encrypted (Windows only). Resolution chain: `--connection` flag > `SQLXL_CONNECTION` env var > `--profile` flag > active profile.

**Demo database:** `SqlXlDemo` on localhost — created by `sqlxl demo --yes`. Contains Products/Categories/Users/Roles/UserRoles domain tables plus a Tier 3 feature ("Assign User Roles") pre-configured.

## Key Design Decisions

### `--file` as the mode switch
The same command name covers both directions: omit `--file` → export/generate a template; supply `--file` → import data. This keeps the user's mental model simple: one command per intent.

### No Web Server
Runs directly on the user's workstation. Connects to SQL Server like SSMS. No ports, no IIS, no hosting setup.

### ClosedXML (not EPPlus)
MIT licensed — no per-user license config, no commercial restrictions. Replaced EPPlus before v1.0 publish because EPPlus licensing was incompatible with free NuGet distribution.

### Spectre.Console.Cli (not System.CommandLine)
Chosen for its rich terminal output (colors, tables, progress) and clean command/settings pattern.

### `--config` is pre-parsed at startup
`ConfigLocator.ExtractFromArgs()` strips `--config <path>` from args before Spectre.Console.Cli sees them, so it works as a true global flag on any command without per-command plumbing.

## How to Build / Test / Release

### Build:
```bash
cd C:\Dev\SqlXlRepo\src\SqlXl
dotnet build
```

### Run locally (development):
```bash
dotnet run -- insert --table dbo.Products --no-launch
dotnet run -- infer products.xlsx --table MyTable
```

### Smoke test (12 steps, requires SQL Server on localhost):
```powershell
cd C:\Dev\SqlXlRepo
.\smoke-test.ps1          # includes dotnet build
.\smoke-test.ps1 -SkipBuild
```

### Pack and install locally:
```bash
dotnet pack -c Release
dotnet tool install --global --add-source ./bin/Release SqlXl
sqlxl --help
dotnet tool uninstall --global SqlXl
```

### Publish to NuGet:
```bash
dotnet pack -c Release
dotnet nuget push bin/Release/SqlXl.1.2.0.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json
```

## Common Tasks for Future Sessions

### Start a new session:
1. Read this file and `CLI_DESIGN.md` for full context
2. `git status` + `git pull`
3. `dotnet build` to confirm clean state
4. Run `.\smoke-test.ps1 -SkipBuild` to confirm everything works end-to-end

### Add a new command:
1. Create `Commands/FooCommand.cs` implementing `Command<FooCommand.Settings>`
2. Register in `Program.cs` with `config.AddCommand<FooCommand>("foo")`
3. Add to `CLI_DESIGN.md` command table
4. Add a step to `smoke-test.ps1`

### Update the SQL infrastructure:
- Edit `src/SqlXl/sql/CreateInfrastructure.sql`
- All DDL must be idempotent (`IF NOT EXISTS`, `CREATE OR ALTER`, constraint drop/re-add pattern)
- Rebuild to embed the updated script: `dotnet build`
- Test: `sqlxl demo --yes` then `sqlxl init --connection "..." --profile test`

## Links

- **GitHub Repo:** https://github.com/ChrisHamiltonSystems/SqlXl
- **NuGet:** https://www.nuget.org/packages/SqlXl
- **SlappFramework (parent project):** https://github.com/ChrisHamiltonSystems/SlappFramework

---

**Last Updated:** 2026-05-01
**Version:** 1.2.0 (published to NuGet)
**Status:** Production-ready. All commands working. Smoke tests passing 12/12.
