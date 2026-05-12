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

## Agent Context — `sqlxl llm-context`

Emits a versioned, machine-readable reference document for the installed binary. Designed so an AI agent (or any LLM) can become fluent in SqlXL from a single command — no external lookups, no separate docs fetch.

```bash
# Markdown (human-readable default)
sqlxl llm-context

# JSON (machine-readable, schema-validated)
sqlxl llm-context --format json

# JSON + live DB state (active profile, configured features, domain tables)
sqlxl llm-context --format json --include-state
```

**Output contract:** `--format json` conforms to `format_version: 1`. Top-level keys: `format_version`, `sqlxl_version`, `generated_at`, `commands`, `template_structure`, `bulk_op_feature_schema`, `agent_best_practices`, `workflows`, `gotchas`, `builtin_schema`, and optionally `active_state`. Breaking changes (removed/renamed fields) bump `format_version`; additive changes do not.

**No DB connection required** unless `--include-state` is passed.

**Key design decisions:**
- Static content (commands, flags, schema) is embedded as a JSON template inside the binary — always in sync with the shipped binary, no external doc fetch.
- Dynamic fields (`generated_at`, `sqlxl_version`, `docs_url`, `connection_model.profile_storage_path`) are substituted at runtime.
- `--include-state` queries `SqlXl.BulkOpFeatures` and `INFORMATION_SCHEMA.TABLES` via Dapper and appends the result as `active_state` — gives the agent a live picture of what features and tables are configured.
- The companion `SKILL.md` (in `ToDo_LLM-Context_Subcommand/`) defines a Claude Code skill that auto-activates on `sqlxl` mentions and bootstraps from `llm-context --format json`.

---

## Schema Bootstrapping — `sqlxl infer`

A pre-Tier-1 helper. Given an Excel file with no destination table yet, infer
column types from the data and emit a `CREATE TABLE` statement. The output is
reviewed by a SQL pro and run manually — `infer` never executes DDL itself.

```bash
# Print DDL to stdout (pipe-friendly)
sqlxl infer products.xlsx --sheet Products

# Pipe directly to sqlcmd
sqlxl infer products.xlsx --sheet Products | sqlcmd -d MyDb

# Write to a file plus a JSON inference report
sqlxl infer products.xlsx --sheet Products --output products.sql --report products.json

# Strict: any invalid value in a column forces NVARCHAR fallback for that column
sqlxl infer products.xlsx --sheet Products --mode strict
```

Unlike `insert` / `update` / `import`, `infer` does not connect to SQL Server.
It reads the local xlsx and emits text. No `--connection` or `--profile`.

**Why this doesn't defeat the validation guarantee:** `infer` produces DDL,
not data. The user reviews and runs the `CREATE TABLE` themselves. Once the
table exists, every subsequent `sqlxl insert --table ...` is bound by
whatever constraints the user kept. So `infer` sits *upstream* of the
staging-validation pipeline — it bootstraps the validator, then hands the
user back to the validated tier model.

**Key design decisions:**

- **DDL never executes automatically.** Output goes to stdout (or `--output`).
  The user's review of the `CREATE TABLE` is the deliberate accountability
  step in the workflow.
- **Next-step hint to stderr.** After emitting the DDL, prints a three-step
  guide on stderr — `sqlcmd ... -i <ddl-file>` then
  `sqlxl insert --table ... --file ...` — pre-filled with the user's actual
  table name and input file. Lowers the friction of remembering the
  next-command syntax without crossing into auto-execute. Suppress with
  `2>/dev/null` if you don't want it.
- **Stdout = clean DDL, stderr = everything else** (status, warnings, errors).
  Makes piping into `sqlcmd` trivial without filtering.
- **Honest warnings.** Invalid values produce stderr notes
  (`[Price] 1 value(s) did not parse as DECIMAL — would become NULL on import;
  marking column NULLABLE`) so a skimming reviewer still sees what they're
  agreeing to.
- **Multi-sheet handling is explicit.** A workbook with more than one sheet
  refuses to auto-pick — `--sheet <name>` is required, with the full sheet
  list (hidden sheets marked) shown on error.
- **Deterministic.** Same input always produces same output. US date format
  is the default for ambiguous numeric dates (`3/4/2024` = March 4); use
  `--date-format iso` to refuse all ambiguous numeric dates.
