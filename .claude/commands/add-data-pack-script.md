Scaffold a new startup data-pack SQL script — for installing/refreshing stored procedures, seeding reference data, or running cleanup against a per-environment Bravo database every time the WebApi starts.

Arguments: $ARGUMENTS
Expected forms (any order):
- `<envKey> <phase> <category> "<filename>"` — e.g. `tesla-retail datateardown stored-procedures "usp_PurgeStaleEvents"`
- `<envKey> <phase>` — choose category + filename interactively
- (no args) — ask the user for env, phase, category, filename in order

Where `<phase>` is `datateardown` (runs first; for proc installs and cleanup) or `datapreparation` (runs second; for seeding that may EXEC the just-installed procs).

## Reference first

Read `docs/data-packs.md` end-to-end before creating files. Key invariants:

- **Folder layout is rigid**: `data/datapacks/<phase>/<envKey>/<NN.subfolder>/<NN.script>.sql` — phase, env key, numbered subfolder, numbered script. The runner sorts by leading numeric prefix.
- **Re-runnability is mandatory**: every script runs on EVERY WebApi startup. Use `CREATE OR ALTER PROCEDURE`, `IF NOT EXISTS ... INSERT`, `MERGE`, or precise-predicate `DELETE`. A non-idempotent `CREATE PROCEDURE` will fail on the second start and abort the env's remaining scripts.
- **No `{{Token}}` substitution**: env-scoping is by parent folder. If a value differs between envs, write it literally in that env's folder.
- **No `sqlcmd` features**: no `:r`, `:setvar`, `GO 5`. Plain T-SQL only.
- **Per-batch autocommit**: there is no outer transaction. Use `BEGIN TRAN ... COMMIT` inside a single batch when you need atomicity.
- **Per-env opt-in only**: scripts only run when `Environments.<envKey>.RunDataPacksOnStartup: true` AND `BravoDbConnectionString` is set. There is intentionally no global toggle.

## Step 1 — Confirm the env exists in config

The folder name must match a key under `TestEnvironment.Environments` in `appsettings.json` (case-insensitive). Folders without a matching env are skipped with a WARN log.

If the user specifies an env not in their `appsettings.json`, point this out and confirm whether they want to:
1. Add the env to `appsettings.json` first (recommended — see `docs/deployment.md` *Multi-Environment Setup*), then proceed
2. Create the folder anyway and accept that scripts won't run until the env is configured

Do NOT silently create folders for unknown envs.

## Step 2 — Pick the phase

Both phases share the same internal structure. The choice signals intent:

| Phase | Typical contents |
|---|---|
| `datateardown` | `CREATE OR ALTER PROCEDURE`, function/view installs, blanket `DELETE FROM ... WHERE ...` cleanup |
| `datapreparation` | Seeding reference data; `EXEC` of procs installed by `datateardown`; `MERGE` for idempotent inserts |

`datateardown` runs first so installed procs are available to preparation scripts that EXEC them. If the user is unsure, default to `datateardown`.

## Step 3 — Pick the category subfolder

Look at existing subfolders under `data/datapacks/<phase>/<envKey>/` first — reuse a category if one fits.

Common conventions:
- `1.stored procedures/` — proc/function/view DDL
- `2.data cleanup scripts/` — DELETE, TRUNCATE-equivalent operations
- `3.seed/` — INSERT/MERGE for reference data (typically in `datapreparation`)

If creating a new category, use the next available numeric prefix and a short kebab/space-separated name. Spaces in folder names are fine — the MSBuild Content rule preserves them.

## Step 4 — Pick the script filename

`NN.descriptive-name.sql` where `NN` is the next available numeric prefix in that subfolder. Sort is by integer value of the prefix (`02` < `10`, not lexical).

If the script installs or modifies a single proc, name the file after the proc: `01.usp_RestoreAccountDataFromBackup.sql`. For data ops, use a verb-noun shape: `02.delete_test_accounts.sql`.

## Step 5 — Write the script body

Match the script's intent to one of the templates below and adapt.

### Template A — install/refresh a stored procedure

```sql
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.usp_RestoreAccountDataFromBackup
    @AccountId int,
    @AsOf datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Body here. Reference tables exist in the Bravo DB this env points at.
    SELECT 1 AS placeholder;
END
GO
```

