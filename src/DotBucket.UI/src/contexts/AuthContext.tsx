import { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from 'react';
import { getToken, setToken as storeToken, clearToken } from '../lib/auth';

interface AuthContextValue {
  isAuthenticated: boolean;
  token: string | null;
  login: (token: string) => void;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setTokenState] = useState<string | null>(getToken);

  const login = useCallback((newToken: string) => {
    storeToken(newToken);
    setTokenState(newToken);
  }, []);

  const logout = useCallback(() => {
    clearToken();
    setTokenState(null);
  }, []);

  useEffect(() => {
    const handler = () => logout();
    window.addEventListener('dotbucket:unauthorized', handler);
    return () => window.removeEventListener('dotbucket:unauthorized', handler);
  }, [logout]);

  return (
    <AuthContext.Provider value={{ isAuthenticated: !!token, token, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
