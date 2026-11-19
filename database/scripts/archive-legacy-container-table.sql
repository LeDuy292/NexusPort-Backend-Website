-- Separately reviewed cutover step. Run after deploying the code
-- that uses public.containers for all PostgreSQL container mappings.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '20s';
CREATE SCHEMA IF NOT EXISTS nexusport_archive;

DO $$
DECLARE
    legacy regclass := to_regclass('public."Containers"');
    canonical regclass := to_regclass('public.containers');
    archived regclass := to_regclass('nexusport_archive."Containers"');
BEGIN
    IF canonical IS NULL THEN
        RAISE EXCEPTION 'Canonical public.containers is required';
    END IF;
    IF legacy IS NULL THEN
        RETURN;
    END IF;
    IF archived IS NOT NULL THEN
        RAISE EXCEPTION 'Archive name is in use';
    END IF;
    LOCK TABLE public."Containers" IN ACCESS EXCLUSIVE MODE;
    IF EXISTS (SELECT 1 FROM public."Containers") THEN
        RAISE EXCEPTION 'Legacy Containers has rows; reconcile their IDs before archiving';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE confrelid = legacy AND contype = 'f')
        OR EXISTS (
            SELECT 1 FROM pg_depend d JOIN pg_rewrite r ON r.oid = d.objid
            WHERE d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_class'::regclass
              AND d.refobjid = legacy AND r.ev_class <> legacy
        ) THEN
        RAISE EXCEPTION 'Legacy Containers still has database dependents';
    END IF;

    ALTER TABLE public."Containers" SET SCHEMA nexusport_archive;
END $$;
COMMIT;
