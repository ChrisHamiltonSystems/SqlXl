---
name: sqlxl
description: Use when the user mentions `sqlxl`, runs `sqlxl` commands, asks about the SqlXl SQL Server ⇄ Excel bridge (template-driven DML, BulkOpFeatures, Excel-based imports/exports for SQL Server, Excel-to-DDL inference), or describes a workflow that fits sqlxl (e.g. "I have an Excel file I need to load into a SQL Server table", "generate a CREATE TABLE from this spreadsheet", "let users edit table data in Excel and re-import"). Activates on file references like `*_insert_YYYYMMDD.xlsx`, `*_update_*.xlsx`, mentions of `SqlXl.BulkOpFeatures`, or invocations of `dotnet tool install ... SqlXl`.
---

# sqlxl skill

`sqlxl` is a .NET global CLI tool that bridges SQL Server and Excel. Install with `dotnet tool install --global SqlXl`. Invoke as `sqlxl <command>`.

## 1. First action when this skill activates

**Always do this before answering operational questions about sqlxl:**

```pwsh
sqlxl llm-context --format json
```

This emits a versioned, machine-readable reference for the installed binary. Parse it once at the start of the session and treat it as the authoritative source of truth for commands, flags, gotchas, and template structure. Don't probe the binary with `--help` if `llm-context` is available — that's slower and less complete.

If you also need the live database state (active profile, configured features, domain tables), pass `--include-state`:

```pwsh
sqlxl llm-context --format json --include-state
```

**JSON shape:** see https://sqlxl.example/schemas/llm-context-v1.json (top-level keys: `format_version`, `sqlxl_version`, `commands`, `template_structure`, `bulk_op_feature_schema`, `agent_best_practices`, `workflows`, `gotchas`, `builtin_schema`, optional `active_state`).

**Version handling:**
- If `format_version > 1`, the schema may have changed — read the document defensively, prefer `commands[].name` lookups over field-position assumptions.
- If `format_version < 1` is ever emitted, it's a pre-release; treat with caution.

## 2. Bootstrap fallbacks

The bootstrap depends on what's installed. Detect and adapt:

```
1. Run: sqlxl --version
2. If exit code != 0 → tool is not installed.
   Suggest: `dotnet tool install --global SqlXl --version <latest>`
   Stop unless the user agrees to install.

3. If version >= 1.3.0 → run `sqlxl llm-context --format json`. Done.

4. If version < 1.3.0 → llm-context is unavailable.
   Fall back to: `sqlxl --help` followed by `sqlxl <cmd> --help`
   for each subcommand listed. Use the static reference in §6 below
   to fill in non-obvious behaviors that --help omits.
   Suggest the user upgrade for a better agent experience.
```

PATH note: dotnet global tools live at `%USERPROFILE%\.dotnet\tools` on Windows and `~/.dotnet/tools` on Unix. If `sqlxl` isn't on PATH, prefix with that directory (`& "$env:USERPROFILE\.dotnet\tools\sqlxl.exe" ...`) or prepend it to PATH for the session.

## 3. Standing rules (apply on every invocation)

These are version-stable behaviors. Apply them regardless of what `llm-context` returns.

1. **Always pass `--no-launch`** when generating an .xlsx file. Without it, Excel auto-opens AND locks the file, which breaks any subsequent re-import.
2. **Before any `--file <path>` import, ensure the file is closed in Excel.** If the user has it open, ask them to close it; the tool errors out on locked files.
3. **For destructive commands (`demo`), always pass `--yes`** in non-interactive contexts. The confirmation prompt will block.
4. **Use absolute paths for `--output`** rather than relying on the default filename pattern. Then parse the `File: <path>` line from stdout to confirm.
5. **`test` commits data to the target DB.** Refuse to run `test` against any profile whose database name doesn't look disposable (e.g. contains "demo", "test", "dev", "scratch", "local"). Ask before running otherwise.
6. **Don't mix `--connection` and `--profile`** in the same command — `--connection` wins, and the user probably meant only one.
7. **`update --where` only applies to template generation,** not to imports. Imports key off the primary key in the file.
8. **`init` is a prerequisite for `export`.** If a user runs `export` against a fresh DB, run `init` first (with their consent).

## 4. Common workflows

When the user describes a goal in natural language, map to a workflow:

| User says... | Use... |
|---|---|
| "I want users to add new rows to table X via Excel" | `insert` (round-trip) — auto-scaffolds a feature on first run |
| "I need to bulk-edit rows in X" | `update --where <filter>` |
| "I want to dump these query results to Excel" | `export --query` |
| "I have this xlsx, what should the table look like?" | `infer <file>` |
| "I want a custom workflow with staging + validation" | Author a `Custom` BulkOpFeature, drive via `import --feature <ID>` |
| "Let me try sqlxl with sample data" | `demo` (drops/recreates SqlXlDemo) |

