'use strict';

const { buildStartupBanner, probeService } = require('../src/utils/startupBanner');

describe('development startup banner', () => {
  it('shows all API and OpenAPI endpoints with service status', () => {
    const banner = buildStartupBanner({
      port: 3001,
      nodeEnv: 'development',
      nodeCoreOnline: true,
      csharpCoreOnline: false,
    });

    expect(banner).toContain('http://localhost:3001/api-docs');
    expect(banner).toContain('http://localhost:4000/openapi.json');
    expect(banner).toContain('http://localhost:5000/swagger/v1/swagger.json');
    expect(banner.match(/\[ONLINE ]/g)).toHaveLength(5);
    expect(banner.match(/\[OFFLINE]/g)).toHaveLength(2);
  });

  it('reports an unavailable service without throwing', async () => {
    await expect(probeService('http://127.0.0.1:1/openapi.json', 50)).resolves.toBe(false);
  });
});
