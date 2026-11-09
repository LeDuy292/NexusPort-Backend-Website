'use strict';

const {
  vessels,
  voyages,
  cutOffHistory,
  billsOfLading,
  shippingInstructions,
  manifests,
  deliveryOrders,
  portReconciliationRecords,
  resetData,
} = require('./shippingLine.data');

// ISO 6346 validation helper: 4 chữ cái (mã chủ sở hữu/loại) + 6 số sê-ri + 1 chữ số kiểm tra (Check digit)
function validateContainerNumberISO(containerNo) {
  if (!containerNo || typeof containerNo !== 'string') {
    return { isValid: false, reason: 'Số container không được để trống' };
  }
  const clean = containerNo.trim().toUpperCase();
  if (clean.length !== 11) {
    return { isValid: false, reason: 'Số container phải có đúng 11 ký tự (4 chữ cái + 7 chữ số)' };
  }
  const isoRegex = /^[A-Z]{4}\d{7}$/;
  if (!isoRegex.test(clean)) {
    return { isValid: false, reason: 'Định dạng container phải gồm 4 chữ cái in hoa và 7 chữ số' };
  }

  const charValues = {
    A: 10, B: 12, C: 13, D: 14, E: 15, F: 16, G: 17, H: 18, I: 19, J: 20,
    K: 21, L: 23, M: 24, N: 25, O: 26, P: 27, Q: 28, R: 29, S: 30, T: 31,
    U: 32, V: 34, W: 35, X: 36, Y: 37, Z: 38
  };

  let sum = 0;
  for (let i = 0; i < 10; i++) {
    const char = clean[i];
    const val = (char >= '0' && char <= '9') ? parseInt(char, 10) : charValues[char];
    sum += val * Math.pow(2, i);
  }

  const remainder = sum % 11;
  const expectedCheckDigit = remainder === 10 ? 0 : remainder;
  const actualCheckDigit = parseInt(clean[10], 10);

  if (actualCheckDigit !== expectedCheckDigit) {
    return {
      isValid: false,
      expectedCheckDigit,
      actualCheckDigit,
      reason: `Chữ số kiểm tra sai chuẩn ISO 6346 (Đang ghi: ${actualCheckDigit}, chuẩn ISO yêu cầu: ${expectedCheckDigit})`,
    };
  }

  return { isValid: true, expectedCheckDigit, actualCheckDigit };
}

class ShippingLineService {

