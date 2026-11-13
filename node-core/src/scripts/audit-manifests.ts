import 'dotenv/config';
import pool from '../infrastructure/database/db';
async function main() {
  const c = await pool.connect();
  try {
    console.log('GATES', (await c.query("SELECT id,code,is_active FROM gates ORDER BY code")).rows);
    const tables = (await c.query(`SELECT table_schema,table_name FROM information_schema.tables WHERE table_schema IN ('public','shipping')
      AND table_name ~ '(manifest|lading|shipping_instruction|delivery_order|export_booking|vessel|voyage)' ORDER BY table_name`)).rows;
    for (const {table_schema: schema,table_name: table} of tables) {
      console.log('TABLE', schema+'.'+table, 'COUNT', (await c.query(`SELECT count(*) FROM "${schema}"."${table}"`)).rows[0].count);
      if (process.argv.includes('--details')) console.log('COLUMNS', (await c.query('SELECT column_name,data_type FROM information_schema.columns WHERE table_schema=$1 AND table_name=$2 ORDER BY ordinal_position', [schema,table])).rows);
    }
    console.log('FOREIGN_KEYS', (await c.query(`SELECT conrelid::regclass::text AS child,confrelid::regclass::text AS parent,pg_get_constraintdef(oid) AS definition FROM pg_constraint
      WHERE contype='f' AND (conrelid::regclass::text ~ '(manifest|lading|delivery_order|shipping_instruction)' OR confrelid::regclass::text ~ '(manifest|lading|delivery_order|shipping_instruction)')`)).rows);
    console.log('REFERENCES', (await c.query(`SELECT table_schema,table_name,column_name FROM information_schema.columns WHERE table_schema IN ('public','shipping')
      AND lower(column_name) ~ '(bl_number|blid|bl_id|bill_of_lading|manifest)' ORDER BY table_schema,table_name,column_name`)).rows);
    for (const table of ['edo_orders','np_sl_edo_orders','np_sl_shipping_containers']) {
      if ((await c.query('SELECT to_regclass($1) AS name',['public.'+table])).rows[0].name)
        console.log('LEGACY_LINKS',table,(await c.query(`SELECT count(*) AS total,count(bl_id) AS linked FROM ${table}`)).rows[0]);
    }
    console.log('EXPORT_BL_REFERENCES',(await c.query('SELECT count(*) AS linked FROM export_bookings WHERE bl_number IS NOT NULL')).rows[0]);
  } finally { c.release(); await pool.end(); }
}
main().catch(e=>{console.error(e.message);process.exitCode=1});
