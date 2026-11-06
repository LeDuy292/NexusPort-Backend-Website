// Real HTTP checks against an explicitly marked LOCAL disposable database only.
// Usage: node database/scripts/check-export-preparation.cjs <runtime.json> <NexusPort.Api.dll>
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), crypto = require('node:crypto'), assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { Client } = require('../../node-core/node_modules/pg');
const runtime = JSON.parse(fs.readFileSync(process.argv[2], 'utf8').replace(/^\uFEFF/, ''));
assert.equal(runtime.host, '127.0.0.1'); assert.equal(runtime.database, 'export_check'); assert.equal(runtime.user, 'export_check');
assert.equal(runtime.port, 55441); assert.match(runtime.marker, /^[a-f0-9]{32}$/);
const dll = path.resolve(process.argv[3]); assert.ok(fs.existsSync(dll));
const db = new Client({ host: runtime.host, port: runtime.port, database: runtime.database, user: runtime.user });
const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'nxp-export-preparation-')), origin = 'http://127.0.0.1:5611', base = '/api/v1/Booking/export';
const secret = crypto.randomBytes(48).toString('hex'), uid = () => crypto.randomUUID(), later = n => new Date(Date.now() + n * 86400000).toISOString();
let api, checks = 0;
const jwt = (user, role) => {
  const enc = value => Buffer.from(JSON.stringify(value)).toString('base64url');
  const value = enc({ alg: 'HS256', typ: 'JWT' }) + '.' + enc({ sub: user, role, exp: Math.floor(Date.now() / 1000) + 1800 });
  return value + '.' + crypto.createHmac('sha256', secret).update(value).digest('base64url');
};
async function call(route, token, body, method) {
  const response = await fetch(origin + route, { method: method || (body === undefined ? 'GET' : 'POST'), headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  const raw = await response.text(); let data; try { data = JSON.parse(raw); } catch { data = raw; }
  return { status: response.status, data };
}
function expect(result, code, label) { assert.equal(result.status, code, label + ': ' + JSON.stringify(result)); checks++; return result.data; }
function eq(actual, expected, label) { assert.deepEqual(actual, expected, label); checks++; }
function iso(serial) {
  const prefix = 'MSCU' + String(serial).padStart(6, '0'), letters = {}; let value = 10;
  for (let c = 65; c <= 90; c++) { while (value % 11 === 0) value++; letters[String.fromCharCode(c)] = value++; }
  return prefix + ([...prefix].reduce((sum, c, i) => sum + (letters[c] ?? Number(c)) * 2 ** i, 0) % 11 % 10);
}
(async () => {
  await db.connect();
  try {
    assert.equal((await db.query("SELECT current_setting('nexusport.export_test',true) AS marker")).rows[0].marker, runtime.marker, 'Refusing writes to an unmarked database.');
    assert.equal((await db.query("SELECT count(*)::int AS n FROM nexusport_schema_versions WHERE version='20261106_033'")).rows[0].n, 1);
    const carrier = uid(), foreignCarrier = uid(), customer = uid(), foreign = uid(), line = uid(), otherLine = uid();
    for (const [id, role] of [[customer, 'Transport Company'], [foreign, 'Transport Company'], [line, 'Shipping Line'], [otherLine, 'Shipping Line']])
      await db.query("INSERT INTO users(id,username,email,password,role,full_name,is_active,created_at,updated_at) VALUES($1,$2,$3,'unused',$4,'Isolated preparation check',true,now(),now())", [id, 'prep-' + id, id + '@example.invalid', role]);
    await db.query("INSERT INTO carriers(id,company_name,status) VALUES($1,'Isolated preparation A','active'),($2,'Isolated preparation B','active')", [carrier, foreignCarrier]);
    await db.query('INSERT INTO carrier_users(carrier_id,user_id) VALUES($1,$2),($3,$4)', [carrier, customer, foreignCarrier, foreign]);
    const types = (await db.query("SELECT id,code FROM container_types WHERE code IN ('20GP','40HC','20RF') ORDER BY code")).rows;
    assert.equal(types.length, 3, 'Reference container types must exist in the disposable DB.');
    const type = code => types.find(t => t.code === code);
    const createSource = async (code = '20GP', cargo = 'general', owner = carrier) => {
      const containerId = uid(), declarationId = uid(); let number;
      do { number = iso(100000 + crypto.randomInt(800000)); } while ((await db.query('SELECT 1 FROM containers WHERE container_no=$1', [number])).rowCount);
      await db.query("INSERT INTO containers(id,container_type_id,container_no,cargo_type,status) VALUES($1,$2,$3,'general','expected')", [containerId, type(code).id, number]);
      await db.query("INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,gross_weight_kg,seal_number,required_power_kw) VALUES($1,$2,$3,'full',$4::cargo_type,18000,'SOURCE-SEAL',$5)", [declarationId, containerId, owner, cargo, cargo === 'reefer' ? 6 : null]);
      return { containerId, declarationId, containerNumber: number, containerTypeCode: code, cargoType: cargo, sealNumber: 'SOURCE-SEAL', vgmKg: null, documents: [] };
    };
    const source1 = await createSource(), source2 = await createSource('40HC'), reefer = await createSource('20RF', 'reefer'), foreignSource = await createSource('20GP', 'general', foreignCarrier);
    const log = fs.openSync(path.join(folder, 'api.log'), 'a');
    api = spawn('dotnet', [dll, '--urls=' + origin], { cwd: folder, windowsHide: true, stdio: ['ignore', log, log], env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_CONTENTROOT: folder, ConnectionStrings__DefaultConnection: 'Host=127.0.0.1;Port=55441;Database=export_check;Username=export_check', Jwt__Secret: secret, AWS__BucketName: 'export-offline-test', AWS__Region: 'us-east-1', AWS__AccessKey: 'offline-test', AWS__SecretKey: 'offline-test' } });
    for (let i = 0; i < 100; i++) { try { await fetch(origin + base); break; } catch { if (api.exitCode !== null) throw Error('Test API exited: ' + folder); await new Promise(resolve => setTimeout(resolve, 100)); } }
    const a = jwt(customer, 'Transport Company'), b = jwt(foreign, 'Transport Company'), l = jwt(line, 'Shipping Line'), other = jwt(otherLine, 'Shipping Line');
    const voyage = expect(await call(base + '/voyages', l, { vesselName: 'Isolated staged export', voyageNumber: 'PREP-' + uid(), portOfLoading: 'VNDAD', portOfDischarge: 'JPTYO', receiveFrom: later(-1), cyCutoff: later(5), vgmCutoff: later(4), siCutoff: later(3), etd: later(7), eta: later(9) }), 200, 'publish test voyage');
    const content = { schemaVersion: 1, mode: 'containers', shippingLineId: line, voyageId: voyage.id, containers: [source1, source2], equipmentRequests: [], sharedDocuments: [], sharedDocumentsConfirmed: false };
    const draftId = uid(), draftRequest = { requestId: uid(), expectedVersion: 0, content };
    let draft = expect(await call(base + '/drafts/' + draftId, a, draftRequest, 'PUT'), 200, 'save private draft without VGM/fleet');
    eq(draft.content.containers[0].vgmKg, null, 'cargo weight is never VGM');
    eq(expect(await call(base + '/drafts/' + draftId, a, draftRequest, 'PUT'), 200, 'same save request is idempotent').version, 1, 'retry does not increment version');
    expect(await call(base + '/drafts/' + draftId, b), 404, 'private draft is company scoped');
    const saves = await Promise.all([1, 2].map(n => call(base + '/drafts/' + draftId, a, { requestId: uid(), expectedVersion: 1, content: { ...content, description: 'tab ' + n } }, 'PUT')));
    eq(saves.map(result => result.status).sort(), [200, 409], 'two tabs cannot silently overwrite');
    draft = expect(await call(base + '/drafts/' + draftId, a), 200, 'restore persisted draft');
    const req = { requestId: uid(), draftId, expectedDraftVersion: draft.version, content };
    let booking = expect(await call(base + '/requests', a, req), 200, 'request capacity using actual containers without VGM or vehicles');
    eq(booking.containerTypeCode, 'MIXED', 'demand supports mixed sizes'); eq(booking.quantity, 2, 'quantity is derived from distinct sources');
    eq(booking.containers.length, 0, 'candidate list is separate from the official list');
    eq(expect(await call(base + '/requests', a, req), 200, 'retry submission').id, booking.id, 'no duplicate booking');
    expect(await call(base + '/drafts/' + draftId, a, { ...draftRequest, requestId: uid(), expectedVersion: draft.version }, 'PUT'), 422, 'submitted draft is immutable');
    const options = expect(await call(base + '/options', a), 200, 'reload real source list');
    eq(options.sources.some(s => s.containerId === source1.containerId || s.containerId === source2.containerId), false, 'pending reservations disappear from new source list');
    expect(await call(base + '/requests', a, { requestId: uid(), content }), 422, 'active source cannot be booked again');
    expect(await call(base + '/requests', a, { requestId: uid(), content: { ...content, containers: [foreignSource] } }), 422, 'foreign declaration rejected');
    expect(await call(base + '/' + booking.id + '/approve', a, { expectedVersion: booking.version }), 403, 'customer cannot self approve');
    expect(await call(base + '/' + booking.id + '/approve', other, { expectedVersion: booking.version }), 404, 'other line cannot approve');
    const gateCount = Number((await db.query('SELECT count(*) AS n FROM bookings WHERE carrier_id=$1', [carrier])).rows[0].n);
    booking = expect(await call(base + '/' + booking.id + '/approve', l, { expectedVersion: booking.version }), 200, 'commercial approval does not depend on free fleet');
    eq(booking.status, 'Approved', 'commercial approval state'); eq(booking.portStatus, 'WaitingContainers', 'port waits for validated list');
    eq(Number((await db.query('SELECT count(*) AS n FROM bookings WHERE carrier_id=$1', [carrier])).rows[0].n), gateCount, 'approval creates no truck appointments');
    const si = { expectedVersion: booking.version, shipper: 'Actual shipper', consignee: 'Actual buyer', notifyParty: '', cargoDescription: 'General cargo', packagesCount: 100, grossWeightKg: 36000, freightTerms: 'Collect', blType: 'Original', destinationPort: 'JPTYO' };
    booking = expect(await call(base + '/' + booking.id + '/si', a, si), 200, 'SI can be submitted before gate arrival and container confirmation');
    expect(await call(base + '/' + booking.id + '/preparation/confirm', a, { requestId: uid(), expectedVersion: booking.version, content }), 422, 'missing VGM blocks list confirmation, not capacity request');
    expect(await call(base + '/' + booking.id + '/containers', a, { expectedVersion: booking.version, containers: content.containers.map(c => ({ ...c, vgmKg: 20000 })) }), 422, 'legacy declaration cannot bypass line review');
    const complete = { ...content, containers: content.containers.map(c => ({ ...c, vgmKg: 20000 })) };
    const confirmation = { requestId: uid(), expectedVersion: booking.version, content: complete };
    booking = expect(await call(base + '/' + booking.id + '/preparation/confirm', a, confirmation), 200, 'send completed list to line');
    eq(booking.portStatus, 'WaitingContainerReview', 'await line list review'); eq(booking.containers.length, 0, 'list not prematurely forwarded to port');
    eq(expect(await call(base + '/' + booking.id + '/preparation/confirm', a, confirmation), 200, 'retry confirmation').version, booking.version, 'no duplicate confirmation version');
    expect(await call(base + '/' + booking.id + '/container-review', a, { expectedVersion: booking.version, approve: true }), 403, 'customer cannot approve own list');
    booking = expect(await call(base + '/' + booking.id + '/container-review', l, { expectedVersion: booking.version, approve: false, reason: 'Check exact seals' }), 200, 'line requests list correction');
    eq(booking.portStatus, 'ContainerRevisionRequested', 'correction state');
    booking = expect(await call(base + '/' + booking.id + '/preparation/confirm', a, { requestId: uid(), expectedVersion: booking.version, content: complete }), 200, 'customer resends corrected list');
    booking = expect(await call(base + '/' + booking.id + '/container-review', l, { expectedVersion: booking.version, approve: true }), 200, 'line approves and forwards list');
    eq(booking.status, 'ContainersDeclared', 'official list status'); eq(booking.portStatus, 'Accepted', 'actual local port receipt'); eq(booking.containers.length, 2, 'official list contains both sizes');
    const hints = expect(await call(base + '/' + booking.id + '/si-suggestions', a), 200, 'cargo hints have own-source provenance');
    eq(hints.grossWeightKg, 36000, 'SI uses cargo weight rather than summed VGM');
    expect(await call(base + '/' + booking.id + '/si-suggestions', b), 404, 'SI hints private to company');
    // Closing a voyage may exclude or roll only part of a batch. Historic rows stay
    // in the dossier, but their cargo must not be counted in the retained SI.
    for (const excludedStatus of ['Excluded', 'Rolled']) {
      await db.query('UPDATE export_booking_containers SET status=$1,active=false WHERE export_booking_id=$2 AND container_id=$3', [excludedStatus, booking.id, source2.containerId]);
      const retained = expect(await call(base + '/' + booking.id + '/si-suggestions', a), 200, 'SI hints after ' + excludedStatus);
      eq(retained.grossWeightKg, 18000, excludedStatus + ' cargo is excluded from SI weight');
      eq(retained.sourceCount, 1, 'SI source count contains retained containers only');
    }
    await db.query("UPDATE export_booking_containers SET status='Expected',active=true WHERE export_booking_id=$1 AND container_id=$2", [booking.id, source2.containerId]);
    const capacity = { schemaVersion: 1, mode: 'capacity', shippingLineId: line, voyageId: voyage.id, equipmentRequests: [{ typeCode: '20RF', quantity: 1 }], containers: [], sharedDocuments: [] };
    let cold = expect(await call(base + '/requests', a, { requestId: uid(), content: capacity }), 200, 'request without any known container number');
    cold = expect(await call(base + '/' + cold.id + '/approve', l, { expectedVersion: cold.version }), 200, 'approve unknown-number reefer demand');
    const coldContent = { ...capacity, mode: 'containers', containers: [{ ...reefer, vgmKg: 20000, targetTemperature: -18 }] };
    expect(await call(base + '/' + cold.id + '/preparation/confirm', a, { requestId: uid(), expectedVersion: cold.version, content: coldContent }), 422, 'reefer instruction document required');
    const documentId = uid(), documentKey = 'NexusPort/documents/' + uid() + '.pdf';
    await db.query("INSERT INTO export_document_files(id,carrier_id,uploaded_by,object_key,original_name,content_type,size_bytes,sha256) VALUES($1,$2,$3,$4,'reefer.pdf','application/pdf',500,$5)", [documentId, carrier, customer, documentKey, 'a'.repeat(64)]);
    coldContent.containers[0].documents = [{ documentId, documentType: 'ReeferTemperatureInstructions', containerNumbers: [source1.containerNumber] }];
    expect(await call(base + '/' + cold.id + '/preparation/confirm', a, { requestId: uid(), expectedVersion: cold.version, content: coldContent }), 422, 'individual document scope must contain its assigned container');
    coldContent.containers[0].documents = [{ documentId, documentType: 'ReeferTemperatureInstructions', fileUrl: 'https://forged.invalid/not-owned.pdf', containerNumbers: [reefer.containerNumber] }];
    cold = expect(await call(base + '/' + cold.id + '/preparation/confirm', a, { requestId: uid(), expectedVersion: cold.version, content: coldContent }), 200, 'reuse authorized S3 identity');
    assert.match(cold.preparation.containers[0].documents[0].fileUrl, /^https:\/\/export-offline-test\.s3\./); checks++;
    const expiredOwnedUrl = 'https://export-offline-test.s3.amazonaws.com/' + documentKey + '?X-Amz-Date=20000101T000000Z&X-Amz-Expires=1&X-Amz-Signature=expired-owned-fixture';
    await db.query("UPDATE export_bookings SET preparation=jsonb_set(preparation,'{containers,0,documents,0,fileUrl}',to_jsonb($1::text)) WHERE id=$2", [expiredOwnedUrl, cold.id]);
    const reopenedCold = expect(await call(base + '/' + cold.id, a), 200, 'reopen own expired S3 document');
    const freshOwnedUrl = reopenedCold.preparation.containers[0].documents[0].fileUrl;
    eq(freshOwnedUrl !== expiredOwnedUrl, true, 'owned object identity receives a fresh URL');
    eq(new URL(freshOwnedUrl).searchParams.has('X-Amz-Signature') || new URL(freshOwnedUrl).searchParams.has('Signature'), true, 'owned object URL is actually signed');
    const foreignDoc = { ...capacity, sharedDocuments: [{ documentId, documentType: 'CustomsDeclaration', containerNumbers: [] }] };
    expect(await call(base + '/requests', b, { requestId: uid(), content: foreignDoc }), 422, 'foreign S3 identity cannot be attached');
    cold = expect(await call(base + '/' + cold.id + '/cancel', a, { expectedVersion: cold.version }), 200, 'cancel before any physical gate operation');
    eq((await db.query('SELECT active FROM export_source_reservations WHERE export_booking_id=$1', [cold.id])).rows.every(r => !r.active), true, 'cancel releases candidate reservation');
    const request2 = { requestId: uid(), content: { ...content, containers: [reefer], equipmentRequests: [] } };
    let pending = expect(await call(base + '/requests', a, request2), 200, 'released container becomes available');
    pending = expect(await call(base + '/requests', a, { ...request2, requestId: uid(), bookingId: pending.id, expectedVersion: pending.version, content: { ...request2.content, description: 'Corrected request' } }), 200, 'edit pending request in place');
    eq(pending.version, 2, 'edit increments version');
    eq(expect(await call(base+'/requests',a,request2),200,'retry earlier request after revision').id,pending.id,'earlier request receipt survives revision');
    pending = expect(await call(base + '/' + pending.id + '/reject', l, { expectedVersion: pending.version, reason: 'Need new booking demand' }), 200, 'line rejects pending demand');
    eq((await db.query('SELECT active FROM export_source_reservations WHERE export_booking_id=$1', [pending.id])).rows.every(r => !r.active), true, 'reject releases source');
    const raceSource=await createSource();
    const race=await Promise.all([1,2].map(()=>call(base+'/requests',a,{requestId:uid(),content:{...content,containers:[raceSource]}})));
    eq(race.map(result=>result.status).sort(),[200,422],'two sessions cannot reserve the same physical container');
    const bulk=[];for(let i=0;i<50;i++)bulk.push(await createSource());
    const fifty=expect(await call(base+'/requests',a,{requestId:uid(),content:{...content,containers:bulk}}),200,'50-container request is accepted atomically');
    eq(fifty.quantity,50,'50 distinct sources counted correctly');
    eq(Number((await db.query('SELECT count(*) AS n FROM export_source_reservations WHERE export_booking_id=$1 AND active',[fifty.id])).rows[0].n),50,'all fifty sources reserved');
    expect(await call(base+'/requests',a,{requestId:uid(),content:{...content,containers:[...bulk,source1]}}),422,'51-container request rejected');
    // A legacy URL is untrusted input. Knowing another company's expired object
    // URL must never turn a customer dossier into an S3 signing oracle.
    const foreignDocumentKey = 'NexusPort/documents/' + uid() + '.pdf';
    await db.query("INSERT INTO export_document_files(id,carrier_id,uploaded_by,object_key,original_name,content_type,size_bytes,sha256) VALUES($1,$2,$3,$4,'private-foreign.pdf','application/pdf',500,$5)", [uid(), foreignCarrier, foreign, foreignDocumentKey, 'b'.repeat(64)]);
    const expiredForeignUrl = 'https://export-offline-test.s3.amazonaws.com/' + foreignDocumentKey + '?X-Amz-Date=20000101T000000Z&X-Amz-Expires=1&X-Amz-Signature=expired-foreign-fixture';
    const legacySource = await createSource();
    let legacy = expect(await call(base, a, { requestId: uid(), shippingLineId: line, voyageId: voyage.id, containerTypeCode: '20GP', quantity: 2, commodityDescription: 'Legacy document authorization check' }), 201, 'create compatible legacy dossier');
    legacy = expect(await call(base + '/' + legacy.id + '/approve', l, { expectedVersion: legacy.version }), 200, 'approve legacy commercial request');
    legacy = expect(await call(base + '/' + legacy.id + '/containers', a, { expectedVersion: legacy.version, containers: [{ ...legacySource, vgmKg: 20000, customsDocumentUrl: expiredForeignUrl }] }), 200, 'legacy document URL remains compatible input');
    const returnedForeignUrl = legacy.containers[0].customsDocumentUrl;
    eq(returnedForeignUrl == null || returnedForeignUrl === '' || returnedForeignUrl === expiredForeignUrl, true, 'foreign S3 key never receives a newly signed URL');
    const partialHints=expect(await call(base+'/'+legacy.id+'/si-suggestions',a),200,'read partial legacy SI hints');
    eq(partialHints.grossWeightKg,null,'partially declared list must not appear to contain the complete shipment weight');
    console.log(JSON.stringify({ passed: true, checks, scope: 'marked disposable PostgreSQL + real C# HTTP', deployedDatabaseWrites: false, logFolder: folder }));
  } finally { api?.kill(); await db.end(); }
})().catch(error => { console.error(error.stack || error.message); process.exitCode = 1; });
