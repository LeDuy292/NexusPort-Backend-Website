'use strict';

/**
 * Báo cáo sự cố / hư hỏng tại bãi: nhân viên bãi gửi -> lưu DB -> Dispatcher nhận realtime và xử lý.
 * Ảnh bằng chứng (data URL) được đẩy lên S3, DB chỉ giữ khóa đối tượng; khi trả ra thì ký link có hạn.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const evidenceStorage = require('../../utils/evidenceStorage');
const { emitToDispatchers } = require('../../config/socket');

const SEVERITIES = ['LOW', 'MEDIUM', 'HIGH', 'CRITICAL'];
const STATUSES = ['NEW', 'ACKNOWLEDGED', 'RESOLVED'];
const MAX_PHOTOS = 6;

let tableReady = null;
function ensureTable() {
  if (!tableReady) {
    tableReady = sequelize.query(`
      CREATE TABLE IF NOT EXISTS yard_incident_reports (
        id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
        code varchar(30) NOT NULL UNIQUE,
        incident_type varchar(200) NOT NULL,
        severity varchar(12) NOT NULL DEFAULT 'MEDIUM',
        location varchar(200) NOT NULL,
        container_no varchar(20),
        equipment varchar(100),
        description text NOT NULL,
        immediate_action text,
        photos jsonb NOT NULL DEFAULT '[]'::jsonb,
        status varchar(14) NOT NULL DEFAULT 'NEW',
        reported_by uuid,
        reported_by_name varchar(150),
        reported_at timestamptz NOT NULL DEFAULT now(),
        acknowledged_by_name varchar(150),
        acknowledged_at timestamptz,
        resolved_by_name varchar(150),
        resolved_at timestamptz,
        resolution_note text
      );
      ALTER TABLE yard_incident_reports ADD COLUMN IF NOT EXISTS details jsonb;
      CREATE INDEX IF NOT EXISTS idx_yard_incident_status ON yard_incident_reports(status, reported_at DESC);
    `).catch((err) => { tableReady = null; throw err; });
  }
  return tableReady;
}

const SEVERITY_VI = { CRITICAL: 'Nghiêm trọng', HIGH: 'Cao', MEDIUM: 'Trung bình', LOW: 'Thấp' };

/** Gửi thông báo sự cố vào chuông thông báo của từng Dispatcher đang hoạt động. */
async function notifyDispatchers(report) {
  try {
    const users = await sequelize.query(`SELECT id FROM users WHERE role = 'Dispatcher' AND is_active = true`, { type: QueryTypes.SELECT });
    if (!users.length) return;
    const sev = SEVERITY_VI[report.severity] || report.severity;
    const where = [report.container_no || report.containerNo, report.location].filter(Boolean).join(' · ');
    const title = `Sự cố bãi (${sev}): ${report.incident_type || report.incidentType || report.type}`;
    const message = `${where ? `${where}. ` : ''}${String(report.description || '').slice(0, 300)}`;
    const urgent = ['CRITICAL', 'HIGH'].includes(report.severity);
    for (const u of users) {
      await sequelize.query(
        `INSERT INTO cargo_notifications (recipient_user_id, recipient_role, event_type, direction, priority, title, message, data,
                                          action_type, action_label, action_url, document_type, document_id, dedup_key, created_at)
         VALUES (:uid, 'dispatcher', 'YardIncident', 'general', :priority, :title, :message, CAST(:data AS jsonb),
                 'view', 'Xử lý sự cố', '/dispatcher/yard-incidents', 'yard_incident', :docId, :dedup, now())
         ON CONFLICT DO NOTHING`,
        { replacements: { uid: u.id, priority: urgent ? 'urgent' : 'normal', title, message,
          data: JSON.stringify({ incidentId: report.id, code: report.code, severity: report.severity }),
          docId: String(report.id), dedup: `incident:${report.id}:${u.id}` } },
      );
    }
  } catch (err) {
    console.warn('[YardIncident] Không gửi được thông báo:', err.message);
  }
}

const clean = (v, max) => String(v == null ? '' : v).trim().slice(0, max);
const fail = (message, statusCode = 422) => Object.assign(new Error(message), { statusCode });

