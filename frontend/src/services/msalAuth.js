// ─────────────────────────────────────────────────────────────────────────
// Interactive Microsoft (Azure AD) sign-in via MSAL.js popup.
//
// Why this exists: BindSnowflakeCredentialsAsync (backend) and the ROPC
// fallback in PowerBIService both need a DELEGATED (real signed-in user)
// AAD token — the service-principal (app-only) token used everywhere else
// cannot bind datasource credentials in every tenant configuration, and
// ROPC requires storing a plaintext password and breaks outright under
// MFA/Conditional Access. A popup sign-in gets a delegated token the
// supported, non-legacy way: no stored password, works with MFA, and the
// user explicitly consents in a real Microsoft sign-in window.
//
// This token is used ONLY for the "bind dataset credentials" step so the
// dataset can actually pull rows (see UploadStep's "Sign in with
// Microsoft" button and PowerBIService.GetEffectiveTokenAsync on the
// backend).
//
// NOTE: cacheLocation is 'localStorage' (see getMsalInstance below), so the
// token now persists across reloads/tabs instead of dying with the tab.
// That's an intentional trade-off for convenience — it also means the
// token is readable by any script running on this page (XSS-exposed) and
// no longer auto-clears when the tab closes. If that trade-off changes,
// switch cacheLocation back to 'memoryStorage'.
// ─────────────────────────────────────────────────────────────────────────
import { PublicClientApplication, InteractionRequiredAuthError } from '@azure/msal-browser';
import { trace, traceOk, traceError } from '../utils/logger';

const TAG = 'MSAL';

// Scope for calling the Power BI REST API on behalf of the signed-in user —
// same scope PowerBIService uses for the app-only token, just delegated.
const PBI_SCOPE = 'https://analysis.windows.net/powerbi/api/.default';

let msalInstance = null;
let initializedForClientId = null;

// MSAL needs an app registration (clientId/tenantId) up front. This reuses
// the same Power BI service-principal app the user already entered in the
// form — that app registration also needs a "Single-page application"
// platform added in Azure AD with this page's URL as a redirect URI, and
// delegated "Power BI Service" → user_impersonation API permission granted.
async function getMsalInstance(clientId, tenantId) {
  if (msalInstance && initializedForClientId === clientId) return msalInstance;

  const instance = new PublicClientApplication({
    auth: {
      clientId,
      authority: `https://login.microsoftonline.com/${tenantId || 'organizations'}`,
      redirectUri: window.location.origin,
    },
    cache: {
      // Persisted in localStorage so the delegated token survives page
      // reloads/new tabs — trade-off: readable by any script on the page
      // (XSS-exposed) and outlives the browser session. See note at top
      // of this file.
      cacheLocation: 'localStorage',
      storeAuthStateInCookie: false,
    },
  });
  await instance.initialize();

  msalInstance = instance;
  initializedForClientId = clientId;
  return instance;
}

// Opens the Microsoft sign-in popup, returns { accessToken, account }.
// Call this from a click handler — browsers block popups not triggered by
// a direct user gesture.
export async function signInWithPopup(clientId, tenantId) {
  trace(TAG, 'signInWithPopup() — opening Microsoft sign-in popup', { clientId, tenantId });
  if (!clientId) throw new Error('Enter a Client ID before signing in with Microsoft.');

  const instance = await getMsalInstance(clientId, tenantId);

  try {
    const loginResult = await instance.loginPopup({
      scopes: [PBI_SCOPE],
      prompt: 'select_account',
    });
    instance.setActiveAccount(loginResult.account);
    traceOk(TAG, 'signInWithPopup() — signed in', {
      username: loginResult.account?.username,
      tokenLength: loginResult.accessToken?.length,
    });
    return { accessToken: loginResult.accessToken, account: loginResult.account };
  } catch (err) {
    traceError(TAG, 'signInWithPopup() failed', err);
    throw new Error(err?.errorMessage || err?.message || 'Microsoft sign-in failed');
  }
}

// Silently refreshes the token for the already-signed-in account, falling
// back to a popup if a silent refresh isn't possible (token expired past
// its refresh window, consent revoked, etc.).
export async function getAccessTokenSilentOrPopup(clientId, tenantId) {
  const instance = await getMsalInstance(clientId, tenantId);
  const account = instance.getActiveAccount() || instance.getAllAccounts()[0];
  if (!account) return signInWithPopup(clientId, tenantId);

  try {
    const result = await instance.acquireTokenSilent({ scopes: [PBI_SCOPE], account });
    return { accessToken: result.accessToken, account: result.account };
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      trace(TAG, 'Silent token refresh needs interaction — falling back to popup');
      return signInWithPopup(clientId, tenantId);
    }
    throw err;
  }
}

export function getSignedInAccount() {
  return msalInstance?.getActiveAccount() || null;
}

// ─────────────────────────────────────────────────────────────────────────
// Full sign-in: one popup, then three silent token acquisitions — one per
// resource this app talks to. AAD tokens are audience-scoped, so the same
// token can't be reused across Power BI / Fabric / OneLake; each needs its
// own acquireTokenSilent call with its own scope, though all three ride on
// the same signed-in session so only ONE popup is ever shown.
//
// Tokens are stored in localStorage as their own separate keys (not bundled
// into the rest of the form state) so a page reload doesn't force the user
// to sign in again. Same caveat as the note at the top of this file: this
// makes them readable by any script on the page and long-lived — call
// clearStoredTokens() on sign-out if that trade-off ever needs to change.
// ─────────────────────────────────────────────────────────────────────────
const PBI_EMBED_SCOPE = 'https://analysis.windows.net/powerbi/api/Report.Read.All';
const FABRIC_SCOPE     = 'https://api.fabric.microsoft.com/.default';
const ONELAKE_SCOPE    = 'https://storage.azure.com/.default';

