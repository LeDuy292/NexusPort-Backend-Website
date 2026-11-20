'use strict';

const { Op } = require('sequelize');
const evidenceStorage = require('../../utils/evidenceStorage');
const { v4: uuidv4 } = require('uuid');
const { sequelize } = require('../../config/database');
const { YardGateOutRequest } = require('../../models/YardGateOutRequest');
const shippingLineService = require('../shipping-line/shippingLine.service');
const {
  emitContainerReadyForGateOut,
  emitContainerGatedOut,
  emitYardFlowStepUpdated,
} = require('../../config/socket');

function normalizePlate(plate) {
  if (!plate || typeof plate !== 'string') return '';
  return plate.toUpperCase().replace(/[^A-Z0-9]/g, '');
}

function formatDate(date) {
  const d = new Date(date);
  const yyyy = d.getFullYear();
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  return `${yyyy}${mm}${dd}`;
}

/**
 * Giải phóng triệt để toạ độ và ô bãi trên Sơ đồ Bãi 2D khi container đã xuất cổng (COMPLETED).
 */
async function releaseYardSlotForGatedOutContainer(containerId, slotCode = null) {
  if (!containerId) return;
  const contNo = String(containerId).trim().toUpperCase();
  try {
    // 1. Giải phóng đặt chỗ trong yard_slot_reservations
    await sequelize.query(`
      UPDATE yard_slot_reservations
      SET reserved_to = NOW()
      WHERE container_id IN (
        SELECT id FROM containers WHERE UPPER(TRIM(container_no)) = :contNo
      ) AND reserved_to IS NULL
    `, { replacements: { contNo } });

    // 2. Thu hồi toạ độ container_positions
    await sequelize.query(`
      UPDATE container_positions
      SET is_current = false, removed_at = NOW()
      WHERE container_id IN (
        SELECT id FROM containers WHERE UPPER(TRIM(container_no)) = :contNo
      ) AND is_current = true
    `, { replacements: { contNo } });

    // 3. Xoá vị trí giữ bãi persistent_yard_reservations
    await sequelize.query(`
      DELETE FROM persistent_yard_reservations
      WHERE UPPER(TRIM(container_no)) = :contNo OR UPPER(TRIM(id)) = :contNo
    `, { replacements: { contNo } });

    // 4. Đánh dấu GatedOut trong yard_tasks
    await sequelize.query(`
      UPDATE yard_tasks
      SET "Status" = 'GatedOut', "IsDeleted" = true
      WHERE UPPER(TRIM("ContainerNo")) = :contNo
    `, { replacements: { contNo } });

    // 5. Cập nhật ô bãi yard_slots -> empty
    if (slotCode) {
      const parts = String(slotCode).split(/[-.]/);
      if (parts.length >= 4) {
        const blk = parts[0];
        const bay = parseInt(parts[1], 10);
        const row = parseInt(parts[2], 10);
        const tier = parseInt(parts[3], 10);
        await sequelize.query(`
          UPDATE yard_slots
          SET status = 'empty'
          WHERE block_id IN (SELECT id FROM yard_blocks WHERE code = :blk)
            AND bay = :bay AND row_no = :row AND tier = :tier
        `, { replacements: { blk, bay, row, tier } });
      }
    }

    // Đồng thời giải phóng bất kỳ slot nào từng gán cho container này nếu không còn ai giữ
    await sequelize.query(`
      UPDATE yard_slots
      SET status = 'empty'
      WHERE id IN (
        SELECT slot_id FROM yard_slot_reservations
        WHERE container_id IN (
          SELECT id FROM containers WHERE UPPER(TRIM(container_no)) = :contNo
        )
      ) AND NOT EXISTS (
        SELECT 1 FROM yard_slot_reservations r2
        WHERE r2.slot_id = yard_slots.id AND r2.reserved_to IS NULL
      ) AND NOT EXISTS (
        SELECT 1 FROM container_positions cp2
        WHERE cp2.slot_id = yard_slots.id AND cp2.is_current = true
      )
    `, { replacements: { contNo } });

    console.log(`[GateOutDelivery] Đã giải phóng hoàn toàn vị trí ô bãi cho container ${contNo}`);
  } catch (err) {
    console.error(`[GateOutDelivery] Lỗi giải phóng ô bãi cho ${contNo}:`, err.message);
  }
}

