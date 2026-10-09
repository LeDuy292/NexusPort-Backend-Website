'use strict';

const { DataTypes, Model } = require('sequelize');
const { sequelize } = require('../config/database');

/**
 * Các trạng thái hợp lệ của chuyến vận chuyển (theo thứ tự).
 * Mỗi bước chỉ được thực hiện khi bước trước đã hoàn thành.
 */
const TRIP_STATUSES = [
  'Assigned',           // 1. Đã phân công cho tài xế
  'Acknowledged',       // 2. Tài xế đã xác nhận nhận chuyến
  'Vehicle Picked Up',  // 3. Đã nhận/lấy xe
  'Container Picked Up',// 4. Đã nhận/lấy container
  'Gate In',            // 5. Đã Gate In
  'In Transit',         // 6. Đang vận chuyển
  'Arrived at Yard',    // 7. Đã đến Yard
  'Container Delivered',// 8. Đã giao container
  'Gate Out Completed', // 9. Đã Gate Out
  'Vehicle Returned',   // 10. Đã trả xe
  'Completed',          // 11. Hoàn thành
  'Cancelled',          // Hủy (có thể ở bất kỳ bước nào)
];

const TRIP_STATUS_ORDER = {
  'Assigned': 0,
  'Acknowledged': 1,
  'Vehicle Picked Up': 2,
  'Container Picked Up': 3,
  'Gate In': 4,
  'In Transit': 5,
  'Arrived at Yard': 6,
  'Container Delivered': 7,
  'Gate Out Completed': 8,
  'Vehicle Returned': 9,
  'Completed': 10,
};

// Map: status hiện tại → status tiếp theo hợp lệ
const NEXT_STATUS_MAP = {
  'Assigned':           'Acknowledged',
  'Acknowledged':       'Vehicle Picked Up',
  'Vehicle Picked Up':  'Container Picked Up',
  'Container Picked Up':'Gate In',
  'Gate In':            'In Transit',
  'In Transit':         'Arrived at Yard',
  'Arrived at Yard':    'Container Delivered',
  'Container Delivered':'Gate Out Completed',
  'Gate Out Completed': 'Vehicle Returned',
  'Vehicle Returned':   'Completed',
};

/**
 * Model: transport_trips
 * Lưu thông tin và trạng thái chuyến vận chuyển container của Driver.
 */
class TransportTrip extends Model {}

