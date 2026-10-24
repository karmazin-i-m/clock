/**
 * fetch() with the two rules the server holds every browser call to: the session cookie goes
 * along (same origin), and every non-GET carries X-Requested-With, without which
 * RequestedWithCsrfFilter answers 403. A 401 means the session is gone, and the whole page is
 * sent to sign in — the server answers /api with 401 rather than a redirect a fetch could not
 * follow.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export function signInUrl(returnTo = location.pathname + location.search): string {
  return `/login?next=${encodeURIComponent(returnTo)}`;
}

export async function api<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const { json, ...rest } = init;
  const headers = new Headers(rest.headers);
  if (json !== undefined) headers.set('Content-Type', 'application/json');
  if ((rest.method ?? 'GET') !== 'GET') headers.set('X-Requested-With', 'kclock-cabinet');

  const response = await fetch(path, {
    ...rest,
    headers,
    credentials: 'same-origin',
    body: json !== undefined ? JSON.stringify(json) : rest.body,
  });

  if (response.status === 401 && location.pathname !== '/login') {
    location.assign(signInUrl());
    throw new ApiError(401, 'signed out');
  }

  if (!response.ok) {
    let message = response.statusText;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) message = body.error;
    } catch {
      // Not every failure has a body; the status says enough.
    }
    throw new ApiError(response.status, message);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