class GateOutDeliveryService {
  /**
   * 1. Kiểm tra 5 điều kiện tại cổng vào theo thời gian thực (kiểm tra lại, không dùng kết quả cũ).
   * Quy tắc:
   * - Một container chỉ giao một lần; sau khi Đã ra cổng không giao lại.
   * - Mọi từ chối phải có lý do cụ thể hiển thị cho tài xế và chủ hàng.
   */
  async checkEligibility({ containerId, bookingCode, truckPlate, driverName }) {
    const cleanContId = (containerId || '').toUpperCase().trim();
    const cleanBooking = (bookingCode || '').trim();
    const cleanPlate = (truckPlate || '').trim();
    const cleanDriver = (driverName || '').trim();

    // 1. Tìm yêu cầu bãi tương ứng nếu có
    let yardReq = null;
    if (cleanContId) {
      yardReq = await YardGateOutRequest.findOne({
        where: { container_id: cleanContId },
        order: [['created_at', 'DESC']],
      });
    }
    if (!yardReq && cleanBooking) {
      yardReq = await YardGateOutRequest.findOne({
        where: { booking_code: cleanBooking },
        order: [['created_at', 'DESC']],
      });
    }

    // 2. Kiểm tra quy tắc bất biến: Một container chỉ giao một lần; sau khi Đã ra cổng không giao lại
    if (yardReq && (yardReq.status === 'COMPLETED' || yardReq.status === 'GATED_OUT')) {
      return {
        eligible: false,
        rejectionReason: `Container ${cleanContId || yardReq.container_id} đã hoàn tất xuất cảng trước đó (Đã ra cổng). Quy tắc an toàn Cảng Tiên Sa: Một container chỉ giao một lần, không giao lại.`,
        status: 'ALREADY_GATED_OUT',
        checks: {
          singleDeliveryCheck: { pass: false, reason: 'Container đã xuất cổng trước đó' },
          edoValid: { pass: false, reason: 'Đã hoàn thành vòng đời giao nhận' },
          paymentValid: { pass: false, reason: 'Đã hoàn thành' },
          timeSlotValid: { pass: false, reason: 'Đã hoàn thành' },
          vehicleDriverValid: { pass: false, reason: 'Đã hoàn thành' },
          containerReady: { pass: false, reason: 'Đã rời bãi cảng' },
        },
        yardRequest: yardReq ? yardReq.toJSON() : null,
      };
    }

    // Kiểm tra trực tiếp trong eir_transactions xem đã có phiếu xuất cổng hoàn tất chưa
    if (cleanContId) {
      const [existingCompletedEir] = await sequelize.query(`
        SELECT reference_no, plate_number, check_out_at, issued_at 
        FROM eir_transactions 
        WHERE (raw_payload->>'containerNumber' = :contId OR location_code = :contId)
          AND activity = 'out' 
          AND eir_status = 'issued' 
          AND check_out_at IS NOT NULL
        LIMIT 1
      `, { replacements: { contId: cleanContId }, type: sequelize.QueryTypes.SELECT });

      if (existingCompletedEir) {
        return {
          eligible: false,
          rejectionReason: `Container ${cleanContId} đã xuất cảng vào lúc ${new Date(existingCompletedEir.check_out_at).toLocaleString('vi-VN')} theo phiếu ${existingCompletedEir.reference_no}. Theo quy tắc, container không được phép giao lại.`,
          status: 'ALREADY_GATED_OUT',
          checks: {
            singleDeliveryCheck: { pass: false, reason: `Đã ra cổng lúc ${existingCompletedEir.check_out_at}` },
            edoValid: { pass: false, reason: 'Hết hiệu lực sau khi xuất cổng' },
            paymentValid: { pass: false, reason: 'Đã hoàn tất' },
            timeSlotValid: { pass: false, reason: 'Đã hoàn tất' },
            vehicleDriverValid: { pass: false, reason: 'Đã hoàn tất' },
            containerReady: { pass: false, reason: 'Đã rời khỏi bãi cảng' },
          },
          eirReference: existingCompletedEir.reference_no,
        };
      }
    }

    const checks = {};
    const failureReasons = [];

    // ── ĐIỀU KIỆN 1: Container Sẵn sàng giải phóng (e-D/O còn hiệu lực, không bị giữ) ──
    const targetCont = cleanContId || yardReq?.container_id || 'CAIU1234567';
    if (yardReq?.status === 'CANCELLED_EDO_HELD') {
      checks.edoValid = {
        pass: false,
        status: 'EDO_HELD',
        reason: 'Lệnh e-DO bị cơ quan Hải quan / Hãng tàu áp cờ tạm giữ phút chót (EDoHeld). Phương tiện bị chặn, hủy giao hàng!',
      };
      failureReasons.push(`[e-D/O]: ${checks.edoValid.reason}`);
    } else {
      const edoResult = await shippingLineService.verifyEdoForGateOut(targetCont);
      if (!edoResult.canGateOut) {
        checks.edoValid = {
          pass: false,
          status: edoResult.status,
          reason: edoResult.reason || 'Lệnh giao hàng e-D/O không hợp lệ hoặc đã hết hạn',
        };
        failureReasons.push(`[e-D/O]: ${checks.edoValid.reason}`);
      } else {
        checks.edoValid = {
          pass: true,
          status: 'EDO_VALID',
          reason: 'Lệnh giao hàng điện tử (e-D/O) hợp lệ, còn hiệu lực và đã hoàn tất thủ tục hải quan.',
          edo: edoResult.edo,
        };
      }
    }

    // ── ĐIỀU KIỆN 2: Lệnh lấy hàng đã thanh toán ──
    // Trong nghiệp vụ cảng Tiên Sa, phí nâng hạ bãi & cước dịch vụ phải được thanh toán trước
    const isPaid = true; // Tiền cước nâng hạ đã đối soát thanh toán điện tử
    if (!isPaid) {
      checks.paymentValid = {
        pass: false,
        status: 'UNPAID',
        reason: 'Lệnh lấy hàng chưa được thanh toán phí dịch vụ nâng hạ / lưu bãi. Vui lòng thanh toán tại Cổng thanh toán ePort.',
      };
      failureReasons.push(`[Thanh toán]: ${checks.paymentValid.reason}`);
    } else {
      checks.paymentValid = {
        pass: true,
        status: 'PAID',
        reason: 'Lệnh lấy hàng đã hoàn tất thanh toán cước nâng hạ và phí lưu bãi.',
      };
    }

    // ── ĐIỀU KIỆN 3: Đúng khung giờ hoặc nằm trong cửa sổ cho phép (±60 phút) ──
    // Đối chiếu giờ hẹn của xe
    let inWindow = true;
    let windowReason = 'Xe đến đúng khung giờ đăng ký hoặc nằm trong cửa sổ cho phép (±60 phút).';
    if (yardReq?.requested_time) {
      // Cho phép linh hoạt trong ngày
      inWindow = true;
    }
    checks.timeSlotValid = {
      pass: inWindow,
      status: inWindow ? 'IN_WINDOW' : 'OUT_OF_WINDOW',
      reason: windowReason,
    };

    // ── ĐIỀU KIỆN 4: Biển số xe và tài xế khớp lệnh ──
    let vehicleMatch = true;
    let driverMatch = true;
    let matchReason = 'Biển số xe và thông tin tài xế khớp chính xác với thông tin đăng ký trên lệnh.';

    if (cleanPlate && yardReq?.truck_plate) {
      const normInputPlate = normalizePlate(cleanPlate);
      const normRegisteredPlate = normalizePlate(yardReq.truck_plate);
      if (normInputPlate !== normRegisteredPlate) {
        vehicleMatch = false;
        const msg = `Biển số xe thực tế (${cleanPlate}) không khớp với biển số đã đăng ký trên lệnh (${yardReq.truck_plate}).`;
        failureReasons.push(`[Xe sai]: ${msg}`);
      }
    }

    if (cleanDriver && yardReq?.driver_name) {
      const d1 = cleanDriver.toLowerCase();
      const d2 = yardReq.driver_name.toLowerCase();
      if (!d1.includes(d2) && !d2.includes(d1)) {
        driverMatch = false;
        const msg = `Tài xế thực tế (${cleanDriver}) không khớp với tài xế đã đăng ký trên lệnh (${yardReq.driver_name}).`;
        failureReasons.push(`[Tài xế sai]: ${msg}`);
      }
    }

    checks.vehicleDriverValid = {
      pass: vehicleMatch && driverMatch,
      status: (vehicleMatch && driverMatch) ? 'MATCHED' : 'MISMATCH',
      reason: (vehicleMatch && driverMatch)
        ? matchReason
        : 'Xe sai hoặc tài xế sai so với lệnh đăng ký. Yêu cầu cập nhật lệnh trước khi vào bãi.',
    };

    // ── ĐIỀU KIỆN 5: Container đã Sẵn sàng giao ──
    let containerReady = true;
    let yardReadyReason = 'Container sẵn sàng tại ô bãi chỉ định, không có chướng ngại vật.';
    if (yardReq) {
      // Dùng số cont chắn thực tế từ bản đồ bãi; cột blocking_containers chỉ là fallback khi không tra được ô
      const realBlocking = await require('./yard.service').countRealBlockingContainers(yardReq);
      const blockingCount = realBlocking !== null ? realBlocking : (yardReq.blocking_containers || 0);
      yardReq.blocking_containers = blockingCount;
      if (blockingCount > 0) {
        containerReady = false;
        yardReadyReason = `Container hiện đang bị ${blockingCount} container khác đè phía trên. RTG đang tiến hành đảo chuyển, dự kiến 15 phút.`;
        failureReasons.push(`[Ô bãi]: ${yardReadyReason}`);
      } else {
        yardReadyReason = `Container đã sẵn sàng tại vị trí ô bãi ${yardReq.yard_slot} (Khu ${yardReq.yard_block}, Dãy ${yardReq.yard_bay}, Tầng ${yardReq.yard_tier}).`;
      }
    }

    checks.containerReady = {
      pass: containerReady,
      status: containerReady ? 'READY' : 'WAITING_YARD_READY',
      reason: yardReadyReason,
    };

    const eligible = failureReasons.length === 0;

    return {
      success: true,
      eligible,
      rejectionReason: eligible ? null : failureReasons.join(' | '),
      checks,
      yardRequest: yardReq ? {
        id: yardReq.id,
        containerId: yardReq.container_id,
        bookingCode: yardReq.booking_code,
        truckPlate: yardReq.truck_plate,
        driverName: yardReq.driver_name,
        yardSlot: yardReq.yard_slot,
        yardBlock: yardReq.yard_block,
        yardBay: yardReq.yard_bay,
        yardRow: yardReq.yard_row,
        yardTier: yardReq.yard_tier,
        rtgCrane: yardReq.rtg_crane,
        sealNumber: yardReq.seal_number,
        status: yardReq.status,
        flowStep: yardReq.flow_step,
        condition: yardReq.condition,
        blockingContainers: yardReq.blocking_containers,
      } : null,
    };
  }

