import { ContainerStatus } from '../modules/container/domain/container.entity';
import shippingLineReference from '../modules/container/domain/shipping-line-reference.json';
import { NodeCoreRouteModule, nodeCoreRouteModules } from './routes';

type HttpMethod = 'get' | 'post' | 'put' | 'patch' | 'delete';
type OpenApiOperation = { path: string; method: HttpMethod; tag: string; secured: boolean };
type ExpressLayer = { route?: { path: string; methods: Partial<Record<HttpMethod, boolean>> } };

const HTTP_METHODS: HttpMethod[] = ['get', 'post', 'put', 'patch', 'delete'];
const joinPaths = (prefix: string, path: string) =>
  `/api/v1${prefix}/${path === '/' ? '' : path}`.replace(/\/+/g, '/').replace(/\/$/, '');
const toOpenApiPath = (path: string) => path.replace(/:([A-Za-z0-9_]+)/g, '{$1}');

export const collectNodeCoreRouteOperations = (
  modules: NodeCoreRouteModule[] = nodeCoreRouteModules,
): OpenApiOperation[] => modules.flatMap((module) => {
  const stack = (module.router as unknown as { stack: ExpressLayer[] }).stack;
  return stack.flatMap((layer) => {
    if (!layer.route) return [];
    return HTTP_METHODS
      .filter((method) => layer.route!.methods[method])
      .map((method) => ({
        path: toOpenApiPath(joinPaths(module.prefix, layer.route!.path)),
        method,
        tag: module.tag,
        secured: module.secured,
      }));
  });
});

const parameterSchema = (path: string) => [...path.matchAll(/\{([A-Za-z0-9_]+)\}/g)].map((match) => ({
  name: match[1],
  in: 'path',
  required: true,
  schema: { type: 'string', format: match[1] === 'id' ? 'uuid' : undefined },
}));

