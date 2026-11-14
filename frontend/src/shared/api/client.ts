const TOKEN_KEY = "mazda.token";

export const tokenStorage = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (token: string) => localStorage.setItem(TOKEN_KEY, token),
  clear: () => localStorage.removeItem(TOKEN_KEY),
};

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly details?: unknown
  ) {
    super(message);
  }
}

interface RequestOpts {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  signal?: AbortSignal;
}

async function request<T>(path: string, opts: RequestOpts = {}): Promise<T> {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  const token = tokenStorage.get();
  if (token) headers["Authorization"] = `Bearer ${token}`;

  const res = await fetch(path, {
    method: opts.method ?? "GET",
    headers,
    body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
    signal: opts.signal,
  });

  if (res.status === 204) return undefined as T;

  const text = await res.text();
  const data = text ? safeJson(text) : null;

  if (!res.ok) {
    throw new ApiError(errorMessage(data, res.status), res.status, data);
  }

  return data as T;
}

/**
 * Pulls the human-readable message out of whatever the API returned.
 * The backend speaks two shapes: `{ error, details? }` for NotFound /
 * Validation / Conflict, and RFC-7807 ProblemDetails (`detail` + `title`) for
 * External (502) and unhandled (500) failures — which is exactly where LLM
 * failures land ("LLM rate limit or quota/budget exceeded (429): ..."). Only
 * reading `error` turned every one of those into "Request failed (502)".
 */
function errorMessage(data: unknown, status: number): string {
  const fallback = `Request failed (${status})`;
  if (typeof data === "string") return data.trim() || fallback;
  if (!data || typeof data !== "object") return fallback;

  const d = data as Record<string, unknown>;
  const base = [d.error, d.detail, d.title, d.message].find(
    (v): v is string => typeof v === "string" && v.trim().length > 0
  );

  // FluentValidation failures ship the per-field errors alongside a generic
  // "Validation failed" — append them so the alert names the actual field.
  if (Array.isArray(d.details)) {
    const fields = d.details
      .map((e) =>
        e && typeof e === "object" && "errorMessage" in e
          ? String((e as { errorMessage: unknown }).errorMessage)
          : typeof e === "string"
            ? e
            : null
      )
      .filter((v): v is string => !!v);
    if (fields.length) return `${base ?? fallback}: ${fields.join("; ")}`;
  }

  return base ?? fallback;
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export const api = {
  get: <T>(path: string, signal?: AbortSignal) => request<T>(path, { signal }),
  post: <T>(path: string, body?: unknown) => request<T>(path, { method: "POST", body }),
  put: <T>(path: string, body?: unknown) => request<T>(path, { method: "PUT", body }),
  patch: <T>(path: string, body?: unknown) => request<T>(path, { method: "PATCH", body }),
  del: <T>(path: string) => request<T>(path, { method: "DELETE" }),
};
