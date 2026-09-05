import { createApp } from './app';
import { collectNodeCoreRouteOperations, nodeCoreOpenApi } from './openapi';

describe('node-core OpenAPI contract', () => {
  it('documents every mounted node-core route', () => {
    const actual = collectNodeCoreRouteOperations()
      .map(({ method, path }) => `${method.toUpperCase()} ${path}`)
      .sort();
    const documented = Object.entries(nodeCoreOpenApi.paths).flatMap(([path, pathItem]) =>
      Object.keys(pathItem)
        .filter((method) => ['get', 'post', 'put', 'patch', 'delete'].includes(method))
        .map((method) => `${method.toUpperCase()} ${path}`))
      .filter((operation) => operation !== 'GET /api/v1/health')
      .sort();
    expect(documented).toEqual(actual);
  });

  it('contains a response contract for every operation', () => {
    for (const pathItem of Object.values(nodeCoreOpenApi.paths)) {
      for (const operation of Object.values(pathItem) as Array<Record<string, unknown>>) {
        expect(operation.responses).toBeDefined();
      }
    }
  });

  it('mounts the generated OpenAPI JSON endpoint', () => {
    const app = createApp();
    const stack = (app as unknown as { _router: { stack: Array<{ route?: { path: string } }> } })._router.stack;
    expect(stack.some((layer) => layer.route?.path === '/openapi.json')).toBe(true);
  });
});
