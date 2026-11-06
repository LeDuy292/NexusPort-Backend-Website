'use strict';

const { DataTypes, Model } = require('sequelize');
const { sequelize } = require('../config/database');

const GATE_OUT_STATUSES = ['PENDING', 'IN_PROGRESS', 'READY_FOR_GATE_OUT', 'COMPLETED', 'CANCELLED'];
const URGENCY_LEVELS = ['CRITICAL', 'HIGH', 'MEDIUM', 'LOW'];
const SLOT_STATUSES = ['Occupied', 'Available', 'Reserved'];

class YardGateOutRequest extends Model {}

YardGateOutRequest.init(
  {
    id: {
      type: DataTypes.UUID,
      defaultValue: DataTypes.UUIDV4,
      primaryKey: true,
    },
    trip_id: {
      type: DataTypes.UUID,
      allowNull: true,
      comment: 'ID chuyến vận chuyển liên kết nếu có',
    },
    container_id: {
      type: DataTypes.STRING(50),
      allowNull: false,
      comment: 'Mã Container (vd: CAIU1234567)',
    },
    container_type: {
      type: DataTypes.STRING(50),
      defaultValue: '40FT HC',
      comment: 'Loại container',
    },
    cargo_type: {
      type: DataTypes.STRING(100),
      defaultValue: 'Hàng Khô Thông Thường',
      comment: 'Loại hàng hóa',
    },
    driver_name: {
      type: DataTypes.STRING(100),
      allowNull: false,
      comment: 'Tên tài xế',
    },
    driver_phone: {
      type: DataTypes.STRING(30),
      allowNull: true,
      comment: 'Số điện thoại tài xế',
    },
    driver_id: {
      type: DataTypes.UUID,
      allowNull: true,
      comment: 'ID tài khoản Driver nếu có',
    },
    truck_plate: {
      type: DataTypes.STRING(30),
      allowNull: false,
      comment: 'Biển số xe nhận container',
    },
    gate_name: {
      type: DataTypes.STRING(50),
      defaultValue: 'Cổng A',
      comment: 'Cổng tài xế đang đợi',
    },
    booking_code: {
      type: DataTypes.STRING(50),
      allowNull: true,
      comment: 'Mã booking',
    },
    carrier_name: {
      type: DataTypes.STRING(100),
      defaultValue: 'Hapag-Lloyd',
      comment: 'Hãng vận chuyển / hãng tàu',
    },
    vessel_name: {
      type: DataTypes.STRING(100),
      defaultValue: 'EVER GIVEN',
      comment: 'Tàu chở',
    },
    weight_kg: {
      type: DataTypes.STRING(50),
      defaultValue: '26,500 KG',
      comment: 'Trọng lượng container',
    },
    seal_number: {
      type: DataTypes.STRING(50),
      allowNull: false,
      comment: 'Số chì niêm phong (Seal)',
    },
    yard_block: {
      type: DataTypes.STRING(20),
      allowNull: false,
      defaultValue: 'B',
      comment: 'Khu bãi (Block)',
    },
    yard_bay: {
      type: DataTypes.STRING(10),
      defaultValue: '04',
      comment: 'Dãy (Bay)',
    },
    yard_row: {
      type: DataTypes.STRING(10),
      defaultValue: '10',
      comment: 'Hàng (Row)',
    },
    yard_tier: {
      type: DataTypes.STRING(10),
      defaultValue: '2',
      comment: 'Tầng (Tier)',
    },
    yard_slot: {
      type: DataTypes.STRING(50),
      allowNull: false,
      defaultValue: 'B-04-10-2',
      comment: 'Vị trí đầy đủ của ô bãi',
    },
    slot_status: {
      type: DataTypes.STRING(20),
      defaultValue: 'Occupied',
      comment: 'Trạng thái ô bãi: Occupied hoặc Available',
    },
    released_at: {
      type: DataTypes.DATE,
      allowNull: true,
      comment: 'Thời điểm giải phóng ô bãi',
    },
    blocking_containers: {
      type: DataTypes.INTEGER,
      defaultValue: 0,
      comment: 'Số container đè phía trên',
    },
    rtg_crane: {
      type: DataTypes.STRING(50),
      defaultValue: 'RTG-05',
      comment: 'Mã cẩu RTG điều phối',
    },
    rtg_operator: {
      type: DataTypes.STRING(100),
      defaultValue: 'Trần Văn Hùng',
      comment: 'Thợ điều khiển cẩu RTG',
    },
    flow_step: {
      type: DataTypes.INTEGER,
      defaultValue: 0,
      comment: 'Tiến trình: 0=Chờ bắt đầu, 1=Cẩu đang di chuyển, 2=Đến vị trí, 3=Đối soát container, 4=Hoàn thành',
    },
    status: {
      type: DataTypes.STRING(30),
      defaultValue: 'PENDING',
      validate: {
        isIn: [GATE_OUT_STATUSES],
      },
      comment: 'Trạng thái: PENDING, IN_PROGRESS, READY_FOR_GATE_OUT, COMPLETED, CANCELLED',
    },
    urgency: {
      type: DataTypes.STRING(20),
      defaultValue: 'HIGH',
      validate: {
        isIn: [URGENCY_LEVELS],
      },
      comment: 'Mức độ ưu tiên: CRITICAL, HIGH, MEDIUM, LOW',
    },
    condition: {
      type: DataTypes.STRING(100),
      defaultValue: 'Tốt (Không Hư Hỏng)',
      comment: 'Tình trạng vỏ container',
    },
    verified_container_id: {
      type: DataTypes.BOOLEAN,
      defaultValue: false,
      comment: 'Đã xác nhận đúng Container ID',
    },
    verified_seal: {
      type: DataTypes.BOOLEAN,
      defaultValue: false,
      comment: 'Đã xác nhận đúng và nguyên vẹn số chì seal',
    },
    verified_no_damage: {
      type: DataTypes.BOOLEAN,
      defaultValue: false,
      comment: 'Đã xác nhận vỏ cont không hư hỏng nặng',
    },
    verified_by: {
      type: DataTypes.UUID,
      allowNull: true,
      comment: 'ID của nhân viên bãi kiểm tra',
    },
    verified_at: {
      type: DataTypes.DATE,
      allowNull: true,
      comment: 'Thời điểm hoàn tất kiểm tra đối soát',
    },
    loaded_at: {
      type: DataTypes.DATE,
      allowNull: true,
      comment: 'Thời điểm container đã được cẩu lên xe tải',
    },
    requested_time: {
      type: DataTypes.STRING(50),
      allowNull: true,
      comment: 'Thời gian yêu cầu từ cổng',
    },
    note: {
      type: DataTypes.TEXT,
      allowNull: true,
      comment: 'Ghi chú thực địa',
    },
  },
  {
    sequelize,
    modelName: 'YardGateOutRequest',
    tableName: 'yard_gate_out_requests',
    timestamps: true,
    underscored: true,
    indexes: [
      { fields: ['status'] },
      { fields: ['container_id'] },
      { fields: ['truck_plate'] },
      { fields: ['yard_slot'] },
      { fields: ['slot_status'] },
    ],
  }
);

module.exports = {
  YardGateOutRequest,
  GATE_OUT_STATUSES,
  URGENCY_LEVELS,
  SLOT_STATUSES,
};
