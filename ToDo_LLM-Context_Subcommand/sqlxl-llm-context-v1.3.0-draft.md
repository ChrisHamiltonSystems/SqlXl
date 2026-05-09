# sqlxl llm-context

```
sqlxl_version:  1.3.0
format_version: 1
generated_at:   <ISO-8601 at runtime>
docs_url:       <fill in: GitHub or docs site>
```

This is a dense, agent-targeted reference for the `sqlxl` CLI. Everything an
LLM needs to operate the tool fluently is below — no external lookups required.

---

## 1. Mental model

`sqlxl` is a **SQL Server ⇄ Excel bridge** for SQL Server.

Three core flows:

1. **Template-driven DML** — `insert`, `update`, `import` generate Excel
   templates that humans fill in, then re-import.
2. **Query export** — `export` runs a SELECT and writes results to .xlsx.
3. **Schema inference** — `infer` reads an .xlsx and emits a `CREATE TABLE`
   statement.

Plus: `test` (synthetic data harness), `demo` (sample database), and
connection-profile management (`init`, `use`, `connections`).

Extensibility is via **BulkOpFeature** rows in the target database — server-side
declarative bulk-op definitions (see §6).

---

## 2. Connection model

| Command           | Purpose                                                 |
|-------------------|---------------------------------------------------------|
| `init`            | Install SqlXl infrastructure into a DB; save a profile  |
| `use <name>`      | Activate a saved profile                                |
| `connections list`| List all saved profiles                                 |
| `connections remove <name>` | Delete a profile                              |

Every operational command accepts:

- `--connection <connstr>` — ad-hoc connection string (overrides profile)
- `--profile <name>`       — named profile (overrides active profile)

If neither is given, the **active profile** is used.

Profile storage path: `<TODO: implementer — emit actual path at runtime>`

---

## 3. Command reference

### `init`

```
sqlxl init --connection <CONNSTR> [--profile <NAME>]
```

| Flag            | Required | Description                                            |
|-----------------|----------|--------------------------------------------------------|
| `--connection`  | yes      | Target SQL Server                                       |
| `--profile`     | no       | Profile name (default: `default`)                       |

**Behavior:** Installs the `SqlXl.*` schema (BulkOpFeatures, ColumnUIConfigurations,
DebugLog, Meta_Columns, RequestContext, SavedQueries, plus staging tables) into
the target database. Idempotent — re-running updates the profile and re-applies
infrastructure.

**Required for:** `export` (validates queries via SqlXl infrastructure).

---

### `use`

```
sqlxl use <PROFILE>
```

Activates the named profile. Subsequent commands without `--connection` /
`--profile` will use it.

---

### `connections`

```
sqlxl connections list
sqlxl connections remove <PROFILE>
```

---

### `insert`

```
sqlxl insert --table <T> [--output <PATH>] [--no-launch]              # generate template
sqlxl insert --table <T> --file <XLSX> [--no-launch]                  # import filled template
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--table`       | `dbo.Foo` or `Foo` (defaults to `dbo`)                     |
| `--file`        | Filled template to import. Omit to generate.               |
| `--output`      | Template output path (default: `<Table>_insert_YYYYMMDD.xlsx`) |
| `--no-launch`   | Don't auto-open Excel (USE THIS in agents/scripts)         |
| `--connection`  | Override profile                                           |
| `--profile`     | Override active profile                                    |

**Behavior:**
- If no `BulkOpFeature` exists for `<T>`, **auto-scaffolds one**:
  - Creates staging table `SqlXl.Staging_<Table>_ForInserts`
  - Creates sproc `<Table>_InsertFromStaging`
  - Inserts a `BulkOpFeatures` row of type `Insert`
- Generated templates **exclude IDENTITY/PK columns**.
- Templates have 3 sheets (see §5).

---

### `update`

```
sqlxl update --table <T> [--where "<CLAUSE>"] [--output <PATH>] [--no-launch]
sqlxl update --table <T> --file <XLSX> [--no-launch]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--table`       | Target domain table                                        |
| `--where`       | SQL WHERE clause to filter rows for the template (TEMPLATE GENERATION ONLY — ignored on import) |
| `--file`        | Filled template to import                                  |
| `--output`      | Template output path (default: `<Table>_update_YYYYMMDD.xlsx`) |
| `--no-launch`   | Don't auto-open Excel                                      |

