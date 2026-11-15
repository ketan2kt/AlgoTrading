# Backup and restore runbook

Verified 15 November 2026. Ketan requested **no new paid resources**. No production database,
blob, connection string, broker account, key ring or recording owner was modified by this drill.

## Actual Azure inventory (read-only)

- PostgreSQL `sarthitradingpg5amvyarepxaci`: Ready, PostgreSQL 16, private VNet only,
  Standard_B1ms, seven-day retention, geo-redundant backup disabled.
- Seven automatic full backups were listed for 8–14 November. Most recent completed
  **14 November 15:37:14 UTC / 21:07:14 IST**. Earliest advertised restore point:
  **8 November 15:32:53 UTC**. These timestamps are inventory evidence, not a restore test.
- `sarthireplay2026`: versioning enabled, blob soft delete 14 days, container soft delete
  14 days. Shared-key access remains disabled. Blob point-in-time restore policy is not enabled.
- Database PITR requires a new server; it does not restore over the original. No paid restore
  was performed because the user declined temporary paid resources. Do not label that gate passed.
- LRS and local-region database backups do not establish cross-region disaster recovery.

Read-only inventory commands:

```powershell
az postgres flexible-server show -g rg-sarthi-trading-centralindia -n sarthitradingpg5amvyarepxaci --query '{state:state,backup:backup,network:network,version:version}'
az postgres flexible-server backup list -g rg-sarthi-trading-centralindia -s sarthitradingpg5amvyarepxaci --query '[].{completed:completedTime,type:backupType}'
az storage account blob-service-properties show -g rg-sarthi-trading-centralindia --account-name sarthireplay2026 --query '{versioning:isVersioningEnabled,blob:deleteRetentionPolicy,container:containerDeleteRetentionPolicy}'
```

## Repeatable no-cost database/key-ring drill

`scripts/test-backup-restore.ps1` creates a unique disposable **loopback-only** PostgreSQL
cluster with synthetic data. It never accepts a production connection string or starts the
web application, trading background workers or broker clients. It uses a dedicated drill
Data Protection application name and fake token, not production credentials.

```powershell
# Use the repository's .NET runtime on PATH and installed PostgreSQL binaries.
./scripts/test-backup-restore.ps1 -PostgresBin D:/AlgoTrading/.tools/postgresql-18.6/pgsql/bin
```

The script applies current EF migrations, persists a setting/audit record, encrypts a synthetic
token and records a partial paper fill through the real paper journal. It then takes a custom
`pg_dump`, backs up the key ring, stops/restarts PostgreSQL, restores into a second database with
`pg_restore --exit-on-error --single-transaction`, verifies dump/key checksums, and runs the
application's readers against restored state. It tests missing keys as a negative control.
The server is stopped in `finally`; synthetic evidence is retained in the printed `.tools/recovery-drill-*`
directory. No automatic recursive deletion is performed. Trust authentication is confined to
this local synthetic cluster, never to production. Use a normal authorized local shell; the
restricted Windows sandbox prevented initdb child-process startup in the initial attempt.

Verified components:

Full script passed on 15 November in **35.2 seconds** on the synthetic fixture (not production RTO),
with the local server confirmed stopped. Evidence is retained under
`.tools/recovery-drill-3631eb6a313d4744aed147ea2f1f80c2` in the release worktree.

- Current migration set and critical row counts restored.
- JSON setting contents, audit records and encrypted token recovered with the restored key ring.
- Missing key ring correctly blocks decryption.
- Partial fill reconstructs three filled units of a seven-unit request after restart.
- Resubmitting the same reference creates no second order/event.

The installed drill runtime is PostgreSQL **18.6**, while Azure is **16**. This proves the
application's logical backup/recovery path on the local runtime, not a production-version PITR,
full production-dataset restore, measured production RPO/RTO, or broker-account reconciliation.

## Replay archive recovery

Existing archive tests cover interrupted writes, corrupt shard isolation, source separation,
checkpoint restart and recorder cutover rollback. The dedicated
`RestoredArchivePreservesAllMarketsSourcesDepthAndKnownGaps` regression copies a closed synthetic
archive into a separate directory, verifies SHA-256 for every file, reloads both CE/PE for all
three indexes from Groww and Upstox, checks exact observation equality/depth quantities and
preserves the known gap marker. Strict replay still excludes backup snapshots.

This local archive restoration does not certify a cloud blob undelete/version promotion.
Do not delete, overwrite or deliberately corrupt a real archive to test recovery.

## Real recovery procedure — approval required before mutations

1. Identify the affected scope and capture state. Preserve original database, blobs, logs and
   immutable build/settings identities. Do not start a competing recorder or live worker.
2. Select a documented recovery timestamp inside database retention and matching archive/key-ring
   versions. Confirm the operator has existing appropriate access; do not enable shared keys,
   public database access or broad permissions to work around a denied read.
3. With separate cost approval, restore PostgreSQL to a uniquely named private server. Keep all
   applications pointed at the original; do not start hosted services against the clone.
4. Validate schema, row counts, audit/ledger links, encrypted-state/key-ring compatibility and
   replay manifest/build/quote fingerprints on the clone. Recover archived blobs into a separate
   prefix/cache from the chosen versions first, preserving source provenance and gap markers.
5. Reconcile actual broker positions and order references before any restored live process can
   act. Restoring an old daily-arm/order state is not permission to submit orders again.
6. Only with explicit cutover approval and outside capture, switch the intended host; verify
   single recording ownership, readiness and read-only replay. Retain the original rollback target.
7. Record elapsed time, recovery point, missing interval and verification result. Delete only
   explicitly identified temporary resources after approval; never remove the original backup.

Official process references:
[Azure PostgreSQL backup/restore](https://learn.microsoft.com/en-us/azure/postgresql/backup-restore/concepts-backup-restore),
[blob version recovery](https://learn.microsoft.com/en-us/azure/storage/blobs/versioning-enable),
[soft-delete recovery](https://learn.microsoft.com/en-us/azure/storage/blobs/soft-delete-blob-manage).
