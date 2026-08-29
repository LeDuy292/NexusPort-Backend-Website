export const SYSTEM_CONSTANTS = {
  APP_NAME: 'NexusPort Node Core',
  DEFAULT_PORT: 4000,
  API_PREFIX: '/api/v1',
};

export const ROLES = {
  SUPER_ADMIN: 'SuperAdmin',
  PORT_MANAGER: 'PortManager',
  BERTH_PLANNER: 'BerthPlanner',
  YARD_PLANNER: 'YardPlanner',
  GATE_OFFICER: 'GateOfficer',
  DISPATCHER: 'Dispatcher',
  DRIVER: 'Driver',
  CARRIER: 'Carrier',
} as const;

export const HTTP_STATUS = {
  OK: 200,
  CREATED: 201,
  NO_CONTENT: 204,
  BAD_REQUEST: 400,
  UNAUTHORIZED: 401,
  FORBIDDEN: 403,
  NOT_FOUND: 404,
  CONFLICT: 409,
  UNPROCESSABLE_ENTITY: 422,
  INTERNAL_SERVER_ERROR: 500,
} as const;
