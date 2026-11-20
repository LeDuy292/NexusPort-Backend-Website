BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

DO $$
DECLARE
    spec record;
    relation regclass;
    conflicts boolean;
BEGIN
    FOR spec IN
        SELECT * FROM (VALUES
            ('booking_dispatch_state', 'booking_id'),
            ('container_cargo_types', 'code'),
            ('container_visit_access', 'container_visit_id, carrier_id'),
            ('eport_service_order_states', 'draft_id'),
            ('gate_transaction_containers', 'gate_transaction_id, container_id')
        ) AS tables(table_name, key_columns)
    LOOP
        relation := format('public.%I', spec.table_name)::regclass;
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', relation);
        EXECUTE format(
            'SELECT EXISTS (SELECT 1 FROM %s AS t GROUP BY %s HAVING count(DISTINCT to_jsonb(t)) > 1)',
            relation, spec.key_columns) INTO conflicts;
        IF conflicts THEN
            RAISE EXCEPTION 'Conflicting rows in % require explicit reconciliation', relation;
        END IF;
        EXECUTE format(
            'DELETE FROM %s AS target USING
             (SELECT ctid, row_number() OVER (PARTITION BY %s ORDER BY ctid) AS duplicate_number FROM %s) AS copies
             WHERE target.ctid = copies.ctid AND copies.duplicate_number > 1',
            relation, spec.key_columns, relation);
        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = relation AND contype = 'p') THEN
            EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I PRIMARY KEY (%s)',
                relation, spec.table_name || '_pkey', spec.key_columns);
        END IF;
    END LOOP;
END $$;

DO $$
DECLARE
    spec record;
    source_relation regclass;
    target_relation regclass;
BEGIN
    FOR spec IN
        SELECT * FROM (VALUES
            ('booking_dispatch_state', 'booking_id', 'bookings', 'ON DELETE CASCADE', true),
            ('booking_dispatch_state', 'driver_id', 'drivers', '', true),
            ('booking_dispatch_state', 'updated_by', 'users', 'ON DELETE SET NULL', true),
            ('container_visit_access', 'container_visit_id', 'container_visits', '', true),
            ('container_visit_access', 'carrier_id', 'carriers', '', true),
            ('container_visit_access', 'granted_by', 'users', '', false),
            ('eport_service_order_states', 'draft_id', 'eport_service_drafts', '', true),
            ('gate_transaction_containers', 'gate_transaction_id', 'gate_transactions', '', true),
            ('gate_transaction_containers', 'container_id', 'containers', '', true)
        ) AS links(source_table, source_column, target_table, delete_action, validate_now)
    LOOP
        source_relation := format('public.%I', spec.source_table)::regclass;
        target_relation := format('public.%I', spec.target_table)::regclass;
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint fk
            JOIN pg_attribute src ON src.attrelid = fk.conrelid AND fk.conkey = ARRAY[src.attnum]
            JOIN pg_attribute dst ON dst.attrelid = fk.confrelid AND fk.confkey = ARRAY[dst.attnum]
            WHERE fk.contype = 'f' AND fk.conrelid = source_relation AND fk.confrelid = target_relation
              AND src.attname = spec.source_column AND dst.attname = 'id'
        ) THEN
            EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I FOREIGN KEY (%I) REFERENCES %s(id) %s %s',
                source_relation, spec.source_table || '_' || spec.source_column || '_fkey',
                spec.source_column, target_relation, spec.delete_action,
                CASE WHEN spec.validate_now THEN '' ELSE 'NOT VALID' END);
        END IF;
    END LOOP;
END $$;

INSERT INTO public.nexusport_schema_versions(version, description)
VALUES ('20261120_060', 'Restore relationship keys in dispatch, visit access, ePort and gate tables')
ON CONFLICT (version) DO NOTHING;
COMMIT;
