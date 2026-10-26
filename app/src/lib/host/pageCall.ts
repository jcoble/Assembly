import { BASE, TOKEN } from './backend';

const notPorted = new Set<string>();

// One of the backend's page calls (Assembly.Infrastructure/Http/PageRoutes.cs): POST /page/<method> with the params as JSON.
// A method the backend doesn't have logs one "not ported" line per name and answers undefined.
export async function pageCall<T>(method: string, params?: Record<string, unknown>): Promise<T> {
  const res = await fetch(`${BASE}/page/${encodeURIComponent(method)}`, {
    method: 'POST',
    headers: { authorization: `Bearer ${TOKEN ?? ''}`, 'content-type': 'application/json' },
    body: JSON.stringify(params ?? {}),
  });
  if (res.status === 404) {
    if (!notPorted.has(method)) console.warn(`not ported: ${method}`);
    notPorted.add(method);
    return undefined as T;
  }
  const answer = (await res.json()) as { result?: T; error?: string };
  if (!res.ok) throw new Error(answer.error ?? `${method} returned ${res.status}`);
  return answer.result as T;
}