async function sign(row) {
  const photos = Array.isArray(row.photos) ? row.photos : [];
  const signed = await Promise.all(photos.map(async (p) => {
    try { return await evidenceStorage.link(p); } catch { return null; }
  }));
  return {
    id: row.id,
    code: row.code,
    type: row.incident_type,
    severity: row.severity,
    location: row.location,
    containerNo: row.container_no,
    equipment: row.equipment,
    description: row.description,
    immediateAction: row.immediate_action,
    photos: signed.filter(Boolean),
    details: row.details || null,
    status: row.status,
    reportedBy: row.reported_by_name,
    reportedAt: row.reported_at,
    acknowledgedBy: row.acknowledged_by_name,
    acknowledgedAt: row.acknowledged_at,
    resolvedBy: row.resolved_by_name,
    resolvedAt: row.resolved_at,
    resolutionNote: row.resolution_note,
  };
}

async function createReport(user, body = {}) {
  await ensureTable();
  const type = clean(body.type, 200);
  const location = clean(body.location, 200);
  const description = clean(body.description, 4000);
  const severity = SEVERITIES.includes(String(body.severity)) ? String(body.severity) : 'MEDIUM';
  if (!type) throw fail('Chọn loại sự cố.');
  if (!location) throw fail('Chọn vị trí xảy ra sự cố.');
  if (description.length < 5) throw fail('Mô tả sự cố quá ngắn.');

  // Chi tiết có cấu trúc (loại hư hỏng, vị trí đánh dấu trên container...): chỉ nhận object nhỏ, cắt gọn.
  let details = null;
  if (body.details && typeof body.details === 'object' && !Array.isArray(body.details)) {
    const d = body.details;
    details = {
      kind: clean(d.kind, 40) || null,
      damageType: clean(d.damageType, 80) || null,
      marks: (Array.isArray(d.marks) ? d.marks : []).slice(0, 30).map((m) => ({
        face: clean(m && m.face, 10),
        x: Math.round(Number(m && m.x) * 10) / 10 || 0,
        y: Math.round(Number(m && m.y) * 10) / 10 || 0,
      })),
      container: d.container && typeof d.container === 'object' ? {
        type: clean(d.container.type, 30) || null,
        line: clean(d.container.line, 80) || null,
        position: clean(d.container.position, 30) || null,
        sealNo: clean(d.container.sealNo, 40) || null,
      } : null,
    };
  }

  const rawPhotos = (Array.isArray(body.photos) ? body.photos : []).slice(0, MAX_PHOTOS);
  const photos = (await evidenceStorage.storeAll(rawPhotos, 'yard-incident'))
    // Chỉ nhận khóa S3 đã lưu thành công; bỏ dữ liệu base64 còn sót để không làm phình DB
    .filter((p) => typeof p === 'string' && !p.startsWith('data:'));

  const code = `SC-${new Date().toISOString().slice(2, 10).replace(/-/g, '')}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`;
  const [row] = await sequelize.query(
    `INSERT INTO yard_incident_reports (code, incident_type, severity, location, container_no, equipment, description, immediate_action,
                                        photos, reported_by, reported_by_name, details)
     VALUES (:code, :type, :severity, :location, :container, :equipment, :description, :action, CAST(:photos AS jsonb), :userId, :userName, CAST(:details AS jsonb))
     RETURNING *`,
    {
      replacements: {
        code, type, severity, location,
        container: clean(body.containerNo, 20).toUpperCase() || null,
        equipment: clean(body.equipment, 100) || null,
        description,
        action: clean(body.immediateAction, 2000) || null,
        photos: JSON.stringify(photos),
        details: details ? JSON.stringify(details) : null,
        userId: user?.id || null,
        userName: user?.full_name || user?.fullName || user?.username || null,
      },
      type: QueryTypes.SELECT,
    },
  );
  const report = await sign(row);
  try { emitToDispatchers('yard_incident:new', report); } catch { /* realtime không làm hỏng việc lưu báo cáo */ }
  await notifyDispatchers(report);
  return report;
}

async function listReports({ status, limit } = {}) {
  await ensureTable();
  const where = STATUSES.includes(String(status)) ? 'WHERE status = :status' : '';
  const rows = await sequelize.query(
    `SELECT * FROM yard_incident_reports ${where}
      ORDER BY (status = 'RESOLVED'), CASE severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END, reported_at DESC
      LIMIT :limit`,
    { replacements: { status, limit: Math.min(Math.max(Number(limit) || 100, 1), 300) }, type: QueryTypes.SELECT },
  );
  const items = await Promise.all(rows.map(sign));
  const [counts] = await sequelize.query(
    `SELECT count(*) FILTER (WHERE status = 'NEW')::int AS "new", count(*) FILTER (WHERE status = 'ACKNOWLEDGED')::int AS "acknowledged",
            count(*) FILTER (WHERE status = 'RESOLVED')::int AS "resolved" FROM yard_incident_reports`,
    { type: QueryTypes.SELECT },
  );
  return { items, counts };
}

