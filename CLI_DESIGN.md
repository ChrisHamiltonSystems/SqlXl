# SqlXL CLI Design

## Audience

- **Developer** — the person building and maintaining SqlXL (currently one person)
- **User** — a SQL Server professional who has installed SqlXL and is using it against their own databases

---

## Core UX Philosophy

**Progressive disclosure.** The tool is immediately useful with zero configuration.
Complexity is only introduced when the user explicitly wants it.

The database already knows the schema — column types, nullability, FK relationships.
A well-defined table *is* the specification. SqlXL treats it as ground truth.

**The `--file` flag is the mode switch** — its presence or absence determines whether
a command is in export (give me a template) or import (here is my data) mode.
The command name stays the same throughout the entire workflow. Minimal cognitive load.

---

## First-Time Setup UX

**The full first-time experience for a new user:**

```bash
dotnet tool install --global SqlXl      # 1. install the tool
sqlxl init --connection "Server=..."    # 2. configure connection + install DB infrastructure
sqlxl insert --table dbo.Products       # 3. start working
```

**Alternatively, for users who want to explore before touching their own database:**

```bash
dotnet tool install --global SqlXl                              # 1. install the tool
sqlxl demo --connection "Server=...;Database=SqlXlDemo;..."    # 2. spin up a demo database
sqlxl insert --table dbo.Products                               # 3. start working against demo data
```

### `sqlxl init`

Bootstraps SqlXL against a target SQL Server database.

```bash
sqlxl init --connection "Server=myserver;Database=MyDB;Integrated Security=true;TrustServerCertificate=true;"
```

**What it does:**
1. Connects to the target database using the supplied connection string
2. Checks whether SqlXL infrastructure already exists (idempotent — safe to re-run)
3. Installs or upgrades the infrastructure if needed (schemas, tables, sprocs, functions)
4. Persists the connection string as the default for future commands

**Key design decisions:**
- The SQL infrastructure script is an **embedded resource** inside the C# assembly — users never see or manually run a `.sql` file
- `init` is idempotent — re-running it against an already-configured database is safe
- After `init`, no `--connection` flag is needed for subsequent commands (uses persisted default)
- Infrastructure is versioned via a `ZZ_SqlXl.SchemaVersion` table — `init` checks the installed version against the embedded version and upgrades if needed, so users never have to manually re-run SQL scripts across releases

### `sqlxl demo`

Creates a self-contained demo database with realistic sample data. Serves users who want to explore SqlXL before touching their own databases, and is also the developer's own test environment.

```bash
sqlxl demo --connection "Server=localhost;Database=SqlXlDemo;Integrated Security=true;TrustServerCertificate=true;"
```

**What it does:**
1. Creates the target database if it does not exist
2. Installs the SqlXL infrastructure (same as `init`)
3. Creates sample domain tables (e.g., Products, Categories) with realistic data
4. Configures BulkOpFeature rows for the sample tables so all three tiers work out of the box

**Key design decisions:**
- The demo database script is an **embedded resource** inside the C# assembly, just like the infrastructure script
- `demo` is idempotent — safe to re-run; resets demo data to a known state
- The same script serves as the developer's test environment and as the user-facing demo — one artifact, two audiences
- Source file: `src/SqlXl/sql/CreateDemoDatabase.sql`

---

## The Three Commands

### `sqlxl insert`

For bulk-inserting new rows into a table.

```bash
# Export: generate empty INSERT template
sqlxl insert --table dbo.Products

# Import: submit populated file
sqlxl insert --table dbo.Products --file new-products.xlsx
```

### `sqlxl update`

For bulk-updating existing rows in a table.

```bash
# Export: generate UPDATE template populated with matching rows
sqlxl update --table dbo.Products --where "CategoryName = 'Electronics'"

# Import: submit populated file
sqlxl update --table dbo.Products --file updated-products.xlsx
```

### `sqlxl import`

Escape hatch for fully custom, pre-configured BulkOpFeature scenarios.
Supports insert/update/delete to one-or-many tables via a custom processing sproc.

```bash
# Export: generate template based on existing BulkOpFeature configuration
sqlxl import --feature 7

# Import: submit populated file against the configured feature
sqlxl import --feature 7 --file data.xlsx
```

---

## Tier Model

### Tier 1 / 2 — Table-centric, zero config required

Commands: `insert`, `update`

