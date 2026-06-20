# Bug: staging-table generation drops `decimal`/`numeric` precision & scale (silent rounding)

> **Status:** Open in SqlXL. Found and fixed downstream in SlappFramework v2 on 2026-06-20;
> this note proposes the same backport here.
> **Severity:** High — **silent data corruption** of scaled numeric columns. No error is raised;
> values are simply rounded on their way through staging.

---

## Summary

When SqlXL scaffolds a bulk-op staging table, it derives each column's type from the domain table
via `INFORMATION_SCHEMA.COLUMNS`. The type-rendering logic emits a size qualifier **only** for
`char`/`varchar`/`nchar`/`nvarchar`. For every other parameterised type it emits the bare type name.

For `decimal` / `numeric` this means the staging column is created as **`decimal(18, 0)`** (SQL
Server's default when precision/scale are omitted) regardless of the domain column's real scale. Any
fractional value is then **rounded to a whole number** when it lands in staging, and that rounded
value is what gets transferred into the domain table.

A price column `decimal(10, 2)` holding `12.50` becomes `13` in staging (`7.25` becomes `7`), and the
bulk insert/update writes the rounded amount. The user sees no warning — the data is simply wrong.

---

## Impact

- **Any `decimal`/`numeric` column with scale > 0** loses its fractional part through a bulk op.
  Prices, rates, weights, percentages, currency amounts — all silently rounded.
- Same class of latent defect for **fractional-second temporal types** (`datetime2`, `time`,
  `datetimeoffset`): omitting the scale defaults them to the maximum (7) rather than the column's
  declared precision. Lower impact than decimal (it over-allocates rather than truncating on the way
  in), but it diverges from the domain schema and should be fixed in the same pass.

This directly undermines SqlXL's core guarantee that staged data is validated against a staging table
that faithfully mirrors the destination.

---

## Where

- **File:** `sql/CreateInfrastructure.sql`
- **Function:** `[SqlXl].[GenerateCreateStagingTableSQLWith_NO_IdentityProperty]` (around line 355)
- **Called by:** `[SqlXl].[ReScaffoldAStagingTable]` (around line 2735), which every
  `ScaffoldAn_INSERT_Feature` / `ScaffoldAn_UPDATE_Feature` ultimately invokes.

### The buggy logic

```sql
SELECT @SQL = COALESCE(@SQL + ', ', '') +
       COLUMN_NAME + ' ' +
       DATA_TYPE +
       CASE WHEN DATA_TYPE IN ('char', 'varchar', 'nchar', 'nvarchar') THEN '(' +
            CASE WHEN CHARACTER_MAXIMUM_LENGTH = -1 THEN 'MAX'
                 ELSE CAST(CHARACTER_MAXIMUM_LENGTH AS NVARCHAR)
            END + ')' ELSE '' END + ' ' +          -- decimal/numeric fall through to '' here
       CASE WHEN IS_NULLABLE = 'NO' THEN 'NOT NULL' ELSE 'NULL' END
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @DomainSchemaName AND TABLE_NAME = @DomainTableName
ORDER BY ORDINAL_POSITION
```

`decimal`/`numeric` (and the temporal types) hit the `ELSE ''` branch → `decimal` with no qualifier
→ `decimal(18, 0)`.

---

## Reproduce

```sql
-- A domain table with a scaled decimal column.
CREATE TABLE dbo.PriceDemo (Id INT IDENTITY(1,1) PRIMARY KEY, Price DECIMAL(10,2) NOT NULL);

-- Inspect what the generator produces for the staging table:
SELECT [SqlXl].[GenerateCreateStagingTableSQLWith_NO_IdentityProperty]('dbo','PriceDemo','SqlXl','Staging_PriceDemo');
-- => CREATE TABLE SqlXl.Staging_PriceDemo (Id int NOT NULL, Price decimal NOT NULL, RequestID NVARCHAR(36) NOT NULL)
--                                                              ^^^^^^^  -> decimal(18,0)

-- Net effect: a value of 12.50 staged into Price becomes 13.
```

---

## Proposed fix

Extend the `CASE` to render the correct qualifier for the parameterised types. Schema-agnostic and
idempotent (the function is already `CREATE OR ALTER`); re-running `sqlxl` setup applies it.

```sql
-- Generate the column definitions excluding identity property
SELECT @SQL = COALESCE(@SQL + ', ', '') +
       COLUMN_NAME + ' ' +
       DATA_TYPE +
       CASE
            WHEN DATA_TYPE IN ('char', 'varchar', 'nchar', 'nvarchar', 'binary', 'varbinary') THEN '(' +
                CASE WHEN CHARACTER_MAXIMUM_LENGTH = -1 THEN 'MAX'
                     ELSE CAST(CHARACTER_MAXIMUM_LENGTH AS NVARCHAR)
                END + ')'
            -- Preserve precision/scale for exact-numeric types. Without this the staging column
            -- defaults to decimal(18,0), silently rounding scaled values (a price 12.50 -> 13).
            WHEN DATA_TYPE IN ('decimal', 'numeric') THEN
                '(' + CAST(NUMERIC_PRECISION AS NVARCHAR) + ',' + CAST(NUMERIC_SCALE AS NVARCHAR) + ')'
            -- Preserve fractional-second precision for temporal types (default would otherwise be 7).
            WHEN DATA_TYPE IN ('datetime2', 'time', 'datetimeoffset') THEN
                '(' + CAST(DATETIME_PRECISION AS NVARCHAR) + ')'
            ELSE '' END + ' ' +
       CASE WHEN IS_NULLABLE = 'NO' THEN 'NOT NULL' ELSE 'NULL' END
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @DomainSchemaName AND TABLE_NAME = @DomainTableName
ORDER BY ORDINAL_POSITION
```

Notes:
- `binary`/`varbinary` are added to the length branch for completeness (same `CHARACTER_MAXIMUM_LENGTH`
  / `-1 = MAX` semantics as the string types). Not required to fix the decimal corruption, but they
  have the identical "qualifier dropped" defect.
- `float` is intentionally left alone: it is approximate by nature and the omitted `float(n)` only
  affects storage precision, not the kind of silent value rounding decimal scale causes. Add it if
  you want exact fidelity (`'(' + CAST(NUMERIC_PRECISION AS NVARCHAR) + ')'`).

### Migration

The fix corrects newly scaffolded staging tables. **Existing** staging tables created before the fix
keep their `decimal(18,0)` columns until re-scaffolded — re-run the feature's scaffold (which calls
`ReScaffoldAStagingTable`) to rebuild them.

---

## How it was found

SlappFramework v2 ported this engine (the `SqlXl` schema → `Slapp`) and built a web bulk-insert
feature against a `Products` table (`Price decimal(10,2)`, FK to `Categories`). An end-to-end upload
of `12.50` / `7.25` committed `13.00` / `7.00`. Tracing it to the staging column type
(`decimal(18,0)`) led here. After the fix the staging column is `decimal(10,2)` and values round-trip
exactly. The same fix has been applied in SlappFramework v2's
`src/SlappFramework.Core/Schema/01_CreateInfrastructure.sql`.