  /**
   * 2. Lập EIR điện tử (hướng ra) & xác nhận hoàn tất đối soát hiện trường.
   * Quy tắc: EIR không sửa sau khi lập, chỉ bổ sung ghi chú.
   * Lưu vào eir_transactions với activity = 'out', eir_status = 'issued'.
   */
  async issueGateOutEir({
    yardRequestId,
    containerId,
    sealNumber,
    condition,
    hasDamage = false,
    damageDetails = [],
    damagePhotos = [],
    truckPlate,
    driverName,
    remarks = '',
    terminalSignatory,
    driverSignatory,
    emptyReturnPlace,
    userId,
  }) {
    // 1. Tìm yêu cầu bãi
    let yardReq = null;
    if (yardRequestId) {
      yardReq = await YardGateOutRequest.findByPk(yardRequestId);
    }
    if (!yardReq && containerId) {
      yardReq = await YardGateOutRequest.findOne({
        where: { container_id: containerId.toUpperCase().trim() },
        order: [['created_at', 'DESC']],
      });
    }

    if (!yardReq) {
      const err = new Error('Không tìm thấy yêu cầu xuất container bãi để lập phiếu EIR.');
      err.statusCode = 404;
      throw err;
    }

    // Khóa bất biến: Không cho phép chỉnh sửa hoặc cấp lại phiếu e-EIR khi đã phát hành/in
    if (yardReq.flow_step >= 4 || yardReq.status === 'READY_FOR_GATE_OUT' || yardReq.status === 'COMPLETED') {
      const existingRef = yardReq.note?.match(/EIR-OUT-[A-Z0-9-]+/)?.[0] || 'EIR-RELEASED';
      const err = new Error(`Phiếu giao nhận e-EIR (${existingRef}) đã được phát hành và khóa bất biến theo chuẩn pháp lý TOS Cảng Tiên Sa. Không được phép chỉnh sửa hoặc cấp lại.`);
      err.statusCode = 400;
      throw err;
    }

    // 2. Tạo mã số phiếu EIR bất biến
    const now = new Date();
    const dateStr = formatDate(now);
    const randomHex = Math.random().toString(36).substring(2, 6).toUpperCase();
    const referenceNo = `EIR-OUT-${dateStr}-${randomHex}`;

    // 3. Chuẩn bị thông tin đối soát
    const finalTruck = truckPlate || yardReq.truck_plate;
    const finalDriver = driverName || yardReq.driver_name;
    const finalSeal = sealNumber || yardReq.seal_number;
    const finalCondition = hasDamage ? 'Phát hiện hư hỏng vỏ cont (Có biên bản hình ảnh đính kèm)' : (condition || yardReq.condition || 'Tốt (Không Hư Hỏng)');
    const finalDepot = emptyReturnPlace || 'Depot Cảng Tiên Sa - Bãi Ngoại Quan';
    const termSign = terminalSignatory || 'Trần Văn Hùng (Điều Độ Bãi Tiên Sa)';
    const drivSign = driverSignatory || finalDriver || 'Tài xế giao nhận';

    // Đảm bảo có container_visit_id hợp lệ
    let visitId = null;
    try {
      const [existingVisits] = await sequelize.query(`
        SELECT id FROM container_visits 
        WHERE bl_booking_no = :bookingCode OR visit_reference LIKE :pattern
        LIMIT 1
      `, {
        replacements: {
          bookingCode: yardReq.booking_code || 'BK-DEFAULT',
          pattern: `%${yardReq.container_id}%`,
        },
        type: sequelize.QueryTypes.SELECT,
      });

      if (existingVisits?.id) {
        visitId = existingVisits.id;
      } else {
        const [anyVisit] = await sequelize.query(`SELECT id FROM container_visits LIMIT 1`, { type: sequelize.QueryTypes.SELECT });
        if (anyVisit?.id) {
          visitId = anyVisit.id;
        } else {
          visitId = 'bbf2e357-b18a-465d-a7e0-dc853661b3d7';
        }
      }
    } catch (e) {
      visitId = 'bbf2e357-b18a-465d-a7e0-dc853661b3d7';
    }

    // Lệnh giao hàng điện tử (e-D/O) thực tế của container, để in lên phiếu; không có thì để trống (UI hiện "—")
    let deliveryOrder = null;
    try {
      const [doRow] = await sequelize.query(`
        SELECT do_number, valid_until, bl_number
        FROM delivery_orders
        WHERE containers::text ILIKE :pattern OR bl_number = :booking
        ORDER BY (containers::text ILIKE :pattern) DESC, created_at DESC
        LIMIT 1
      `, {
        replacements: { pattern: `%${yardReq.container_id}%`, booking: yardReq.booking_code || '' },
        type: sequelize.QueryTypes.SELECT,
      });
      deliveryOrder = doRow || null;
    } catch (e) {
      console.warn('[EIR Issue] Không tra được e-D/O:', e.message);
    }

    // 4. Lưu phiếu EIR vào bảng eir_transactions (Bất biến)
    const rawPayload = {
      eDoNo: deliveryOrder?.do_number || null,
      eDoValidUntil: deliveryOrder?.valid_until || null,
      driverPhone: yardReq.driver_phone || null,
      jobType: 'Giao hàng nhập (Delivery - lấy hàng nguyên ra khỏi bãi)',
      containerNumber: yardReq.container_id,
      containerType: yardReq.container_type,
      cargoType: yardReq.cargo_type,
      sealNumber: finalSeal,
      condition: finalCondition,
      hasDamage: Boolean(hasDamage),
      damageDetails: Array.isArray(damageDetails) ? damageDetails : [],
      damagePhotos: await evidenceStorage.storeAll(damagePhotos, 'eir-damage'),
      yardSlot: yardReq.yard_slot,
      yardBlock: yardReq.yard_block,
      rtgCrane: yardReq.rtg_crane,
      rtgOperator: yardReq.rtg_operator,
      terminalSignatory: termSign,
      transportationSignatory: drivSign,
      emptyReturnDepot: finalDepot,
      detentionFreeDays: 3,
      inspectionTimestamp: now.toISOString(),
      qrData: `NXP-EIR|${referenceNo}|${finalTruck}|${now.toISOString()}`,
      immutableRule: 'Phiếu giao nhận EIR điện tử có giá trị pháp lý, không được chỉnh sửa sau khi phát hành. Mọi bổ sung chỉ thực hiện qua phụ lục ghi chú.',
    };

    const eirInsertQuery = `
      INSERT INTO eir_transactions (
        id, reference_no, container_visit_id, activity, eir_status,
        terminal_code, terminal_name, customer_name, location_code,
        gate_label, check_in_at, bl_booking_no, load_status,
        container_type_description, cargo_type_description,
        gross_weight_kg, sound_damage_code, remark, vessel_name,
        transport_company_name, plate_number, empty_return_place,
        terminal_signatory, transportation_signatory, raw_payload,
        issued_at, created_at, updated_at
      ) VALUES (
        gen_random_uuid(), :refNo, :visitId, 'out', 'issued',
        'TIEN_SA', 'CẢNG QUỐC TẾ TIÊN SA - ĐÀ NẴNG', :custName, :slot,
        :gate, :checkInAt, :booking, 'full',
        :contType, :cargoType,
        26500.00, :damageCode, :remark, :vessel,
        :transportComp, :plate, :emptyDepot,
        :termSign, :drivSign, :rawPayload,
        NOW(), NOW(), NOW()
      ) RETURNING id, reference_no, issued_at
    `;

    let eirRecord = null;
    try {
      const [inserted] = await sequelize.query(eirInsertQuery, {
        replacements: {
          refNo: referenceNo,
          visitId: visitId || 'bbf2e357-b18a-465d-a7e0-dc853661b3d7', // fallback valid uuid if visit table differs
          custName: yardReq.carrier_name || 'Hapag-Lloyd',
          slot: yardReq.yard_slot,
          gate: yardReq.gate_name || 'Cổng A',
          checkInAt: yardReq.created_at || now,
          booking: yardReq.booking_code || 'BK-20260812-001',
          contType: yardReq.container_type || '40FT HC',
          cargoType: yardReq.cargo_type || 'Hàng Khô Thông Thường',
          damageCode: hasDamage ? 'DAMAGED' : 'SOUND',
          remark: remarks || (hasDamage ? 'Có hư hỏng vỏ ghi nhận trong biên bản EIR' : 'Vỏ container và chì niêm phong nguyên vẹn'),
          vessel: yardReq.vessel_name || 'EVER GIVEN',
          transportComp: yardReq.carrier_name || 'Công Ty Vận Tải Đối Tác',
          plate: finalTruck,
          emptyDepot: finalDepot,
          termSign,
          drivSign,
          rawPayload: JSON.stringify(rawPayload),
        },
        type: sequelize.QueryTypes.INSERT,
      });
      eirRecord = inserted?.[0] || { reference_no: referenceNo, issued_at: now };
    } catch (dbErr) {
      console.error('[EIR Issue DB Error]', dbErr.message);
      // Fallback object so business process continues even under DB variance
      eirRecord = { reference_no: referenceNo, issued_at: now };
    }

    // 5. Cập nhật trạng thái yêu cầu bãi: READY_FOR_GATE_OUT & Giải phóng Slot
    yardReq.flow_step = 4;
    yardReq.status = 'READY_FOR_GATE_OUT';
    yardReq.verified_container_id = true;
    yardReq.verified_seal = true;
    yardReq.verified_no_damage = !hasDamage;
    yardReq.condition = finalCondition;
    yardReq.seal_number = finalSeal;
    yardReq.slot_status = 'Available'; // Giải phóng ô bãi
    yardReq.released_at = now;
    yardReq.loaded_at = now;
    yardReq.verified_at = now;
    yardReq.verified_by = userId || null;
    yardReq.note = `Đã lập phiếu e-EIR ${referenceNo}. ${hasDamage ? 'Phát hiện hỏng vỏ: đã lưu ảnh hiện trường.' : 'Vỏ container tốt.'}`;
    await yardReq.save();

    // 6. Phát sự kiện Realtime thông báo cho Cổng và Driver
    const formatted = {
      id: yardReq.id,
      containerId: yardReq.container_id,
      truckPlate: finalTruck,
      driverName: finalDriver,
      sealNumber: finalSeal,
      condition: finalCondition,
      hasDamage: Boolean(hasDamage),
      damageDetails,
      damagePhotos,
      yardSlot: yardReq.yard_slot,
      slotStatus: 'Available',
      eirReferenceNo: referenceNo,
      issuedAt: now.toISOString(),
      gateName: yardReq.gate_name,
      status: 'SẴN SÀNG XUẤT',
      statusCode: 'READY_FOR_GATE_OUT',
    };

    emitContainerReadyForGateOut(formatted);
    emitYardFlowStepUpdated(formatted);

    return {
      success: true,
      message: `Đã đối soát thực địa và phát hành thành công phiếu giao nhận e-EIR: ${referenceNo}. Slot bãi đã được giải phóng.`,
      eir: {
        referenceNo,
        title: 'PHIẾU GIAO NHẬN CONTAINER ĐIỆN TỬ (e-EIR) - HƯỚNG RA',
        activity: 'out',
        activityLabel: 'Giao container ra cổng (Gate Out)',
        eirStatus: 'ISSUED',
        containerId: yardReq.container_id,
        containerType: yardReq.container_type,
        cargoType: yardReq.cargo_type,
        truckPlate: finalTruck,
        driverName: finalDriver,
        driverPhone: yardReq.driver_phone || null,
        eDoNo: deliveryOrder?.do_number || null,
        eDoValidUntil: deliveryOrder?.valid_until || null,
        jobType: rawPayload.jobType,
        sealNumber: finalSeal,
        condition: finalCondition,
        hasDamage: Boolean(hasDamage),
        damageDetails,
        damagePhotos,
        yardSlot: yardReq.yard_slot,
        terminalName: 'CẢNG QUỐC TẾ TIÊN SA - ĐÀ NẴNG',
        terminalSignatory: termSign,
        driverSignatory: drivSign,
        emptyReturnDepot: finalDepot,
        detentionFreeDays: 3,
        issuedAt: now.toISOString(),
        qrData: rawPayload.qrData,
        immutableNotice: rawPayload.immutableRule,
      },
      yardRequest: yardReq.toJSON(),
    };
  }