**Behavior:** Template is **pre-populated with existing rows** matching `--where`
(or all rows if omitted). User edits values in place; import applies updates
keyed by primary key.

---

### `import`

```
sqlxl import --feature <ID> [--output <PATH>] [--no-launch]
sqlxl import --feature <ID> --file <XLSX> [--no-launch]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--feature`     | `BulkOpFeatures.ID` from `SqlXl.BulkOpFeatures`            |
| `--file`        | Filled template to import                                  |
| `--output`      | Default: `Feature_YYYYMMDD.xlsx`                           |
| `--no-launch`   | Don't auto-open Excel                                      |

**Behavior:** Generic entry point for any `BulkOpFeature` — `Insert`, `Update`,
`Delete`, or `Custom`. Use this when you want to drive a manually-authored
`Custom` feature, or invoke a specific feature ID rather than the default for
a table.

---

### `export`

```
sqlxl export --query "<SELECT>" [--output <PATH>] [--no-launch]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--query`       | SELECT statement (validated via SqlXl infrastructure first)|
| `--output`      | Default: `export_YYYYMMDD_HHmmss.xlsx`                     |
| `--no-launch`   | Don't auto-open Excel                                      |

**Requires:** `sqlxl init` to have been run on the target DB.

**Output:** Single-sheet workbook with column headers in row 1, data below.

---

### `test`

```
sqlxl test --table <T> [--rows <N>]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--table`       | Target table                                               |
| `--rows`        | Rows per feature (default: 1, max: 100)                    |

**Behavior:** For every `BulkOpFeature` configured against `<T>`, generates
synthetic rows and runs the full pipeline. Uniqueness-aware: respects unique
constraints against both existing data AND across the generated batch. Each
feature runs independently — one failing does not stop others.

**Note:** Test data is **committed to the database**. Run only against a
disposable DB.

**Common failure mode:** with small tables having narrow unique-column value
spaces, `--rows N` (N>1) often collides. Use `--rows 1` to verify pipeline,
larger N to stress-test.

---

### `infer`

```
sqlxl infer <INPUT> [OPTIONS]
```

| Flag                       | Description                                                              |
|----------------------------|--------------------------------------------------------------------------|
| `--table <NAME>`           | Target table name (default: sanitized file basename)                     |
| `--schema <NAME>`          | Target schema (default: `dbo`)                                           |
| `--sheet <NAME>`           | Worksheet to read (REQUIRED if workbook has multiple sheets)             |
| `--sample-size <N>`        | Rows to sample for type inference (default: 1000)                        |
| `--confidence-threshold <RATIO>` | Min valid-ratio for a type to be selected (default: 0.9)           |
| `--mode <MODE>`            | `permissive` (invalid → NULL) or `strict` (any invalid forces NVARCHAR)  |
| `--max-varchar <N>`        | Cap inferred NVARCHAR length (default: 255)                              |
| `--date-format <STYLE>`    | `us` (M/d/yyyy + ISO + long forms) or `iso` (ISO + long only). Default: `us` |
| `--output <PATH>`          | Write DDL to file (default: stdout)                                      |
| `--report <PATH>`          | Write JSON inference report                                              |

**Behavior:** Pure read operation. No DB connection needed. Pipeable —
DDL goes to stdout unless `--output` given.

---

### `demo`

```
sqlxl demo --connection <CONNSTR> [--yes]
```

**Drops and recreates `SqlXlDemo`** on the target server with sample data
(11 domain tables, 1 pre-configured `Custom` feature: "Assign User Roles").
`--yes` skips the confirmation prompt.

---

## 4. Two-phase template/import workflow

The `insert`, `update`, and `import` commands all follow the same shape:

```
PHASE 1 (generate):  omit --file       → produces .xlsx template
PHASE 2 (import):    pass --file <X>   → loads template into staging, then sproc
                                          processes valid rows into domain table
```

