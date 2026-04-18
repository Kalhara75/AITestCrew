import { createContext, useContext, useState, useEffect } from 'react';
import type { ReactNode } from 'react';

interface AuthUser {
  id: string;
  name: string;
}

interface AuthContextType {
  user: AuthUser | null;
  apiKey: string | null;
  authRequired: boolean;
  login: (apiKey: string) => Promise<boolean>;
  logout: () => void;
  isLoading: boolean;
}

const AuthContext = createContext<AuthContextType | null>(null);

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}

const STORAGE_KEY = 'aitestcrew_api_key';
const BASE_URL = import.meta.env.VITE_API_URL || '/api';

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [apiKey, setApiKey] = useState<string | null>(() => localStorage.getItem(STORAGE_KEY));
  const [authRequired, setAuthRequired] = useState(false);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    init();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function init() {
    // Check if the server requires auth
    try {
      const res = await fetch(`${BASE_URL}/auth/status`);
      if (res.ok) {
        const data = await res.json();
        if (!data.authEnabled) {
          // File mode — no auth needed
          setIsLoading(false);
          return;
        }
        setAuthRequired(true);
      }
    } catch {
      // Server unreachable or no auth endpoint — skip auth
      setIsLoading(false);
      return;
    }

    // Auth is required — validate stored key if we have one
    if (apiKey) {
      const valid = await validateKey(apiKey);
      if (!valid) {
        localStorage.removeItem(STORAGE_KEY);
        setApiKey(null);
      }
    }
    setIsLoading(false);
  }

  async function validateKey(key: string): Promise<boolean> {
    try {
      const res = await fetch(`${BASE_URL}/users/validate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ apiKey: key }),
      });
      if (!res.ok) return false;
      const data = await res.json();
      if (data.valid && data.user) {
        setUser({ id: data.user.id, name: data.user.name });
        return true;
      }
      return false;
    } catch {
      return false;
    }
  }

  async function login(key: string): Promise<boolean> {
    const valid = await validateKey(key);
    if (valid) {
      setApiKey(key);
      localStorage.setItem(STORAGE_KEY, key);
    }
    return valid;
  }

  function logout() {
    setUser(null);
    setApiKey(null);
    localStorage.removeItem(STORAGE_KEY);
  }

  return (
    <AuthContext.Provider value={{ user, apiKey, authRequired, login, logout, isLoading }}>
      {children}
    </AuthContext.Provider>
  );
}
