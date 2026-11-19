import { useState } from 'react';
import { useAuth } from '../contexts/useAuth';
import { SummaryView } from './SummaryView';
import { StorageView } from './StorageView';
import { IamView } from './IamView';
import { ClusterView } from './ClusterView';
import { Button } from '@/components/ui/button';
import {
  LayoutDashboard,
  Folder,
  ShieldCheck,
  Server,
  LogOut,
  ChevronLeft,
  ChevronRight,
  Menu,
} from 'lucide-react';

type ViewType = 'summary' | 'storage' | 'iam' | 'cluster';

export function Dashboard() {
  const { logout } = useAuth();
  const [activeView, setActiveView] = useState<ViewType>('summary');
  const [isSidebarCollapsed, setIsSidebarCollapsed] = useState(false);

  const navItems = [
    { id: 'summary', label: 'Summary', icon: LayoutDashboard },
    { id: 'storage', label: 'Storage', icon: Folder },
    { id: 'iam', label: 'Identity', icon: ShieldCheck },
    { id: 'cluster', label: 'Cluster', icon: Server },
  ] as const;

  const renderView = () => {
    switch (activeView) {
      case 'summary':
        return <SummaryView />;
      case 'storage':
        return <StorageView />;
      case 'iam':
        return <IamView />;
      case 'cluster':
        return <ClusterView />;
      default:
        return <SummaryView />;
    }
  };

  return (
    <div className="flex h-screen overflow-hidden bg-slate-50">
      {/* Sidebar */}
      <aside
        className={`flex flex-col border-r bg-white transition-all duration-300 ease-in-out ${
          isSidebarCollapsed ? 'w-16' : 'w-64'
        }`}
      >
        <div className="flex items-center justify-between border-b p-4">
          {!isSidebarCollapsed && (
            <div className="flex items-center space-x-2 overflow-hidden">
              <img src="/dotbucket-logo.svg" alt="Logo" className="h-8 w-8 flex-shrink-0" />
              <span className="truncate text-lg font-bold">DotBucket</span>
            </div>
          )}
          {isSidebarCollapsed && (
            <img src="/dotbucket-logo.svg" alt="Logo" className="mx-auto h-8 w-8" />
          )}
        </div>

        <nav className="flex-1 space-y-1 overflow-y-auto p-2">
          {navItems.map((item) => (
            <button
              key={item.id}
              onClick={() => setActiveView(item.id)}
              className={`flex w-full items-center rounded-md p-2 transition-colors ${
                activeView === item.id
                  ? 'bg-indigo-50 text-indigo-700'
                  : 'text-slate-600 hover:bg-slate-100'
              }`}
              title={isSidebarCollapsed ? item.label : undefined}
            >
              <item.icon className={`h-5 w-5 ${isSidebarCollapsed ? 'mx-auto' : 'mr-3'}`} />
              {!isSidebarCollapsed && <span className="text-sm font-medium">{item.label}</span>}
            </button>
          ))}
        </nav>

        <div className="space-y-1 border-t p-2">
          <button
            onClick={() => setIsSidebarCollapsed(!isSidebarCollapsed)}
            className="flex w-full items-center rounded-md p-2 text-slate-500 hover:bg-slate-100"
          >
            {isSidebarCollapsed ? (
              <ChevronRight className="mx-auto h-5 w-5" />
            ) : (
              <>
                <ChevronLeft className="mr-3 h-5 w-5" />
                <span className="text-sm font-medium">Collapse</span>
              </>
            )}
          </button>
          <button
            onClick={logout}
            className="flex w-full items-center rounded-md p-2 text-red-600 hover:bg-red-50"
          >
            <LogOut className={`h-5 w-5 ${isSidebarCollapsed ? 'mx-auto' : 'mr-3'}`} />
            {!isSidebarCollapsed && <span className="text-sm font-medium">Logout</span>}
          </button>
        </div>
      </aside>

      {/* Main Content */}
      <main className="flex flex-1 flex-col overflow-hidden">
        <header className="flex items-center justify-between border-b bg-white p-4 lg:hidden">
          <div className="flex items-center space-x-2">
            <img src="/dotbucket-logo.svg" alt="Logo" className="h-6 w-6" />
            <span className="font-bold">DotBucket</span>
          </div>
          <Button variant="ghost" size="icon">
            <Menu className="h-5 w-5" />
          </Button>
        </header>
        <div className="flex-1 overflow-y-auto p-8">{renderView()}</div>
      </main>
    </div>
  );
}
