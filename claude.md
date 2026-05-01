# SqlXL - Context for LLMs

## What is SqlXL?

SqlXL is a **lightweight CLI tool** for SQL Server data professionals to efficiently move data between SQL Server and Excel files using [SlappFramework](https://github.com/ChrisHamiltonSystems/SlappFramework) infrastructure.

**Target users:** DBAs, data analysts, SQL developers in enterprise environments who need a fast, no-web-server way to export/import data with validation.

## Core Value Proposition

**"Excel ↔ SQL Server, done right - no web server required!"**

- **Simple CLI** - Just type `sqlxl export` or `sqlxl import`
- **Leverages SlappFramework's staging table validation** - Your database constraints ARE your validators
- **No infrastructure needed** - Direct SQL Server connection, generates Excel locally
- **Works with existing SlappFramework databases** - Uses `ZZ_SlappFramework.BulkOpFeatures` configuration

## Relationship to SlappFramework

SqlXL **extracts** the Excel export/import workflows from SlappFramework web app and packages them as a standalone CLI tool.

**What we took from SlappFramework:**
- Core business logic: `BulkOpsHelper.cs`, `DataService.cs`, `ExcelTemplateGenerator.cs`, `ExcelImporter.cs`
- Staging table validation pattern
- BulkOpFeature configuration system

**What we LEFT BEHIND:**
- ASP.NET Core web hosting (Controllers, Views, Razor, wwwroot)
- Handsontable grid UI
- All web-specific dependencies
- Dependency injection container

## The Two Commands

### 1. `export` - Generate Excel template from BulkOpFeature
```bash
sqlxl export --feature 3 --output products.xlsx
sqlxl export --feature 2 --query "SELECT * FROM Products WHERE Price > 100" --output expensive-products.xlsx
```

**What it does:**
- Queries `ZZ_SlappFramework.BulkOpFeatures` for metadata
- Generates dark-themed Excel file with:
  - Column headers (with display names)
  - FK dropdown validation (from related tables)
  - Metadata sheet (column mapping)
  - Sheet protection (PK columns locked)
- **For INSERT features:** Empty template (just schema)
- **For UPDATE features:** Populated with query results

### 2. `import` - Import Excel data back to SQL Server
```bash
sqlxl import --file products.xlsx --feature 3
```

**What it does:**
- Parses Excel file (validates structure, FK values)
- Bulk copies to `#ZZTemp` table
- Validates via staging table (e.g., `Staging_Products_ForInserts`)
- SQL Server enforces constraints
- Processes valid data via domain sproc (e.g., `Products_InsertFromStaging`)
- Returns detailed error messages for any validation failures

## Technology Stack

**Distribution:** .NET Global Tool via NuGet

**Backend:**
- .NET 10.0 (console app)
- C# 12
- SQL Server 2019+ (direct connection, no ORM)
- System.CommandLine (CLI framework)
- Dapper (SQL queries)
- SqlBulkCopy (bulk inserts)
- ClosedXML 0.102.2 (Excel generation)
- Microsoft.Data.SqlClient (SQL Server connection)

**No web stack!** No ASP.NET Core, no IIS, no Kestrel, no HTTP.

## Project Structure

```
SqlXlRepo/
├── src/
│   └── SqlXl/
│       ├── Commands/          # CLI command handlers
│       │   ├── ExportCommand.cs
│       │   └── ImportCommand.cs
│       ├── Core/              # Business logic (from SlappFramework)
│       │   ├── BulkOpsHelper.cs        (~70 KB)
│       │   ├── DataService.cs          (~14 KB)
│       │   ├── ExcelTemplateGenerator.cs (~20 KB)
│       │   └── ExcelImporter.cs        (~18 KB)
│       ├── Models/            # Data models
│       │   ├── BulkOpFeature.cs
│       │   └── MenuItem.cs
│       ├── Program.cs         # Entry point
│       └── SqlXl.csproj       # Configured as dotnet tool
├── claude.md                  # This file
├── README.md
└── .gitignore
```

## Current Status (as of 2026-03-05)

### ✅ Completed:
- [x] Git repo initialized and pushed to GitHub
- [x] .NET 8.0 console app created
- [x] Configured as dotnet tool (`<PackAsTool>true</PackAsTool>`)
- [x] Copied Core business logic from SlappFramework
- [x] Updated all namespaces (`SlappFramework` → `SqlXl.Core`, `SqlXl.Models`)
- [x] Added all NuGet packages (ClosedXML, Dapper, SqlClient, System.CommandLine, etc.)
- [x] Removed test dependencies (NUnit), commented out Assert statements
- [x] Created Models folder with BulkOpFeature and MenuItem
- [x] Database connectivity verified (TestDatabase001, localhost)
- [x] Confirmed SlappFramework infrastructure exists (ZZ_SlappFramework schema)
- [x] Found 2 test features available (Insert ID 3, Update ID 2)
- [x] Created ExportCommand and ImportCommand stubs

### ⚠️ Known Issues:
- [ ] Commands have build errors (System.CommandLine v3 preview API mismatch)
- [ ] Need to verify/create DataService helper methods (`GetBulkOpFeatureByIdAsync`, `GetExcelTemplateDataAsync`)
- [ ] BulkOpsHelper constructor/methods need verification

### 🎯 Next Steps:
1. Fix System.CommandLine v3 API usage in commands
2. Create missing DataService wrapper methods or fix calls
3. Test `export` command (generate Excel template)
4. Test `import` command (import Excel data)
5. Polish CLI output (emojis, progress indicators, error handling)
6. Package and publish to NuGet as global tool

## Database Requirements

**Prerequisite:** Target SQL Server database must have SlappFramework infrastructure installed.

**Required:**
- `ZZ_SlappFramework` schema
- `ZZ_SlappFramework.BulkOpFeatures` table (configured features)
- Staging tables (e.g., `Staging_Products_ForInserts`)
- Domain processing sprocs (e.g., `Products_InsertFromStaging`)

**Test Database:**
- Server: `localhost`
- Database: `TestDatabase001`
- Connection: `Data Source=localhost;Database=TestDatabase001;Integrated Security=true;TrustServerCertificate=true;`

**Available Test Features:**
- ID 2: "Edit Products - Find & Edit" (UPDATE feature)
- ID 3: "Add Products - Bulk Grid" (INSERT feature)

## Key Design Decisions

### Command Names: `export` / `import`
- **Considered:** `pull`/`push`, `template`/`import`, `get`/`import`, `generate`/`import`
- **Chose:** `export`/`import` - Industry standard, clear, symmetrical
- Rationale: SQL professionals immediately understand export/import

### No Web Server
- CLI tool runs **directly on user's workstation**
- No IIS, Kestrel, HTTP, or ports
- Connects directly to SQL Server (like SSMS)
- Perfect for enterprise environments with SQL access but restricted web hosting

### Reused SlappFramework Core
- Battle-tested Excel workflows
- Staging table validation pattern proven
- No need to reinvent constraint-driven validation

### ClosedXML (not EPPlus)
- MIT licensed — no per-user license config required, no commercial restrictions
- Replaced EPPlus before v1.0 publish; EPPlus licensing was incompatible with free NuGet distribution
- All required capabilities (cell styling, data validation, sheet protection, dropdowns) available in ClosedXML

## How to Build/Run

### Build:
```bash
cd C:\Dev\SqlXlRepo\src\SqlXl
dotnet build
```

### Run locally (development):
```bash
cd C:\Dev\SqlXlRepo\src\SqlXl
dotnet run -- export --feature 3 --output test.xlsx
```

### Pack as tool:
```bash
cd C:\Dev\SqlXlRepo\src\SqlXl
dotnet pack
```

### Install globally (from local build):
```bash
dotnet tool install --global --add-source ./bin/Release SqlXl
sqlxl --help
```

### Uninstall:
```bash
dotnet tool uninstall --global SqlXl
```

## Testing Strategy

### Manual Test Plan:
1. **Export empty template (INSERT):**
   ```bash
   sqlxl export --feature 3 --output new-products.xlsx
   ```
   - Verify Excel has column headers
   - Verify FK dropdowns exist (if any)
   - Verify metadata sheet exists

2. **Fill Excel manually** - Add new product rows

3. **Import filled template:**
   ```bash
   sqlxl import --file new-products.xlsx --feature 3
   ```
   - Verify data validates
   - Verify inserts to Products table
   - Check error messages for invalid data

4. **Export with data (UPDATE):**
   ```bash
   sqlxl export --feature 2 --output existing-products.xlsx
   ```
   - Verify Excel has existing product data
   - Edit some rows
   - Re-import and verify updates

## Important Files to Know

| File | Purpose | Lines | Status |
|------|---------|-------|--------|
| `Core/BulkOpsHelper.cs` | Core validation engine, orchestrates staging table validation | ~1,600 | ✅ Compiles |
| `Core/DataService.cs` | SQL Server wrapper, executes sprocs via Dapper | ~450 | ✅ Compiles |
| `Core/ExcelTemplateGenerator.cs` | Generates dark-themed Excel with FK dropdowns | ~456 | ✅ Compiles |
| `Core/ExcelImporter.cs` | Parses Excel, validates structure, extracts data | ~454 | ✅ Compiles |
| `Commands/ExportCommand.cs` | CLI export command handler | ~95 | ⚠️ Build errors |
| `Commands/ImportCommand.cs` | CLI import command handler | ~130 | ⚠️ Build errors |
| `Program.cs` | Entry point, sets up System.CommandLine | ~12 | ⚠️ Build errors |

## Connection String Configuration

**Current default:**
```
Data Source=localhost;Database=TestDatabase001;Integrated Security=true;TrustServerCertificate=true;
```

**Can be overridden via `--connection` flag:**
```bash
sqlxl export --feature 3 --connection "Server=prodserver;Database=MyDB;..." --output data.xlsx
```

**Future consideration:** Support config file or environment variable for default connection string.

## Git Workflow

**Main branch:** `main`
**Remote:** https://github.com/ChrisHamiltonSystems/SqlXl

**Current commits:**
- `8485585` - Initial commit (dotnet tool scaffolding)
- `5159f31` - Add Core business logic and initial command structure

## Common Tasks for Future Sessions

### Start a new session:
1. Read this file: `C:\Dev\SqlXlRepo\claude.md`
2. Check current branch: `git status`
3. Pull latest: `git pull`
4. Check build status: `dotnet build`

### Fix the build errors:
1. Read System.CommandLine v3 preview docs or examples
2. Update `Commands/ExportCommand.cs` and `Commands/ImportCommand.cs`
3. Verify DataService has needed methods or create wrapper methods
4. Test build: `dotnet build`

### Test the tool:
1. Build and pack: `dotnet pack`
2. Install locally: `dotnet tool install --global --add-source ./bin/Release SqlXl`
3. Run: `sqlxl export --feature 3 --output test.xlsx`
4. Verify Excel file generated
5. Import test: `sqlxl import --file test.xlsx --feature 3`

## Links

- **GitHub Repo:** https://github.com/ChrisHamiltonSystems/SqlXl
- **SlappFramework (parent project):** https://github.com/ChrisHamiltonSystems/SlappFramework
- **NuGet (future):** https://www.nuget.org/packages/SqlXl (not published yet)

---

**Last Updated:** 2026-03-05
**Version:** 0.1.0 (pre-release, not published)
**Status:** Foundation complete, commands need API fixes
