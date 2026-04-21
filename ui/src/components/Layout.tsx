import { Link, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { useChat } from '../contexts/ChatContext';
import { ChatDrawer } from './chat/ChatDrawer';

export function Layout() {
  const location = useLocation();
  const isHome = location.pathname === '/';
  const { user, authRequired, logout } = useAuth();
  const { isOpen: chatOpen, toggle: toggleChat } = useChat();

  return (
    <div style={{ minHeight: '100vh', background: '#f8fafc' }}>
      <header style={{
        background: 'linear-gradient(135deg, #0f172a 0%, #1e293b 100%)',
        color: '#fff',
        padding: '0 32px',
        height: 56,
        display: 'flex',
        alignItems: 'center',
        gap: 32,
        boxShadow: '0 1px 3px rgba(0,0,0,0.2)',
      }}>
        <Link to="/" style={{
          color: '#fff',
          textDecoration: 'none',
          fontSize: 18,
          fontWeight: 700,
          display: 'flex',
          alignItems: 'center',
          gap: 10,
        }}>
          <span style={{
            background: 'linear-gradient(135deg, #38bdf8, #818cf8)',
            borderRadius: 6,
            width: 28,
            height: 28,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontSize: 14,
            fontWeight: 800,
          }}>
            AT
          </span>
          AI Test Crew
        </Link>
        <nav style={{ display: 'flex', gap: 8 }}>
          <NavLink to="/" label="Modules" active={isHome} />
        </nav>
        <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 12, fontSize: 13 }}>
          <button
            onClick={toggleChat}
            title="Assistant"
            style={{
              background: chatOpen ? 'rgba(56,189,248,0.2)' : 'rgba(255,255,255,0.1)',
              border: '1px solid rgba(255,255,255,0.2)',
              color: chatOpen ? '#38bdf8' : '#e2e8f0',
              padding: '4px 12px',
              borderRadius: 4,
              cursor: 'pointer',
              fontSize: 12,
              fontWeight: 500,
            }}
          >
            Assistant
          </button>
          {authRequired && user && (
            <>
              <span style={{ color: '#94a3b8' }}>{user.name}</span>
              <button
                onClick={logout}
                style={{
                  background: 'rgba(255,255,255,0.1)',
                  border: '1px solid rgba(255,255,255,0.2)',
                  color: '#94a3b8',
                  padding: '4px 12px',
                  borderRadius: 4,
                  cursor: 'pointer',
                  fontSize: 12,
                }}
              >
                Logout
              </button>
            </>
          )}
        </div>
      </header>
      <main style={{ maxWidth: 1200, margin: '0 auto', padding: '28px 24px' }}>
        <Outlet />
      </main>
      <ChatDrawer />
    </div>
  );
}

function NavLink({ to, label, active }: { to: string; label: string; active: boolean }) {
  return (
    <Link to={to} style={{
      color: active ? '#fff' : '#94a3b8',
      textDecoration: 'none',
      fontSize: 14,
      fontWeight: 500,
      padding: '6px 14px',
      borderRadius: 6,
      background: active ? 'rgba(255,255,255,0.1)' : 'transparent',
      transition: 'background 0.15s, color 0.15s',
    }}>
      {label}
    </Link>
  );
}