async function updateStatus(user, id, body = {}) {
  await ensureTable();
  const next = String(body.status || '').toUpperCase();
  if (!['ACKNOWLEDGED', 'RESOLVED'].includes(next)) throw fail('Trạng thái không hợp lệ.');
  if (!/^[0-9a-f-]{36}$/i.test(String(id))) throw fail('Mã báo cáo không hợp lệ.');
  const name = user?.full_name || user?.fullName || user?.username || null;
  const note = clean(body.note, 2000) || null;
  const [row] = await sequelize.query(
    next === 'ACKNOWLEDGED'
      ? `UPDATE yard_incident_reports SET status = 'ACKNOWLEDGED', acknowledged_by_name = :name, acknowledged_at = now()
          WHERE id = :id AND status = 'NEW' RETURNING *`
      : `UPDATE yard_incident_reports SET status = 'RESOLVED', resolved_by_name = :name, resolved_at = now(), resolution_note = :note,
                 acknowledged_by_name = COALESCE(acknowledged_by_name, :name), acknowledged_at = COALESCE(acknowledged_at, now())
          WHERE id = :id AND status <> 'RESOLVED' RETURNING *`,
    { replacements: { id, name, note }, type: QueryTypes.SELECT },
  );
  if (!row) throw fail('Báo cáo không tồn tại hoặc đã được xử lý.', 409);
  const report = await sign(row);
  try { emitToDispatchers('yard_incident:updated', report); } catch { /* bỏ qua */ }
  return report;
}

/** Báo cáo do hệ thống tự sinh (ví dụ xung đột vị trí đặt chỗ). details giữ nguyên cấu trúc, đã được hệ thống tạo nên không cần lọc. */
async function createSystemReport({ type, severity = 'HIGH', location, containerNo, description, details }) {
  await ensureTable();
  const code = `SC-${new Date().toISOString().slice(2, 10).replace(/-/g, '')}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`;
  const [row] = await sequelize.query(
    `INSERT INTO yard_incident_reports (code, incident_type, severity, location, container_no, description, photos, reported_by_name, details)
     VALUES (:code, :type, :severity, :location, :container, :description, '[]'::jsonb, 'Hệ thống bãi (tự động)', CAST(:details AS jsonb))
     RETURNING *`,
    {
      replacements: {
        code, type: clean(type, 200), severity: SEVERITIES.includes(severity) ? severity : 'HIGH', location: clean(location, 200),
        container: clean(containerNo, 20).toUpperCase() || null, description: clean(description, 4000), details: JSON.stringify(details || null),
      },
      type: QueryTypes.SELECT,
    },
  );
  const report = await sign(row);
  try { emitToDispatchers('yard_incident:new', report); } catch { /* bỏ qua */ }
  await notifyDispatchers(report);
  return report;
}

async function findOpenSlotConflicts() {
  await ensureTable();
  return sequelize.query(
    `SELECT id, container_no AS "containerNo", details->>'reservedSlot' AS "reservedSlot", status
       FROM yard_incident_reports WHERE details->>'kind' = 'SLOT_CONFLICT' AND status <> 'RESOLVED'`,
    { type: QueryTypes.SELECT },
  );
}

async function getReportRow(id) {
  await ensureTable();
  const [row] = await sequelize.query('SELECT * FROM yard_incident_reports WHERE id = :id', { replacements: { id }, type: QueryTypes.SELECT });
  return row || null;
}

/** Đóng báo cáo bằng tên người/hệ thống, kèm ghi chú. */
async function resolveReport(id, byName, note) {
  await ensureTable();
  const [row] = await sequelize.query(
    `UPDATE yard_incident_reports SET status = 'RESOLVED', resolved_by_name = :by, resolved_at = now(), resolution_note = :note,
            acknowledged_by_name = COALESCE(acknowledged_by_name, :by), acknowledged_at = COALESCE(acknowledged_at, now())
      WHERE id = :id AND status <> 'RESOLVED' RETURNING *`,
    { replacements: { id, by: byName, note: clean(note, 2000) || null }, type: QueryTypes.SELECT },
  );
  if (!row) return null;
  const report = await sign(row);
  try { emitToDispatchers('yard_incident:updated', report); } catch { /* bỏ qua */ }
  return report;
}

module.exports = { createReport, listReports, updateStatus, createSystemReport, findOpenSlotConflicts, getReportRow, resolveReport };