  // Khôi phục dữ liệu mẫu về trạng thái gốc
  async resetSeedData() {
    const summary = resetData();
    return summary;
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // 1. DASHBOARD OVERVIEW
  // ═══════════════════════════════════════════════════════════════════════════
  async getDashboardSummary() {
    // 1. KPIs
    const totalVessels = vessels.length;
    const activeVoyages = voyages.filter(v => ['SCHEDULED', 'IN_TRANSIT', 'BERTHED', 'OPERATING'].includes(v.status)).length;
    
    let totalContainers = 0;
    manifests.forEach(m => { totalContainers += (m.records ? m.records.length : 0); });

    const totalManifests = manifests.length;
    const totalBillsOfLading = billsOfLading.length;
    const pendingEdos = deliveryOrders.filter(d => d.status === 'PENDING').length;
    const issuedEdos = deliveryOrders.filter(d => d.status === 'ISSUED').length;
    const expiredEdos = deliveryOrders.filter(d => d.status === 'EXPIRED').length;

    // 2. Cảnh báo nghiệp vụ (Alerts)
    const alerts = [];
    
    // Cảnh báo Cut-off sắp đến
    voyages.forEach(v => {
      if (['SCHEDULED', 'IN_TRANSIT', 'BERTHED'].includes(v.status)) {
        alerts.push({
          id: `alt-cut-${v.id}`,
          type: 'WARNING',
          category: 'VOYAGE_CUTOFF',
          title: `Chuyến tàu ${v.voyageNumber} (${v.vesselName}) sắp chốt Cut-off`,
          description: `CY Cut-off: ${v.cyCutOff} | VGM Cut-off: ${v.vgmCutOff} tại cảng ${v.pol}`,
          targetPath: '/shipping-line/voyages',
          timestamp: v.cyCutOff,
        });
      }
    });

    // Cảnh báo Manifest có ngoại lệ
    manifests.forEach(m => {
      if (m.status === 'EXCEPTION' || m.exceptionsCount > 0) {
        alerts.push({
          id: `alt-mnf-${m.id}`,
          type: 'ERROR',
          category: 'MANIFEST_EXCEPTION',
          title: `Bản khai ${m.manifestNumber} có ${m.exceptionsCount} ngoại lệ cần xử lý`,
          description: `Chuyến tàu ${m.voyageNumber}: Phát hiện dữ liệu thiếu số seal hoặc không hợp lệ.`,
          targetPath: '/shipping-line/manifest',
          timestamp: m.submittedAt,
        });
      }
    });

    // Cảnh báo eDO sắp hết hạn hoặc hết hạn
    deliveryOrders.forEach(d => {
      if (d.status === 'EXPIRED') {
        alerts.push({
          id: `alt-edo-${d.id}`,
          type: 'WARNING',
          category: 'EDO_EXPIRED',
          title: `Lệnh eDO ${d.edoNumber} đã hết hạn hiệu lực giao hàng`,
          description: `Chủ hàng: ${d.consignee} (Vận đơn: ${d.blNumber}) cần làm thủ tục gia hạn.`,
          targetPath: '/shipping-line/delivery-orders',
          timestamp: d.validUntil,
        });
      }
    });

    // 3. Hoạt động gần đây (Recent Activities)
    const recentActivities = [
      {
        id: 'act-1',
        title: 'Cập nhật mốc CY Cut-off chuyến EG-2605W',
        detail: 'Gia hạn thêm 2 tiếng hỗ trợ đoàn xe hàng lạnh miền Tây',
        user: 'shipping01',
        time: 'Hôm nay 14:20',
        badge: 'LỊCH TÀU',
      },
      {
        id: 'act-2',
        title: 'Phát hành lệnh giao hàng điện tử EDO-2026-0112',
        detail: 'Cấp cho 2 container vận đơn BL-VNDAD-8902',
        user: 'shipping01',
        time: '04/11/2026 15:30',
        badge: 'eDO ISSUED',
      },
      {
        id: 'act-3',
        title: 'Đối soát Manifest chuyến MM-2601S thành công',
        detail: '100% container khớp hoàn toàn với dữ liệu cổng TC-HICT',
        user: 'shipping01',
        time: '05/11/2026 16:40',
        badge: 'ĐỐI SOÁT',
      },
      {
        id: 'act-4',
        title: 'Tiếp nhận bản khai Manifest chuyến OC-2612E',
        detail: 'Hệ thống tự động phát hiện 1 container thiếu số chì (Seal)',
        user: 'shipping01',
        time: '06/11/2026 09:15',
        badge: 'MANIFEST',
      },
    ];

    return {
      kpis: {
        totalVessels,
        activeVoyages,
        totalContainers,
        totalManifests,
        totalBillsOfLading,
        pendingEdos,
        issuedEdos,
        expiredEdos,
      },
      alerts,
      recentActivities,
      recentVoyages: voyages.slice(0, 4),
    };
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // 2. VESSEL & VOYAGE MANAGEMENT
  // ═══════════════════════════════════════════════════════════════════════════
  async getVessels(search) {
    if (!search) return vessels;
    const q = search.trim().toLowerCase();
    return vessels.filter(
      v => v.vesselName.toLowerCase().includes(q) ||
           v.imoNumber.includes(q) ||
           v.callSign.toLowerCase().includes(q)
    );
  }

  async getVoyages(filters = {}) {
    let result = [...voyages];
    if (filters.status) {
      result = result.filter(v => v.status === filters.status);
    }
    if (filters.search) {
      const q = filters.search.trim().toLowerCase();
      result = result.filter(
        v => v.voyageNumber.toLowerCase().includes(q) ||
             v.vesselName.toLowerCase().includes(q) ||
             v.pol.toLowerCase().includes(q) ||
             v.pod.toLowerCase().includes(q)
      );
    }
    return result;
  }

  async getVoyageDetail(id) {
    const voy = voyages.find(v => v.id === id || v.voyageNumber === id);
    if (!voy) throw new Error('Không tìm thấy thông tin chuyến tàu');

    const relatedManifests = manifests.filter(m => m.voyageNumber === voy.voyageNumber);
    const relatedBls = billsOfLading.filter(b => b.voyageNumber === voy.voyageNumber);
    const history = cutOffHistory.filter(h => h.voyageNumber === voy.voyageNumber);

    return {
      ...voy,
      manifests: relatedManifests,
      billsOfLading: relatedBls,
      cutOffHistory: history,
    };
  }

  async createVoyage(payload, user = 'shipping01') {
    if (!payload.vesselId) {
      throw new Error('Tàu được chọn là bắt buộc');
    }

    const vessel = vessels.find(v => v.id === payload.vesselId);
    if (!vessel) throw new Error('Tàu được chọn không hợp lệ');

    // Tự động sinh mã chuyến tàu nếu để trống
    let voyageNumber = payload.voyageNumber ? payload.voyageNumber.trim().toUpperCase() : '';
    if (!voyageNumber) {
      const words = vessel.vesselName.split(' ');
      const prefix = words.length >= 2 ? (words[0][0] + words[1][0]).toUpperCase() : vessel.vesselName.slice(0, 2).toUpperCase();
      const num = 2605 + voyages.length + 1;
      voyageNumber = `${prefix}-${num}W`;
    }

    const exists = voyages.some(v => v.voyageNumber.toUpperCase() === voyageNumber);
    if (exists) {
      throw new Error(`Mã chuyến tàu ${voyageNumber} đã tồn tại trong hệ thống`);
    }

    const newVoyage = {
      id: `voy-${Date.now().toString().slice(-4)}`,
      voyageNumber,
      vesselId: vessel.id,
      vesselName: vessel.vesselName,
      pol: payload.pol || 'Cảng Tiên Sa, Đà Nẵng (VNDAD)',
      pod: payload.pod || 'Tokyo, Nhật Bản (JPTYO)',
      eta: payload.eta || '2026-11-20 08:00',
      etd: payload.etd || '2026-11-21 18:00',
      ata: null,
      atd: null,
      cyCutOff: payload.cyCutOff || '2026-11-19 20:00',
      vgmCutOff: payload.vgmCutOff || '2026-11-19 12:00',
      status: payload.status || 'SCHEDULED',
      terminal: payload.terminal || 'Bến số 1 (TS-01) - Cảng Tiên Sa',
      remarks: payload.remarks || '',
      manifestCount: 0,
      containerCount: 0,
    };

    voyages.unshift(newVoyage);
    return newVoyage;
  }

  async updateVoyage(id, payload, user = 'shipping01') {
    const idx = voyages.findIndex(v => v.id === id || v.voyageNumber === id);
    if (idx === -1) throw new Error('Không tìm thấy chuyến tàu');

    const current = voyages[idx];

    // Ghi nhận lịch sử nếu thay đổi Cut-off
    if (payload.cyCutOff && payload.cyCutOff !== current.cyCutOff) {
      cutOffHistory.unshift({
        id: `coh-${Date.now().toString().slice(-4)}`,
        voyageNumber: current.voyageNumber,
        field: 'CY Cut-off',
        oldValue: current.cyCutOff,
        newValue: payload.cyCutOff,
        reason: payload.updateReason || 'Điều chỉnh kế hoạch tiếp nhận hàng tại bãi',
        updatedBy: user,
        timestamp: new Date().toISOString().replace('T', ' ').slice(0, 19),
      });
    }

    if (payload.vgmCutOff && payload.vgmCutOff !== current.vgmCutOff) {
      cutOffHistory.unshift({
        id: `coh-${Date.now().toString().slice(-4)}`,
        voyageNumber: current.voyageNumber,
        field: 'VGM Cut-off',
        oldValue: current.vgmCutOff,
        newValue: payload.vgmCutOff,
        reason: payload.updateReason || 'Điều chỉnh thời hạn chốt tải trọng',
        updatedBy: user,
        timestamp: new Date().toISOString().replace('T', ' ').slice(0, 19),
      });
    }

    const nowStr = new Date().toISOString().replace('T', ' ').slice(0, 16);
    let extraFields = {};
    if (payload.status === 'BERTHED' && !current.ata) {
      extraFields.ata = nowStr;
    }
    if (payload.status === 'DEPARTED' && !current.atd) {
      extraFields.atd = nowStr;
    }

    voyages[idx] = {
      ...current,
      ...payload,
      ...extraFields,
      id: current.id,
      voyageNumber: current.voyageNumber,
    };

    return voyages[idx];
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // 3. MANIFEST MANAGEMENT
  // ═══════════════════════════════════════════════════════════════════════════
  async getManifests(filters = {}) {
    let result = [...manifests];
    if (filters.voyageNumber) {
      result = result.filter(m => m.voyageNumber === filters.voyageNumber);
    }
    if (filters.status) {
      result = result.filter(m => m.status === filters.status);
    }
    if (filters.direction) {
      result = result.filter(m => m.direction === filters.direction);
    }
    return result;
  }

  async getManifestDetail(id) {
    const mnf = manifests.find(m => m.id === id || m.manifestNumber === id);
    if (!mnf) throw new Error('Không tìm thấy bản khai hàng hóa (Manifest)');

    // 1. Gắn thông tin chi tiết Voyage phục vụ Header đầy đủ
    const voy = voyages.find(v => v.voyageNumber === mnf.voyageNumber);
    const voyageInfo = voy ? {
      voyageNumber: voy.voyageNumber,
      vesselName: voy.vesselName,
      pol: voy.pol,
      pod: voy.pod,
      terminal: voy.terminal,
      eta: voy.eta,
      etd: voy.etd,
      status: voy.status,
    } : null;

    // 2. Thẩm định từng container: chuẩn ISO 6346 (chữ số kiểm tra), số chì (Seal), khối lượng và trạng thái B/L
    const enhancedRecords = (mnf.records || []).map(rec => {
      const isoRes = validateContainerNumberISO(rec.containerNumber);
      const hasSeal = Boolean(rec.sealNumber && rec.sealNumber.trim());
      const hasWeight = Boolean(Number(rec.grossWeightKg) > 0);
      const bl = billsOfLading.find(b => b.blNumber === rec.blNumber);
      const isBlIssued = bl ? (bl.status === 'ISSUED' || bl.status === 'TELEX_RELEASED' || bl.status === 'SURRENDERED') : true;

      const isException = !isoRes.isValid || !hasSeal || !hasWeight || !isBlIssued;
      let reasons = [];
      if (!isoRes.isValid) reasons.push(isoRes.reason);
      if (!hasSeal) reasons.push('Thiếu số chì niêm phong (Seal Number)');
      if (!hasWeight) reasons.push('Chưa khai báo trọng lượng VGM');
      if (!isBlIssued) reasons.push(`Vận đơn ${rec.blNumber} chưa được phát hành chính thức`);

      const matchingBlContainer = bl?.containers?.find(c => c.containerNumber === rec.containerNumber);
      const tempRequired = rec.tempRequired || matchingBlContainer?.tempRequired || (rec.isoType && rec.isoType.includes('RF') ? '-18.0°C' : null);

      return {
        ...rec,
        isIsoValid: isoRes.isValid,
        expectedCheckDigit: isoRes.expectedCheckDigit,
        actualCheckDigit: isoRes.actualCheckDigit,
        status: isException ? 'EXCEPTION' : 'VALID',
        exceptionReason: reasons.length ? reasons.join('; ') : null,
        tempRequired,
        cargoDescription: bl?.cargoDescription || rec.commodity,
      };
    });

    // 3. Gom nhóm theo Vận đơn (B/L) cho cấu trúc bảng phân tầng (Accordion)
    const blMap = {};
    enhancedRecords.forEach(rec => {
      const blNo = rec.blNumber || 'KHONG_CO_BL';
      if (!blMap[blNo]) {
        const blObj = billsOfLading.find(b => b.blNumber === blNo);
        blMap[blNo] = {
          blNumber: blNo,
          shipper: blObj?.shipper || 'Chưa xác định người gửi',
          consignee: blObj?.consignee || 'Chưa xác định người nhận',
          notifyParty: blObj?.notifyParty || 'Như người nhận',
          cargoDescription: blObj?.cargoDescription || rec.commodity || 'Hàng hóa thông thường',
          packageCount: blObj?.packageCount || 1,
          packageUnit: blObj?.packageUnit || 'Kiện',
          grossWeightKg: blObj?.grossWeightKg || 0,
          blType: blObj?.blType || 'SEAWAY_BILL',
          status: blObj?.status || 'ISSUED',
          isIssued: blObj ? (blObj.status === 'ISSUED' || blObj.status === 'TELEX_RELEASED' || blObj.status === 'SURRENDERED') : false,
          containers: [],
        };
      }
      blMap[blNo].containers.push(rec);
    });

    const blGroups = Object.values(blMap);

    // 4. Kiểm tra cảnh báo B/L chưa phát hành trong chuyến tàu
    const voyageBls = billsOfLading.filter(b => b.voyageNumber === mnf.voyageNumber);
    const hasUnissuedBls = voyageBls.some(b => b.status === 'DRAFT');

    const validContainersCount = enhancedRecords.filter(r => r.status === 'VALID').length;
    const exceptionContainersCount = enhancedRecords.filter(r => r.status === 'EXCEPTION').length;

    const updatedStatus = exceptionContainersCount > 0 
      ? 'EXCEPTION' 
      : (mnf.status === 'RECONCILED' ? 'RECONCILED' : 'VALIDATED');

    return {
      ...mnf,
      status: updatedStatus,
      records: enhancedRecords,
      blGroups,
      voyageInfo,
      hasUnissuedBls,
      validContainersCount,
      exceptionContainersCount,
    };
  }

  // Lấy danh sách Vận đơn (B/L) ứng viên cho chuyến tàu (dùng cho tạo manifest xuất khẩu tự động)
  async getCandidateBlsForVoyage(voyageNumber) {
    if (!voyageNumber) return { candidateBls: [], totalContainers: 0, hasUnissuedBls: false };
    const matchedBls = billsOfLading.filter(b => b.voyageNumber === voyageNumber);
    const voy = voyages.find(v => v.voyageNumber === voyageNumber);

    let totalContainers = 0;
    let totalGrossWeight = 0;
    const containers = [];

    matchedBls.forEach(bl => {
      const isIssued = bl.status === 'ISSUED' || bl.status === 'TELEX_RELEASED' || bl.status === 'SURRENDERED';
      (bl.containers || []).forEach(c => {
        totalContainers++;
        const gross = Number(c.grossWeightKg) || 0;
        totalGrossWeight += gross;
        const isReefer = (c.isoType && c.isoType.includes('RF')) || c.cargoType === 'REEFER';
        const isDangerous = c.cargoType === 'DANGEROUS' || Boolean(c.unNumber || c.dgClass);
        const isOog = (c.isoType && (c.isoType.includes('FR') || c.isoType.includes('OT'))) || c.cargoType === 'OOG';
        const isConditional = c.cargoType === 'CONDITIONAL' || Boolean(c.quarantineType);

        containers.push({
          containerNumber: c.containerNumber,
          isoType: c.isoType || '40HC',
          sealNumber: c.sealNumber || '',
          grossWeightKg: gross,
          tempRequired: c.tempRequired || (isReefer ? '-18.0°C' : null),
          ventilation: c.ventilation || (isReefer ? '25 CBM/h' : null),
          humidity: c.humidity || (isReefer ? '85% RH' : null),
          unNumber: c.unNumber || (isDangerous ? 'UN 1993' : null),
          dgClass: c.dgClass || (isDangerous ? 'Class 3' : null),
          packingGroup: c.packingGroup || (isDangerous ? 'PG II' : null),
          flashPoint: c.flashPoint || (isDangerous ? '23°C' : null),
          hasDgd: c.hasDgd !== undefined ? c.hasDgd : isDangerous,
          dgdFile: c.dgdFile || (isDangerous ? 'DGD_Signed_IMO.pdf' : null),
          hasMsds: c.hasMsds !== undefined ? c.hasMsds : isDangerous,
          msdsFile: c.msdsFile || (isDangerous ? 'MSDS_Chemical_Safety.pdf' : null),
          overHeightCm: c.overHeightCm || null,
          overWidthCm: c.overWidthCm || null,
          dimensions: c.dimensions || null,
          quarantineType: c.quarantineType || null,
          certificateNo: c.certificateNo || null,
          blNumber: bl.blNumber,
          commodity: bl.cargoDescription || 'Hàng hóa theo vận đơn',
          cargoType: isDangerous ? 'DANGEROUS' : isReefer ? 'REEFER' : isOog ? 'OOG' : isConditional ? 'CONDITIONAL' : (c.cargoType || 'GENERAL'),
          shipper: bl.shipper,
          consignee: bl.consignee,
          packageCount: bl.packageCount,
          packageUnit: bl.packageUnit,
          isBlIssued: isIssued,
          blStatus: bl.status,
        });
      });
    });

    const hasUnissuedBls = matchedBls.some(b => b.status === 'DRAFT' || b.status === 'CANCELLED');

    // Kiểm tra xem chuyến tàu đã có manifest chưa
    const existingManifests = manifests.filter(m => m.voyageNumber === voyageNumber);

    return {
      voyageNumber,
      voyageInfo: voy ? {
        voyageNumber: voy.voyageNumber,
        vesselName: voy.vesselName,
        pol: voy.pol,
        pod: voy.pod,
        terminal: voy.terminal,
        eta: voy.eta,
        etd: voy.etd,
        cyCutOff: voy.cyCutOff,
        vgmCutOff: voy.vgmCutOff,
        status: voy.status,
      } : null,
      blCount: matchedBls.length,
      candidateBls: matchedBls,
      totalContainers,
      totalGrossWeight,
      hasUnissuedBls,
      existingManifestsCount: existingManifests.length,
      existingManifest: existingManifests[0] || null,
      containers,
    };
  }

  async createManifest(payload, user = 'shipping01') {
    const inputRecords = payload.records || payload.containers || [];
    if (!payload.voyageNumber || !inputRecords.length) {
      throw new Error('Chuyến tàu và danh sách container là bắt buộc');
    }

    const voy = voyages.find(v => v.voyageNumber === payload.voyageNumber);
    const direction = payload.direction || 'EXPORT';
    const isDraft = payload.status === 'DRAFT' || payload.isDraft === true;

    // Kiểm tra khai trùng cho cùng chuyến tàu và hướng khai
    const existingIndex = manifests.findIndex(
      m => m.voyageNumber === payload.voyageNumber && m.direction === direction
    );
    const existing = existingIndex !== -1 ? manifests[existingIndex] : null;

    if (existing && !payload.isAmendment && !payload.allowOverwrite) {
      if (existing.status !== 'DRAFT') {
        throw new Error(
          `Chuyến tàu ${payload.voyageNumber} hướng ${direction === 'EXPORT' ? 'Xuất khẩu' : 'Nhập khẩu'} đã có bản khai ${existing.manifestNumber} (${existing.status}). Vui lòng chọn "Tạo bản khai điều chỉnh (Amendment)" hoặc cập nhật bản khai cũ.`
        );
      }
    }

    // Đếm và phát hiện trùng lặp container trong cùng đợt khai
    const seenContainers = new Set();
    const duplicateContainers = new Set();
    inputRecords.forEach(r => {
      const cNo = (r.containerNumber || '').toUpperCase().trim();
      if (cNo) {
        if (seenContainers.has(cNo)) duplicateContainers.add(cNo);
        seenContainers.add(cNo);
      }
    });

    let totalWeight = 0;
    const records = inputRecords.map((r, i) => {
      const gross = Number(r.grossWeightKg) || 0;
      totalWeight += gross;
      const cNo = (r.containerNumber || '').toUpperCase().trim();

      // Kiểm tra ISO 6346 với chữ số kiểm tra
      const isoRes = validateContainerNumberISO(cNo);
      const hasSeal = Boolean(r.sealNumber && r.sealNumber.trim());
      const hasWeight = gross > 0 && gross <= 36000;
      const bl = billsOfLading.find(b => b.blNumber === r.blNumber);
      const isBlIssued = bl ? (bl.status === 'ISSUED' || bl.status === 'TELEX_RELEASED' || bl.status === 'SURRENDERED') : true;
      const isDuplicate = duplicateContainers.has(cNo);

      const isReefer = (r.isoType && r.isoType.includes('RF')) || r.cargoType === 'REEFER';
      const hasTemp = !isReefer || Boolean(r.tempRequired && r.tempRequired.trim());

      const isDangerous = r.cargoType === 'DANGEROUS' || Boolean(r.unNumber || r.dgClass);
      const hasDgInfo = !isDangerous || Boolean(r.unNumber && r.dgClass);
      const hasDgd = !isDangerous || Boolean(r.hasDgd || r.dgdFile);
      const hasMsds = !isDangerous || Boolean(r.hasMsds || r.msdsFile);

      const isOog = (r.isoType && (r.isoType.includes('FR') || r.isoType.includes('OT'))) || r.cargoType === 'OOG';
      const hasOogDims = !isOog || Boolean(r.overHeightCm || r.overWidthCm || r.dimensions);

      const isConditional = r.cargoType === 'CONDITIONAL' || Boolean(r.quarantineType);
      const hasCert = !isConditional || Boolean(r.certificateNo);

      let reasons = [];
      if (!isoRes.isValid) reasons.push(isoRes.reason);
      if (isDuplicate) reasons.push(`Trùng lặp số container ${cNo} trong đợt khai`);
      if (!hasSeal) reasons.push('Thiếu số chì niêm phong (Seal Number)');
      if (!hasWeight) reasons.push(gross <= 0 ? 'Chưa khai báo trọng lượng VGM' : 'Khối lượng vượt tải trọng cho phép (> 36.000 kg)');
      if (!isBlIssued) reasons.push(`Vận đơn ${r.blNumber} chưa được phát hành chính thức`);
      if (!hasTemp) reasons.push('Container lạnh bắt buộc có nhiệt độ cài đặt (Set Temperature)');
      if (isDangerous && !hasDgInfo) reasons.push('Hàng nguy hiểm bắt buộc khai báo Mã UN và Phân nhóm IMO');
      if (isDangerous && (!hasDgd || !hasMsds)) reasons.push('Hàng nguy hiểm bắt buộc nộp Bản khai DGD và Giấy an toàn hóa chất (MSDS)');
      if (isOog && !hasOogDims) reasons.push('Hàng quá khổ (OOG) bắt buộc có thông số kích thước vượt khổ');
      if (isConditional && !hasCert) reasons.push('Hàng có điều kiện bắt buộc có Số giấy chứng nhận kiểm dịch / hun trùng');

      const isException = reasons.length > 0;

      return {
        id: `rec-${Date.now()}-${i}`,
        containerNumber: cNo,
        isoType: r.isoType || '40HC',
        sealNumber: (r.sealNumber || '').trim(),
        grossWeightKg: gross,
        // Thông số hàng lạnh
        tempRequired: r.tempRequired || (isReefer ? '-18.0°C' : null),
        ventilation: r.ventilation || (isReefer ? '25 CBM/h' : null),
        humidity: r.humidity || (isReefer ? '85% RH' : null),
        // Thông số hàng nguy hiểm
        unNumber: r.unNumber || (isDangerous ? 'UN 1993' : null),
        dgClass: r.dgClass || (isDangerous ? 'Class 3' : null),
        packingGroup: r.packingGroup || (isDangerous ? 'PG II' : null),
        flashPoint: r.flashPoint || (isDangerous ? '23°C' : null),
        hasDgd: Boolean(r.hasDgd || r.dgdFile),
        dgdFile: r.dgdFile || (isDangerous && r.hasDgd !== false ? 'DGD_Signed_IMO.pdf' : null),
        hasMsds: Boolean(r.hasMsds || r.msdsFile),
        msdsFile: r.msdsFile || (isDangerous && r.hasMsds !== false ? 'MSDS_Chemical_Safety.pdf' : null),
        // Thông số hàng quá khổ
        overHeightCm: Number(r.overHeightCm) || (isOog ? 45 : null),
        overWidthCm: Number(r.overWidthCm) || (isOog ? 30 : null),
        overLengthCm: Number(r.overLengthCm) || null,
        dimensions: r.dimensions || (isOog ? '12.2m x 2.85m x 3.05m' : null),
        // Thông số hàng có điều kiện
        quarantineType: r.quarantineType || (isConditional ? 'Kiểm dịch thực vật' : null),
        certificateNo: r.certificateNo || (isConditional ? 'KDTV-DAD-2026-88' : null),
        blNumber: (r.blNumber || '').trim(),
        commodity: r.commodity || (bl ? bl.cargoDescription : 'Hàng hóa thông thường'),
        cargoType: isDangerous ? 'DANGEROUS' : isReefer ? 'REEFER' : isOog ? 'OOG' : isConditional ? 'CONDITIONAL' : (r.cargoType || 'GENERAL'),
        shipper: r.shipper || (bl ? bl.shipper : null),
        consignee: r.consignee || (bl ? bl.consignee : null),
        packageCount: r.packageCount || (bl ? bl.packageCount : null),
        packageUnit: r.packageUnit || (bl ? bl.packageUnit : null),
        status: isException ? 'EXCEPTION' : 'VALID',
        exceptionReason: reasons.length ? reasons.join('; ') : null,
        isIsoValid: isoRes.isValid,
        expectedCheckDigit: isoRes.expectedCheckDigit,
        actualCheckDigit: isoRes.actualCheckDigit,
      };
    });

    const hasExceptions = records.some(r => r.status === 'EXCEPTION');
    const exceptionsCount = records.filter(r => r.status === 'EXCEPTION').length;

    let targetStatus = 'VALIDATED';
    if (isDraft) {
      targetStatus = 'DRAFT';
    } else if (hasExceptions) {
      targetStatus = 'EXCEPTION';
    } else {
      targetStatus = 'SUBMITTED';
    }

    let manifestNumber = `MNF-2026-${(manifests.length + 1).toString().padStart(3, '0')}`;
    let version = 1;
    let history = [
      {
        action: isDraft ? 'CREATED_DRAFT' : 'SUBMITTED',
        by: user,
        timestamp: new Date().toISOString().replace('T', ' ').slice(0, 19),
        note: isDraft ? 'Lưu bản nháp manifest' : 'Nộp chính thức bản khai sang cảng',
      }
    ];

    if (existing) {
      if (existing.status === 'DRAFT' || payload.allowOverwrite) {
        // Cập nhật bản nháp cũ
        existing.records = records;
        existing.totalContainers = records.length;
        existing.totalGrossWeightKg = totalWeight;
        existing.status = targetStatus;
        existing.exceptionsCount = exceptionsCount;
        existing.updatedAt = new Date().toISOString().replace('T', ' ').slice(0, 19);
        existing.history = existing.history || [];
        existing.history.push({
          action: isDraft ? 'UPDATED_DRAFT' : 'SUBMITTED_FROM_DRAFT',
          by: user,
          timestamp: new Date().toISOString().replace('T', ' ').slice(0, 19),
          note: isDraft ? 'Cập nhật lại bản nháp' : 'Chính thức nộp bản khai từ bản nháp',
        });
        return existing;
      } else if (payload.isAmendment) {
        manifestNumber = `${existing.manifestNumber}-AMD${(existing.version || 1) + 1}`;
        version = (existing.version || 1) + 1;
        history.push({
          action: 'AMENDMENT',
          by: user,
          timestamp: new Date().toISOString().replace('T', ' ').slice(0, 19),
          note: `Bản khai sửa đổi phiên bản ${version} thay thế cho ${existing.manifestNumber}`,
        });
      }
    }

    const newManifest = {
      id: `mnf-${Date.now().toString().slice(-4)}`,
      manifestNumber,
      version,
      voyageNumber: voy ? voy.voyageNumber : payload.voyageNumber,
      vesselName: voy ? voy.vesselName : 'Vessel',
      direction,
      totalContainers: records.length,
      totalGrossWeightKg: totalWeight,
      status: targetStatus,
      submittedAt: new Date().toISOString().replace('T', ' ').slice(0, 19),
      submittedBy: user,
      validatedAt: isDraft ? null : new Date().toISOString().replace('T', ' ').slice(0, 19),
      reconciledAt: null,
      reconciliationStatus: 'PENDING',
      exceptionsCount,
      history,
      records,
    };

    manifests.unshift(newManifest);
    return newManifest;
  }

  async resolveManifestException(manifestId, recordId, updatedData, user = 'shipping01') {
    const mnf = manifests.find(m => m.id === manifestId || m.manifestNumber === manifestId);
    if (!mnf) throw new Error('Không tìm thấy Manifest');

    const rec = mnf.records.find(r => r.id === recordId);
    if (!rec) throw new Error('Không tìm thấy bản ghi container trong Manifest');

    // Cập nhật thông tin sửa đổi
    if (updatedData.cargoType) rec.cargoType = updatedData.cargoType;
    if (updatedData.containerNumber) rec.containerNumber = updatedData.containerNumber.toUpperCase().trim();
    if (updatedData.sealNumber !== undefined) rec.sealNumber = updatedData.sealNumber.trim();
    if (updatedData.grossWeightKg) rec.grossWeightKg = Number(updatedData.grossWeightKg);
    if (updatedData.blNumber) rec.blNumber = updatedData.blNumber.trim();
    if (updatedData.tempRequired !== undefined) rec.tempRequired = updatedData.tempRequired;
    if (updatedData.ventilation !== undefined) rec.ventilation = updatedData.ventilation;
    if (updatedData.humidity !== undefined) rec.humidity = updatedData.humidity;
    if (updatedData.unNumber !== undefined) rec.unNumber = updatedData.unNumber;
    if (updatedData.dgClass !== undefined) rec.dgClass = updatedData.dgClass;
    if (updatedData.hasDgd !== undefined) {
      rec.hasDgd = Boolean(updatedData.hasDgd);
      if (rec.hasDgd && !rec.dgdFile) rec.dgdFile = 'DGD_Signed_IMO.pdf';
    }
    if (updatedData.dgdFile !== undefined) rec.dgdFile = updatedData.dgdFile;
    if (updatedData.hasMsds !== undefined) {
      rec.hasMsds = Boolean(updatedData.hasMsds);
      if (rec.hasMsds && !rec.msdsFile) rec.msdsFile = 'MSDS_Chemical_Safety.pdf';
    }
    if (updatedData.msdsFile !== undefined) rec.msdsFile = updatedData.msdsFile;
    if (updatedData.overHeightCm !== undefined) rec.overHeightCm = Number(updatedData.overHeightCm);
    if (updatedData.overWidthCm !== undefined) rec.overWidthCm = Number(updatedData.overWidthCm);
    if (updatedData.dimensions !== undefined) rec.dimensions = updatedData.dimensions;
    if (updatedData.quarantineType !== undefined) rec.quarantineType = updatedData.quarantineType;
    if (updatedData.certificateNo !== undefined) rec.certificateNo = updatedData.certificateNo;

    // Kiểm tra lại toàn diện ISO 6346, Seal, VGM, B/L, DG và Hàng đặc thù
    const isoRes = validateContainerNumberISO(rec.containerNumber);
    const hasSeal = Boolean(rec.sealNumber && rec.sealNumber.trim());
    const hasWeight = Boolean(Number(rec.grossWeightKg) > 0);
    const bl = billsOfLading.find(b => b.blNumber === rec.blNumber);
    const isBlIssued = bl ? (bl.status === 'ISSUED' || bl.status === 'TELEX_RELEASED' || bl.status === 'SURRENDERED') : true;

    const isReefer = (rec.isoType && rec.isoType.includes('RF')) || rec.cargoType === 'REEFER';
    const hasTemp = !isReefer || Boolean(rec.tempRequired && rec.tempRequired.trim());

    const isDangerous = rec.cargoType === 'DANGEROUS' || rec.cargoType === 'DG' || Boolean(rec.unNumber || rec.dgClass);
    const hasDgInfo = !isDangerous || Boolean(rec.unNumber && rec.dgClass);
    const hasDgd = !isDangerous || Boolean(rec.hasDgd || rec.dgdFile);
    const hasMsds = !isDangerous || Boolean(rec.hasMsds || rec.msdsFile);

    const isOog = (rec.isoType && (rec.isoType.includes('FR') || rec.isoType.includes('OT'))) || rec.cargoType === 'OOG';
    const hasOogDims = !isOog || Boolean(rec.overHeightCm || rec.overWidthCm || rec.dimensions);

    const isConditional = rec.cargoType === 'CONDITIONAL' || Boolean(rec.quarantineType);
    const hasCert = !isConditional || Boolean(rec.certificateNo);

    let reasons = [];
    if (!isoRes.isValid) reasons.push(isoRes.reason);
    if (!hasSeal) reasons.push('Thiếu số chì niêm phong (Seal Number)');
    if (!hasWeight) reasons.push('Chưa khai báo trọng lượng VGM');
    if (!isBlIssued) reasons.push(`Vận đơn ${rec.blNumber} chưa được phát hành chính thức`);
    if (!hasTemp) reasons.push('Container lạnh bắt buộc có nhiệt độ cài đặt');
    if (isDangerous && !hasDgInfo) reasons.push('Hàng nguy hiểm bắt buộc có Mã UN và Phân nhóm IMO');
    if (isDangerous && (!hasDgd || !hasMsds)) reasons.push('Hàng nguy hiểm bắt buộc nộp Bản khai DGD và Giấy an toàn hóa chất (MSDS)');
    if (isOog && !hasOogDims) reasons.push('Hàng quá khổ (OOG) bắt buộc có thông số kích thước vượt khổ');
    if (isConditional && !hasCert) reasons.push('Hàng có điều kiện bắt buộc có Số giấy chứng nhận kiểm dịch / hun trùng');

    const isException = reasons.length > 0;
    rec.status = isException ? 'EXCEPTION' : 'VALID';
    rec.exceptionReason = reasons.length ? reasons.join('; ') : null;

    // Đánh giá lại tổng thể Manifest
    const remainingExceptions = mnf.records.filter(r => r.status === 'EXCEPTION').length;
    mnf.exceptionsCount = remainingExceptions;
    if (remainingExceptions === 0) {
      mnf.status = 'VALIDATED';
    } else {
      mnf.status = 'EXCEPTION';
    }

    return await this.getManifestDetail(manifestId);
  }

  async reconcileManifest(manifestId) {
    const mnf = manifests.find(m => m.id === manifestId || m.manifestNumber === manifestId);
    if (!mnf) throw new Error('Không tìm thấy Manifest');

    const TOLERANCE_KG = 100; // Ngưỡng dung sai trạm cân Cảng cho phép: ±100 kg

    let portReceivedCount = 0;
    let matchedCount = 0;
    let mismatchedCount = 0;
    let pendingPortCount = 0;

    const reconciliationDetails = mnf.records.map(rec => {
      const portRec = portReconciliationRecords.find(p => p.containerNumber === rec.containerNumber);
      
      // Nếu container chưa qua cổng hoặc chưa có dữ liệu cân/seal từ cảng
      const isGateCompleted = portRec && portRec.portStatus === 'GATE_IN_COMPLETED' && portRec.portGateInTime;
      if (!isGateCompleted) {
        pendingPortCount++;
        return {
          containerNumber: rec.containerNumber,
          manifestWeight: rec.grossWeightKg,
          manifestSeal: rec.sealNumber,
          portWeight: null,
          portSeal: null,
          portGateInTime: null,
          status: 'PENDING_PORT',
          statusText: 'Chờ cảng (Chưa qua cổng)',
          isMatched: false,
          isPending: true,
          weightDiff: null,
          notes: 'Xe chưa hoàn tất thủ tục hạ bãi qua cổng Cảng Tiên Sa',
        };
      }

      portReceivedCount++;
      const weightDiff = Math.abs(rec.grossWeightKg - portRec.portGrossWeightKg);
      const isWeightAcceptable = weightDiff <= TOLERANCE_KG;
      const isSealMatch = rec.sealNumber === portRec.portSealNumber;

      if (isWeightAcceptable && isSealMatch) {
        matchedCount++;
        const diffNote = weightDiff === 0 
          ? 'Khớp tuyệt đối 100%' 
          : `Lệch ${weightDiff} kg (nằm trong ngưỡng dung sai ±${TOLERANCE_KG} kg)`;
        return {
          containerNumber: rec.containerNumber,
          manifestWeight: rec.grossWeightKg,
          manifestSeal: rec.sealNumber,
          portWeight: portRec.portGrossWeightKg,
          portSeal: portRec.portSealNumber,
          portGateInTime: portRec.portGateInTime,
          status: 'MATCHED',
          statusText: 'Khớp 100%',
          isMatched: true,
          isPending: false,
          weightDiff,
          notes: `Số seal trùng khớp; Trọng lượng VGM ${diffNote}`,
        };
      } else {
        mismatchedCount++;
        let reason = [];
        if (!isSealMatch) reason.push(`Sai lệch số seal (Bản khai: ${rec.sealNumber} ≠ Cảng: ${portRec.portSealNumber || 'Chưa ghi nhận'})`);
        if (!isWeightAcceptable) reason.push(`Sai lệch trọng lượng vượt ngưỡng (Chênh lệch: ${weightDiff} kg > ${TOLERANCE_KG} kg)`);
        return {
          containerNumber: rec.containerNumber,
          manifestWeight: rec.grossWeightKg,
          manifestSeal: rec.sealNumber,
          portWeight: portRec.portGrossWeightKg,
          portSeal: portRec.portSealNumber,
          portGateInTime: portRec.portGateInTime,
          status: 'MISMATCHED',
          statusText: 'Lệch số liệu',
          isMatched: false,
          isPending: false,
          weightDiff,
          notes: reason.join('; '),
        };
      }
    });

    const matchPercentage = portReceivedCount > 0 
      ? Math.round((matchedCount / portReceivedCount) * 100) 
      : 0;

    const summary = {
      totalContainers: mnf.records.length,
      portReceivedCount,
      pendingPortCount,
      matchedCount,
      mismatchedCount,
      matchPercentage,
      toleranceKg: TOLERANCE_KG,
    };

    mnf.reconciledAt = new Date().toISOString().replace('T', ' ').slice(0, 19);
    mnf.reconciliationStatus = (mismatchedCount === 0 && portReceivedCount > 0) ? 'MATCHED' : (mismatchedCount > 0 ? 'MISMATCHED' : 'PENDING');
    if (mismatchedCount === 0 && portReceivedCount === mnf.records.length) {
      mnf.status = 'RECONCILED';
    }

    return {
      manifestId: mnf.id,
      manifestNumber: mnf.manifestNumber,
      summary,
      matchPercentage,
      reconciliationDetails,
    };
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // 4. BILL OF LADING (B/L) MANAGEMENT
  // ═══════════════════════════════════════════════════════════════════════════
  async getBillsOfLading(filters = {}) {
    let result = billsOfLading.map(b => {
      const activeEdo = deliveryOrders.find(d => d.blNumber === b.blNumber && d.status !== 'CANCELLED');
      const lastCancelledEdo = deliveryOrders.find(d => d.blNumber === b.blNumber && d.status === 'CANCELLED');
      return {
        ...b,
        edoIssued: Boolean(activeEdo),
        edoNumber: activeEdo ? activeEdo.edoNumber : null,
        activeEdo: activeEdo || null,
        lastCancelledEdo: !activeEdo && lastCancelledEdo ? lastCancelledEdo : null,
      };
    });
    if (filters.status) {
      result = result.filter(b => b.status === filters.status);
    }
    if (filters.voyageNumber) {
      result = result.filter(b => b.voyageNumber === filters.voyageNumber);
    }
    if (filters.search) {
      const q = filters.search.trim().toLowerCase();
      result = result.filter(
        b => b.blNumber.toLowerCase().includes(q) ||
             b.shipper.toLowerCase().includes(q) ||
             b.consignee.toLowerCase().includes(q) ||
             (b.containers && b.containers.some(c => c.containerNumber.toLowerCase().includes(q)))
      );
    }
    return result;
  }

  async getBillOfLadingDetail(id) {
    const rawBl = (id || '').trim();
    const cleanNo = rawBl.replace(/\s+/g, '').toUpperCase();
    const bl = billsOfLading.find(b => 
      b.id === id || 
      b.blNumber === rawBl || 
      b.blNumber?.toUpperCase() === rawBl.toUpperCase() ||
      b.blNumber?.replace(/\s+/g, '').toUpperCase() === cleanNo
    );
    if (!bl) throw new Error('Không tìm thấy Vận đơn (Bill of Lading)');

    const activeEdo = deliveryOrders.find(d => d.blNumber === bl.blNumber && d.status !== 'CANCELLED');
    const lastCancelledEdo = deliveryOrders.find(d => d.blNumber === bl.blNumber && d.status === 'CANCELLED');

    return {
      ...bl,
      edoIssued: Boolean(activeEdo),
      edoNumber: activeEdo ? activeEdo.edoNumber : null,
      activeEdo: activeEdo || null,
      lastCancelledEdo: !activeEdo && lastCancelledEdo ? lastCancelledEdo : null,
      containers: bl.containers || [],
    };
  }

  // Lấy danh sách Hướng dẫn gửi hàng (SI) phục vụ tạo B/L xuất khẩu
  async getShippingInstructions(filters = {}) {
    let result = [...shippingInstructions];
    if (filters.voyageNumber) {
      result = result.filter(s => s.voyageNumber === filters.voyageNumber);
    }
    if (filters.search) {
      const q = filters.search.trim().toLowerCase();
      result = result.filter(
        s => s.siNumber.toLowerCase().includes(q) ||
             s.shipper.toLowerCase().includes(q) ||
             s.consignee.toLowerCase().includes(q) ||
             s.bookingNumber.toLowerCase().includes(q)
      );
    }
    return result;
  }

  async createBillOfLading(payload, user = 'shipping01') {
    const direction = payload.direction === 'EXPORT' ? 'EXPORT' : 'IMPORT';

    // 1. Kiểm tra bắt buộc Người nhận hàng (Consignee *)
    if (!payload.consignee || !payload.consignee.trim()) {
      throw new Error('Người nhận hàng (Consignee) là thông tin bắt buộc có dấu * làm căn cứ pháp lý giải phóng hàng hóa');
    }

    // 2. Xử lý số vận đơn: Nhập khẩu yêu cầu nhập (hoặc lấy từ Manifest), Xuất khẩu do hệ thống sinh
    let blNumber = (payload.blNumber || '').trim().toUpperCase();
    if (direction === 'EXPORT') {
      if (!blNumber || blNumber.startsWith('AUTO') || blNumber === 'HỆ THỐNG TỰ SINH') {
        blNumber = `BL-EXP-DAD-${new Date().getFullYear().toString().slice(-2)}${String(new Date().getMonth() + 1).padStart(2, '0')}-${Math.floor(1000 + Math.random() * 9000)}`;
      }
    } else {
      // Chiều nhập khẩu bắt buộc có số B/L từ chứng từ gốc cảng đi
      if (!blNumber) {
        throw new Error('Vui lòng nhập số vận đơn B/L cho hàng nhập khẩu (số B/L do hãng tàu tại cảng đi cấp)');
      }
    }

    if (billsOfLading.some(b => b.blNumber === blNumber && b.status !== 'CANCELLED')) {
      throw new Error(`Số vận đơn ${blNumber} đã tồn tại trong hệ thống`);
    }

    // 3. Lấy thông tin chuyến tàu & kiểm tra xung đột container
    const voyageNo = payload.voyageNumber || 'EG-2605W';
    const voy = voyages.find(v => v.voyageNumber === voyageNo);
    const vesselName = payload.vesselName || voy?.vesselName || 'EVER GLORY';
    const pol = payload.pol || (voy ? voy.pol : (direction === 'EXPORT' ? 'Cảng Tiên Sa, Đà Nẵng (VNDAD)' : 'Yokohama, Nhật Bản (JPYOK)'));
    const pod = payload.pod || (voy ? voy.pod : (direction === 'EXPORT' ? 'Tokyo, Nhật Bản (JPTYO)' : 'Cảng Tiên Sa, Đà Nẵng (VNDAD)'));

    // Kiểm tra quy tắc: Một container chỉ thuộc một B/L trong cùng một chuyến tàu
    const containers = Array.isArray(payload.containers) ? payload.containers : [];
    if (containers.length > 0) {
      const activeBlsInVoyage = billsOfLading.filter(b =>
        b.voyageNumber === voyageNo &&
        b.status !== 'CANCELLED' &&
        b.blNumber?.toUpperCase() !== blNumber.toUpperCase()
      );
      for (const cont of containers) {
        const cNo = (cont.containerNumber || '').trim().toUpperCase();
        if (!cNo) continue;
        const dupBl = activeBlsInVoyage.find(b => (b.containers || []).some(bc => (bc.containerNumber || '').trim().toUpperCase() === cNo));
        if (dupBl) {
          throw new Error(`Xung đột container: Container ${cNo} đã thuộc vận đơn ${dupBl.blNumber} trong cùng chuyến tàu ${voyageNo}. Theo quy tắc hàng hải quốc tế, một container chỉ thuộc duy nhất một B/L trong cùng một chuyến.`);
        }
      }
    }

    // Tự động tính tổng trọng lượng VGM từ container nếu chưa có
    const totalVgmKg = containers.reduce((sum, c) => sum + (Number(c.grossWeightKg) || 0), 0);
    const grossWeightKg = Number(payload.grossWeightKg) || (totalVgmKg > 0 ? totalVgmKg : 20000);

    // 4. Quản lý tài chính: KHÔNG cho chọn đã thu phụ phí khi tạo B/L.
    // Phụ phí địa phương mặc định là UNPAID (sẽ ghi nhận qua nghiệp vụ thu phí có chứng từ).
    // Riêng cước trả trước (PREPAID), hệ thống tự ghi nhận đã thu tại cảng đi.
    const freightStatus = payload.freightStatus === 'COLLECT' ? 'COLLECT' : 'PREPAID';
    const isFreightPrepaid = freightStatus === 'PREPAID';

    const newBl = {
      id: `bl-${Date.now().toString().slice(-4)}`,
      blNumber,
      direction,
      blType: payload.blType || 'ORIGINAL',
      voyageNumber: voyageNo,
      vesselName,
      pol,
      pod,
      shipper: payload.shipper || 'Công ty TNHH Tiếp Vận Hàng Hải Tiên Sa',
      consignee: payload.consignee.trim(),
      notifyParty: payload.notifyParty || 'Same as Consignee',
      cargoDescription: payload.cargoDescription || 'Hàng hóa thông thường',
      packageCount: Number(payload.packageCount) || 100,
      packageUnit: payload.packageUnit || 'Packages',
      grossWeightKg,
      measurementCbm: Number(payload.measurementCbm) || 45,
      freightStatus,
      freightAmountUsd: Number(payload.freightAmountUsd) || 2000,
      freightPaid: isFreightPrepaid,
      freightPaymentStatus: isFreightPrepaid ? 'PAID' : 'UNPAID',
      localChargesStatus: 'UNPAID', // Mặc định luôn là Chưa thu
      status: payload.isDraft || payload.status === 'DRAFT' ? 'DRAFT' : 'ISSUED',
      edoIssued: false,
      edoNumber: null,
      sourceType: payload.sourceType || 'MANUAL', // MANUAL, MANIFEST_IMPORT, SI_EXPORT
      sourceReference: payload.sourceReference || null,
      issueDate: new Date().toISOString().replace('T', ' ').slice(0, 16),
      attachedFiles: [],
      containers,
    };

    billsOfLading.unshift(newBl);
    return newBl;
  }

  async updateBlStatus(id, newStatus, user = 'shipping01') {
    const bl = billsOfLading.find(b => b.id === id || b.blNumber === id);
    if (!bl) throw new Error('Không tìm thấy vận đơn');

    const validStatuses = ['DRAFT', 'ISSUED', 'SURRENDERED', 'TELEX_RELEASED', 'CANCELLED'];
    if (!validStatuses.includes(newStatus)) {
      throw new Error('Trạng thái vận đơn không hợp lệ');
    }

    bl.status = newStatus;
    return bl;
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // 5. DELIVERY ORDER (eDO) & LINER HOLD MANAGEMENT
  // ═══════════════════════════════════════════════════════════════════════════
  async getDeliveryOrders(filters = {}) {
    let result = [...deliveryOrders];
    if (filters.status) {
      result = result.filter(d => d.status === filters.status);
    }
    if (filters.search) {
      const q = filters.search.trim().toLowerCase();
      result = result.filter(
        d => d.edoNumber.toLowerCase().includes(q) ||
             d.blNumber.toLowerCase().includes(q) ||
             d.consignee.toLowerCase().includes(q)
      );
    }
    return result;
  }

  async getDeliveryOrderDetail(id) {
    const edo = deliveryOrders.find(d => d.id === id || d.edoNumber === id);
    if (!edo) throw new Error('Không tìm thấy lệnh giao hàng eDO');

    return {
      ...edo,
      containers: edo.containers || [],
    };
  }

  // Luồng kiểm tra 4 điều kiện cốt lõi phát hành eDO (Eligibility Check)
  async checkEdoEligibility(blNumber) {
    const rawBl = (blNumber || '').trim();
    const cleanBlNo = rawBl.replace(/\s+/g, '').toUpperCase();
    const bl = billsOfLading.find(b => 
      b.blNumber === rawBl || 
      b.blNumber.toUpperCase() === rawBl.toUpperCase() ||
      b.blNumber.replace(/\s+/g, '').toUpperCase() === cleanBlNo
    );

    if (!bl) {
      return {
        eligible: false,
        status: 'INELIGIBLE',
        isFeeUnpaid: false,
        criteria: [
          {
            id: 'BL_EXIST',
            name: '1. Tìm thấy vận đơn (B/L)',
            passed: false,
            detail: `Không tìm thấy số vận đơn "${blNumber}" trong hệ thống hãng tàu.`,
          },
        ],
        reasons: [`Không tìm thấy số vận đơn ${blNumber} trong hệ thống hãng tàu`],
      };
    }

    // ── KIỂM TRA 0: HƯỚNG VẬN TẢI (CHỈ PHÁT HÀNH CHO CHIỀU NHẬP KHẨU) ──
    const isImport = bl.direction === 'IMPORT';
    let directionDetail = '';
    if (isImport) {
      directionDetail = 'Vận đơn thuộc luồng HÀNG NHẬP KHẨU (Cảng dỡ: Cảng Tiên Sa) — Hợp lệ để phát hành lệnh giao hàng eDO';
    } else {
      directionDetail = 'Vận đơn thuộc luồng HÀNG XUẤT KHẨU (Export). Theo tập quán và quy định hàng hải quốc tế, Lệnh giao hàng (D/O / eDO) CHỈ phát hành ở chiều NHẬP KHẨU tại cảng đến để người nhận giải phóng hàng; TUYỆT ĐỐI KHÔNG áp dụng cho hàng xuất khẩu.';
    }

    // ── KIỂM TRA 1: GIẢI PHÓNG VẬN ĐƠN (B/L RELEASE) ──
    let isBlReleased = false;
    let blReleaseDetail = '';
    let blActionRequired = null;

    if (bl.status === 'CANCELLED') {
      blReleaseDetail = 'Vận đơn B/L đã bị hủy (Cancelled), không có giá trị giao hàng';
    } else if (bl.status === 'TELEX_RELEASED') {
      isBlReleased = true;
      blReleaseDetail = 'Đã phát hành Điện giao hàng (Telex Release)';
    } else if (bl.status === 'SURRENDERED') {
      isBlReleased = true;
      blReleaseDetail = 'Đã thu hồi B/L gốc tại hãng tàu (Surrendered B/L)';
    } else if (bl.status === 'ISSUED' && bl.blType === 'SEAWAY_BILL') {
      isBlReleased = true;
      blReleaseDetail = 'Vận đơn Seaway Bill — Giải phóng tự động, không yêu cầu nộp bản gốc';
    } else if (bl.blType === 'ORIGINAL') {
      isBlReleased = false;
      blReleaseDetail = 'Chưa nộp B/L gốc: Chủ hàng bắt buộc xuất trình và nộp lại vận đơn gốc trước khi nhận hàng';
      blActionRequired = 'SURRENDER_ORIGINAL';
    } else {
      isBlReleased = false;
      blReleaseDetail = 'Vận đơn chưa được giải phóng hợp lệ tại cảng đến';
      blActionRequired = 'RELEASE_BL';
    }

    // ── KIỂM TRA 2: TÀI CHÍNH & CƯỚC PHÍ (FINANCIAL SETTLEMENT) ──
    const isLocalChargesPaid = bl.localChargesStatus === 'PAID';
    const isFreightCollect = bl.freightStatus === 'COLLECT';
    const isFreightPaid = bl.freightStatus === 'PREPAID' || bl.freightPaid === true || bl.freightPaymentStatus === 'PAID';

    let isFinanceClear = true;
    const feeDetails = [];
    const feeIssues = [];

    if (!isLocalChargesPaid) {
      isFinanceClear = false;
      feeIssues.push('Phụ phí cảng đến (Local Charges) chưa thanh toán');
      feeDetails.push('Chưa thu phụ phí cảng đến');
    }

    if (isFreightCollect && !isFreightPaid) {
      isFinanceClear = false;
      const amount = (bl.freightAmountUsd || 0).toLocaleString();
      feeIssues.push(`Cước tàu trả sau ($${amount} USD) chưa thu`);
      feeDetails.push(`Chưa thu cước tàu trả sau ($${amount} USD)`);
    }

    const financeDetail = isFinanceClear
      ? 'Đã thu đủ cước biển và phụ phí tại Cảng Tiên Sa'
      : feeDetails.join(' & ');

    // ── KIỂM TRA 3: NGƯỜI NHẬN HÀNG HỢP LỆ (CONSIGNEE VERIFICATION) ──
    const hasConsignee = !!(bl.consignee && bl.consignee.trim().length > 3);
    const isConsigneeHold = !!bl.consigneeHold;
    const isConsigneeValid = hasConsignee && !isConsigneeHold;
    const consigneeDetail = isConsigneeValid
      ? `Đã xác thực pháp nhân nhận hàng: ${bl.consignee}`
      : isConsigneeHold
        ? `Người nhận hàng đang có tranh chấp: ${bl.consigneeHoldReason || 'Tạm giữ theo yêu cầu pháp lý'}`
        : 'Chưa xác định danh tính hoặc thiếu thông tin người nhận hàng hợp lệ';

    // ── KIỂM TRA 4: KHAI BÁO MANIFEST & TÌNH TRẠNG TẠM GIỮ (MANIFEST & HOLD) ──
    const isInManifest = bl.inManifest !== false;
    const isCustomsHold = !!bl.isCustomsHold;
    const isCarrierHold = !!bl.isCarrierHold;
    const isHold = isCustomsHold || isCarrierHold;
    const isManifestHoldPassed = isInManifest && !isHold;

    let manifestHoldDetail = '';
    if (!isInManifest) {
      manifestHoldDetail = 'Vận đơn chưa được đăng ký trong Bản khai hàng hóa (Manifest) gửi Cảng Tiên Sa';
    } else if (isCustomsHold) {
      manifestHoldDetail = `Cơ quan Hải quan tạm giữ: ${bl.holdReason || 'Kiểm tra thực tế hàng hóa (Luồng Đỏ)'}`;
    } else if (isCarrierHold) {
      manifestHoldDetail = `Hãng tàu tạm giữ hàng: ${bl.holdReason || 'Tranh chấp chứng từ thương mại'}`;
    } else {
      manifestHoldDetail = 'Đã có trong Manifest nhập khẩu gửi cảng, không có lệnh tạm giữ từ Hải quan / Hãng tàu';
    }

    // ── KIỂM TRA 5: KIỂM SOÁT TRÙNG LẶP LỆNH GIAO HÀNG (eDO DUPLICATE CHECK) ──
    const activeEdo = deliveryOrders.find(d => d.blNumber === bl.blNumber && d.status !== 'CANCELLED');
    const lastCancelledEdo = deliveryOrders.find(d => d.blNumber === bl.blNumber && d.status === 'CANCELLED');
    const isEdoNotDuplicate = !activeEdo;

    let duplicateDetail = '';
    if (activeEdo) {
      duplicateDetail = `Vận đơn đã được cấp lệnh eDO ${activeEdo.edoNumber} (ngày ${activeEdo.issueDate}). Nghiệp vụ hàng hải nghiêm cấm phát hành trùng khi lệnh cũ còn hiệu lực. Bắt buộc hủy lệnh cũ trước khi cấp lại.`;
    } else if (lastCancelledEdo) {
      duplicateDetail = `Lệnh eDO trước đó (${lastCancelledEdo.edoNumber}) đã được hủy ngày ${lastCancelledEdo.cancelledAt || 'gần đây'}. Đủ điều kiện phát hành lệnh eDO mới thay thế.`;
    } else {
      duplicateDetail = 'Chưa có lệnh eDO nào được cấp cho vận đơn này (Sẵn sàng cấp mới)';
    }

    // Tổng hợp danh sách tiêu chí kiểm tra nghiệp vụ cấp eDO
    const criteria = [
      {
        id: 'DIRECTION_CHECK',
        name: 'Hướng vận chuyển (Chỉ cấp cho hàng Nhập khẩu)',
        passed: isImport,
        detail: directionDetail,
        actionRequired: null,
      },
      {
        id: 'BL_RELEASE',
        name: 'Giải phóng Vận đơn (B/L Release)',
        passed: isBlReleased,
        detail: blReleaseDetail,
        actionRequired: blActionRequired,
      },
      {
        id: 'FINANCIAL',
        name: 'Tài chính & Cước phí (Financial Settlement)',
        passed: isFinanceClear,
        detail: financeDetail,
        isFeeUnpaid: !isFinanceClear,
        feeIssues,
        actionRequired: !isFinanceClear ? 'COLLECT_FEES' : null,
      },
      {
        id: 'CONSIGNEE',
        name: 'Người nhận hàng hợp lệ (Consignee Verification)',
        passed: isConsigneeValid,
        detail: consigneeDetail,
        actionRequired: null,
      },
      {
        id: 'MANIFEST_HOLD',
        name: 'Khai báo Manifest & Kiểm soát tạm giữ',
        passed: isManifestHoldPassed,
        detail: manifestHoldDetail,
        isHold,
        actionRequired: null,
      },
      {
        id: 'EDO_DUPLICATE_CHECK',
        name: 'Kiểm soát trùng lặp lệnh giao hàng (eDO)',
        passed: isEdoNotDuplicate,
        detail: duplicateDetail,
        activeEdoNumber: activeEdo ? activeEdo.edoNumber : null,
        activeEdoId: activeEdo ? activeEdo.id : null,
        actionRequired: activeEdo ? 'CANCEL_EXISTING_EDO' : null,
      },
    ];

    const eligible = criteria.every(c => c.passed);
    const failedCriteria = criteria.filter(c => !c.passed);
    const reasons = failedCriteria.map(c => `${c.name}: ${c.detail}`);

    // Gợi ý thông tin dự thảo eDO
    const now = new Date();
    const expiryDate = new Date(now.getTime() + 7 * 24 * 3600 * 1000);
    const formatDt = (d) => {
      const yr = d.getFullYear();
      const mo = String(d.getMonth() + 1).padStart(2, '0');
      const da = String(d.getDate()).padStart(2, '0');
      return `${yr}-${mo}-${da} 23:59:00`;
    };

    return {
      eligible,
      status: eligible ? 'ELIGIBLE' : 'INELIGIBLE',
      criteria,
      reasons,
      isExport: !isImport,
      direction: bl.direction,
      isAlreadyIssued: Boolean(activeEdo),
      activeEdo: activeEdo || null,
      lastCancelledEdo: lastCancelledEdo || null,
      isFeeUnpaid: !isFinanceClear,
      feeIssues,
      bl,
      containers: bl.containers || [],
      edoDraftInfo: {
        consignee: bl.consignee,
        vesselVoyage: `${bl.vesselName} (${bl.voyageNumber})`,
        terminal: bl.terminal || 'Bến số 1 (TS-01) - Cảng Tiên Sa',
        pol: bl.pol,
        pod: bl.pod,
        suggestedValidUntil: formatDt(expiryDate),
        defaultEmptyReturnDepot: 'Bãi Depot Cảng Tiên Sa (Khu C)',
        depotOptions: [
          'Bãi Depot Cảng Tiên Sa (Khu C)',
          'Bãi Depot Cảng Liên Chiểu',
          'Bãi Depot ICD Hòa Cầm - Đà Nẵng',
          'Bãi Depot Khu Kinh Tế Chu Lai',
        ],
        freeTimeDays: 7,
      },
      financialSummary: {
        freightStatus: bl.freightStatus,
        freightAmountUsd: bl.freightAmountUsd || 0,
        isFreightPaid,
        localChargesStatus: bl.localChargesStatus,
        isLocalChargesPaid,
        totalFeesPending: !isFinanceClear,
      },
    };
  }

  // Luồng phát hành eDO
  async issueEdo(payload, user = 'shipping01') {
    const { blNumber, validUntil, containerNumbers, emptyReturnDepot, freeTimeDays, notes } = payload;
    if (!blNumber) throw new Error('Số B/L là bắt buộc');

    // Kiểm tra điều kiện trước khi phát hành - Chặn 100% nếu chưa đạt
    const check = await this.checkEdoEligibility(blNumber);
    if (!check.eligible) {
      if (check.isExport || check.bl?.direction !== 'IMPORT') {
        throw new Error(`TỪ CHỐI PHÁT HÀNH: Vận đơn ${blNumber} là vận đơn XUẤT KHẨU. Quy định hàng hải quốc tế quy định Lệnh giao hàng (D/O) CHỈ được phát hành ở chiều NHẬP KHẨU tại cảng đến, KHÔNG phát hành ở chiều xuất.`);
      }
      if (check.isAlreadyIssued) {
        throw new Error(`TỪ CHỐI PHÁT HÀNH: Vận đơn ${blNumber} đã có lệnh giao hàng điện tử ${check.activeEdo?.edoNumber} đang có hiệu lực. Nghiệp vụ hàng hải nghiêm cấm phát hành trùng 2 lệnh giao hàng. Vui lòng hủy lệnh cũ trước khi phát hành lại.`);
      }
      if (check.isFeeUnpaid) {
        throw new Error(`TỪ CHỐI PHÁT HÀNH eDO: Vận đơn ${blNumber} chưa hoàn tất nộp phí dịch vụ (${check.feeIssues.join('; ')}). Quy định an toàn tài chính nghiêm cấm phát hành lệnh giao hàng khi chưa thu phí.`);
      }
      throw new Error(`Không đủ điều kiện phát hành eDO: ${check.reasons.join('; ')}`);
    }

    const bl = check.bl;
    const edoNumber = `EDO-2026-${(deliveryOrders.length + 110).toString()}`;
    const selectedContainers = (bl.containers || [])
      .filter(c => !containerNumbers || containerNumbers.includes(c.containerNumber))
      .map(c => ({
        containerNumber: c.containerNumber,
        isoType: c.isoType,
        sealNumber: c.sealNumber,
        pickupDepot: 'Bãi Container Cảng Tiên Sa - Khu A',
        gateOutStatus: 'NOT_GATE_OUT',
      }));

    const newEdo = {
      id: `edo-${Date.now().toString().slice(-4)}`,
      edoNumber,
      blNumber: bl.blNumber,
      voyageNumber: bl.voyageNumber,
      vesselName: bl.vesselName,
      consignee: bl.consignee,
      issueDate: new Date().toISOString().replace('T', ' ').slice(0, 16),
      validUntil: validUntil || check.edoDraftInfo?.suggestedValidUntil || '2026-11-20 23:59:00',
      emptyReturnDepot: emptyReturnDepot || 'Bãi Depot Cảng Tiên Sa (Khu C)',
      freeTimeDays: Number(freeTimeDays) || 7,
      pinCode: `PIN-${Math.floor(1000 + Math.random() * 9000)}`,
      status: 'ISSUED',
      issuedBy: user,
      releaseOfficer: 'Đại diện Hãng tàu (NexusPort Liner Agent)',
      containers: selectedContainers,
      notes: notes || 'Lệnh phát hành hợp lệ, đủ điều kiện làm thủ tục lấy container tại cổng cảng',
    };

    deliveryOrders.unshift(newEdo);

    // Cập nhật trạng thái trên B/L
    bl.edoIssued = true;
    bl.edoNumber = edoNumber;
    bl.activeEdo = newEdo;

    return newEdo;

    return newEdo;
  }

  // Thu hồi B/L gốc để giải phóng hàng
  async surrenderBl(idOrBlNumber, user = 'shipping01') {
    const raw = (idOrBlNumber || '').trim();
    const cleanNo = raw.replace(/\s+/g, '').toUpperCase();
    const bl = billsOfLading.find(b => 
      b.id === raw || 
      b.blNumber === raw ||
      b.blNumber.toUpperCase() === raw.toUpperCase() ||
      b.blNumber.replace(/\s+/g, '').toUpperCase() === cleanNo
    );

    if (!bl) throw new Error('Không tìm thấy vận đơn để thu hồi');

    bl.status = 'SURRENDERED';
    const check = await this.checkEdoEligibility(bl.blNumber);
    return {
      bl,
      eligibility: check,
      message: `Đã xác nhận thu hồi B/L gốc cho vận đơn ${bl.blNumber}`,
    };
  }

  // Thu phí dịch vụ cho vận đơn (Cước biển & Local Charges)
  async collectBlFees(idOrBlNumber, user = 'shipping01') {
    const raw = (idOrBlNumber || '').trim();
    const cleanNo = raw.replace(/\s+/g, '').toUpperCase();
    const bl = billsOfLading.find(b => 
      b.id === raw || 
      b.blNumber === raw ||
      b.blNumber.toUpperCase() === raw.toUpperCase() ||
      b.blNumber.replace(/\s+/g, '').toUpperCase() === cleanNo
    );

    if (!bl) throw new Error('Không tìm thấy vận đơn để thu phí');

    bl.localChargesStatus = 'PAID';
    if (bl.freightStatus === 'COLLECT') {
      bl.freightPaid = true;
      bl.freightPaymentStatus = 'PAID';
    }

    const check = await this.checkEdoEligibility(bl.blNumber);
    return {
      bl,
      eligibility: check,
      message: `Đã xác nhận thu đủ phí cho vận đơn ${bl.blNumber}`,
    };
  }

  // Gia hạn eDO
  async extendEdo(id, newValidUntil, reason, user = 'shipping01') {
    const edo = deliveryOrders.find(d => d.id === id || d.edoNumber === id);
    if (!edo) throw new Error('Không tìm thấy eDO');

    edo.validUntil = newValidUntil;
    edo.status = 'ISSUED';
    edo.notes = `${edo.notes || ''} | Gia hạn đến ${newValidUntil} (Lý do: ${reason || 'Yêu cầu từ chủ hàng'})`;
    return edo;
  }

  // Hủy eDO
  async cancelEdo(id, reason, user = 'shipping01') {
    const edo = deliveryOrders.find(d => d.id === id || d.edoNumber === id);
    if (!edo) throw new Error('Không tìm thấy eDO');

    const cancelledAt = new Date().toISOString().replace('T', ' ').slice(0, 16);
    edo.status = 'CANCELLED';
    edo.cancelledAt = cancelledAt;
    edo.cancelReason = reason || 'Sai lệch thông tin / Yêu cầu hủy để cấp lại';
    edo.cancelledBy = user;
    edo.notes = `Hủy bởi ${user} ngày ${cancelledAt}. Lý do: ${edo.cancelReason}`;

    // Cập nhật lại B/L tương ứng để mở khóa quyền phát hành lại lệnh eDO
    const remainingActive = deliveryOrders.find(d => d.blNumber === edo.blNumber && d.status !== 'CANCELLED' && d.id !== edo.id && d.edoNumber !== edo.edoNumber);
    const targetBl = billsOfLading.find(b => b.blNumber === edo.blNumber);
    if (targetBl) {
      targetBl.edoIssued = Boolean(remainingActive);
      targetBl.edoNumber = remainingActive ? remainingActive.edoNumber : null;
      targetBl.activeEdo = remainingActive || null;
      targetBl.lastCancelledEdo = {
        id: edo.id,
        edoNumber: edo.edoNumber,
        cancelledAt: edo.cancelledAt,
        reason: edo.cancelReason,
      };
    }

    return edo;
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // 6. PORT GATE OUT INTEGRATION (TÍCH HỢP CỔNG CẢNG)
  // ═══════════════════════════════════════════════════════════════════════════
  async verifyEdoForGateOut(containerNumber) {
    const cleanNo = (containerNumber || '').toUpperCase().trim();

    // 1. Tìm eDO liên quan đến container
    let matchedEdo = null;
    let matchedCont = null;

    for (const edo of deliveryOrders) {
      const cont = (edo.containers || []).find(c => c.containerNumber === cleanNo);
      if (cont) {
        matchedEdo = edo;
        matchedCont = cont;
        break;
      }
    }

    if (!matchedEdo) {
      return {
        canGateOut: false,
        reason: 'Chưa có Lệnh giao hàng điện tử (eDO) được phát hành cho container này',
        status: 'NO_EDO_FOUND',
      };
    }

    // 2. Kiểm tra trạng thái eDO
    if (matchedEdo.status === 'CANCELLED') {
      return {
        canGateOut: false,
        reason: 'Lệnh giao hàng eDO đã bị hủy bởi hãng tàu',
        status: 'EDO_CANCELLED',
      };
    }

    // 3. Kiểm tra thời hạn hiệu lực
    const now = new Date();
    const validUntilDate = new Date(matchedEdo.validUntil);
    if (!isNaN(validUntilDate) && now > validUntilDate) {
      return {
        canGateOut: false,
        reason: `Lệnh giao hàng eDO đã hết hạn hiệu lực lúc ${matchedEdo.validUntil}`,
        status: 'EDO_EXPIRED',
      };
    }

    return {
      canGateOut: true,
      reason: 'Lệnh giao hàng eDO hợp lệ và còn hiệu lực. Đủ điều kiện ra cổng.',
      status: 'EDO_VALID',
      edo: {
        edoNumber: matchedEdo.edoNumber,
        blNumber: matchedEdo.blNumber,
        consignee: matchedEdo.consignee,
        validUntil: matchedEdo.validUntil,
        pickupDepot: matchedCont.pickupDepot,
      },
    };
  }
}

module.exports = new ShippingLineService();

