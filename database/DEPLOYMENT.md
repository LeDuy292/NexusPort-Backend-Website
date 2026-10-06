# Deploy NexusPort PostgreSQL to Aiven

This procedure deploys the current local NexusPort database to an empty Aiven
PostgreSQL service. It intentionally excludes files in `database/samples/`.

## Safety rules

- Never commit an Aiven Service URI, password, CA certificate, or generated dump.
- Rotate any credential that has been posted in chat or a screenshot.
- Restore only into an empty target database. The deployment script stops when
  it detects existing public tables.
- Keep the source and target PostgreSQL major versions compatible. The current
  local NexusPort database uses PostgreSQL 18.

## Prerequisites

1. Local `.env` points to the authoritative NexusPort database.
2. PostgreSQL command-line tools (`pg_dump`, `pg_restore`, and `psql`) are
   installed.
3. The Aiven service runs PostgreSQL 18 and its Service URI uses
   `sslmode=require`.

## Backup only

From the backend repository root:

```powershell
.\scripts\deploy-aiven-db.ps1
```

The dump is written outside the repository to `../db-backups/`.

## Backup and restore to Aiven

```powershell
.\scripts\deploy-aiven-db.ps1 -Restore
```

When prompted, paste the Aiven Service URI. Input is masked and the URI is not
written to disk. After the restore, the script verifies
`nexusport_schema_versions`, database size, and the public table count.

## Apply later migrations

Do not restore the complete dump again after the initial deployment. Add a new
additive and idempotent SQL file to `database/migrations/`, using the next name
in chronological order, for example:

```text
20261010_005_add_example_field.sql
```

The migration must run inside a transaction and record `20261010_005` in
`nexusport_schema_versions`. Then apply every migration that is missing from the
deployed database:

```powershell
.\scripts\apply-db-migrations.ps1
```

The script reads migration files in filename order, skips versions already
recorded, stops on the first error, and verifies the version row after every
successful migration. It never runs files in `database/samples/`.

## Reset development data to default accounts

This is a destructive maintenance operation, not a migration. It creates a
restorable Aiven backup first, empties every application table while preserving
`nexusport_schema_versions`, then recreates only the seven accounts declared in
`src/scripts/seed.js`. It also creates the single carrier and `carrier_users`
link required for `carrier01` to work.

```powershell
.\scripts\reset-aiven-data.ps1 -ConfirmReset
```

Do not add this command to CI/CD or normal application startup. Use it only for
an explicitly approved environment reset. The SQL lives in
`database/maintenance/`, outside the ordered migration directory.

## Application connection values

Use deployment-platform secrets rather than committed configuration files.

Node.js:

```text
DATABASE_URL=<Aiven Service URI>
```

.NET:

```text
ConnectionStrings__DefaultConnection=Host=<host>;Port=<port>;Database=<database>;Username=<user>;Password=<password>;SSL Mode=Require;Maximum Pool Size=8
```

Keep the Node.js pool at no more than 5 connections. Aiven Free PostgreSQL has
a 20-connection limit, so this leaves capacity for the .NET API and maintenance.

## Run local backends against Aiven

Keep all Aiven credentials only in ignored `.env` files. In the repository root,
set `DB_TARGET=aiven`; the legacy Node backend then reads the `AIVEN_DB_*`
values. In `node-core/.env`, use the Aiven `DATABASE_URL` with
`uselibpqcompat=true&sslmode=require` and set `DB_POOL_MAX=5`.

Run the C# API through the local launcher so its connection string is assembled
from the root `.env` without writing credentials to tracked appsettings files:

```powershell
.\scripts\run-csharp-aiven.ps1
```

The launcher sets `Maximum Pool Size=8`. Together, the two Node pools and the
C# pool must stay within Aiven Free PostgreSQL's 20-connection limit.
