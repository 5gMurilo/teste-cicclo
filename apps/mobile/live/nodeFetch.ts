import http from 'node:http';

// The React Native Jest environment replaces fetch with a stub that never talks HTTP; use a minimal real one.
/** Installs a minimal real fetch over node:http, only for the live tests. */
export function installNodeFetch(): void {
  globalThis.fetch = ((input: string | URL | Request, init?: RequestInit) =>
  new Promise<Response>((resolve, reject) => {
    const request = http.request(
      String(input),
      { method: init?.method ?? 'GET', headers: init?.headers as Record<string, string> },
      (response) => {
        const chunks: Buffer[] = [];
        response.on('data', (chunk: Buffer) => chunks.push(chunk));
        response.on('end', () => {
          const status = response.statusCode ?? 0;
          resolve({
            status,
            ok: status >= 200 && status < 300,
            headers: { get: (name: string) => (response.headers[name.toLowerCase()] as string | undefined) ?? null },
            text: async () => Buffer.concat(chunks).toString('utf8'),
          } as unknown as Response);
        });
      },
    );
    request.on('error', reject);
    init?.signal?.addEventListener('abort', () => request.destroy(new Error('aborted')));
    if (init?.body !== undefined) request.write(init.body as string);
    request.end();
  })) as typeof fetch;
}
