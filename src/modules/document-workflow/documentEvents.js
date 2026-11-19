'use strict';

const { sequelize } = require('../../config/database');

/**
 * Ghi một dòng lịch sử cho hồ sơ chứng từ (bảng shipment_document_events, chỉ ghi thêm).
 * Không ném lỗi ra ngoài: lỗi ghi lịch sử không được làm hỏng thao tác nghiệp vụ chính.
 *
 * @param {Object} e
 * @param {'EXPORT'|'IMPORT'} e.direction
 * @param {string} e.fileRef        Số booking (xuất) hoặc số B/L (nhập)
 * @param {string} e.documentType   BOOKING | SI | VGM | DRAFT_BL | BL | PRE_ALERT | ARRIVAL_NOTICE | DO | PAYMENT
 * @param {string} e.action
 * @param {Object} [e.actor]        { id, username, role } lấy từ req.user
 */
async function recordDocumentEvent(e) {
  try {
    const actor = e.actor || {};
    await sequelize.query(
      `INSERT INTO shipment_document_events
        (direction, file_ref, document_type, action, from_status, to_status,
         actor_user_id, actor_username, actor_role, channel, field_changes,
         document_version, snapshot, note)
       VALUES
        (:direction, :fileRef, :documentType, :action, :fromStatus, :toStatus,
         :actorId, :actorUsername, :actorRole, :channel, CAST(:fieldChanges AS jsonb),
         :documentVersion, CAST(:snapshot AS jsonb), :note)`,
      {
        replacements: {
          direction: e.direction,
          fileRef: String(e.fileRef),
          documentType: e.documentType,
          action: e.action,
          fromStatus: e.fromStatus || null,
          toStatus: e.toStatus || null,
          actorId: actor.id || null,
          actorUsername: actor.username || null,
          actorRole: actor.role || null,
          channel: e.channel || (actor.id ? 'PORTAL' : 'SYSTEM'),
          fieldChanges: JSON.stringify(e.fieldChanges || []),
          documentVersion: e.documentVersion ?? null,
          snapshot: e.snapshot ? JSON.stringify(e.snapshot) : null,
          note: e.note || null,
        },
      },
    );
  } catch (err) {
    console.warn('[DocumentEvents] Không ghi được lịch sử:', err.message);
  }
}

async function getDocumentEvents(direction, fileRef) {
  const [rows] = await sequelize.query(
    `SELECT id, direction, file_ref AS "fileRef", document_type AS "documentType", action,
            from_status AS "fromStatus", to_status AS "toStatus",
            actor_user_id AS "actorUserId", actor_username AS "actorUsername", actor_role AS "actorRole",
            channel, field_changes AS "fieldChanges", document_version AS "documentVersion", note, created_at AS "createdAt"
       FROM shipment_document_events
      WHERE direction = :direction AND file_ref = :fileRef
      ORDER BY created_at, id`,
    { replacements: { direction, fileRef: String(fileRef) } },
  );
  return rows;
}

module.exports = { recordDocumentEvent, getDocumentEvents };