const operationOverrides: Record<string, Record<string, unknown>> = {
  'GET /api/v1/containers/shipping-lines': {
    summary: 'List shipping line profiles and their registered BIC prefixes',
    description: 'Returns the current database directory. The response example shows five profiles from the reference import with illustrative IDs.',
    responses: {
      200: {
        description: 'Shipping line directory',
        content: { 'application/json': {
          schema: {
            type: 'object', required: ['success', 'data', 'timestamp'],
            properties: {
              success: { type: 'boolean' },
              data: { type: 'array', items: { $ref: '#/components/schemas/ShippingLineProfile' } },
              timestamp: { type: 'string', format: 'date-time' },
            },
          },
          example: {
            success: true,
            data: shippingLineReference.map(({ fullName, shortName, prefixes }, index) => ({
              id: `00000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`, fullName, shortName, prefixes,
            })),
            timestamp: '2026-11-09T00:00:00Z',
          },
        } },
      },
      401: { description: 'Missing or invalid token' },
      403: { description: 'Role cannot access the directory' },
    },
  },
  'GET /api/v1/containers/shipping-lines/me': {
    summary: 'Get the profile assigned to the authenticated shipping line account',
    responses: {
      200: { description: 'Assigned profile, or null for an unlinked account' },
      401: { description: 'Missing or invalid token' },
      403: { description: 'Shipping Line role required' },
    },
  },
  'GET /api/v1/containers/identities': {
    summary: 'Search registered containers using at least one equipment filter',
    parameters: [
      ...['containerNumber', 'prefix', 'shippingLineId', 'containerTypeId', 'isoSizeType', 'size', 'category', 'manufacturer']
        .map(name => ({ name, in: 'query', schema: { type: 'string' } })),
      { name: 'minPayloadKg', in: 'query', schema: { type: 'number', minimum: 0, exclusiveMinimum: true } },
    ],
  },
  'GET /api/v1/containers/identities/mine': {
    summary: 'List registered containers for the signed-in Shipping Line',
    description: 'The service resolves the shipping line from the active account. No search filter is required.',
    parameters: [
      { name: 'page', in: 'query', schema: { type: 'integer', minimum: 1, maximum: 10000, default: 1 } },
      { name: 'limit', in: 'query', schema: { type: 'integer', minimum: 1, maximum: 50, default: 20 } },
      ...['containerNumber', 'prefix', 'containerTypeId', 'isoSizeType', 'size', 'category', 'manufacturer']
        .map(name => ({ name, in: 'query', schema: { type: 'string' } })),
      { name: 'minPayloadKg', in: 'query', schema: { type: 'number', minimum: 0, exclusiveMinimum: true } },
    ],
  },
  'GET /api/v1/containers/yard-visits': {
    summary: 'Search yard visits, planned appointments and issued EIRs',
    description: 'Requires a filter. Visits and linked details are scoped to the authenticated role and account. Date filters match actual gate events or scheduled appointments.',
    parameters: [
      { name: 'containerNumber', in: 'query', schema: { type: 'string', minLength: 4, maxLength: 11 } },
      { name: 'visitStatus', in: 'query', schema: { type: 'string', enum: ['planned', 'active', 'completed', 'canceled'] } },
      ...['dateFrom', 'dateTo'].map(name => ({ name, in: 'query', schema: { type: 'string', format: 'date-time' } })),
      ...['plateNumber', 'customerName'].map(name => ({ name, in: 'query', description: 'Yard visit operators only', schema: { type: 'string' } })),
      { name: 'page', in: 'query', schema: { type: 'integer', minimum: 1, maximum: 10000, default: 1 } },
      { name: 'limit', in: 'query', schema: { type: 'integer', minimum: 1, maximum: 50, default: 20 } },
    ],
  },
  'GET /api/v1/containers/intake/shipping-line/iso-types': {
    summary: 'List ISO markings supported for registration with their catalog types and nominal dimensions',
  },
  'POST /api/v1/containers/intake/shipping-line/manual': {
    summary: 'Register a physical container (Shipping Line only)',
    requestBody: { required: true, content: { 'application/json': { schema: {
      type: 'object', additionalProperties: false,
      required: ['containerNumber', 'isoSizeType', 'tareWeightKg', 'maxGrossMassKg'],
      properties: {
        containerNumber: { type: 'string', description: 'ISO 6346 equipment number' },
        containerTypeId: { type: 'string', format: 'uuid', description: 'Optional. Must match the selected ISO code.' },
        isoSizeType: { type: 'string', description: 'Use a code returned by the shipping-line ISO types endpoint.' },
        equipmentDescription: { type: 'string' }, manufacturer: { type: 'string' },
        externalLengthMm: { type: 'integer', description: 'Optional. Must match the nominal length.' },
        externalWidthMm: { type: 'integer', description: 'Optional. Must match the nominal width.' },
        externalHeightMm: { type: 'integer', description: 'Optional. Must match the nominal height.' },
        tareWeightKg: { type: 'number' }, maxGrossMassKg: { type: 'number' }, capacityM3: { type: 'number' },
      },
    } } } },
    responses: {
      201: { description: 'Container registered' },
      401: { description: 'Missing or invalid token' },
      403: { description: 'Shipping Line role required' },
      409: { description: 'Container number already registered' },
      422: { description: 'Invalid container identity' },
    },
  },
  'GET /api/v1/drivers/me/yard-map': {
    summary: 'Read the approved 2D yard layout before Gate In without granting entry permission',
    security: [{ BearerAuth: [] }],
  },
  'GET /api/v1/gate/locations': {
    summary: 'Read configured physical gate IDs, codes and directions',
  },
  'POST /api/v1/gate/complete-out': {
    summary: 'Gate Officer confirms physical truck exit after every booked container is handed over',
    security: [{ BearerAuth: [] }],
    requestBody: { required: true, content: { 'application/json': { schema: {
      type: 'object', required: ['bookingId', 'gateId'], additionalProperties: false,
      properties: { bookingId: { type: 'string', format: 'uuid' }, gateId: { type: 'string', format: 'uuid' } },
    } } } },
  },
  'GET /api/v1/dispatcher/smart-routes': {
    summary: 'Monitor actual Gate In, yard arrival, container handover and Gate Out for dispatched trucks',
    security: [{ BearerAuth: [] }],
  },
  'PUT /api/v1/dispatcher/smart-routes/conditions': {
    summary: 'Set a temporary road restriction or congestion observation on the active yard map',
    security: [{ BearerAuth: [] }],
    requestBody: { required: true, content: { 'application/json': { schema: {
      type: 'object', required: ['targetType', 'targetId', 'restricted'], additionalProperties: false,
      properties: { targetType: { type: 'string', enum: ['node', 'edge'] }, targetId: { type: 'string', maxLength: 120 },
        restricted: { type: 'boolean' }, congestion: { type: 'number', minimum: 0, maximum: 1 }, validUntil: { type: 'string', format: 'date-time' } },
    } } } },
  },
  'POST /api/v1/drivers/me/routes/{containerId}/progress': {
    summary: 'Driver reports arrival at the currently assigned handover point',
    security: [{ BearerAuth: [] }],
    requestBody: { required: true, content: { 'application/json': { schema: {
      type: 'object', required: ['bookingId', 'action'], additionalProperties: false,
      properties: { bookingId: { type: 'string', format: 'uuid' }, action: { type: 'string', enum: ['arrive'] }, currentNodeId: { type: 'string', maxLength: 120 } },
    } } } },
  },
  'POST /api/v1/drivers/me/routes/{containerId}/confirm-container': {
    summary: 'Confirm the booked container after arrival and verified yard completion; return the next route',
    security: [{ BearerAuth: [] }],
    requestBody: { required: true, content: { 'application/json': { schema: {
      type: 'object', required: ['bookingId'], additionalProperties: false,
      properties: { bookingId: { type: 'string', format: 'uuid' }, condition: { type: 'string', enum: ['OK', 'DAMAGED', 'ISSUE'] }, notes: { type: 'string', maxLength: 1000 } },
    } } } },
  },
  'GET /api/v1/dispatcher/bookings/ready': {
    summary: 'List approved bookings and their containers, driver and vehicle for dispatch',
    security: [{ BearerAuth: [] }],
    responses: {
      200: { description: 'Approved bookings ready for dispatch' },
      401: { description: 'Missing or invalid token' },
      403: { description: 'Only Dispatcher or Administrator can access this list' },
    },
  },
  'GET /api/v1/dispatcher/yard/destinations': {
    summary: 'List Yard destinations with Block, Bay, Row, Tier for Dispatcher selection',
    security: [{ BearerAuth: [] }],
    parameters: [{
      name: 'status', in: 'query', required: false,
      schema: { type: 'string', enum: ['empty', 'reserved', 'occupied', 'maintenance'] },
    }],
    responses: {
      200: { description: 'Yard destinations' },
      401: { description: 'Missing or invalid token' },
      403: { description: 'Only Dispatcher or Administrator can view Yard destinations' },
      422: { description: 'Invalid status filter' },
    },
  },
  'GET /api/v1/dispatcher/operations/{operationId}/destination': {
    summary: 'Get the destination currently linked to an Operation',
    security: [{ BearerAuth: [] }],
    responses: {
      200: { description: 'Destination assignment' },
      404: { description: 'Operation has no destination assignment' },
    },
  },
  'PUT /api/v1/dispatcher/operations/{operationId}/destination': {
    summary: 'Assign an available Yard destination to an Operation and its Driver',
    security: [{ BearerAuth: [] }],
    requestBody: {
      required: true,
      content: { 'application/json': { schema: {
        type: 'object', required: ['slotId'], additionalProperties: false,
        properties: { slotId: { type: 'string', format: 'uuid' }, bookingId: { type: 'string', format: 'uuid', description: 'Explicit booking link for preassigned operations; driver, container and visit are validated.' } },
      } } },
    },
    responses: {
      200: { description: 'Destination saved and linked to the Operation' },
      404: { description: 'Operation or Yard slot not found' },
      409: { description: 'Operation cannot be assigned or destination is unavailable' },
      422: { description: 'Invalid request' },
    },
  },
  'GET /api/v1/drivers/me/destinations': {
    summary: 'List immutable destination assignments for the authenticated Driver',
    security: [{ BearerAuth: [] }],
    responses: {
      200: { description: 'Assigned Operations and their Block, Bay, Row, Tier destinations' },
      401: { description: 'Missing or invalid Driver token' },
      403: { description: 'Token does not have Driver role' },
    },
  },
  'GET /api/v1/drivers/me/routes': {
    summary: 'List Gate-In destinations and Smart Route availability for the authenticated Driver',
    description: 'Preserves legacy route fields. Adds operation, target and navigation. navigation.status is available, unavailable or pending_destination. Refresh every 15 seconds; never follow a route whose navigation is unavailable.',
    security: [{ BearerAuth: [] }],
    responses: {
      200: { description: 'Routes with destination and directions when a Yard slot is assigned' },
      401: { description: 'Missing or invalid Driver token' },
      403: { description: 'Token does not have Driver role' },
    },
  },
  'GET /api/v1/drivers/me/routes/{containerId}': {
    summary: 'Calculate a Smart Route to the current Operation/Yard destination after Gate-In',
    description: 'Re-reads Operation, Yard destination and map conditions on every request. Returns destination even if no safe route exists (navigation.status=unavailable, reasonCode and message explain why). Legacy destination/route fields remain compatible.',
    security: [{ BearerAuth: [] }],
    parameters: [
      { name: 'containerId', in: 'path', required: true, schema: { type: 'string', format: 'uuid' } },
      { name: 'bookingId', in: 'query', required: false, description: 'Exact booking occurrence; required if more than one visit is visible for this container.', schema: { type: 'string', format: 'uuid' } },
      { name: 'currentNodeId', in: 'query', required: false, description: 'Driver-confirmed map node. Omit to start from the actual Gate In gate.', schema: { type: 'string', minLength: 1, maxLength: 120 } },
    ],
    responses: {
      200: { description: 'Container destination with Block, Bay, Row, Tier and directions' },
      401: { description: 'Missing or invalid Driver token' },
      403: { description: 'Token does not have Driver role' },
      404: { description: 'No passed Gate-In transaction for this Driver and Container' },
      409: { description: 'Container has multiple visible bookings; select the exact booking' },
      422: { description: 'Invalid container ID or currentNodeId' },
    },
  },
  'GET /api/v1/containers/{id}/status': {
    summary: 'Get the current container lifecycle status',
  },
  'GET /api/v1/containers/{id}/status/history': {
    summary: 'Get the container status transition history',
  },
  'POST /api/v1/containers/{id}/status/transition': {
    summary: 'Transition a container to its next lifecycle status',
    requestBody: {
      required: true,
      content: { 'application/json': { schema: { $ref: '#/components/schemas/ContainerStatusTransition' } } },
    },
    responses: {
      200: { description: 'Status transitioned and history recorded' },
      409: { description: 'Invalid transition or concurrent update conflict' },
      422: { description: 'Invalid request body' },
    },
  },
};

