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

The power-user escape hatch. Where `insert` and `update` are table-centric and zero-config,
`import` is fully custom — the SQL pro owns the staging table shape, the validation logic,
and the processing sproc entirely. SqlXl just moves the data.

```bash
# Export: generate template from the custom staging table shape
sqlxl import --feature 7

# Import: submit populated file through the configured processing sproc
sqlxl import --feature 7 --file data.xlsx
```

#### The vision — why this command matters

Consider a typical enterprise RBAC schema: a `Users` table, a `Roles` table, and a
`UserRoles` many-to-many association table. A data entry person should never have to
think about foreign keys or normalized tables. The SQL pro designs a staging table shaped
for the *human workflow*:

```
Staging_UserRoleAssignments
  UserName    | RoleName1   | RoleName2   | RoleName3   | RoleName4
  joeSmith    | sysAdmin    | financeUser | execTeam    |
  maryJones   | financeUser |             |             |
```

The processing sproc then takes that clean, human-readable staged data and handles all
the FK lookups, upserts, and business rules across the three domain tables — whatever
SQL logic is needed. SqlXl generates the Excel template from the staging table shape
and routes the filled file back through the sproc. **The staging table shape is the API
contract the SQL pro designs for the data entry person — not for the database.**

This pattern handles scenarios that `insert` and `update` simply cannot:
- Writes that span multiple domain tables in one operation
- Denormalized input that maps to normalized storage (like the RBAC example)
- Complex business rules enforced entirely in SQL, with row-level error reporting
- Any custom workflow the SQL pro can express as a staging table + processing sproc

`--feature N` references `SqlXl.BulkOpFeatures.ID` directly.

---

## Tier Model

### Tier 1 / 2 — Table-centric, zero config required

Commands: `insert`, `update`

- No BulkOpFeature row required
- Tool calls `ScaffoldAn_INSERT_Feature` or `ScaffoldAn_UPDATE_Feature` sprocs on-the-fly
  to derive sensible defaults from the table definition (column types, nullability, etc.)
- FK dropdowns and other metadata use scaffold sproc defaults (no customization at this tier)

**Prerequisites:** A well-defined domain table. That's it.

### Tier 3 — Feature-centric, fully configured

Command: `import`

- Requires a manually configured `SqlXl.BulkOpFeatures` row (ID, staging table name,
  processing sproc name, `GetRowsToEdit_SelectStatement` for template generation)
- Requires a permanent, custom staging table designed by the SQL pro
- If either prerequisite is missing, the command fails with a clear actionable error message
- The staging table shape drives the Excel template — columns, types, FK dropdowns
- The processing sproc owns all domain logic: inserts, updates, deletes, lookups,
  business rules — across as many tables as needed
- Validation: if any row fails staging table constraints, full rollback + row-level errors

**Prerequisites:** Configured `BulkOpFeatures` row + custom staging table + processing sproc.

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

## Distribution

**NuGet only.** SqlXL is a .NET global tool — NuGet is the one and only distribution channel.

```bash
dotnet tool install --global SqlXl
```

VS Marketplace is not in scope — it targets Visual Studio extensions (VSIX), a different artifact type entirely. If IDE integration ever becomes a goal, that is a separate future project.

---

## License

SqlXL is **MIT licensed**. All dependencies must be MIT-compatible.

**EPPlus is a pre-publish blocker.** EPPlus 8.x requires either a paid commercial license or a NonCommercial personal/org license. A freely distributed NuGet tool cannot ship with EPPlus without running into licensing ambiguity — particularly for the primary audience of large enterprise organizations.

**Resolution: migrate from EPPlus to ClosedXML** before v1.0 is published. ClosedXML is MIT licensed with zero restrictions. The migration is fully contained to two files:
- `Core/ExcelTemplateGenerator.cs`
- `Core/ExcelImporter.cs`

All required capabilities (cell styling, data validation, sheet protection, dropdowns) are available in ClosedXML. The API is different but the migration is mechanical.

---

## Roadmap to v1.0