TransportTrip.init(
  {
    id: {
      type: DataTypes.UUID,
      defaultValue: DataTypes.UUIDV4,
      primaryKey: true,
    },
    trip_code: {
      type: DataTypes.STRING(50),
      allowNull: false,
      unique: true,
    },
    driver_id: {
      type: DataTypes.UUID,
      allowNull: false,
      comment: 'ID của Driver trong bảng users',
    },
    booking_id: {
      type: DataTypes.UUID,
      allowNull: true,
      comment: 'Booking liên quan (nullable – có thể giao thẳng không qua booking)',
    },
    booking_code: {
      type: DataTypes.STRING(50),
      allowNull: true,
      comment: 'Bản chụp mã booking (booking nằm ở service .NET, không join trực tiếp)',
    },
    booking_type: {
      type: DataTypes.STRING(20),
      allowNull: true,
      comment: 'Loại booking: Pickup | Dropoff',
    },
    // Thông tin xe và container
    truck_plate: {
      type: DataTypes.STRING(20),
      allowNull: true,
    },
    truck_id: {
      type: DataTypes.UUID,
      allowNull: true,
    },
    container_no: {
      type: DataTypes.STRING(20),
      allowNull: true,
      comment: 'Số container cần vận chuyển',
    },
    container_type: {
      type: DataTypes.STRING(50),
      allowNull: true,
    },
    cargo_type: {
      type: DataTypes.STRING(50),
      allowNull: true,
    },
    // Thông tin địa điểm
    gate_in_code: {
      type: DataTypes.STRING(20),
      allowNull: true,
      comment: 'Cổng vào',
    },
    gate_out_code: {
      type: DataTypes.STRING(20),
      allowNull: true,
      comment: 'Cổng ra',
    },
    yard_block: {
      type: DataTypes.STRING(20),
      allowNull: true,
      comment: 'Block bãi đích (vd: Block A)',
    },
    yard_slot: {
      type: DataTypes.STRING(20),
      allowNull: true,
      comment: 'Slot cụ thể trong block (vd: A05-02)',
    },
    yard_zone: {
      type: DataTypes.STRING(50),
      allowNull: true,
    },
    yard_instructions: {
      type: DataTypes.TEXT,
      allowNull: true,
      comment: 'Hướng dẫn di chuyển & chỉ đường chi tiết đến bãi (Yard)',
    },
    // Thông tin giao nhận container
    seal_no: {
      type: DataTypes.STRING(50),
      allowNull: true,
      comment: 'Số chì container',
    },
    container_condition: {
      type: DataTypes.STRING(100),
      allowNull: true,
      comment: 'Tình trạng container khi giao (Nguyên chì / Tốt / Móp nhẹ...)',
    },
    recipient_name: {
      type: DataTypes.STRING(100),
      allowNull: true,
      comment: 'Người / Đơn vị tiếp nhận container tại Yard',
    },
    // Trạng thái
    status: {
      type: DataTypes.STRING(50),
      allowNull: false,
      defaultValue: 'Assigned',
      validate: {
        isIn: {
          args: [TRIP_STATUSES],
          msg: `Status phải là một trong: ${TRIP_STATUSES.join(', ')}`,
        },
      },
    },
    // Thời gian thực hiện từng bước
    acknowledged_at: { type: DataTypes.DATE, allowNull: true },
    vehicle_picked_up_at: { type: DataTypes.DATE, allowNull: true },
    container_picked_up_at: { type: DataTypes.DATE, allowNull: true },
    gate_in_at: { type: DataTypes.DATE, allowNull: true },
    in_transit_at: { type: DataTypes.DATE, allowNull: true },
    arrived_at_yard_at: { type: DataTypes.DATE, allowNull: true },
    container_delivered_at: { type: DataTypes.DATE, allowNull: true },
    gate_out_at: { type: DataTypes.DATE, allowNull: true },
    vehicle_returned_at: { type: DataTypes.DATE, allowNull: true },
    completed_at: { type: DataTypes.DATE, allowNull: true },
    // Thông tin bổ sung
    appointment_start: {
      type: DataTypes.DATE,
      allowNull: true,
      comment: 'Thời gian hẹn bắt đầu',
    },
    appointment_end: {
      type: DataTypes.DATE,
      allowNull: true,
    },
    note: {
      type: DataTypes.TEXT,
      allowNull: true,
    },
    assigned_by: {
      type: DataTypes.UUID,
      allowNull: true,
      comment: 'Người phân công (Dispatcher / Admin)',
    },
  },
  {
    sequelize,
    modelName: 'TransportTrip',
    tableName: 'transport_trips',
  }
);

/**
 * Model: transport_trip_history
 * Lưu lịch sử từng lần thay đổi trạng thái của chuyến.
 */
class TransportTripHistory extends Model {}

TransportTripHistory.init(
  {
    id: {
      type: DataTypes.UUID,
      defaultValue: DataTypes.UUIDV4,
      primaryKey: true,
    },
    trip_id: {
      type: DataTypes.UUID,
      allowNull: false,
    },
    from_status: {
      type: DataTypes.STRING(50),
      allowNull: true,
    },
    to_status: {
      type: DataTypes.STRING(50),
      allowNull: false,
    },
    changed_by: {
      type: DataTypes.UUID,
      allowNull: true,
      comment: 'ID của user thực hiện thay đổi (thường là Driver)',
    },
    note: {
      type: DataTypes.TEXT,
      allowNull: true,
    },
    changed_at: {
      type: DataTypes.DATE,
      allowNull: false,
      defaultValue: DataTypes.NOW,
    },
  },
  {
    sequelize,
    modelName: 'TransportTripHistory',
    tableName: 'transport_trip_history',
    timestamps: false,
  }
);

// Associations
TransportTrip.hasMany(TransportTripHistory, { foreignKey: 'trip_id', as: 'history' });
TransportTripHistory.belongsTo(TransportTrip, { foreignKey: 'trip_id', as: 'trip' });

module.exports = {
  TransportTrip,
  TransportTripHistory,
  TRIP_STATUSES,
  TRIP_STATUS_ORDER,
  NEXT_STATUS_MAP,
};