export const buildNodeCoreOpenApi = () => {
  const paths: Record<string, Record<string, unknown>> = {
    '/api/v1/health': {
      get: { tags: ['System'], summary: 'Node Core health check', responses: { 200: { description: 'Healthy' } } },
    },
  };
  for (const route of collectNodeCoreRouteOperations()) {
    paths[route.path] ||= {};
    const parameters = parameterSchema(route.path);
    const operation: Record<string, unknown> = {
      tags: [route.tag],
      summary: `${route.method.toUpperCase()} ${route.path}`,
      responses: { 200: { description: 'Successful response' } },
    };
    if (parameters.length) operation.parameters = parameters;
    if (route.secured) operation.security = [{ BearerAuth: [] }];
    if (['post', 'put', 'patch'].includes(route.method)) {
      operation.requestBody = {
        required: true,
        content: { 'application/json': { schema: { type: 'object', additionalProperties: true } } },
      };
    }
    Object.assign(operation, operationOverrides[`${route.method.toUpperCase()} ${route.path}`]);
    paths[route.path][route.method] = operation;
  }

  return {
    openapi: '3.0.3',
    info: {
      title: 'NexusPort TypeScript Core API',
      version: '1.0.0',
      description: 'OpenAPI generated from the Express routers mounted by node-core.',
    },
    servers: [{ url: 'http://localhost:4000', description: 'TypeScript Core development server' }],
    tags: ['System', ...nodeCoreRouteModules.map((module) => module.tag)].map((name) => ({ name })),
    paths,
    components: {
      securitySchemes: {
        BearerAuth: { type: 'http', scheme: 'bearer', bearerFormat: 'JWT' },
      },
      schemas: {
        ShippingLineProfile: {
          type: 'object',
          required: ['id', 'fullName', 'shortName', 'prefixes'],
          properties: {
            id: { type: 'string', format: 'uuid' },
            fullName: { type: 'string', example: 'Hapag-Lloyd AG' },
            shortName: { type: 'string', example: 'Hapag-Lloyd' },
            prefixes: { type: 'array', items: { type: 'string', pattern: '^[A-Z]{3}[UJZ]$' }, example: ['HAMU', 'HLBU'] },
          },
        },
        ContainerStatus: { type: 'string', enum: Object.values(ContainerStatus) },
        ContainerStatusTransition: {
          type: 'object',
          required: ['status'],
          additionalProperties: false,
          properties: { status: { $ref: '#/components/schemas/ContainerStatus' } },
        },
      },
    },
  };
};

export const nodeCoreOpenApi = buildNodeCoreOpenApi();