### v1.0 definition — the bar for NuGet publish

| # | Item | Notes |
|---|------|-------|
| 1 | ~~**EPPlus → ClosedXML migration**~~ ✅ | License non-negotiable before public publish |
| 2 | ~~**`sqlxl import --feature N`**~~ ✅ | Tier 3 command — completes the three-tier design. RBAC demo (Users/Roles/UserRoles) end-to-end verified. |
| 3 | ~~**Connection string persistence**~~ ✅ | Named profiles in `~/.sqlxl/config.json`. DPAPI encryption for SQL Auth. `sqlxl use <profile>`, `sqlxl connections list/remove`. Resolution chain: `--connection` > `SQLXL_CONNECTION` env var > `--profile` flag > active profile. |
| 4 | ~~**Delete old `ExportCommand` / `ImportCommand`**~~ ✅ | Both commands were fully rewritten as part of the new design — no dead code remains. |
| 5 | **NuGet package metadata** | Description, tags, icon, authors, project URL, license expression in `.csproj` |
| 6 | **Basic README** | Install instructions, quickstart, connection string example |

### Known issues (present in v1.0-beta, fix before v1.0 stable)

- **`sqlxl init` is not idempotent** — `CreateInfrastructure.sql` uses bare `CREATE SCHEMA SqlXl` with no `IF NOT EXISTS` guard. Re-running `init` against an already-configured database throws a SQL exception and exits without saving the profile. The database itself is unharmed, but the user cannot add or update a profile for an existing database without hand-editing `~/.sqlxl/config.json`. Fix: wrap all object-creation DDL in `CreateInfrastructure.sql` with `IF NOT EXISTS` / `IF OBJECT_ID IS NULL` guards. SQL-only change, no C# required.

### Post-v1.0 backlog (do not block publish on these)

- **Schema drift / `sqlxl refresh --table`** — re-scaffold staging table and sproc when domain table columns change after initial scaffold. Important long-term but rarely hit in practice.
- **`sqlxl test` unique constraint limitation** — `GenerateTestData` uses fixed sample values; second run fails on unique columns. Workaround: `sqlxl demo --yes` to reset. Long-term fix: append timestamp/GUID to string sample values.
- **Multi-feature ambiguity** — if `dbo.Products` has two INSERT features in `BulkOpFeatures`, `insert --table` should fail loudly and tell the user to use `import --feature` directly.
- **`--quiet` / `--json` output flags** — for agent/scripting contexts.
- **Code signing** — increases trust in enterprise environments; can be added post-publish.
- **`--where` quoting docs** — shell quoting of SQL fragments needs clear guidance in docs.
- **Bulk update concurrency (lost update problem)** — current behavior is last-write-wins. User A and User B can both pull a template from the same state, User A submits first, then User B's submit silently clobbers A's changes. The standard SQL Server fix is optimistic concurrency via a `rowversion` column: export includes the rowversion, the `_UpdateFromStaging` sproc checks it on UPDATE, and rows where the version has changed since the template was pulled are returned as conflicts rather than silently overwritten. Realistic implementation path: `ScaffoldAn_UPDATE_Feature` detects a `rowversion` column on the domain table and wires in the conflict check automatically — tables without one get last-write-wins as today, with a warning on export. This requires schema changes to domain tables, staging tables, and the scaffolded update sproc. Not blocking for v1.0 since bulk updates in practice are usually coordinated, but worth addressing if SqlXL is used in high-concurrency environments.

---

## Unsupported SQL Column Types

The following SQL Server column types are **not supported** by SqlXL and should never appear in tables used with this tool:

- `BINARY` / `VARBINARY` — fixed and variable-length binary data

Tables containing these types will not work correctly with the Excel import/export pipeline. Document this clearly in user-facing docs when the time comes.

---

---

*Last updated: 2026-04-12*
*Status: Connection string persistence complete (named profiles, DPAPI, sqlxl use/connections). Pre-publish blockers remaining: NuGet package metadata, basic README. Known issue: init not idempotent (see above). Publishing as v1.0-beta.*
