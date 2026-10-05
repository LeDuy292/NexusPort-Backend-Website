'use strict';

const HTTP_METHODS = new Set(['get', 'post', 'put', 'patch', 'delete', 'options', 'head']);

const joinPaths = (prefix, routePath) => {
  const combined = `${prefix === '/' ? '' : prefix}/${routePath === '/' ? '' : routePath}`
    .replace(/\/+/g, '/')
    .replace(/\/$/, '');
  return combined || '/';
};

const toOpenApiPath = (path) => path.replace(/:([A-Za-z0-9_]+)/g, '{$1}');

const pathParameters = (path) => [...path.matchAll(/\{([A-Za-z0-9_]+)\}/g)].map((match) => ({
  name: match[1],
  in: 'path',
  required: true,
  schema: { type: 'string' },
}));

const collectRouteOperations = (mountedRouters) => mountedRouters.flatMap((mount) =>
  mount.router.stack.flatMap((layer) => {
    if (!layer.route) return [];
    const path = toOpenApiPath(joinPaths(mount.prefix, layer.route.path));
    return Object.keys(layer.route.methods)
      .filter((method) => HTTP_METHODS.has(method) && layer.route.methods[method])
      .map((method) => ({ path, method, tag: mount.tag, secured: mount.secured }));
  }));

const addDiscoveredRoutes = (document, mountedRouters) => {
  document.paths ||= {};
  for (const route of collectRouteOperations(mountedRouters)) {
    document.paths[route.path] ||= {};
    const operation = document.paths[route.path][route.method] || {};
    operation.tags ||= [route.tag];
    operation.summary ||= `${route.method.toUpperCase()} ${route.path}`;
    operation.responses ||= { 200: { description: 'Successful response' } };
    const parameters = pathParameters(route.path);
    if (parameters.length && !operation.parameters) operation.parameters = parameters;
    if (route.secured && !operation.security) operation.security = [{ BearerAuth: [] }];
    if (['post', 'put', 'patch'].includes(route.method) && !operation.requestBody) {
      operation.requestBody = {
        required: true,
        content: { 'application/json': { schema: { type: 'object', additionalProperties: true } } },
      };
    }
    document.paths[route.path][route.method] = operation;
  }
  return document;
};

module.exports = { addDiscoveredRoutes, collectRouteOperations };
