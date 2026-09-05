'use strict';

const request = require('supertest');
const app = require('../src/app');
const { swaggerSpec } = require('../src/docs/swagger');
const { mountedRouters } = require('../src/routes');
const { collectRouteOperations } = require('../src/docs/route-openapi');

const documentedOperations = (document) => Object.entries(document.paths).flatMap(([path, item]) =>
  Object.keys(item)
    .filter((method) => ['get', 'post', 'put', 'patch', 'delete'].includes(method))
    .map((method) => `${method.toUpperCase()} ${path}`));

describe('aggregated OpenAPI', () => {
  it('documents every mounted JavaScript API route', () => {
    const actual = collectRouteOperations(mountedRouters).map(({ method, path }) => `${method.toUpperCase()} ${path}`);
    expect(documentedOperations(swaggerSpec).sort()).toEqual(actual.sort());
  });

  it('serves a valid JavaScript OpenAPI document', async () => {
    const response = await request(app).get('/api-docs/openapi.json').expect(200);
    expect(response.body.openapi).toMatch(/^3\./);
    expect(Object.keys(response.body.paths).length).toBeGreaterThan(0);
    for (const pathItem of Object.values(response.body.paths)) {
      for (const [method, operation] of Object.entries(pathItem)) {
        if (['get', 'post', 'put', 'patch', 'delete'].includes(method)) {
          expect(operation.responses).toBeDefined();
        }
      }
    }
  });

  it('configures all three API specifications in the Swagger UI', async () => {
    const response = await request(app).get('/api-docs/').expect(200);
    expect(response.text).toContain('<title>NexusPort API Docs</title>');
    const initializer = await request(app).get('/api-docs/swagger-ui-init.js').expect(200);
    expect(initializer.text).toContain('/api-docs/openapi.json');
    expect(initializer.text).toContain('http://localhost:4000/openapi.json');
    expect(initializer.text).toContain('http://localhost:5000/swagger/v1/swagger.json');
  });
});