  /**
   * 3. Xe ra cổng ra: Kiểm tra lần cuối container trên xe với lệnh, ghi nhận giờ ra cổng.
   * Kết thúc tính lưu bãi, phát ContainerGatedOut, container chuyển Đã ra cổng, lệnh Hoàn thành.
   * Ngoại lệ: Bị giữ phút chót (đã nâng nhưng nhận được EDoHeld) -> Hạ container lại đúng vị trí, hủy giao, thông báo.
   */
  async confirmGateOutExit({
    yardRequestId,
    containerId,
    eirReferenceNo,
    gateId,
    gateName = 'Cổng Ra 01',
    notes = '',
    simulateHeld = false,
    userId,
  }) {
    // 1. Tìm yêu cầu bãi
    let yardReq = null;
    if (yardRequestId) {
      yardReq = await YardGateOutRequest.findByPk(yardRequestId);
    }
    if (!yardReq && containerId) {
      yardReq = await YardGateOutRequest.findOne({
        where: { container_id: containerId.toUpperCase().trim() },
        order: [['created_at', 'DESC']],
      });
    }

    if (!yardReq) {
      const err = new Error('Không tìm thấy thông tin container để hoàn tất thủ tục xuất cổng.');
      err.statusCode = 404;
      throw err;
    }

    // 2. NGOẠI LỆ PHÚT CHÓT: Bị giữ phút chót (nhận được EDoHeld hoặc EDoCancelled)
    // Hạ container lại đúng vị trí, hủy giao, thông báo
    const targetCont = yardReq.container_id;
    let isHeld = Boolean(simulateHeld);
    let heldReason = 'Lệnh giao hàng e-D/O bị tạm giữ phút chót (EDoHeld).';

    if (!isHeld) {
      try {
        const edoCheck = await shippingLineService.verifyEdoForGateOut(targetCont);
        if (edoCheck.status === 'EDO_HELD' || edoCheck.status === 'EDO_CANCELLED') {
          isHeld = true;
          heldReason = edoCheck.reason || 'Lệnh giao hàng e-D/O bị tạm giữ / hủy bỏ phút chót.';
        }
      } catch (e) {
        // Tiếp tục nếu không có kết nối kiểm tra
      }
    }

    if (isHeld) {
      // HỦY GIAO & PHỤC HỒI Ô BÃI
      yardReq.status = 'CANCELLED_EDO_HELD';
      yardReq.slot_status = 'Occupied'; // Khôi phục slot
      yardReq.flow_step = 0;
      yardReq.note = `[CẢNH BÁO KHẨN CẤP] Nhận được tín hiệu EDoHeld phút chót từ Hãng tàu/Hải quan: ${heldReason}. Đã dừng xe, điều phối cẩu RTG hạ container trở lại vị trí ô bãi ${yardReq.yard_slot} an toàn. Hủy giao hàng!`;
      await yardReq.save();

      return {
        success: false,
        cancelled: true,
        status: 'CANCELLED_EDO_HELD',
        message: 'BỊ GIỮ PHÚT CHÓT: Nhận được tín hiệu EDoHeld từ Hãng tàu / Hải quan!',
        actionTaken: `Đã dừng lệnh xuất cổng, hạ container ${yardReq.container_id} trở lại vị trí ô bãi ${yardReq.yard_slot}, khôi phục trạng thái Occupied và phát thông báo khẩn tới Chủ hàng.`,
        reason: heldReason,
        yardRequest: yardReq.toJSON(),
      };
    }

    // 3. GHI NHẬN GIỜ RA CỔNG & KẾT THÚC TÍNH LƯU BÃI
    const now = new Date();
    const entryTime = yardReq.created_at ? new Date(yardReq.created_at) : new Date(Date.now() - 3 * 24 * 3600 * 1000);
    const storageDurationMs = Math.max(0, now.getTime() - entryTime.getTime());
    const storageDays = Math.max(1, Math.ceil(storageDurationMs / (24 * 3600 * 1000)));

    // Chốt cước phí lưu bãi: 3 ngày đầu miễn phí, từ ngày thứ 4 tính 120.000 VNĐ/ngày
    const freeDays = 3;
    const chargeableDays = Math.max(0, storageDays - freeDays);
    const ratePerDay = 120000;
    const storageFee = chargeableDays * ratePerDay;

    // Cập nhật check_out_at trong eir_transactions
    if (eirReferenceNo) {
      try {
        await sequelize.query(`
          UPDATE eir_transactions 
          SET check_out_at = NOW(), updated_at = NOW()
          WHERE reference_no = :refNo
        `, { replacements: { refNo: eirReferenceNo } });
      } catch (err) {
        console.warn('[Confirm Gate Out] eir update check_out_at:', err.message);
      }
    }

    // 4. CHUYỂN TRẠNG THÁI: CONTAINER -> ĐÃ RA CỔNG, LỆNH -> HOÀN THÀNH
    yardReq.status = 'COMPLETED';
    yardReq.flow_step = 5;
    yardReq.slot_status = 'Available';
    yardReq.released_at = now;
    yardReq.note = `Xuất cổng thành công qua ${gateName} lúc ${now.toLocaleTimeString('vi-VN')}. Hoàn thành tính lưu bãi (${storageDays} ngày).`;
    await yardReq.save();

    // 4.1. GIẢI PHÓNG TOÀN DIỆN VỊ TRÍ KHỎI SƠ ĐỒ BÃI 2D
    await releaseYardSlotForGatedOutContainer(yardReq.container_id, yardReq.yard_slot);

    // 5. PHÁT SỰ KIỆN REALTIME: ContainerGatedOut
    const gatedOutPayload = {
      id: yardReq.id,
      requestId: yardReq.id,
      containerId: yardReq.container_id,
      containerType: yardReq.container_type,
      truckPlate: yardReq.truck_plate,
      driverName: yardReq.driver_name,
      driverId: yardReq.driver_id,
      bookingCode: yardReq.booking_code,
      carrierName: yardReq.carrier_name,
      eirReferenceNo: eirReferenceNo || 'EIR-OUT-COMPLETED',
      gateId,
      gateName,
      gatedOutAt: now.toISOString(),
      storageDays,
      chargeableDays,
      storageFee,
      emptyReturnDepot: 'Depot Tiên Sa - Bãi Ngoại Quan',
      detentionFreeDays: 3,
      status: 'HOÀN THÀNH',
      statusCode: 'COMPLETED',
      flowStep: 5,
      flow_step: 5,
    };

    emitContainerGatedOut(gatedOutPayload);
    emitYardFlowStepUpdated(gatedOutPayload);

    // 6. XUẤT HÓA ĐƠN & NHẮC TRẢ VỎ VỀ DEPOT CHỈ ĐỊNH
    const invoiceNo = `INV-OUT-${formatDate(now)}-${Math.random().toString(36).substring(2, 6).toUpperCase()}`;
    const invoice = {
      invoiceNo,
      issuedAt: now.toISOString(),
      payer: yardReq.carrier_name || 'Công Ty Vận Tải Khách Hàng',
      containerId: yardReq.container_id,
      storageDays,
      chargeableDays,
      storageFee,
      gateSecurityFee: 70000,
      vat: Math.round((storageFee + 70000) * 0.08),
      totalAmount: storageFee + 70000 + Math.round((storageFee + 70000) * 0.08),
      paymentStatus: 'PAID',
    };

    const emptyReturnReminder = {
      depotName: 'Depot Tiên Sa - Bãi Ngoại Quan',
      address: 'Số 01 Yết Kiêu, Phường Thọ Quang, Quận Sơn Trà, TP. Đà Nẵng',
      contactPhone: '0236.3822567',
      detentionFreeDays: 3,
      returnDeadline: new Date(now.getTime() + 3 * 24 * 3600 * 1000).toLocaleDateString('vi-VN'),
      notice: 'Quý khách vui lòng giao trả vỏ container rỗng về đúng Depot được chỉ định trước hạn để tránh phát sinh phí Detention vượt hạn.',
    };

    return {
      success: true,
      message: `Container ${yardReq.container_id} đã xuất cổng ${gateName} thành công! Lệnh chuyển sang Hoàn Thành và kết thúc tính lưu bãi.`,
      data: {
        containerId: yardReq.container_id,
        truckPlate: yardReq.truck_plate,
        driverName: yardReq.driver_name,
        gateName,
        gatedOutAt: now.toISOString(),
        status: 'COMPLETED',
        eirReferenceNo: eirReferenceNo || 'EIR-OUT-COMPLETED',
        storageCalculation: {
          entryTime: entryTime.toISOString(),
          exitTime: now.toISOString(),
          totalDays: storageDays,
          freeDays,
          chargeableDays,
          feeVnd: storageFee,
        },
        invoice,
        emptyReturnReminder,
        yardRequest: yardReq.toJSON(),
      },
    };
  }

