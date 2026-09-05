'use strict';

const BANNER_WIDTH = 86;

const probeService = async (url, timeoutMs = 1500) => {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(url, { method: 'GET', signal: controller.signal });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeout);
  }
};

const boxLine = (content = '') => {
  const normalized = `  ${content}`.slice(0, BANNER_WIDTH - 2);
  return `║${normalized.padEnd(BANNER_WIDTH - 2)}║`;
};

const endpointLine = (label, url, online) => {
  const status = online ? '[ONLINE ]' : '[OFFLINE]';
  return boxLine(`${label.padEnd(18)} ${url.padEnd(48)} ${status}`);
};

const buildStartupBanner = ({ port, nodeEnv, nodeCoreOnline, csharpCoreOnline }) => {
  const jsBaseUrl = `http://localhost:${port}`;
  return [
    '',
    `╔${'═'.repeat(BANNER_WIDTH - 2)}╗`,
    boxLine('NexusPort API Development Console'),
    `╠${'═'.repeat(BANNER_WIDTH - 2)}╣`,
    boxLine(`Environment        ${nodeEnv}`),
    boxLine(),
    endpointLine('JavaScript API', `${jsBaseUrl}/api`, true),
    endpointLine('JavaScript OpenAPI', `${jsBaseUrl}/api-docs/openapi.json`, true),
    endpointLine('TypeScript API', 'http://localhost:4000/api/v1', nodeCoreOnline),
    endpointLine('TypeScript OpenAPI', 'http://localhost:4000/openapi.json', nodeCoreOnline),
    endpointLine('C# API', 'http://localhost:5000/api/v1', csharpCoreOnline),
    endpointLine('C# OpenAPI', 'http://localhost:5000/swagger/v1/swagger.json', csharpCoreOnline),
    `╠${'═'.repeat(BANNER_WIDTH - 2)}╣`,
    endpointLine('Swagger Hub', `${jsBaseUrl}/api-docs`, true),
    `╚${'═'.repeat(BANNER_WIDTH - 2)}╝`,
    '  OFFLINE chỉ báo service phụ chưa chạy; JavaScript API vẫn hoạt động.',
    '',
  ].join('\n');
};

const printStartupBanner = async ({ port, nodeEnv }) => {
  const [nodeCoreOnline, csharpCoreOnline] = await Promise.all([
    probeService('http://localhost:4000/openapi.json'),
    probeService('http://localhost:5000/swagger/v1/swagger.json'),
  ]);
  console.log(buildStartupBanner({ port, nodeEnv, nodeCoreOnline, csharpCoreOnline }));
};

module.exports = { buildStartupBanner, printStartupBanner, probeService };
