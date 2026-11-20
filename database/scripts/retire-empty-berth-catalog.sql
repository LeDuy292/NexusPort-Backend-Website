-- Back up the empty table definition before running this maintenance script.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '20s';

DO $$
BEGIN
    IF to_regclass('public.berths') IS NULL THEN
        RETURN;
    END IF;
    LOCK TABLE public.berths IN ACCESS EXCLUSIVE MODE;
    IF EXISTS (SELECT 1 FROM public.berths) THEN
        RAISE EXCEPTION 'Berth catalog has data; manual reconciliation is required';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname NOT IN ('pg_catalog','information_schema')
          AND p.prokind IN ('f','p')
          AND p.prosrc ~* '(^|[^a-z0-9_])berths([^a-z0-9_]|$)'
    ) THEN
        RAISE EXCEPTION 'A database routine still uses the berth catalog';
    END IF;
    DROP TABLE public.berths RESTRICT;
END $$;
COMMIT;