Always check `active_state.configured_features` (from `--include-state`) before scaffolding — there may already be a feature you should reuse.

## 5. Authoring custom BulkOpFeatures

When the user wants behavior beyond INSERT/UPDATE/DELETE, they need a Custom feature. The flow:

1. Create staging table `SqlXl.Staging_<Name>` with the columns the template should expose.
2. Create a sproc that reads from staging and applies whatever logic — usually one or more INSERTs/UPDATEs against domain tables, with validation.
3. Insert a row into `SqlXl.BulkOpFeatures`:
   - `InsertUpdateDeleteOrCustom = 'Custom'`
   - `GetRowsToEdit_SelectStatement` defines the Data sheet's column shape (use `[DbCol|ExcelHeader]` aliasing)
   - `GetRowsToChooseFrom_SelectStatement` populates the DropdownOptions sheet (FK lookups)
   - `SprocToProcessPerfectStagedData` names the sproc from step 2
4. Drive via `sqlxl import --feature <ID>`.

The schema lives in `bulk_op_feature_schema` from llm-context — pull exact column types from there.

## 6. Static reference (fallback for pre-1.3.0)

This is current as of v1.2.0, version-frozen. Use only when `llm-context` is unavailable.

### Commands

| Command | Purpose |
|---|---|
| `init --connection <C> [--profile <N>]` | Install SqlXl infra + save profile |
| `use <PROFILE>` | Activate profile |
| `connections list` / `connections remove <P>` | Manage profiles |
| `insert --table <T> [--file <X>] [--output <P>] [--no-launch]` | Generate INSERT template OR import filled. Auto-scaffolds feature on first call per table. |
| `update --table <T> [--where <W>] [--file <X>] [--output <P>] [--no-launch]` | Generate UPDATE template (pre-populated, filterable) OR import. `--where` is template-only. |
| `import --feature <ID> [--file <X>] [--output <P>] [--no-launch]` | Generic feature driver |
| `export --query <SQL> [--output <P>] [--no-launch]` | Run SELECT to xlsx. Requires init. |
| `test --table <T> [--rows <N>]` | Synthetic test data per feature. Max 100 rows. **Commits to DB.** |
| `infer <INPUT> [--table <N>] [--schema <S>] [--sheet <S>] [--output <P>] ...` | xlsx → CREATE TABLE DDL |
| `demo --connection <C> [--yes]` | Drop/recreate SqlXlDemo |

### Template structure (3 sheets)

| Sheet | Columns | Notes |
|---|---|---|
| `Data` | (dynamic, from feature) | Identity/PK columns excluded for Insert features |
| `DropdownOptions` | `[ForColumn, OptionText]` | FK values formatted as `"<id> - <label>"` |
| `Metadata` | `[DbColumnName, ExcelColumnName, SqlDataType, IsPrimaryKey]` | |

### `SqlXl.BulkOpFeatures` columns

`ID` (int, pk) · `UserFriendlyFeatureName` · `InsertUpdateDeleteOrCustom` (Insert/Update/Delete/Custom) · `DomainSchemaName`/`DomainTableName` · `StagingSchemaName`/`StagingTableName` · `GetRowsToChooseFrom_SelectStatement` · `GetRowsToEdit_SelectStatement` · `SprocToProcessPerfectStagedData` · `MenuDisplayRanking`

### Auto-scaffold side effects

`sqlxl insert --table dbo.X` (with no existing feature for X) creates:
- `SqlXl.Staging_X_ForInserts` table
- `X_InsertFromStaging` sproc
- A `BulkOpFeatures` row of type `Insert`

Mention this side-effect to the user when running against a shared DB.

## 7. When NOT to use this skill

- Don't run `llm-context` for trivial questions like "is sqlxl installed?" — just `sqlxl --version`.
- Don't run `--include-state` unless you actually need active-DB info; it requires a connection and is slower.
- Don't author a Custom BulkOpFeature when a stock Insert/Update would do — the auto-scaffolded INSERT covers most cases.

## 8. Reporting back to the user

When you finish an sqlxl operation, surface:
- The sqlxl version you used (so they know if they should upgrade)
- The absolute path of any generated file (parse from the `File: <path>` line)
- For imports: rows inserted/updated/failed counts (parse from stdout)
- For `test`: per-feature pass/fail summary
- Any side-effects (auto-scaffolded objects, especially against shared DBs)
