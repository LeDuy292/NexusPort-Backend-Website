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
    it('exposes only the shipping-line intake write route and no general container edit/delete', () => {
        const all = routes();
        expect(all.filter(r => r.path.startsWith('/intake/') && r.methods.post).map(r => r.path))
            .toEqual(['/intake/shipping-line/manual']);
        expect(all.some(r => r.path === '/' && r.methods.post)).toBe(false);
        expect(all.some(r => r.path === '/:id' && (r.methods.put || r.methods.patch || r.methods.delete))).toBe(false);
    });
    it.each(['Administrator', 'Dispatcher', 'Yard Staff', 'Gate Officer', 'Carrier', 'Driver', 'Berth Staff'])('lets %s search identities and forbids registration', role => {
        const all = routes();
        const req = { containerUser: { id: 'test', role } } as unknown as Request;
        const searchNext = jest.fn();
        all.find(r => r.path === '/identities')!.stack[0].handle(req, {} as Response, searchNext);
        expect(searchNext).toHaveBeenCalledWith();

        const registerNext = jest.fn();
        all.find(r => r.path === '/intake/shipping-line/manual')!.stack[0].handle(req, {} as Response, registerNext);
        expect(registerNext).toHaveBeenCalledWith(expect.objectContaining({ statusCode: 403 }));
    });
    it('allows shipping lines to register while retaining staff status transitions', () => {
        const next = jest.fn();
        routes().find(r => r.path === '/intake/shipping-line/manual')!.stack[0].handle({ containerUser: { id: 'test', role: 'Shipping Line' } } as unknown as Request, {} as Response, next);
        expect(next).toHaveBeenCalledWith();
        expect(routes().some(r => r.path === '/:id/status/transition' && r.methods.post)).toBe(true);
    });
});
