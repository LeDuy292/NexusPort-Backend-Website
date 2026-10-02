# Tiên Sa EIR database update

This database-only change keeps the original schema compatible and adds the
missing historical boundary between a physical container and each port visit/EIR.
No application code is changed by this package.

## Files

- `migrations/20260925_001_tien_sa_eir.sql`: production-safe additive migration.
- `samples/20260925_tien_sa_eir_sample.sql`: optional acceptance data based on the
  two supplied Tiên Sa receipts. Do not run it on production unless sample data is
  explicitly wanted.
- `verify/20260925_verify_tien_sa_eir.sql`: post-migration checks.

## Apply order

1. Back up the target PostgreSQL database.
2. Apply the existing `nexusport.sql` only when creating a new database.
3. Apply `migrations/20260925_001_tien_sa_eir.sql` to both new and existing databases.
4. On a development database only, optionally apply the sample file.
5. Run the verification file. Every query should return zero invalid rows and the
   sample projection should show two EIR rows when the optional sample was loaded.

## Compatibility policy

The legacy `containers.seal_no`, `gross_weight_kg`, `vessel_call_id`, `arrived_at`
and `left_at` columns are intentionally retained. Existing code can continue to
read them during the later application migration. New EIR-aware code must treat
`container_visits`, `eir_transactions` and `eir_seals` as authoritative history.

Media files are not stored in PostgreSQL. `eir_media` stores durable S3 bucket/object
keys (not expiring presigned URLs) plus camera, checksum, capture stage, subject and AI metadata, so vehicle
approach/departure video and container/seal/damage photographs can be added later
without another structural redesign.