- **Permissive vs strict modes.** Permissive (default) lets a column be
  inferred as a strict type even if a few values fail to parse — those would
  become NULL on import, and the column is marked NULLABLE in the DDL to
  match. Strict mode rejects any candidate type with a single invalid value,
  forcing NVARCHAR fallback.
- **Supported types:** `BIT`, `INT`, `BIGINT`, `DECIMAL(p,s)`, `FLOAT`,
  `DATETIME2`, `NVARCHAR(n)`, `NVARCHAR(MAX)`. Anything that doesn't meet
  the configured confidence threshold for a stricter type falls back to
  NVARCHAR.

Full inference algorithm: see `SPEC_SCHEMA_INFERENCE_IDEA.md`.

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

## Workflow Example (Bootstrap from spreadsheet — `infer` → `insert`)

When you have a spreadsheet but no destination table yet:

```bash
# Step 1 — generate DDL from the spreadsheet
sqlxl infer products.xlsx --sheet Products --table Products --output products.sql
# => emits CREATE TABLE [dbo].[Products] (...); review stderr warnings

# Step 2 — review and (optionally) edit products.sql
#   common edits: add Id INT IDENTITY(1,1) PRIMARY KEY, FKs, indexes,
#   tighten types (e.g. NVARCHAR(50) → NVARCHAR(20)) where the data allows.

# Step 3 — apply the DDL to your database (any tool; sqlxl does NOT execute DDL)
sqlcmd -S <server> -d <database> -E -i products.sql

# Step 4 — load the spreadsheet through staging-table validation
sqlxl insert --table dbo.Products --file products.xlsx
```

The same xlsx is used in step 1 and step 4 — no reformatting needed. `infer`
shaped the table to match the file, so `sqlxl insert` can skip its own
template-export and go straight to import. Staging validation runs on this
load and every load after.

**Adding a primary key in step 2** is safe: when `sqlxl insert` scaffolds the
staging table, it excludes `IDENTITY` columns by convention. The file shape
still matches the staging shape, so the import works without an `Id` column
in the spreadsheet.

---

## Additional Flags (data commands)

These apply to `insert`, `update`, `import`, `export`, and `test`. (`infer` is
connectionless and has its own flag set — see the `sqlxl infer` section above.)

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

**Excel library: ClosedXML** (MIT licensed, zero restrictions). EPPlus was used initially but replaced before v1.0 — its commercial licensing terms were incompatible with free NuGet distribution.

---

## Roadmap to v1.0

### v1.0 definition — the bar for NuGet publish

| # | Item | Notes |
|---|------|-------|
| 1 | ~~**EPPlus → ClosedXML migration**~~ ✅ | License non-negotiable before public publish |
| 2 | ~~**`sqlxl import --feature N`**~~ ✅ | Tier 3 command — completes the three-tier design. RBAC demo (Users/Roles/UserRoles) end-to-end verified. |
| 3 | ~~**Connection string persistence**~~ ✅ | Named profiles in `~/.sqlxl/config.json`. DPAPI encryption for SQL Auth. `sqlxl use <profile>`, `sqlxl connections list/remove`. Resolution chain: `--connection` > `SQLXL_CONNECTION` env var > `--profile` flag > active profile. |
| 4 | ~~**Delete old `ExportCommand` / `ImportCommand`**~~ ✅ | Both commands were fully rewritten as part of the new design — no dead code remains. |
| 5 | ~~**NuGet package metadata**~~ ✅ | Description, tags, icon, authors, project URL, license expression in `.csproj` |
| 6 | ~~**Basic README**~~ ✅ | Install instructions, quickstart, connection string example |

### Known issues (present in v1.0-beta, fix before v1.0 stable)

~~**`sqlxl init` is not idempotent**~~ ✅ — Partially fixed in commit `0bb2ceb` (table/sproc DDL). Fully fixed in v1.2.0: `CREATE OR ALTER FUNCTION` on `TableExists`, `SprocExists`, and `ColumnExists` now drops and re-adds their referencing check constraints, so re-running `init` on an already-configured database is safe in all cases.

### Post-v1.0 backlog (do not block publish on these)

