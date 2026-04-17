// When VITE_API_URL is set (e.g. during dev with separate Vite server), use it.
// Otherwise use a relative path so the UI works when co-hosted with the WebApi.
const BASE_URL = import.meta.env.VITE_API_URL || '/api';
const STORAGE_KEY = 'aitestcrew_api_key';

export async function apiFetch<T>(path: string, options?: RequestInit): Promise<T> {
  const apiKey = localStorage.getItem(STORAGE_KEY);
  const res = await fetch(`${BASE_URL}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...(apiKey ? { 'X-Api-Key': apiKey } : {}),
      ...options?.headers,
    },
  });
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`API ${res.status}: ${body}`);
  }
  return res.json();
}
