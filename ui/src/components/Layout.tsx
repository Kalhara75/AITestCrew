import { Link, Outlet, useLocation } from 'react-router-dom';

export function Layout() {
  const location = useLocation();
  const isHome = location.pathname === '/';

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
      </header>
      <main style={{ maxWidth: 1200, margin: '0 auto', padding: '28px 24px' }}>
        <Outlet />
      </main>
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
