# Data Packs — Startup-Time SQL Scripts

Data packs are version-controlled `.sql` scripts that the **WebApi runs against per-environment Bravo databases at startup**. Use them to install/refresh stored procedures, seed reference data, or run cleanup before the first test of the day.

> **Destructive by default.** Data-pack execution intentionally bypasses the
> `SqlGuardrails` denylist (so it can run `CREATE/ALTER PROCEDURE`, `EXEC`,
> unbounded `DELETE`). Opt-in is **per environment**, never global. Never
> point a data-pack-enabled env at a production database.

---

## Folder layout

Scripts live at the solution root under `data/datapacks/`:

```
data/datapacks/
├── datateardown/                  ← phase folder (runs FIRST at startup)
│   ├── tesla-retail/              ← env key (matches TestEnvironment.Environments)
│   │   ├── 1.stored procedures/
│   │   │   ├── 01.usp_RestoreAccountDataFromBackup.sql
│   │   │   └── 02.usp_PurgeStaleEvents.sql
│   │   └── 2.data cleanup scripts/
│   │       └── 01.delete_test_accounts.sql
│   └── sumo-retail/
│       └── 1.stored procedures/
│           └── 01.usp_RestoreAccountDataFromBackup.sql
└── datapreparation/               ← phase folder (runs SECOND)
    └── tesla-retail/
        └── 1.seed/
            └── 01.seed_reference_meters.sql
```

Rules:

- **Phases (`datateardown` / `datapreparation`)** are fixed. `datateardown` runs first so installed procs are available to preparation scripts that `EXEC` them.
- **Env folder name** must match an entry under `TestEnvironment.Environments` in `appsettings.json` (case-insensitive). Folders that don't match are warn-logged and skipped.
- **Subfolder + script names** sort by their leading numeric prefix (`^\d+\.`). `2.foo` runs before `10.bar`. Names without a prefix sort to the end with a warning.
- **Only `.sql` files** at the **top level** of each subfolder are executed. Nested `.sql` files inside a subfolder are ignored.
- **Folder names with spaces are fine** (`1.stored procedures/` works as-is).

---

## Authoring a script

### Re-runnability is your responsibility

The runner executes scripts on **every** WebApi startup. Authors must make scripts idempotent.

| Want to... | Use this T-SQL |
|---|---|
| Install/update a stored proc | `CREATE OR ALTER PROCEDURE ...` (SQL Server 2016 SP1+) |
| Replace a function/view | `CREATE OR ALTER FUNCTION ...` / `CREATE OR ALTER VIEW ...` |
| Seed a row idempotently | `IF NOT EXISTS (SELECT 1 FROM ... WHERE ...) INSERT ...` or `MERGE` |
| Delete test data | `DELETE FROM ... WHERE <a precise predicate>` |

A non-idempotent `CREATE PROCEDURE` will fail on the second start with "There is already an object named...". The runner will log the error, mark the script `Failed`, and **abort the rest of that env's scripts**.

### Batches and `GO`

The runner splits each `.sql` file on standalone `GO` lines (the same way `sqlcmd` does) and runs each batch as a separate `SqlCommand`.

```sql
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER PROCEDURE dbo.usp_RestoreAccountDataFromBackup
    @AccountId int
AS
BEGIN
    -- ...
END
GO
```

A `GO` inside a `'...'` string literal, a `--` line comment, or a `/* ... */` block comment is **not** treated as a separator. `sqlcmd`-only features (`:r`, `:setvar`, `GO 5`) are not supported — keep scripts to plain T-SQL.

### Per-batch autocommit

There is **no outer transaction** around the script — each batch runs in its own autocommit. This mirrors `sqlcmd` and avoids DDL-in-transaction quirks. If you need atomicity for a specific operation, wrap it explicitly:

```sql
BEGIN TRAN;
    DELETE FROM dbo.Foo WHERE TestRun = 'X';
    DELETE FROM dbo.Bar WHERE TestRun = 'X';
COMMIT;
GO
```

### No token substitution

Unlike per-test-set teardown (`BravoTeardownExecutor`), data-pack scripts do **not** support `{{Token}}` placeholders. Env-scoping is done by parent folder — there is no per-script env injection. If you need a value to differ between envs, write that value into the script in **that env's folder** (or factor common code into a procedure that takes parameters).

### Connection string

Each env's scripts run against the connection string in
`TestEnvironment.Environments.<envKey>.BravoDbConnectionString`. Falls back to
`TestEnvironment.AseXml.BravoDb.ConnectionString` when the env-block omits it.

### Calling installed procs from per-test-set teardown

Once a script in `datateardown/<env>/.../` installs a stored proc, you can invoke it from the per-test-set **Data Teardown (SQL)** panel on the test set detail page:

```sql
EXEC dbo.usp_RestoreAccountDataFromBackup @AccountId = {{AccountId}};
```

`SqlGuardrails` allows `EXEC` of any proc whose name starts with one of `TestEnvironment.TeardownExecAllowedPrefixes` (default `["usp_"]`). Naming installed procs `usp_*` is the standard convention. `{{Token}}` substitution and the rest of the teardown safety stack still apply.

See `docs/functional.md` *Data Teardown (SQL) → Guardrails* for the full rules.

