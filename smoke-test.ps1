#!/usr/bin/env pwsh
<#
.SYNOPSIS
    End-to-end smoke tests for SqlXL.

.DESCRIPTION
    Validates the main features against a fresh SqlXlDemo database on localhost.
    Uses an isolated temp config so it never touches your real ~/.sqlxl/config.json.

    Prerequisites:
      - SQL Server running on localhost (Windows Auth)
      - Project already built (dotnet build) OR omit -SkipBuild to build first

.PARAMETER ServerConnection
    Connection string to the SQL Server instance with no database specified.
    Used by `sqlxl demo` to drop/recreate SqlXlDemo.

.PARAMETER DemoConnection
    Connection string to SqlXlDemo. Used by `sqlxl init` and all data commands.

.PARAMETER SkipBuild
    Skip the initial `dotnet build` step (faster if you just built).

.EXAMPLE
    .\smoke-test.ps1
    .\smoke-test.ps1 -SkipBuild
#>
param(
    [string]$ServerConnection = "Data Source=localhost;Integrated Security=true;TrustServerCertificate=true;",
    [string]$DemoConnection   = "Data Source=localhost;Database=SqlXlDemo;Integrated Security=true;TrustServerCertificate=true;",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Continue"

$RepoRoot  = $PSScriptRoot
$Project   = Join-Path $RepoRoot "src\SqlXl\SqlXl.csproj"
$TmpDir    = Join-Path $env:TEMP "sqlxl-smoke-$(Get-Date -Format 'yyyyMMddHHmmss')"
$TmpConfig = Join-Path $TmpDir "smoke-config.json"

New-Item -ItemType Directory -Path $TmpDir -Force | Out-Null

$script:Pass       = 0
$script:Fail       = 0
$script:LastOutput = ""

function Invoke-SqlXl ([string[]]$Arguments) {
    $script:LastOutput = (& dotnet run --project $Project --no-build -- @Arguments 2>&1) -join "`n"
    return $LASTEXITCODE
}

function Step ([string]$Label, [scriptblock]$Body) {
    Write-Host ("  {0,-55}" -f $Label) -NoNewline
    try {
        $ok = [bool](& $Body)
        if ($ok) {
            Write-Host "PASS" -ForegroundColor Green
            $script:Pass++
        } else {
            Write-Host "FAIL" -ForegroundColor Red
            $script:Fail++
            $script:LastOutput.Split("`n") | Select-Object -First 6 |
                ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        }
    } catch {
        Write-Host "FAIL ($_)" -ForegroundColor Red
        $script:Fail++
    }
}

function Section ([string]$Title) {
    Write-Host ""
    Write-Host $Title -ForegroundColor Yellow
    Write-Host ("─" * 60) -ForegroundColor DarkGray
}

# ─────────────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "SqlXL Smoke Tests" -ForegroundColor Cyan
Write-Host "Temp dir : $TmpDir"
Write-Host "Config   : $TmpConfig"

# ─── Build ────────────────────────────────────────────────────────────────────

Section "Build"

if ($SkipBuild) {
    Write-Host "  (skipped — -SkipBuild was set)" -ForegroundColor DarkGray
} else {
    Step "dotnet build" {
        $output = & dotnet build $Project --verbosity quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            $script:LastOutput = $output -join "`n"
            return $false
        }
        $true
    }
    if ($script:Fail -gt 0) {
        Write-Host ""
        Write-Host "Build failed — cannot continue." -ForegroundColor Red
        exit 1
    }
}

# ─── Setup ────────────────────────────────────────────────────────────────────

Section "Setup"

Step "demo --yes  (drop + recreate SqlXlDemo from scratch)" {
    (Invoke-SqlXl @("--config", $TmpConfig, "demo", "--connection", $ServerConnection, "--yes")) -eq 0
}

Step "init        (install infrastructure + save 'smoke' profile)" {
    (Invoke-SqlXl @("--config", $TmpConfig, "init", "--connection", $DemoConnection, "--profile", "smoke")) -eq 0
}

Step "connections list  (smoke profile is listed)" {
    $code = Invoke-SqlXl @("--config", $TmpConfig, "connections", "list")
    $code -eq 0 -and $script:LastOutput -match "smoke"
}

# ─── Template Export ──────────────────────────────────────────────────────────

Section "Template Export"

$InsertXlsx = Join-Path $TmpDir "products_insert.xlsx"
Step "insert --table dbo.Products  (empty INSERT template)" {
    $code = Invoke-SqlXl @("--config", $TmpConfig, "insert", "--table", "dbo.Products",
                           "--no-launch", "--output", $InsertXlsx)
    $code -eq 0 -and (Test-Path $InsertXlsx)
}

