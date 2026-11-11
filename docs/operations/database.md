# Database, backups, and upgrades

## Isolation

One database holds one event. Local demo and non-demo SQLite files are separate.
Hosted staging uses `EventWallStaging`; production uses `EventWallProd`.
`Database__Purpose` validates those names and must agree with the environment.
The name guard is deliberate protection for destructive tooling, not a branding setting.

The published migration chain includes its initial baseline and subsequent
incremental migrations. New databases apply all of them. Do not remove historical
migrations or copy a database from an unrelated/older private development baseline.

## Initial setup and upgrades

Use `tools/EventWall.DatabaseSetup` with a privileged deployment identity:

```powershell
dotnet run --project tools\EventWall.DatabaseSetup --configuration Release
```

Provide the process settings from the [Azure guide](../deployment/azure.md).
The tool loads environment configuration, applies migrations, seeds the configured
event, creates/updates the three contained runtime users, and checks their grants.
Reuse the runtime passwords already stored in the apps unless intentionally
coordinating a rotation.

For each release:

1. Review migrations and compatibility with the currently running package.
2. Back up and rehearse the upgrade against a staging copy with appropriate privacy controls.
3. Schedule downtime/stop writers if required by the change.
4. Apply migrations with the deployment identity and rerun runtime grants.
5. Deploy matching Admin, Public, and Submission packages.
6. Verify moderation, intake, wall reads, and connector state before reopening.

Hosted Admin defaults to `Database__ApplyMigrations=false` and fails when pending
migrations remain. It must not be granted DDL permissions to get around that check.
Development Admin applies migrations automatically.

## Backups

Azure SQL has managed backup capabilities, but confirm your actual point-in-time
retention, redundancy, restore permissions, region support, and recovery objectives.
Configure longer retention if needed. A backup that has never been restored is
not a validated recovery plan.

Back up **both database and Admin Data Protection keys**. Twitch's rotated refresh
token cannot be decrypted without the key ring. Submission has a separate key
ring for protected browser state/tokens. Keep keys outside cleaned deployment
directories and protect backups as credentials.

For local SQLite, stop all processes using the database before copying it, or use
a proper SQLite backup operation. Do not copy only the main file during active
WAL writes and assume it is consistent.

## Restore and rollback

Rehearse restoring into a separately named recovery database under a controlled
environment. The production database-name guard means a restore under a new name
cannot simply be pointed at the existing Production apps: plan a controlled
cutover/rename or reviewed guard-compatible recovery process.

Restore keys and matching application versions as needed. Check retained
checkpoints, provider credentials, X usage accounting, and moderator sessions.
Restoring an old database can also restore old spend counters; pause collection
until that risk is reconciled with provider billing.

Application rollback does not automatically roll back the schema. Prefer
backward-compatible additive changes. Only deploy an older artifact if it remains
compatible with the current schema; otherwise follow the tested recovery plan.

## Credential rotation

Rotate one environment at a time:

1. Pause affected writers/collectors.
2. Supply new contained-user passwords to the setup tool for that database.
3. Update matching role connection strings/Bicep secret inputs without logging them.
4. Restart and verify all roles.
5. Update the protected source of truth used for future deployments.

Owner passcode rotation invalidates staff sessions. Moderator access-code rotation
is separate. Losing the Data Protection key ring may require provider reauthorization,
not merely replacing a connection string.

## Demo reset is destructive

Local: `.\tools\demo\start-demo.ps1 -Reset` deletes only the demo SQLite database.
Stop other users of that database first.

Hosted: use `tools/azure/reset-demo.ps1` only against disposable demo data,
with explicit resource group and all three staging app names. It validates each
connection targets `EventWallStaging`, asks for confirmation, stops the apps,
clears/reseeds demo content, and restores their prior running state. It preserves
provider credentials and usage accounting; it is not a full database recreation.

The guard cannot know whether you put real data in staging. **Never reset a
staging database containing attendee data you need to retain.** Ordinary upgrades,
deployments, and source changes do not require any reset.

## Retention

No automatic retention/purge scheduler is included. Establish a policy for posts,
identities, audit history, exports, backups, and provider tokens, then implement an
approved, tested maintenance procedure. Avoid ad-hoc deletion of relational rows
without understanding audit/checkpoint dependencies.
