'use strict';

const targetUrl = process.argv[2];
if (!targetUrl) {
  console.error('Usage: node scripts/validate-openapi-url.js <openapi-url>');
  process.exit(2);
}

const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

const fetchWithRetry = async (url, attempts = 30) => {
  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    try {
      const response = await fetch(url);
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.json();
    } catch (error) {
      lastError = error;
      if (attempt < attempts) await delay(1000);
    }
  }
  throw lastError;
};

const validate = (document) => {
  if (!document || typeof document !== 'object') throw new Error('Document is not a JSON object.');
  if (!/^3\./.test(document.openapi || '')) throw new Error(`Unsupported OpenAPI version: ${document.openapi}`);
  if (!document.info?.title || !document.info?.version) throw new Error('OpenAPI info.title and info.version are required.');
  if (!document.paths || Object.keys(document.paths).length === 0) throw new Error('OpenAPI document has no paths.');

  const methods = new Set(['get', 'post', 'put', 'patch', 'delete', 'options', 'head']);
  let operationCount = 0;
  for (const [path, pathItem] of Object.entries(document.paths)) {
    for (const [method, operation] of Object.entries(pathItem)) {
      if (!methods.has(method)) continue;
      operationCount += 1;
      if (!operation.responses || Object.keys(operation.responses).length === 0) {
        throw new Error(`${method.toUpperCase()} ${path} has no responses.`);
      }
    }
  }
  if (!operationCount) throw new Error('OpenAPI document has no HTTP operations.');
  return operationCount;
};

(async () => {
  try {
    const document = await fetchWithRetry(targetUrl);
    const operationCount = validate(document);
    console.log(`Valid OpenAPI document at ${targetUrl} (${operationCount} operations).`);
  } catch (error) {
    console.error(`OpenAPI validation failed for ${targetUrl}: ${error.message}`);
    process.exit(1);
  }
})();
