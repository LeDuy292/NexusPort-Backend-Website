BEGIN;

-- Dòng mới ghi bằng line_user_id (code cũ) tự có shipping_line_id của hãng tương ứng.
CREATE OR REPLACE FUNCTION np_fill_shipping_line_id() RETURNS trigger AS $$
BEGIN
  IF NEW.shipping_line_id IS NULL AND NEW.line_user_id IS NOT NULL THEN
    SELECT a.shipping_line_id INTO NEW.shipping_line_id FROM shipping_line_accounts a WHERE a.user_id = NEW.line_user_id LIMIT 1;
  END IF;
  RETURN NEW;
END $$ LANGUAGE plpgsql;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['export_voyages','export_bookings','import_shipments'] LOOP
    EXECUTE format('DROP TRIGGER IF EXISTS trg_%s_fill_line ON %I', t, t);
    EXECUTE format('CREATE TRIGGER trg_%s_fill_line BEFORE INSERT OR UPDATE OF line_user_id ON %I FOR EACH ROW EXECUTE FUNCTION np_fill_shipping_line_id()', t, t);
  END LOOP;
END $$;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_060', 'Tự điền shipping_line_id từ line_user_id'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_060');

COMMIT;
