# Database agent instructions

Read `database/README.md` before changing database-backed code.

1. Inspect `nexusport_schema_versions` and apply every missing file in
   `database/migrations/` in filename order before running or testing Node code.
2. Never edit an applied migration. Add a new additive, idempotent migration.
3. Never run files in `database/samples/` unless development sample data is
   explicitly wanted. Never run them in production.
4. Keep `containers` as physical master data. Store each operational occurrence
   in `container_visits`; one Container may have many visits and many Bookings.
5. Transport-company declarations are input for Booking, not a port approval
   workflow. Their `data_status` must remain `NULL`.
6. NXP-038 work belongs to Node, frontend, and additive SQL migrations. Do not
   modify C# files unless the user explicitly changes this project constraint.
7. After a migration, verify its row in `nexusport_schema_versions`, run Node
   tests/build, and report the migration that was applied.
