import { useState, useEffect, useCallback, type ReactNode } from 'react';
import { getToken, setToken as storeToken, clearToken } from '../lib/auth';

import { AuthContext } from './useAuth';

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
