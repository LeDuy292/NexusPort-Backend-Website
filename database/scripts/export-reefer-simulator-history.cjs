'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { createGzip, createGunzip } = require('node:zlib');
const { createInterface } = require('node:readline');
const { Readable } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const { createRequire } = require('node:module');

const repo = path.resolve(__dirname, '../..');
const load = createRequire(path.join(repo, 'node-core/package.json'));
const { Client } = load('pg');
const dotenv = load('dotenv');
const connectionString = dotenv.parse(fs.readFileSync(path.join(repo, 'node-core/.env'))).DATABASE_URL;
const url = new URL(connectionString);
const days = Number(process.argv[2] ?? 7);
if (url.hostname !== 'hopper.proxy.rlwy.net' || url.pathname !== '/railway') throw new Error('Unexpected Railway database target');
if (!Number.isSafeInteger(days) || days < 7 || days > 365) throw new Error('Retention must be between 7 and 365 days');

const marker = 'Mô phỏng chu kỳ telemetry tự động (Chu kỳ: %)';
const output = path.resolve(repo, '../db-backups', 'railway-reefer-simulator-' + new Date().toISOString().replace(/[:.]/g, '-') + '.ndjson.gz');
const client = new Client({ connectionString, ssl: false, connectionTimeoutMillis: 7000 });
let exported = 0;

async function* readings() {
    for (;;) {
        const { rows } = await client.query('FETCH FORWARD 8000 FROM reefer_archive');
        if (!rows.length) return;
        exported += rows.length;
        for (const row of rows) yield row.reading + '\n';
    }
}

async function run() {
    await client.connect();
    try {
        await client.query('BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY');
        await client.query("SET LOCAL statement_timeout='25s'");
        const { rows: [{ cutoff }] } = await client.query(
            'SELECT (now() - make_interval(days => $1))::text AS cutoff', [days]);
        await client.query(`DECLARE reefer_archive NO SCROLL CURSOR FOR
            WITH alert_windows AS (
                SELECT session_id,
                       range_agg(tstzrange(ts - interval '2 minutes',ts + interval '2 minutes','[]')) AS intervals
                FROM public.reefer_alerts
                CROSS JOIN LATERAL (VALUES (created_at),(resolved_at)) AS times(ts)
                WHERE ts IS NOT NULL GROUP BY session_id
            ), latest AS (
                SELECT session_id,max(recorded_at) AS time FROM public.reefer_temperature_records GROUP BY session_id
            )
            SELECT to_jsonb(t)::text AS reading
            FROM public.reefer_temperature_records t JOIN latest l USING(session_id)
            LEFT JOIN alert_windows w USING(session_id)
            WHERE t.recorded_at < $1::timestamptz AND t.recorded_at < l.time
              AND t.device_id='IOT-REEFER-SIM-01' AND t.data_source='IOT_TELEMATICS'
              AND t.recorded_by IS NULL AND t.notes LIKE $2 AND NOT t.is_abnormal
              AND NOT coalesce(w.intervals @> t.recorded_at,false)`, [cutoff,marker]);
        const hashes = createHash('sha256');
        const gzip = createGzip();
        gzip.on('data', chunk => hashes.update(chunk));
        await pipeline(Readable.from(readings()), gzip, fs.createWriteStream(output,{ flags:'wx' }));
        await client.query('ROLLBACK');

        const ids = new Set();
        for await (const line of createInterface({input:fs.createReadStream(output).pipe(createGunzip()),crlfDelay:Infinity})) {
            const row = JSON.parse(line);
            if (!row.id || ids.has(row.id)) throw new Error('Archive has a missing or duplicate reading ID');
            ids.add(row.id);
        }
        if (ids.size !== exported) throw new Error('Archive count mismatch');
        const manifest = { archive:path.basename(output), database:url.hostname, relation:'public.reefer_temperature_records',
            cutoff, days, rows:exported, sha256:hashes.digest('hex'), preserved:'latest per session, abnormal readings, and within two minutes of alerts' };
        fs.writeFileSync(output+'.manifest.json',JSON.stringify(manifest,null,2)+'\n',{flag:'wx'});
        console.log(JSON.stringify({...manifest,bytes:fs.statSync(output).size}));
    } finally { await client.end(); }
}
run().catch(error=>{ console.error(error.code || error.message); process.exitCode=1; });