export const TOKEN_STORAGE_KEYS = {
  pbi:     'pbixMigrator.pbiAccessToken',
  fabric:  'pbixMigrator.fabricAccessToken',
  onelake: 'pbixMigrator.oneLakeAccessToken',
  account: 'pbixMigrator.account',
};

async function acquireOne(instance, account, scope, clientId, tenantId) {
  try {
    const result = await instance.acquireTokenSilent({ scopes: [scope], account });
    return result.accessToken;
  } catch (err) {
    trace(TAG, `acquireOne(${scope}) — silent failed, falling back to popup`, err);
    const result = await instance.acquireTokenPopup({ scopes: [scope] });
    if (!instance.getActiveAccount()) instance.setActiveAccount(result.account);
    return result.accessToken;
  }
}

// Call this FIRST, before anything else in the app — every backend call
// downstream needs one of these three tokens.
export async function signInAndAcquireAllTokens(clientId, tenantId) {
  if (!clientId || !tenantId) throw new Error('Enter Tenant ID and Client ID before signing in.');

  const instance = await getMsalInstance(clientId, tenantId);

  trace(TAG, 'signInAndAcquireAllTokens() → opening Microsoft sign-in popup');
  const loginResult = await instance.loginPopup({
    scopes: [PBI_EMBED_SCOPE], // broadest/first scope; consent covers this app registration as a whole
    prompt: 'select_account',
  });
  instance.setActiveAccount(loginResult.account);

  const [pbiToken, fabricToken, oneLakeToken] = await Promise.all([
    Promise.resolve(loginResult.accessToken), // already acquired above for this scope
    acquireOne(instance, loginResult.account, FABRIC_SCOPE, clientId, tenantId),
    acquireOne(instance, loginResult.account, ONELAKE_SCOPE, clientId, tenantId),
  ]);

  const account = { username: loginResult.account?.username, name: loginResult.account?.name };

  localStorage.setItem(TOKEN_STORAGE_KEYS.pbi, pbiToken);
  localStorage.setItem(TOKEN_STORAGE_KEYS.fabric, fabricToken);
  localStorage.setItem(TOKEN_STORAGE_KEYS.onelake, oneLakeToken);
  localStorage.setItem(TOKEN_STORAGE_KEYS.account, JSON.stringify(account));

  traceOk(TAG, 'signInAndAcquireAllTokens() — all three tokens acquired + stored', {
    username: account.username,
    pbiLen: pbiToken?.length, fabricLen: fabricToken?.length, oneLakeLen: oneLakeToken?.length,
  });

  return { pbiToken, fabricToken, oneLakeToken, account };
}

// Reads back whatever's already in localStorage from a previous sign-in —
// call on app load so a page refresh doesn't force a re-login.
export function loadStoredTokens() {
  const pbiToken     = localStorage.getItem(TOKEN_STORAGE_KEYS.pbi) || '';
  const fabricToken  = localStorage.getItem(TOKEN_STORAGE_KEYS.fabric) || '';
  const oneLakeToken = localStorage.getItem(TOKEN_STORAGE_KEYS.onelake) || '';
  const accountRaw   = localStorage.getItem(TOKEN_STORAGE_KEYS.account);
  const account       = accountRaw ? JSON.parse(accountRaw) : null;
  return { pbiToken, fabricToken, oneLakeToken, account };
}

export function clearStoredTokens() {
  Object.values(TOKEN_STORAGE_KEYS).forEach(k => localStorage.removeItem(k));
}

// ─────────────────────────────────────────────────────────────────────────
// Gets an access token scoped specifically to Report.Read.All, for the
// "get embed token" call. Tries silent acquisition first (works if the
// user already has a cached session); falls back to the popup sign-in —
// same flow signInWithPopup() already uses elsewhere in this app.
//
// (Not acquireTokenRedirect: that navigates the whole page away and only
// resolves a token if something calls handleRedirectPromise() on the next
// page load. This app never does that — see index.js — so a redirect here
// would leave the user stuck on a blank reload with no token. Popup keeps
// the SPA on the same page and returns the token directly.)
// ─────────────────────────────────────────────────────────────────────────
const REPORT_EMBED_SCOPE = 'https://analysis.windows.net/powerbi/api/Report.Read.All';

export async function getReportEmbedAccessToken(clientId, tenantId) {
  const instance = await getMsalInstance(clientId, tenantId);
  const account = instance.getActiveAccount() || instance.getAllAccounts()[0];

  if (!account) {
    trace(TAG, 'getReportEmbedAccessToken() — no cached account, opening popup sign-in');
    const result = await signInWithPopup(clientId, tenantId);
    return result.accessToken;
  }

  try {
    const result = await instance.acquireTokenSilent({ scopes: [REPORT_EMBED_SCOPE], account });
    traceOk(TAG, 'getReportEmbedAccessToken() — acquired silently', { tokenLength: result.accessToken?.length });
    return result.accessToken;
  } catch (err) {
    traceError(TAG, 'getReportEmbedAccessToken() — silent acquisition failed, falling back to popup', err);
    const result = await signInWithPopup(clientId, tenantId);
    return result.accessToken;
  }
}

export function signOut() {
  const account = msalInstance?.getActiveAccount();
  clearStoredTokens();
  if (msalInstance && account) {
    // Popup logout so we don't navigate the whole app away.
    msalInstance.logoutPopup({ account }).catch(() => {});
  }
}