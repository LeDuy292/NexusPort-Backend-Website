'use strict';

const swaggerJsdoc = require('swagger-jsdoc');

const options = {
  definition: {
    openapi: '3.0.0',
    info: {
      title: 'NexusPort API',
      version: '1.0.0',
      description:
        'API documentation cho hệ thống quản lý cảng container NexusPort.\n\n' +
        '**Accounts mặc định (seed data):**\n\n' +
        '| Username | Role | Password |\n' +
        '|----------|------|----------|\n' +
        '| admin | Administrator | NexusPort@2026 |\n' +
        '| dispatcher01 | Dispatcher | NexusPort@2026 |\n' +
        '| gate01 | Gate Officer | NexusPort@2026 |\n' +
        '| yard01 | Yard Operator | NexusPort@2026 |\n' +
        '| berth01 | Berth Staff | NexusPort@2026 |\n' +
        '| carrier01 | Transport Company | NexusPort@2026 |\n' +
        '| driver01 | Driver | NexusPort@2026 |\n',
      contact: {
        name: 'NexusPort Dev Team',
        email: 'dev@nexusport.vn',
      },
    },
    servers: [
      {
        url: 'http://localhost:3001',
        description: 'Development Server',
      },
    ],
    components: {
      securitySchemes: {
        BearerAuth: {
          type: 'http',
          scheme: 'bearer',
          bearerFormat: 'JWT',
          description: 'Nhập JWT token nhận được từ POST /api/auth/login',
        },
      },
      schemas: {
        UserSafe: {
          type: 'object',
          properties: {
            id: {
              type: 'string',
              format: 'uuid',
              example: '550e8400-e29b-41d4-a716-446655440000',
            },
            username: {
              type: 'string',
              example: 'dispatcher01',
            },
            email: {
              type: 'string',
              format: 'email',
              example: 'dispatcher01@nexusport.vn',
            },
            role: {
              type: 'string',
              enum: [
                'Administrator',
                'Transport Company',
                'Driver',
                'Dispatcher',
                'Gate Officer',
                'Yard Operator',
                'Berth Staff',
              ],
              example: 'Dispatcher',
            },
            fullName: {
              type: 'string',
              example: 'Nguyễn Văn A',
            },
            isActive: {
              type: 'boolean',
              example: true,
            },
            createdAt: {
              type: 'string',
              format: 'date-time',
            },
          },
        },
      },
    },
  },
  apis: ['./src/modules/**/*.routes.js'],
};

const swaggerSpec = swaggerJsdoc(options);

module.exports = { swaggerSpec };