Round-trip example:

```pwsh
sqlxl insert --table dbo.Products --output template.xlsx --no-launch
# (human edits template.xlsx, closes Excel)
sqlxl insert --table dbo.Products --file template.xlsx --no-launch
```

---

## 5. Template structure (3-sheet contract)

Every generated template is a workbook with **exactly three sheets**:

| Sheet              | Purpose                                                                        |
|--------------------|--------------------------------------------------------------------------------|
| `Data`             | Fillable rows. Columns from `GetRowsToEdit_SelectStatement`                    |
| `DropdownOptions`  | FK / lookup values. Columns: `[ForColumn, OptionText]`. Format: `"<id> - <label>"` |
| `Metadata`         | Per-column schema. Columns: `[DbColumnName, ExcelColumnName, SqlDataType, IsPrimaryKey]` |

**Column-aliasing syntax** in `GetRowsToEdit_SelectStatement`:
`[DbCol|ExcelHeader]` — left side is the db column, right is the Excel header.

**FK dropdown format:** `"1 - Electronics"`, `"3 - Books"`. Friendly label
preserves the underlying ID — agents writing rows should emit values in this
exact format for FK columns.

**Identity/PK columns** are omitted from the `Data` sheet for `Insert` features
(but present in `Metadata` with `IsPrimaryKey=YES`).

---

## 6. BulkOpFeature schema

Definition lives in `SqlXl.BulkOpFeatures`:

| Column                                | Type            | Description                                                                |
|---------------------------------------|-----------------|----------------------------------------------------------------------------|
| `ID`                                  | int (PK)        | Feature ID — passed to `import --feature`                                  |
| `UserFriendlyFeatureName`             | nvarchar        | Display name                                                               |
| `InsertUpdateDeleteOrCustom`          | nvarchar        | `Insert` \| `Update` \| `Delete` \| `Custom`                               |
| `DomainSchemaName` / `DomainTableName`| nvarchar        | Target table                                                               |
| `StagingSchemaName` / `StagingTableName` | nvarchar     | Staging landing zone                                                       |
| `GetRowsToChooseFrom_SelectStatement` | nvarchar(max)   | Feeds the `DropdownOptions` sheet                                          |
| `GetRowsToEdit_SelectStatement`       | nvarchar(max)   | Defines the `Data` sheet shape (use `[Col\|Alias]` syntax)                 |
| `SprocToProcessPerfectStagedData`     | nvarchar        | Sproc that promotes validated staged rows → domain                         |
| `MenuDisplayRanking`                  | int             | UI ordering hint                                                           |

**Authoring a Custom feature:**

1. Create staging table `SqlXl.Staging_<Name>` with the columns the template
   should expose.
2. Create sproc that reads from staging and writes wherever needed.
3. Insert a row into `SqlXl.BulkOpFeatures` with `InsertUpdateDeleteOrCustom='Custom'`.
4. Drive it via `sqlxl import --feature <ID>`.

---

## 7. Agent-mode best practices

| Rule                                                                                | Why                                                                |
|-------------------------------------------------------------------------------------|--------------------------------------------------------------------|
| Always pass `--no-launch` on commands that produce .xlsx                             | Without it, Excel opens AND locks the file, breaking re-imports    |
| Before `--file` import, ensure file is closed in Excel                               | Tool errors out on locked file                                     |
| Parse `File: <path>` line from stdout to capture output paths                        | Use absolute paths printed by the tool, not your guess of cwd      |
| Exit code 0 = success, non-zero = failure                                            | Diagnostics are on stderr                                          |
| For destructive ops (`demo`), always pass `--yes`                                    | Confirmation prompt blocks non-TTY callers                         |
| Use `--output <abs-path>` rather than relying on default filename patterns           | Defaults change less than you'd think, but explicit is safer       |

---

## 8. Common workflows

### Round-trip insert (auto-scaffolded)

```pwsh
sqlxl insert --table dbo.Products --output t.xlsx --no-launch
# write rows into t.xlsx Data sheet (use FK format "id - label" for category columns)
sqlxl insert --table dbo.Products --file t.xlsx --no-launch
```

