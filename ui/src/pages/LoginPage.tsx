import { useState } from 'react';
import { useAuth } from '../contexts/AuthContext';

export function LoginPage() {
  const { login } = useAuth();
  const [key, setKey] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);
    const ok = await login(key.trim());
    setLoading(false);
    if (!ok) setError('Invalid or inactive API key');
  }

  return (
    <div style={{
      minHeight: '100vh',
      background: 'linear-gradient(135deg, #0f172a 0%, #1e293b 50%, #0f172a 100%)',
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
    }}>
      {/* Logo */}
      <div style={{ marginBottom: 32, textAlign: 'center' }}>
        <div style={{
          background: 'linear-gradient(135deg, #38bdf8, #818cf8)',
          borderRadius: 14,
          width: 56,
          height: 56,
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          fontSize: 22,
          fontWeight: 800,
          color: '#fff',
          marginBottom: 16,
        }}>
          AT
        </div>
        <h1 style={{
          color: '#f1f5f9',
          fontSize: 24,
          fontWeight: 700,
          margin: 0,
        }}>
          AI Test Crew
        </h1>
        <p style={{ color: '#64748b', fontSize: 14, margin: '8px 0 0' }}>
          Sign in with your API key
        </p>
      </div>

      {/* Card */}
      <form onSubmit={handleSubmit} style={{
        width: 380,
        padding: '28px 32px 32px',
        borderRadius: 12,
        background: 'rgba(255,255,255,0.04)',
        border: '1px solid rgba(255,255,255,0.08)',
        backdropFilter: 'blur(12px)',
      }}>
        <label style={{ display: 'block', color: '#94a3b8', fontSize: 13, fontWeight: 500, marginBottom: 6 }}>
          API Key
        </label>
        <input
          type="password"
          value={key}
          onChange={(e) => setKey(e.target.value)}
          placeholder="atc_..."
          autoFocus
          style={{
            width: '100%',
            padding: '10px 12px',
            marginBottom: 16,
            borderRadius: 8,
            border: '1px solid rgba(255,255,255,0.12)',
            background: 'rgba(0,0,0,0.3)',
            color: '#e2e8f0',
            fontSize: 14,
            boxSizing: 'border-box',
            outline: 'none',
            transition: 'border-color 0.15s',
          }}
          onFocus={(e) => e.target.style.borderColor = 'rgba(129,140,248,0.5)'}
          onBlur={(e) => e.target.style.borderColor = 'rgba(255,255,255,0.12)'}
        />

        {error && (
          <p style={{
            color: '#f87171',
            fontSize: 13,
            margin: '-8px 0 12px',
            display: 'flex',
            alignItems: 'center',
            gap: 6,
          }}>
            {error}
          </p>
        )}

        <button
          type="submit"
          disabled={loading || !key.trim()}
          style={{
            width: '100%',
            padding: '10px 16px',
            borderRadius: 8,
            border: 'none',
            background: loading || !key.trim()
              ? 'rgba(99,102,241,0.4)'
              : 'linear-gradient(135deg, #6366f1, #818cf8)',
            color: '#fff',
            cursor: loading || !key.trim() ? 'not-allowed' : 'pointer',
            fontWeight: 600,
            fontSize: 14,
            transition: 'opacity 0.15s',
          }}
        >
          {loading ? 'Validating...' : 'Sign In'}
        </button>
      </form>

      <p style={{ color: '#475569', fontSize: 12, marginTop: 24 }}>
        Ask your admin for an API key to get started
      </p>
    </div>
  );
}
