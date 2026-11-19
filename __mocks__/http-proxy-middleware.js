'use strict';

/**
 * Mock cho http-proxy-middleware (ESM module) trong môi trường Jest (CommonJS).
 * Tránh lỗi: SyntaxError: Unexpected token 'export' khi chạy Jest tests.
 */
function createProxyMiddleware() {
  return function proxyMiddleware(req, res, next) {
    if (typeof next === 'function') {
      next();
    }
  };
}

module.exports = {
  createProxyMiddleware,
};
