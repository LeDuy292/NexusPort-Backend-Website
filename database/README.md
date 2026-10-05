# NexusPort database changes

## Current model

- `containers` stores the unique physical identity (`container_no`).
- `container_visits` stores each planned or actual port occurrence. The same
  Container may return many times, so it may have many visits.
- `booking_containers` is the Booking-to-Container junction. The same Container
  may participate in many Bookings over time.
- `eir_transactions` is the immutable operational snapshot for a visit. EIR is
  outside NXP-038 phase 1.
- Transport-company declarations are only Booking input. They do not wait for
  port reconciliation and have `container_visits.data_status = NULL`.

## Migration order

Apply the base NexusPort schema first, then every file below in order:

1. `migrations/20260925_001_tien_sa_eir.sql`
2. `migrations/20261002_002_container_intake_import.sql`
3. `migrations/20261003_003_container_requested_service_date.sql`
4. `migrations/20261003_004_transport_declaration_without_review.sql`

Each migration records its version in `nexusport_schema_versions`. Do not edit a
migration after it has been shared or applied; create the next numbered file.

## Supporting files

- `verify/20260925_verify_tien_sa_eir.sql` contains post-migration checks.
- `samples/20260925_tien_sa_eir_sample.sql` is optional development/acceptance
  data based on Tiên Sa EIR examples. Do not run it in production.
- Frontend Excel templates are stored in `public/templates/` of the frontend
  repository and must remain aligned with the Node import mapping.

## NXP-038 API scope

Node provides manual and Excel intake for port/vessel and transport-company
sources, import history, Container CRUD/search/detail, and visit-aware data.
Booking validation/approval remains in the Booking workflow; declaration itself
does not create or approve a NexusPort Booking.