`CREATE OR ALTER` requires SQL Server 2016 SP1 or later. If targeting older servers, fall back to:

```sql
IF OBJECT_ID('dbo.usp_X', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_X;
GO
CREATE PROCEDURE dbo.usp_X AS BEGIN ... END
GO
```

### Template B — idempotent reference-data seed

```sql
MERGE dbo.LookupReason AS target
USING (VALUES
    ('TEST-REASON-A', 'For automated test scenarios A'),
    ('TEST-REASON-B', 'For automated test scenarios B')
) AS source (Code, Description)
ON target.Code = source.Code
WHEN NOT MATCHED THEN
    INSERT (Code, Description) VALUES (source.Code, source.Description)
WHEN MATCHED AND target.Description <> source.Description THEN
    UPDATE SET target.Description = source.Description;
GO
```

Or for a single row:

```sql
IF NOT EXISTS (SELECT 1 FROM dbo.Setting WHERE Key = 'TestMode')
    INSERT INTO dbo.Setting (Key, Value) VALUES ('TestMode', 'true');
GO
```

### Template C — bounded cleanup

```sql
-- Delete only test-flagged rows so production-like data is untouched.
DELETE FROM dbo.TestAccount WHERE CreatedBy = 'AITestCrew';
GO

DELETE FROM dbo.TestEvent WHERE CorrelationId LIKE 'AITC-%';
GO
```

Avoid `TRUNCATE TABLE` — it bypasses triggers and can silently break invariants. Use `DELETE` with a `WHERE`.

### Template D — call an installed proc

(Goes in `datapreparation` after the proc is installed in `datateardown`.)

```sql
DECLARE @AccountId int = 100123;
EXEC dbo.usp_RestoreAccountDataFromBackup @AccountId = @AccountId;
GO
```

## Step 6 — Confirm packaging is wired

Run `dotnet build src/AiTestCrew.WebApi/AiTestCrew.WebApi.csproj` and verify the file lands in `bin/`:

```
src/AiTestCrew.WebApi/bin/Debug/net8.0-windows*/datapacks/<phase>/<envKey>/<NN.subfolder>/<NN.script>.sql
```

If it's missing, the MSBuild Content Include in `AiTestCrew.WebApi.csproj` is broken or the file path doesn't match `..\..\data\datapacks\**\*.sql`. The user should not need to add anything to the csproj — the wildcard already covers any file under `data/datapacks/`.

## Step 7 — Confirm the env is opted in

Read the user's deployed `appsettings.json` (or remind them to update it):

```jsonc
"Environments": {
  "<envKey>": {
    "BravoDbConnectionString": "Server=...;Database=...;...",
    "RunDataPacksOnStartup": true
  }
}
```

Without `RunDataPacksOnStartup: true`, the dashboard panel will show "Skipped (opt-out)" for that env and the script will never run.

## Step 8 — Tell the user how to verify

Restart the WebApi (or `docker compose up -d` after `docker compose build`), then open the dashboard. The **Startup Data Packs** panel above the modules grid should show:

- The env in the list with status pill `Ran`
- An expandable row containing the new script with a green ✓
- Batch count + elapsed ms

If the panel shows ✗ with a red error message, copy the verbatim SQL exception into the user's DB tool to debug. The runner aborts the env's remaining scripts on first failure — fix the script and restart.

For deeper troubleshooting (`Skipped (no DB conn)`, `Connection failed`, etc.), see the troubleshooting table in `docs/data-packs.md`.

## What you should NOT do

- Do not modify `DataPackRunner.cs`, `DataPackRegistry.cs`, or `SqlBatchSplitter.cs` — adding a script is content-only.
- Do not modify `WebApi.csproj` — the Content Include wildcard already covers any new `.sql` file.
- Do not introduce `{{Token}}` placeholders — the runner does not substitute them. Hardcode env-specific values in env-specific folders.
- Do not split a logical operation across multiple scripts in the same env — within an env, a failure aborts the rest, so chaining is fragile. Keep one logical operation per script.
- Do not write a script that depends on the env containing pre-existing test data — scripts run on a fresh restart, often after a deploy, so any test data may not be present yet.
