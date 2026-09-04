'use strict';

const { DataTypes, Model } = require('sequelize');
const { sequelize } = require('../config/database');

/**
 * Danh sách roles hợp lệ trong hệ thống NexusPort.
 */
const VALID_ROLES = [
  'Administrator',
  'Transport Company',
  'Driver',
  'Dispatcher',
  'Gate Officer',
  'Yard Operator',
  'Berth Staff',
];

class User extends Model {
  /**
   * Trả về object user an toàn (không có password).
   */
  toSafeObject() {
    return {
      id: this.id,
      username: this.username,
      email: this.email,
      role: this.role,
      fullName: this.full_name,
      isActive: this.is_active,
      createdAt: this.created_at,
    };
  }
}

User.init(
  {
    id: {
      type: DataTypes.UUID,
      defaultValue: DataTypes.UUIDV4,
      primaryKey: true,
    },
    username: {
      type: DataTypes.STRING(100),
      allowNull: false,
      unique: {
        name: 'users_username_unique',
        msg: 'Username đã tồn tại.',
      },
      validate: {
        notEmpty: { msg: 'Username không được để trống.' },
        len: { args: [3, 100], msg: 'Username phải từ 3–100 ký tự.' },
      },
    },
    email: {
      type: DataTypes.STRING(255),
      allowNull: false,
      unique: {
        name: 'users_email_unique',
        msg: 'Email đã tồn tại.',
      },
      validate: {
        isEmail: { msg: 'Email không hợp lệ.' },
        notEmpty: { msg: 'Email không được để trống.' },
      },
    },
    password: {
      type: DataTypes.STRING(255),
      allowNull: false,
      validate: {
        notEmpty: { msg: 'Password không được để trống.' },
      },
    },
    role: {
      type: DataTypes.STRING(50),
      allowNull: false,
      validate: {
        isIn: {
          args: [VALID_ROLES],
          msg: `Role phải là một trong: ${VALID_ROLES.join(', ')}`,
        },
      },
    },
    full_name: {
      type: DataTypes.STRING(255),
      allowNull: true,
    },
    is_active: {
      type: DataTypes.BOOLEAN,
      defaultValue: true,
      allowNull: false,
    },
    reset_token: {
      type: DataTypes.STRING(255),
      allowNull: true,
    },
    reset_token_exp: {
      type: DataTypes.DATE,
      allowNull: true,
    },
  },
  {
    sequelize,
    modelName: 'User',
    tableName: 'users',
  }
);

module.exports = { User, VALID_ROLES };
