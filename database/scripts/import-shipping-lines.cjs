'use strict';

const { readFile, writeFile, unlink } = require('node:fs/promises');
const { randomBytes } = require('node:crypto');
const { resolve } = require('node:path');
const { parseArgs } = require('node:util');
const bcrypt = require('bcryptjs');
const { QueryTypes } = require('sequelize');
const directory = require('../../node-core/src/modules/container/domain/shipping-line-reference.json');
const { isValidIso6346, normalizeContainerNumber } = require('../../node-core/dist/modules/container/application/container.validator');

async function main() {
  const { values } = parseArgs({ options: {
    apply: { type: 'boolean' },
    'allow-remote': { type: 'boolean' },
    containers: { type: 'string' },
    'credentials-file': { type: 'string' },
    report: { type: 'string' },
  } });
  const apply = Boolean(values.apply);
  if (!values.containers || (apply && !values['credentials-file'])) {
    throw new Error('Provide --containers <file> and, with --apply, --credentials-file <new private file>.');
  }
  const { db, bcrypt: bcryptConfig } = require('../../src/config/env');
  if (apply && !['127.0.0.1', 'localhost', '::1'].includes(db.host) && !values['allow-remote']) {
    throw new Error('Remote writes require --apply --allow-remote.');
  }
  const credentialsPath = apply ? resolve(values['credentials-file']) : null;
  if (values.report && resolve(values.report) === credentialsPath) throw new Error('Use separate report and credentials files.');
  const input = (await readFile(values.containers, 'utf8')).split(/\r?\n/).map(normalizeContainerNumber).filter(Boolean);
  const containerNumbers = [...new Set(input)];
  const { sequelize } = require('../../src/config/database');
  let credentialsWritten = false;
  let committed = false;
  try {
    const report = await sequelize.transaction(async transaction => {
      const select = (sql, bind = []) => sequelize.query(sql, { bind, transaction, type: QueryTypes.SELECT });
      const execute = (sql, bind = []) => sequelize.query(sql, { bind, transaction });
      const lock = apply ? ' FOR UPDATE' : '';
      const versions = await select("SELECT version FROM nexusport_schema_versions WHERE version IN ('20261109_034','20261109_035','20261109_039')");
      if (versions.length !== 3) throw new Error('Apply migrations 20261109_034, 20261109_035 and 20261109_039 before importing.');
      if (apply) await execute("SELECT pg_advisory_xact_lock(hashtext('nexusport-import-shipping-lines'))");
      const prefixOwners = new Map();
      const credentials = [];
      const lines = [];

      for (const reference of directory) {
        const { fullName, shortName, prefixes, seedUsername: username } = reference;
        const email = `${username}@nexusport.invalid`;
        const profiles = await select(`SELECT id,full_name,short_name FROM shipping_lines
          WHERE lower(short_name)=lower($1) OR lower(full_name)=lower($2)${lock}`, [shortName, fullName]);
        if (profiles.length > 1 || (profiles[0] && profiles[0].full_name.toLowerCase() !== fullName.toLowerCase())) {
          throw new Error(`Conflicting profile for ${shortName}. Check the existing record before importing.`);
        }
        const owners = await select(`SELECT prefix,shipping_line_id FROM shipping_line_prefixes
          WHERE prefix=ANY($1::varchar[])${lock}`, [prefixes]);
        if (owners.some(owner => owner.shipping_line_id !== profiles[0]?.id)) {
          throw new Error(`At least one BIC prefix for ${shortName} already belongs to a different profile.`);
        }
        const accounts = await select(`SELECT u.username,u.email,u.role,a.shipping_line_id,u.is_active FROM users u
          LEFT JOIN shipping_line_accounts a ON a.user_id=u.id
          WHERE lower(u.username)=lower($1) OR lower(u.email)=lower($2)${apply ? ' FOR UPDATE OF u' : ''}`, [username, email]);
        const account = accounts[0];
        if (accounts.length > 1 || (account && (account.username !== username || account.email !== email ||
          account.role !== 'Shipping Line' || account.shipping_line_id !== profiles[0]?.id || !account.is_active))) {
          throw new Error(`Account collision for ${username}. Existing credentials and roles were preserved.`);
        }

        let lineId = profiles[0]?.id;
        if (apply && !lineId) {
          const [line] = await select('INSERT INTO shipping_lines(full_name,short_name) VALUES($1,$2) RETURNING id', [fullName, shortName]);
          lineId = line.id;
        }
        if (apply) {
          const missingPrefixes = prefixes.filter(prefix => !owners.some(owner => owner.prefix === prefix));
          await execute('INSERT INTO shipping_line_prefixes(prefix,shipping_line_id) SELECT unnest($1::varchar[]),$2', [missingPrefixes, lineId]);
          if (!account) {
            const password = randomBytes(20).toString('base64url') + 'Aa9!';
            const hash = await bcrypt.hash(password, bcryptConfig.saltRounds);
            const [created] = await select(`INSERT INTO users(id,username,email,password,role,full_name,is_active,created_at,updated_at)
              VALUES(gen_random_uuid(),$1,$2,$3,'Shipping Line',$4,true,now(),now()) RETURNING id`, [username, email, hash, fullName]);
            await execute('INSERT INTO shipping_line_accounts(user_id,shipping_line_id) VALUES($1,$2)', [created.id, lineId]);
            credentials.push({ username, password });
          }
        }
        const profile = { id: lineId ?? null, fullName, shortName, prefixes, username,
          profileStatus: profiles.length ? 'existing' : apply ? 'created' : 'would_create',
          accountStatus: account ? 'existing' : apply ? 'created' : 'would_create' };
        lines.push(profile);
        for (const prefix of prefixes) prefixOwners.set(prefix, profile);
      }

      const eligible = containerNumbers.filter(number => number[3] === 'U' && isValidIso6346(number) && prefixOwners.has(number.slice(0, 4)));
      const existing = new Map((await select(`SELECT id,container_no,shipping_line_id FROM containers
        WHERE container_no=ANY($1::varchar[]) ORDER BY id${lock}`, [eligible])).map(row => [row.container_no, row]));
      const containers = [];
      for (const number of containerNumbers) {
        const line = prefixOwners.get(number.slice(0, 4));
        const row = existing.get(number);
        let status;
        if (number[3] !== 'U' || !isValidIso6346(number)) status = 'invalid_iso6346';
        else if (!line) status = 'prefix_outside_import';
        else if (!row) status = 'eligible_not_registered';
        else if (row.shipping_line_id) status = row.shipping_line_id === line.id ? 'already_linked' : 'ownership_conflict';
        else {
          if (apply) await execute('UPDATE containers SET shipping_line_id=$1,updated_at=now() WHERE id=$2', [line.id, row.id]);
          status = apply ? 'linked' : 'would_link';
        }
        containers.push({ containerNumber: number, shippingLine: line?.shortName ?? null, status });
      }

      if (credentials.length) {
        await writeFile(credentialsPath, JSON.stringify(credentials, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
        credentialsWritten = true;
      }
      const counts = Object.fromEntries([...new Set(containers.map(row => row.status))].map(status =>
        [status, containers.filter(row => row.status === status).length]));
      return { mode: apply ? 'apply' : 'preview', supplied: input.length, duplicates: input.length - containerNumbers.length,
        counts, lines, containers };
    });
    committed = apply;
    if (values.report) await writeFile(resolve(values.report), JSON.stringify(report, null, 2) + '\n', { flag: 'wx' });
    console.log(JSON.stringify({ ...report, containers: undefined, credentialsFile: credentialsWritten ? credentialsPath : undefined }, null, 2));
  } catch (error) {
    if (credentialsWritten && !committed) await unlink(credentialsPath);
    throw error;
  } finally {
    await sequelize.close();
  }
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
