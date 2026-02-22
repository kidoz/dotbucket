import { AuthProvider, useAuth } from './contexts/AuthContext';
import { Dashboard } from './pages/Dashboard';
import { LoginPage } from './pages/LoginPage';

function AppContent() {
  const { isAuthenticated } = useAuth();

  return (
    <div className="min-h-screen bg-slate-50 text-slate-900">
      {isAuthenticated ? <Dashboard /> : <LoginPage />}
    </div>
  );
}

function App() {
  return (
    <AuthProvider>
      <AppContent />
    </AuthProvider>
  );
}

export default App;
