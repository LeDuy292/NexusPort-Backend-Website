import { ContainerStatus } from '../modules/container/domain/container.entity';
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
