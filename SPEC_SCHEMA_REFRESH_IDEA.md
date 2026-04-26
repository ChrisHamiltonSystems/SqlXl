# SPEC: `sqlxl refresh --table`

## 1. Purpose

When a user first runs `sqlxl insert --table dbo.Products`, SqlXL scaffolds a staging table and processing sproc derived from `dbo.Products` **as it exists at that moment**. If the domain table later evolves (column added, type widened, nullability changed), the scaffolded artifacts stay frozen. This produces silent failures: new columns are missing from the template (silent data loss), dropped columns are still in the template (import errors), narrowed types truncate data.

`refresh --table` reconciles the scaffolded artifacts back to the current domain shape.

## 2. Tier scope

**Tier 1 / Tier 2 only.** `refresh` rebuilds artifacts SqlXL owns. Tier 3 (`import --feature`) staging tables and sprocs are user-designed contracts and must never be touched.

If a Tier 3 `BulkOpFeature` references the same domain table, `refresh --table` refreshes only the Tier 1/2 artifacts and prints a notice that the Tier 3 feature is unaffected.

## 3. CLI surface

```bash
# Detect drift, no changes
sqlxl refresh --table dbo.Products --dry-run

# Apply: refresh both insert and update scaffolds
sqlxl refresh --table dbo.Products

# Scope to one direction
sqlxl refresh --table dbo.Products --insert-only
sqlxl refresh --table dbo.Products --update-only

# Allow narrowing types or other risky changes
sqlxl refresh --table dbo.Products --force

# Explicit rename hint (avoids drop+add semantics)
sqlxl refresh --table dbo.Products --rename ProductName:Name

# Refresh every drifted table SqlXL has scaffolded
sqlxl refresh --all
sqlxl refresh --all --dry-run
```

Standard flags apply: `--connection`, `--profile`.

## 4. Algorithm

For each direction (insert, update) in scope:

1. **Locate scaffold artifacts** by SqlXL convention or via `SqlXl.BulkOpFeatures`: `Staging_<Table>_ForInserts`, `<Table>_InsertFromStaging`, and the update-side equivalents.
2. **Read domain schema** from `sys.columns` / `sys.indexes` / `sys.foreign_keys`: name, type, length/precision/scale, nullability, identity, computed, default, PK membership, FK references.
3. **Read staging schema** with the same query shape.
4. **Compute diff** (see §5).
5. If `--dry-run`: print diff, exit 0 (or non-zero with `--exit-code-on-drift` for CI).
6. Otherwise:
   - **Refuse if staging is non-empty.** Print row count and how to clear.
   - **Refuse risky changes without `--force`** (see §5).
   - In a transaction per direction: drop and recreate the staging table, `CREATE OR ALTER` the processing sproc, update `BulkOpFeatures` metadata if present (preserving user-edited fields, see §6).
   - Print applied summary.

## 5. Drift cases

| Drift | Detection | Refresh behavior |
|---|---|---|
| Column added to domain | In domain, not in staging | Add to staging; add to sproc column list |
| Column dropped from domain | In staging, not in domain | Remove from staging; remove from sproc |
| Column type widened (`varchar(50)` → `varchar(200)`, `int` → `bigint`) | Type mismatch | Apply |
| Column type narrowed (`bigint` → `int`, `varchar(200)` → `varchar(50)`) | Type mismatch | **Refuse without `--force`** — narrowing risks silent truncation |
| Nullability `NULL` → `NOT NULL` | Mismatch | Apply (stricter validation is safe) |
| Nullability `NOT NULL` → `NULL` | Mismatch | Apply |
| Identity flag changed | Mismatch | Re-scaffold; identity columns are excluded from insert staging by convention |
| Computed column added | In domain as computed, not in staging | Skip — computed columns are never in staging |
| Default value changed | Mismatch | Update staging default |
| PK columns changed | Set differs | **Tier 1 (insert):** safe; re-scaffold. **Tier 2 (update):** PK is the join key — re-scaffold but warn loudly that pending templates will fail |
| FK added or removed | Diff against `sys.foreign_keys` | Update FK metadata used for Excel dropdown lookups |
| Column renamed | Naive diff sees drop + add | Without `--rename`, treat as drop+add (with warning). With `--rename old:new`, preserve metadata |

## 6. Preservation rules for `BulkOpFeatures` metadata

When a `BulkOpFeatures` row exists for this scaffold, preserve user-edited values for unchanged columns:

- `DisplayName` overrides
- `WhereClause`, `OrderBy`, custom select fragments
- Custom `GetRowsToEdit_SelectStatement` if non-default

Refresh only:

- The column set (insert/remove based on diff)
- Type metadata (length/precision/scale/nullability)
- FK lookup wiring

For renamed columns, `DisplayName` carries over only when `--rename` is supplied.

## 7. Safety

- **Idempotent.** No drift = no-op + "no drift detected" + exit 0.
- **Refuses dirty staging.** Will not destroy in-flight data; tells the user how to clear.
- **Atomic per direction.** Staging recreate + sproc alter in one transaction. Failure leaves prior scaffold intact.
- **Reviewable.** Prints the SQL it intends to run (or runs in dry-run mode) before executing.

## 8. Output

Dry-run example:

```
Drift detected for dbo.Products:

  Insert direction:
    + Column added:    [Sku] NVARCHAR(50) NOT NULL
    ~ Column changed:  [Price] DECIMAL(8,2) -> DECIMAL(10,2)
    - Column dropped:  [LegacyCode] NVARCHAR(20)

  Update direction:
    (no drift)

Run `sqlxl refresh --table dbo.Products` to apply.
```

Apply mode = same diff with applied/failed markers per item. Exit 0 on success, non-zero on any refusal or error.

## 9. Pending Excel template invalidation

After a refresh that changes the staging shape, any Excel templates already on the user's filesystem for this table will fail validation on next import. SqlXL can't see the filesystem, so after any column-set or rename change, print:

```
Note: Excel templates exported before this refresh will fail validation on import.
Re-export with `sqlxl insert --table dbo.Products` to get a fresh template.
```

## 10. Out of scope

- Migrating data inside a non-empty staging table.
- Heuristic rename detection (too risky to do silently — require `--rename`).
- Refreshing Tier 3 artifacts. User-owned.
- Cross-database staging.

## 11. Open questions for the implementer

1. **Does the current scaffold persist any version/hash of the source domain shape?** If yes, refresh can compare against it. If no, refresh derives drift solely from current domain vs current staging. Recommend: don't add scaffold-version tracking just for this; current-state diff is sufficient and simpler.
2. **`refresh --all` — parallel or serial?** Recommend serial. A single failure stops the run; clearer error reporting.
3. **What if no scaffold exists for the table?** Recommend refusing with "no scaffold exists for dbo.Products — run `sqlxl insert --table dbo.Products` first." Don't conflate `refresh` with first-time scaffold; that path already exists.

---

*Last updated: 2026-04-25*
*Status: spec only — no implementation yet*