- **Schema drift / `sqlxl refresh --table`** — re-scaffold staging table and sproc when domain table columns change after initial scaffold. Any team using SqlXL on a live, evolving domain table will hit this within a quarter; not blocking for v1.0 but high-priority post-launch. A detailed feature spec exists at `SPEC_SCHEMA_REFRESH_IDEA.md` in the repo root — refer to that before implementing.
- **`sqlxl test` unique constraint limitation** — `GenerateTestData` uses fixed sample values; second run fails on unique columns. Workaround: `sqlxl demo --yes` to reset. Long-term fix: append timestamp/GUID to string sample values.
- **Multi-feature ambiguity** — if `dbo.Products` has two INSERT features in `BulkOpFeatures`, `insert --table` should fail loudly and tell the user to use `import --feature` directly.
- **`--quiet` / `--json` output flags** — for agent/scripting contexts.
- **Code signing** — increases trust in enterprise environments; can be added post-publish.
- **`--where` quoting docs** — shell quoting of SQL fragments needs clear guidance in docs.
- ~~**Excel → SQL Server schema inference (`sqlxl infer`)**~~ ✅ — Shipped. `sqlxl infer <file.xlsx>` reads an Excel file and emits `CREATE TABLE` DDL inferred from the data; never executes DDL itself. See the `Schema Bootstrapping — sqlxl infer` section above for design rationale and the spec at `SPEC_SCHEMA_INFERENCE_IDEA.md` for the full inference algorithm.
- **Automated tests for `sqlxl infer` engine** — no unit tests exist yet. `smoke-test.ps1` (added in v1.2.0) covers the happy path end-to-end, but edge cases (DECIMAL precision overflow → FLOAT fallback, `--mode strict` boundary behavior, all-null columns, NVARCHAR(MAX) trigger above 4000 chars, `--date-format iso` rejecting US-only formats, duplicate header detection) are not regression-protected. Estimated ~3–4 hrs including test project scaffold (none exists in the repo today). The engine — `Core/SchemaInference/SchemaInferrer.cs` plus `TypeEvaluators.cs` — is pure with no I/O, so unit testing is straightforward.
- ~~**`--config <path>` / `SQLXL_CONFIG` env var for CI/CD**~~ ✅ — Shipped. Resolution order: `--config` flag > `SQLXL_CONFIG` env var > `~/.sqlxl/config.json` default. Override applies to both reads and writes. `--config` is pre-parsed at startup in `Program.cs` so it works as a global flag on any command without per-command plumbing. See `Config/ConfigLocator.cs`.
- **Bulk update concurrency (lost update problem)** — current behavior is last-write-wins. User A and User B can both pull a template from the same state, User A submits first, then User B's submit silently clobbers A's changes. The standard SQL Server fix is optimistic concurrency via a `rowversion` column: export includes the rowversion, the `_UpdateFromStaging` sproc checks it on UPDATE, and rows where the version has changed since the template was pulled are returned as conflicts rather than silently overwritten. Realistic implementation path: `ScaffoldAn_UPDATE_Feature` detects a `rowversion` column on the domain table and wires in the conflict check automatically — tables without one get last-write-wins as today, with a warning on export. This requires schema changes to domain tables, staging tables, and the scaffolded update sproc. Not blocking for v1.0 since bulk updates in practice are usually coordinated, but worth addressing if SqlXL is used in high-concurrency environments.

---

## Next steps to publish v1.3.0

v1.3.0 is ready to ship. All checklist items are complete:

- ✅ `sqlxl llm-context` command implemented (`Commands/LlmContextCommand.cs`)
- ✅ README updated with `llm-context` section
- ✅ `PackageReleaseNotes` updated in `SqlXl.csproj`
- ✅ Version bumped to `1.3.0`
- ✅ `smoke-test.ps1` passes 14/14 end-to-end
- ✅ `dotnet pack -c Release` — package at `bin/Release/SqlXl.1.3.0.nupkg`

**To publish:**
```bash
dotnet nuget push src/SqlXl/bin/Release/SqlXl.1.3.0.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json
```

---

## Unsupported SQL Column Types

The following SQL Server column types are **not supported** by SqlXL and should never appear in tables used with this tool:

- `BINARY` / `VARBINARY` — fixed and variable-length binary data

Tables containing these types will not work correctly with the Excel import/export pipeline. Document this clearly in user-facing docs when the time comes.

---

---

*Last updated: 2026-05-12*
*Status: v1.3.0 ready to publish. Added `sqlxl llm-context` command.*
