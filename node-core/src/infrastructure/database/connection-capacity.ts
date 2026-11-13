export interface CapacityRetryOptions {
  timeoutMs?: number;
  delay?: (milliseconds: number) => Promise<void>;
  now?: () => number;
  onRetry?: (delayMs: number) => void;
}

// Retry acquiring a connection only. Never replay SQL or a transaction: a write
// may have committed even when its response was lost.
export async function connectWithCapacityRetry<T>(connect: () => Promise<T>, options: CapacityRetryOptions = {}): Promise<T> {
  const now = options.now ?? Date.now;
  const delay = options.delay ?? (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const deadline = now() + (options.timeoutMs ?? 3000);
  let attempt = 0;
  for (;;) {
    try { return await connect(); }
    catch (error) {
      if ((error as { code?: string }).code !== '53300') throw error;
      const remaining = deadline - now();
      if (remaining <= 0) throw error;
      const wait = Math.min(500 * 2 ** Math.min(attempt++, 3), 3000, remaining);
      options.onRetry?.(wait);
      await delay(wait);
    }
  }
}
