import { createContainerRouter } from './container.controller';
import { Request, Response, NextFunction } from 'express';
type Layer = {
    route?: {
        path: string;
        methods: Record<string, boolean>;
        stack: Array<{
            handle: (req: Request, res: Response, next: NextFunction) => void;
        }>;
    };
};
const routes = () => (createContainerRouter() as unknown as {
    stack: Layer[];
}).stack.filter(l => l.route).map(l => l.route!);
describe('Container declaration access', () => {
    it('removes staff create/edit/delete and port intake endpoints', () => {
        const all = routes();
        expect(all.some(r => r.path.includes('/intake/port'))).toBe(false);
        expect(all.some(r => r.path === '/' && r.methods.post)).toBe(false);
        expect(all.some(r => r.path === '/:id' && (r.methods.put || r.methods.delete))).toBe(false);
    });
    it.each(['Administrator', 'Dispatcher', 'Yard Staff', 'Gate Officer'])('forbids %s on carrier declaration routes', role => {
        for (const path of ['/intake/transport/manual', '/intake/transport/import', '/intake/transport/template']) {
            const next = jest.fn();
            const route = routes().find(r => r.path === path)!;
            route.stack[0].handle({ containerUser: { id: 'test', role } } as unknown as Request, {} as Response, next);
            expect(next.mock.calls[0][0]).toMatchObject({ statusCode: 403 });
        }
    });
    it('allows carriers to declare while retaining staff status transitions', () => {
        const next = jest.fn();
        routes().find(r => r.path === '/intake/transport/manual')!.stack[0].handle({ containerUser: { id: 'test', role: 'Carrier' } } as unknown as Request, {} as Response, next);
        expect(next).toHaveBeenCalledWith();
        expect(routes().some(r => r.path === '/:id/status/transition' && r.methods.post)).toBe(true);
    });
});