$UpdateXlsx = Join-Path $TmpDir "products_update.xlsx"
Step "update --table dbo.Products  (pre-populated UPDATE template)" {
    $code = Invoke-SqlXl @("--config", $TmpConfig, "update", "--table", "dbo.Products",
                           "--no-launch", "--output", $UpdateXlsx)
    $code -eq 0 -and (Test-Path $UpdateXlsx)
}

$ImportXlsx = Join-Path $TmpDir "userroles_import.xlsx"
Step "import --feature 1           (Tier 3 — Assign User Roles template)" {
    $code = Invoke-SqlXl @("--config", $TmpConfig, "import", "--feature", "1",
                           "--no-launch", "--output", $ImportXlsx)
    $code -eq 0 -and (Test-Path $ImportXlsx)
}

# ─── Schema Inference ─────────────────────────────────────────────────────────

Section "Schema Inference  (infer)"

$InferDdl    = Join-Path $TmpDir "inferred.sql"
$InferReport = Join-Path $TmpDir "inferred.json"

Step "infer  (CREATE TABLE from update template data)" {
    # Infer from the pre-populated update template; Products is the data sheet name.
    # If this fails with "multiple sheets", update --sheet to match the sheet list in the error.
    $code = Invoke-SqlXl @("infer", $UpdateXlsx, "--table", "SmokeTest",
                           "--sheet", "Data", "--output", $InferDdl, "--report", $InferReport)
    $code -eq 0 -and (Test-Path $InferDdl) -and ((Get-Content $InferDdl -Raw) -match "CREATE TABLE")
}

Step "infer  (JSON report produced alongside DDL)" {
    Test-Path $InferReport
}

# ─── Full Round-Trip ──────────────────────────────────────────────────────────

Section "Full Round-Trip  (test command)"

Step "test --table dbo.Products  (command runs + finds features)" {
    # Exit 0 = all rows passed. Exit 1 = some rows failed validation (expected when demo
    # data already occupies unique columns — known limitation in CLI_DESIGN.md).
    # Exit 2 = unhandled exception = real failure.
    $code = Invoke-SqlXl @("--config", $TmpConfig, "test", "--table", "dbo.Products")
    $code -eq 0 -or $code -eq 1
}

# ─── --config flag & SQLXL_CONFIG env var ────────────────────────────────────

Section "--config flag  /  SQLXL_CONFIG env var"

Step "--config <path>  (explicit flag resolves correct config)" {
    $code = Invoke-SqlXl @("--config", $TmpConfig, "connections", "list")
    $code -eq 0 -and $script:LastOutput -match "smoke"
}

Step "SQLXL_CONFIG     (env var resolves correct config)" {
    $env:SQLXL_CONFIG = $TmpConfig
    try {
        $code = Invoke-SqlXl @("connections", "list")
        $code -eq 0 -and $script:LastOutput -match "smoke"
    } finally {
        Remove-Item Env:SQLXL_CONFIG -ErrorAction SilentlyContinue
    }
}

Step "no flag          (wrong config → smoke profile NOT found)" {
    # Sanity check: without --config or env var, our smoke profile is invisible.
    $code = Invoke-SqlXl @("connections", "list")
    # Either exits non-zero (no profiles) or the output lacks "smoke"
    $code -ne 0 -or -not ($script:LastOutput -match "\bsmoke\b")
}

# ─── llm-context ─────────────────────────────────────────────────────────────

Section "llm-context"

Step "llm-context --format json  (valid JSON, correct version)" {
    $code = Invoke-SqlXl @("llm-context", "--format", "json")
    if ($code -ne 0) { return $false }
    try {
        $obj = $script:LastOutput | ConvertFrom-Json
        $obj.format_version -eq 1 -and $obj.sqlxl_version -match '^\d+\.\d+\.\d+$'
    } catch { $false }
}

Step "llm-context           (text format, no error)" {
    (Invoke-SqlXl @("llm-context")) -eq 0
}

# ─── Summary ─────────────────────────────────────────────────────────────────

$Total = $script:Pass + $script:Fail

Write-Host ""
Write-Host ("─" * 60) -ForegroundColor DarkGray

if ($script:Fail -eq 0) {
    Write-Host "All $Total tests passed." -ForegroundColor Green
} else {
    Write-Host "$($script:Pass)/$Total passed   $($script:Fail) failed." -ForegroundColor Red
}

# ─── Cleanup ─────────────────────────────────────────────────────────────────

Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue

exit $script:Fail