- No BulkOpFeature row required
- Tool calls `ScaffoldAn_INSERT_Feature` or `ScaffoldAn_UPDATE_Feature` sprocs on-the-fly
  to derive sensible defaults from the table definition (column types, nullability, etc.)
- Staging table is **ephemeral** — created fresh each run, dropped after processing
- On failure, user fixes their Excel file and re-runs; the file is the artifact being iterated on
- FK dropdowns and other metadata use scaffold sproc defaults (no customization at this tier)

**Prerequisites:** A well-defined domain table. That's it.

### Tier 3 — Feature-centric, fully configured

Command: `import`

- Requires a fully populated, valid `ZZ_SlappFramework.BulkOpFeatures` row
- Requires a permanent staging table (pre-existing, not generated on-the-fly)
- If either prerequisite is missing, the command fails with a clear actionable error message
- BulkOpFeature config drives the template: column display names, FK dropdowns,
  sheet protection, custom processing sproc, etc.
- Custom processing sproc can insert/update/delete across one or many tables —
  literally anything the user wants to do
- Validation: if input data is not 100% valid, the tool rolls back and returns
  detailed row-level error messages

**Prerequisites:** Configured BulkOpFeature row + permanent staging table.

---

## Validation Behavior (all tiers)

- Data is bulk-copied to the staging table
- SQL Server constraints on the staging table ARE the validators
- If any row fails: full rollback, detailed error messages returned to the user
- If all rows pass: processing sproc executes, changes committed
- Exit code 0 = success, non-zero = failure (agent/script friendly)

---

## Workflow Example (Tier 1/2)

```bash
# Step 1 — get the template
sqlxl insert --table dbo.Products
# => generates: Products_insert_template.xlsx (or user specifies --output)

# Step 2 — fill in the Excel file (manually or via agent/script)

# Step 3 — submit the data
sqlxl insert --table dbo.Products --file Products_insert_template.xlsx
# => validates via ephemeral staging table, inserts, reports success/errors
```

---

## Workflow Example (Tier 3)

```bash
# Step 1 — get the configured template
sqlxl import --feature 7
# => generates template driven by BulkOpFeature config (FK dropdowns, display names, etc.)

# Step 2 — fill in the Excel file

# Step 3 — submit the data
sqlxl import --feature 7 --file data.xlsx
# => validates via permanent staging table, runs custom sproc, reports success/errors
```

---

## Additional Flags (all commands)

| Flag | Description |
|------|-------------|
| `--connection <CONNSTR>` | SQL Server connection string (overrides default) |
| `--output <FILE>` | Output file path for template export (optional; tool generates a sensible default name) |

---

## Agent / Script Friendliness

- All inputs via flags — no interactive prompts
- Structured exit codes (0 = success, 1 = validation errors, 2 = connection error, etc.)
- Clean stdout output; errors to stderr
- `--quiet` flag (future) to suppress decorative output for scripting contexts

---

## What Gets Replaced

The current `ExportCommand` and `ImportCommand` are **replaced entirely** by this design.
Do not extend them — delete and rebuild as `InsertCommand`, `UpdateCommand`, `ImportCommand`.

---

## Unsupported SQL Column Types

The following SQL Server column types are **not supported** by SqlXL and should never appear in tables used with this tool:

- `BINARY` / `VARBINARY` — fixed and variable-length binary data

Tables containing these types will not work correctly with the Excel import/export pipeline. Document this clearly in user-facing docs when the time comes.

---

## Open Questions / Future Considerations

- **Schema drift (important, deferred):** When a domain table's columns change after
  `ScaffoldAn_INSERT_Feature` has already run, the scaffolded staging table, sproc, and
  BulkOpFeature row will be out of sync. The fix is likely a `sqlxl refresh --table dbo.Products`
  command that re-runs scaffolding safely (drop/recreate staging table, replace sproc, update
  BulkOpFeature row). Not a v0.1 concern — address when real users hit it.

- Default output filename convention when `--output` is not specified
  (e.g., `Products_insert_YYYYMMDD.xlsx`?)
- `--where` quoting docs — shell quoting of SQL fragments needs clear guidance
- Multi-feature ambiguity: if `dbo.Products` has two INSERT features configured,
  `insert --table` should fail loudly and tell the user to use `import --feature` directly
- `--quiet` / `--json` output flags for agent/scripting use cases (Tier 2 consideration)

---

*Last updated: 2026-04-10*
*Status: Design agreed, not yet implemented*