---

## Opting an environment in

Per-env opt-in only — there is intentionally no global toggle.

```jsonc
{
  "TestEnvironment": {
    "DataPacksPath": "datapacks",          // bin-relative root (default "datapacks")
    "Environments": {
      "tesla-retail": {
        "BravoDbConnectionString": "Server=...;Database=...;...",
        "RunDataPacksOnStartup": true       // ← required for scripts to execute
      },
      "sumo-retail": {
        "BravoDbConnectionString": "Server=...;Database=...;..."
        // RunDataPacksOnStartup omitted → defaults to false → skipped
      }
    }
  }
}
```

In Docker, set via environment variable:
```
AITESTCREW_TestEnvironment__Environments__tesla-retail__RunDataPacksOnStartup=true
```

---

## Execution order at startup

```
For each phase in [datateardown, datapreparation]:
    For each env folder under data/datapacks/<phase>/:
        Skip if env not in TestEnvironment.Environments        (warn-log)
        Skip if RunDataPacksOnStartup != true                  (info-log)
        Skip if BravoDbConnectionString is empty               (info-log)
        Open one SqlConnection (reused across phases)
        For each numbered subfolder (numeric-prefix sort):
            For each *.sql file (numeric-prefix sort):
                Read file, split on GO, run each batch
                On failure: mark script Failed, abort remaining
                            scripts for THIS env, move on to next env
        Close connection
Log final summary, store report for /api/data-packs/startup-report
```

Envs run **sequentially**. Other envs continue even if one env aborts on a failed script.

---

## Verifying it ran — the dashboard panel

Open the WebApi dashboard (`http://<host>:5050`) and navigate to the **System Health** page (`/system`). The **Startup Data Packs** panel shows:

| Status pill | Meaning | What to do |
|---|---|---|
| **Ran** (green) | Connection opened, scripts executed | Expand the row to see each script's ✓/✗/— and elapsed ms |
| **Skipped (not configured)** (amber) | Env folder exists on disk but no matching env in `Environments` | Add the env to `appsettings.json` or rename/remove the folder |
| **Skipped (opt-out)** (grey) | `RunDataPacksOnStartup` is false | Set it to `true` for that env |
| **Skipped (no DB conn)** (amber) | `BravoDbConnectionString` is empty | Set the connection string (per-env or top-level fallback) |
| **Connection failed** (red) | `SqlConnection.OpenAsync` threw | Hover the row to see the SQL exception verbatim |

A red `✗` against a script row shows the SQL error verbatim — copy/paste into your DB tool to debug. Subsequent scripts in that env show `—` ("Skipped because an earlier script in this env failed").

The panel also shows the resolved root path (e.g. `C:\app\datapacks\`). If it says `(not found on disk)`, the deployment is missing the data folder — see [Packaging](#packaging).

If the panel shows **"Could not reach /api/data-packs/startup-report"**, the deployed WebApi pre-dates this feature — rebuild and redeploy.

---

## Logs (alternative to the panel)

Grep the WebApi container's stdout:

```bash
docker logs <container> 2>&1 | grep -E "DataPackRunner|DATAPACKS|datapacks:"
```

Key lines:
- `DataPackRunner: scanning '/app/datapacks' for envs...` — startup banner
- `DATAPACKS: about to execute N script(s) against env=...` — destructive-action banner (WARN)
- `datapacks: env=X phase=Y script=... batches=N elapsedMs=M` — one per successful script
- `DataPackRunner: env=X phase=Y script=... batch#0 failed — <error>` — script failure
- `DataPackRunner finished: envsConsidered=... envsRan=... scripts=... failures=...` — summary

---

## Packaging

The MSBuild rule in `src/AiTestCrew.WebApi/AiTestCrew.WebApi.csproj` copies `data/datapacks/**/*.sql` into `bin/datapacks/` at every build:

```xml
<Content Include="..\..\data\datapacks\**\*.sql"
         Link="datapacks\%(RecursiveDir)%(Filename)%(Extension)"
         CopyToOutputDirectory="PreserveNewest" />
```

This applies automatically to:
- `dotnet build` (used by `build-all.ps1`)
- `dotnet publish` (used by `publish.ps1`)

The Docker build context is restricted to `src/`, `templates/`, and `data/datapacks/` — the Dockerfile explicitly `COPY`s the data folder so the in-container `dotnet publish` can find it.

> **Adding a new env or phase folder requires a rebuild + redeploy** for it to land in `bin/datapacks/`. Editing the appsettings opt-in flag does not — it's read at WebApi startup.

---

## Limitations and out-of-scope

- **Runner CLI does not run data packs.** Only the WebApi at startup. (Adding a `--run-data-packs` flag would be a separate v2.)
- **No `sqlcmd` features** — no `:r`, `:setvar`, `GO 5`.
- **No transaction across batches** — each batch is autocommitted independently.
- **No `{{Token}}` substitution** — scripts are static SQL.
- **No retry** — a failed script aborts the env. Fix the script and restart the WebApi.
- **No multi-line string awareness in the GO splitter** — a `'...'` literal that spans multiple lines and contains a standalone `GO` line will be incorrectly split (extremely rare in practice).
