import { create } from "zustand";
import { useShallow } from "zustand/react/shallow";
import { api, tokenStorage } from "@/shared/api/client";
import type { AuthResponse } from "@/shared/api/types";

interface AuthUser {
  userId: string;
  email: string;
  displayName: string;
}

interface AuthState {
  user: AuthUser | null;
  hydrated: boolean;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string, displayName?: string) => Promise<void>;
  logout: () => void;
  hydrate: () => void;
}

function decodePayload(token: string): AuthUser | null {
  try {
    const [, payload] = token.split(".");
    if (!payload) return null;
    const json = JSON.parse(atob(payload.replace(/-/g, "+").replace(/_/g, "/")));
    if (!json.sub || !json.email) return null;
    const exp = Number(json.exp);
    if (exp && exp * 1000 < Date.now()) return null;
    return { userId: String(json.sub), email: String(json.email), displayName: String(json.email).split("@")[0] };
  } catch {
    return null;
  }
}

export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  hydrated: false,
  hydrate: () => {
    const token = tokenStorage.get();
    const user = token ? decodePayload(token) : null;
    if (!user && token) tokenStorage.clear();
    set({ user, hydrated: true });
  },
  login: async (email, password) => {
    const res = await api.post<AuthResponse>("/api/auth/login", { email, password });
    tokenStorage.set(res.token);
    set({
      user: { userId: res.userId, email: res.email, displayName: res.displayName },
    });
  },
  register: async (email, password, displayName) => {
    const res = await api.post<AuthResponse>("/api/auth/register", { email, password, displayName });
    tokenStorage.set(res.token);
    set({
      user: { userId: res.userId, email: res.email, displayName: res.displayName },
    });
  },
  logout: () => {
    tokenStorage.clear();
    set({ user: null });
  },
}));

export function useAuth() {
  return useAuthStore(
    useShallow((s) => ({
      user: s.user,
      isAuthenticated: !!s.user,
      login: s.login,
      register: s.register,
      logout: s.logout,
    }))
  );
}
