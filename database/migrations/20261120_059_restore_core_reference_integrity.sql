BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '45s';

-- These tables have stable canonical keys. Only remove copies whose entire
-- row matches another row with the same key.
DO $$
DECLARE
    spec record;
    relation regclass;
    validator "char";
    has_duplicates boolean;
    has_conflicts boolean;
BEGIN
    FOR spec IN
        SELECT * FROM (VALUES
            ('carrier_users', 'carrier_id, user_id', NULL),
            ('booking_containers', 'booking_id, container_id', 'trg_validate_changed_link'),
            ('container_declarations', 'id', 'trg_lock_declaration'),
            ('container_visits', 'id', 'trg_protect_visit'),
            ('eir_seals', 'id', 'trg_protect_receipt_child'),
            ('eir_transactions', 'id', 'trg_protect_receipt'),
            ('eport_service_bills', 'id', 'eport_bill_immutable'),
            ('eport_service_order_events', 'id', 'eport_service_order_event_immutable'),
            ('eport_service_payments', 'id', 'eport_service_payment_identity'),
            ('eport_service_registrations', 'id', 'eport_service_registration_immutable'),
            ('nexusport_schema_versions', 'version', NULL)
        ) AS tables(table_name, key_columns, delete_guard)
    LOOP
        relation := format('public.%I', spec.table_name)::regclass;
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', relation);
        EXECUTE format(
            'SELECT count(*) > 0, coalesce(bool_or(variants > 1),false)
             FROM (SELECT count(DISTINCT to_jsonb(row_data)) AS variants
                   FROM %s AS row_data GROUP BY %s HAVING count(*) > 1) AS groups',
            relation, spec.key_columns)
        INTO has_duplicates, has_conflicts;
        IF has_conflicts THEN
            RAISE EXCEPTION 'Conflicting rows in % require explicit reconciliation', relation;
        END IF;

        IF has_duplicates THEN
            -- The ordinary delete guard protects the logical record. An identical
            -- physical copy can be removed while retaining the full record.
            SELECT tgenabled INTO validator FROM pg_trigger
            WHERE tgrelid = relation AND tgname = spec.delete_guard AND NOT tgisinternal;
            IF validator IN ('O', 'R', 'A') THEN
                EXECUTE format('ALTER TABLE %s DISABLE TRIGGER %I', relation, spec.delete_guard);
            END IF;
            EXECUTE format(
                'DELETE FROM %s AS target USING
                 (SELECT ctid, row_number() OVER (PARTITION BY %s ORDER BY ctid) AS duplicate_number FROM %s) AS copies
                 WHERE target.ctid = copies.ctid AND copies.duplicate_number > 1',
                relation, spec.key_columns, relation);
            IF validator IN ('O', 'R', 'A') THEN
                EXECUTE format('ALTER TABLE %s ENABLE %s TRIGGER %I', relation,
                    CASE validator WHEN 'R' THEN 'REPLICA' WHEN 'A' THEN 'ALWAYS' ELSE '' END,
                    spec.delete_guard);
            END IF;
        END IF;

        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = relation AND contype = 'p') THEN
            EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I PRIMARY KEY (%s)',
                relation, spec.table_name || '_pkey', spec.key_columns);
        END IF;
    END LOOP;
END $$;

DO $$
DECLARE
    link record;
    source_relation regclass;
    target_relation regclass;
BEGIN
    FOR link IN
        SELECT * FROM (VALUES
            ('carrier_users', 'carrier_id', 'carriers', 'fk_carrier_users_carrier_id_carriers_id', '', true),
            ('carrier_users', 'user_id', 'users', 'fk_carrier_users_user', '', false),
            ('drivers', 'carrier_id', 'carriers', 'fk_drivers_carrier_id_carriers_id', '', true),
            ('drivers', 'user_id', 'users', 'fk_driver_login_user', 'ON DELETE RESTRICT', false),
            ('trucks', 'carrier_id', 'carriers', 'fk_trucks_carrier_id_carriers_id', '', true),
            ('trucks', 'driver_id', 'drivers', 'fk_trucks_driver_id', 'ON DELETE SET NULL', true),
            ('bookings', 'carrier_id', 'carriers', 'fk_bookings_carrier_id_carriers_id', '', true),
            ('bookings', 'driver_id', 'drivers', 'fk_bookings_driver_id_drivers_id', '', true),
            ('booking_containers', 'booking_id', 'bookings', 'fk_booking_containers_booking_id_bookings_id', '', true),
            ('booking_containers', 'container_id', 'containers', 'fk_booking_containers_container_id_containers_id', '', true),
            ('booking_containers', 'container_visit_id', 'container_visits', 'booking_containers_container_visit_id_fkey', '', true),
            ('booking_containers', 'declaration_id', 'container_declarations', 'booking_containers_declaration_id_fkey', '', true),
            ('shipping_line_accounts', 'user_id', 'users', 'shipping_line_accounts_user_id_fkey', 'ON DELETE CASCADE', true)
        ) AS specs(source_table, source_column, target_table, constraint_name, delete_action, validate_now)
    LOOP
        source_relation := format('public.%I', link.source_table)::regclass;
        target_relation := format('public.%I', link.target_table)::regclass;
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint fk
            JOIN pg_attribute src ON src.attrelid = fk.conrelid AND fk.conkey = ARRAY[src.attnum]
            JOIN pg_attribute dst ON dst.attrelid = fk.confrelid AND fk.confkey = ARRAY[dst.attnum]
            WHERE fk.contype = 'f' AND fk.conrelid = source_relation AND fk.confrelid = target_relation
              AND src.attname = link.source_column AND dst.attname = 'id'
        ) THEN
            EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I FOREIGN KEY (%I) REFERENCES %s(id) %s %s',
                source_relation, link.constraint_name, link.source_column, target_relation, link.delete_action,
                CASE WHEN link.validate_now THEN '' ELSE 'NOT VALID' END);
        END IF;
    END LOOP;
END $$;

INSERT INTO public.nexusport_schema_versions(version, description)
VALUES ('20261120_059', 'Restore core relationship keys and reject duplicate carrier and booking memberships')
ON CONFLICT (version) DO NOTHING;

COMMIT;