### Filtered update

```pwsh
sqlxl update --table dbo.Products --where "CategoryID = 1" --output u.xlsx --no-launch
# edit u.xlsx
sqlxl update --table dbo.Products --file u.xlsx --no-launch
```

### Custom feature import

```pwsh
sqlxl import --feature 1 --output assignments.xlsx --no-launch
# fill rows
sqlxl import --feature 1 --file assignments.xlsx --no-launch
```

### Infer DDL from spreadsheet, then build the table

```pwsh
sqlxl infer customers.xlsx --schema sales --table Customers --output ddl.sql
sqlcmd -S localhost -d MyDb -E -i ddl.sql
sqlxl init --connection "Server=localhost;Database=MyDb;Trusted_Connection=True;" --profile mydb
```

### Ad-hoc query export

```pwsh
sqlxl export --query "SELECT * FROM dbo.Orders WHERE OrderDate > '2026-01-01'" --output orders.xlsx --no-launch
```

---

## 9. Gotchas

- `update --where` applies only to **template generation**, not import. Import is keyed by PK.
- `init` is required before `export` — query validation depends on SqlXl infrastructure.
- `infer` writes DDL to **stdout** by default — pipe-friendly. `--output` to file.
- Tables can be `dbo.Foo` or `Foo` (assumes `dbo`).
- `test` commits data — never run against production.
- `test` with `--rows N>1` often collides on unique constraints in small tables; not a tool bug.
- Auto-scaffolding (on first `insert` for a table) creates DB objects as a side-effect — note this when running against shared databases.
- The `[Col|Alias]` aliasing syntax in `GetRowsToEdit_SelectStatement` is sqlxl-specific, not standard SQL.

---

## 10. Built-in `SqlXl.*` schema (created by `init`)

| Table                                 | Purpose                                                       |
|---------------------------------------|---------------------------------------------------------------|
| `SqlXl.BulkOpFeatures`                | Feature registry (see §6)                                     |
| `SqlXl.ColumnUIConfigurations`        | Per-column UI hints (display formatting, dropdown sources)    |
| `SqlXl.Meta_Columns`                  | Schema metadata cache                                         |
| `SqlXl.RequestContext`                | Per-request execution context (used during imports)           |
| `SqlXl.SavedQueries`                  | Persisted SELECT statements                                   |
| `SqlXl.DebugLog`                      | Diagnostic log written by sprocs                              |
| `SqlXl.Staging_<Name>`                | One per feature — landing zone for imports                    |

---

## 11. Quick reference (commands + key flags)

```
sqlxl init --connection <C> [--profile <N>]
sqlxl use <PROFILE>
sqlxl connections list | remove <PROFILE>

sqlxl insert --table <T> [--output <P>] [--no-launch]            # gen template (auto-scaffolds feature)
sqlxl insert --table <T> --file <X>   [--no-launch]              # import

sqlxl update --table <T> [--where <W>] [--output <P>] [--no-launch]
sqlxl update --table <T> --file <X>                [--no-launch]

sqlxl import --feature <ID> [--output <P>] [--no-launch]
sqlxl import --feature <ID> --file <X>     [--no-launch]

sqlxl export --query <SQL> [--output <P>] [--no-launch]

sqlxl test   --table <T> [--rows <N>]

sqlxl infer  <INPUT> [--table <N>] [--schema <S>] [--sheet <S>] [--sample-size <N>]
                     [--confidence-threshold <R>] [--mode permissive|strict]
                     [--max-varchar <N>] [--date-format us|iso]
                     [--output <P>] [--report <P>]

sqlxl demo   --connection <C> [--yes]
```

---

## 12. Versioning

This document's structure is versioned. `format_version: 1` is the initial
contract. Breaking changes (removing/renaming sections, changing JSON shape
under `--format json`) require bumping `format_version`. Additive changes
(new commands, new flags, new gotchas appended) do not.

Agents parsing this output should:

1. Read `format_version` first.
2. Locate sections by their `## N. <title>` headers (numbered, stable order).
3. Treat tables as the source of truth for flag/column lists.
