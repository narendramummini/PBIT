import React, { useState, useEffect } from 'react';
import { signInAndAcquireAllTokens, loadStoredTokens, clearStoredTokens } from '../services/msalAuth';
import { trace, traceOk, traceError } from '../utils/logger';

const TAG = 'SIGNIN_GATE';

// This app no longer uses a service principal (client secret) for auth
// anywhere. Every backend call — Power BI, Fabric, OneLake — runs as
// whichever real user signs in here, first thing, before the rest of the
// app is reachable. The three resulting tokens are stored in localStorage
// as their own separate keys (see msalAuth.js TOKEN_STORAGE_KEYS) and
// passed up to App via onSignedIn so they can be threaded into every
// PowerBIConfig sent to the backend from here on.
export default function SignInGate({ onSignedIn }) {
  const [tenantId, setTenantId] = useState('');
  const [clientId, setClientId] = useState('');
  const [signingIn, setSigningIn] = useState(false);
  const [error, setError] = useState(null);
  const [restoredAccount, setRestoredAccount] = useState(null);

  // On mount: if a previous sign-in already left tokens in localStorage,
  // surface that instead of forcing a fresh popup every reload.
  useEffect(() => {
    const stored = loadStoredTokens();
    if (stored.pbiToken && stored.fabricToken && stored.oneLakeToken && stored.account) {
      trace(TAG, 'Found previously-stored tokens — offering to reuse them', { username: stored.account.username });
      setRestoredAccount(stored.account);
    }
  }, []);

  const handleSignIn = async () => {
    setError(null);
    setSigningIn(true);
    try {
      const { pbiToken, fabricToken, oneLakeToken, account } =
        await signInAndAcquireAllTokens(clientId, tenantId);
      traceOk(TAG, 'Signed in — handing tokens up to App', { username: account.username });
      onSignedIn({ pbiToken, fabricToken, oneLakeToken, account, tenantId, clientId });
    } catch (err) {
      traceError(TAG, 'Sign-in failed', err);
      setError(err.message || 'Microsoft sign-in failed');
    } finally {
      setSigningIn(false);
    }
  };

  const handleContinueWithStored = () => {
    const stored = loadStoredTokens();
    onSignedIn({ ...stored, tenantId, clientId });
  };

  const handleForgetAndResignIn = () => {
    clearStoredTokens();
    setRestoredAccount(null);
  };

  return (
    <div className="card" style={{ maxWidth: 480, margin: '48px auto' }}>
      <h2 className="card-title">Sign in with Microsoft</h2>
      <p className="hint">
        Every step below runs as your own Power BI / Fabric identity — there's no
        service-principal/client-secret fallback anymore, so this has to happen first.
      </p>

      {restoredAccount ? (
        <div className="section-block">
          <p>Found a stored session for <b>{restoredAccount.username}</b>.</p>
          <button type="button" className="btn-primary" onClick={handleContinueWithStored}>
            Continue as {restoredAccount.username}
          </button>
          <button type="button" className="btn-secondary" onClick={handleForgetAndResignIn} style={{ marginLeft: 8 }}>
            Sign in as someone else
          </button>
        </div>
      ) : (
        <div className="section-block">
          <div className="field-row">
            <label>Tenant ID</label>
            <input type="text" value={tenantId} onChange={e => setTenantId(e.target.value)} autoComplete="off" />
          </div>
          <div className="field-row">
            <label>Client ID</label>
            <input type="text" value={clientId} onChange={e => setClientId(e.target.value)} autoComplete="off" />
          </div>
          <p className="hint">
            The app registration's own Client ID / Tenant ID — it needs a "Single-page application"
            platform with this page's URL as a redirect URI, and delegated permissions granted for
            Power BI Service, Fabric, and Azure Storage (user_impersonation).
          </p>
          <button type="button" className="btn-primary" onClick={handleSignIn} disabled={signingIn || !tenantId || !clientId}>
            {signingIn ? 'Signing in…' : 'Sign in with Microsoft'}
          </button>
        </div>
      )}

      {error && <div className="error-banner"><span>⚠ {error}</span></div>}
    </div>
  );
}