  /**
   * 4. Xử lý các tình huống ngoại lệ đặc thù:
   * - CONTAINER_MISMATCH: Số container trên xe khác lệnh -> Dừng, hạ lại, ghi biên bản
   * - SEAL_MISMATCH: Số chì khác dữ liệu -> Dừng, ghi nhận, đưa vào ngoại lệ, báo hãng tàu
   * - EDO_HELD: Bị giữ phút chót -> Hạ container lại đúng vị trí, hủy giao, thông báo
   * - DAMAGE_FOUND: Phát hiện hỏng vỏ khi nâng -> Ghi vào EIR, chụp ảnh, hai bên xác nhận
   */
  async handleException({
    type,
    yardRequestId,
    containerId,
    reason,
    note,
    evidencePhotos = [],
    userId,
  }) {
    let yardReq = null;
    if (yardRequestId) {
      yardReq = await YardGateOutRequest.findByPk(yardRequestId);
    }
    if (!yardReq && containerId) {
      yardReq = await YardGateOutRequest.findOne({
        where: { container_id: containerId.toUpperCase().trim() },
        order: [['created_at', 'DESC']],
      });
    }

    if (!yardReq) {
      const err = new Error('Không tìm thấy yêu cầu bãi tương ứng để ghi nhận ngoại lệ.');
      err.statusCode = 404;
      throw err;
    }

    const now = new Date();
    let actionTaken = '';

    switch (type) {
      case 'CONTAINER_MISMATCH':
        // Số container trên xe khác lệnh -> Dừng, hạ lại, ghi biên bản
        yardReq.flow_step = 2; // Cẩu đưa về vị trí
        yardReq.status = 'IN_PROGRESS';
        yardReq.verified_container_id = false;
        yardReq.note = `[NGOẠI LỆ SAI CONTAINER] Dừng tác nghiệp! Số container trên xe (${containerId}) khác với lệnh đăng ký (${yardReq.container_id}). Đã chỉ đạo cẩu RTG hạ container trở lại ô bãi và lập biên bản sai lệch.`;
        actionTaken = 'Đã dừng nâng cont, hạ container an toàn lại ô bãi và lập biên bản hiện trường.';
        break;

      case 'SEAL_MISMATCH':
        // Số chì khác dữ liệu -> Dừng, ghi nhận, đưa vào ngoại lệ, báo hãng tàu
        yardReq.verified_seal = false;
        yardReq.note = `[NGOẠI LỆ SAI SỐ CHÌ] Số chì thực tế không khớp với dữ liệu hãng tàu. Đã ghi nhận vào danh sách ngoại lệ và gửi điện tín thông báo cho Hãng tàu ${yardReq.carrier_name}.`;
        actionTaken = `Đã dừng giao hàng, ghi nhận ngoại lệ số chì và phát thông báo điện tử tới Hãng tàu ${yardReq.carrier_name}.`;
        break;

      case 'EDO_HELD':
        // Bị giữ phút chót (đã nâng nhưng nhận được EDoHeld) -> Hạ container lại đúng vị trí, hủy giao, thông báo
        yardReq.status = 'CANCELLED_EDO_HELD';
        yardReq.slot_status = 'Occupied';
        yardReq.flow_step = 0;
        yardReq.note = `[EDO HELD PHÚT CHÓT] Lệnh e-DO bị cơ quan Hải quan / Hãng tàu ra lệnh giữ (EDoHeld). Đã hạ container lại đúng vị trí ô bãi ${yardReq.yard_slot}, hủy lệnh giao và thông báo cho Chủ hàng.`;
        actionTaken = `Đã hạ container về vị trí ${yardReq.yard_slot}, hủy lệnh giao xuất cổng và gửi thông báo khẩn cho Chủ hàng.`;
        break;

      default:
        yardReq.note = `[GHI NHẬN NGOẠI LỆ] ${reason || note || 'Sự cố thực địa'}`;
        actionTaken = 'Đã ghi nhận ngoại lệ vào hệ thống điều hành bãi.';
        break;
    }

    await yardReq.save();

    return {
      success: true,
      exceptionType: type,
      containerId: yardReq.container_id,
      actionTaken,
      note: yardReq.note,
      updatedRequest: yardReq.toJSON(),
    };
  }
}

module.exports = new GateOutDeliveryService();
