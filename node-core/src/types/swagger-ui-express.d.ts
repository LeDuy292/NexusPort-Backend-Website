declare module 'swagger-ui-express' {
  import { RequestHandler } from 'express';
  export const serve: RequestHandler[];
  export function setup(swaggerDoc?: any, opts?: any, options?: any, customCss?: any, customfavIcon?: any, swaggerUrl?: any, customSiteTitle?: any): RequestHandler;
}
