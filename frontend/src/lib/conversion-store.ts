import { useEffect, useState } from "react";

export type ConversionState = {
  user?: { name: string; email: string };
  accessToken?: string;
  apiAccessToken?: string;
  refreshToken?: string;
  tenantId?: string;
  clientId?: string;
  microsoftEmail?: string;
  fabricAccessToken?: string;
  oneLakeAccessToken?: string;
  migrationSessionId?: string;
  migrationStatus?: any;
  migrationReport?: any;
  availableSchemas?: string[];
  visualBindings?: any[];
  embed?: { embedUrl?: string; embedToken?: string; reportId?: string; datasetId?: string };
  selectedReportId?: string;
  selectedReportName?: string;
  selectedWorkspace?: string;
  selectedWorkspaceId?: string;
  snowflake?: {
    accountUrl: string;
    warehouse: string;
    database: string;
    schema: string;
    username: string;
  };
};

// PBIX files and credentials stay in memory only; never serialize them into web storage.
let pendingPbix: File | null = null;
export function setPendingPbix(file: File | null) { pendingPbix = file; }
export function getPendingPbix() { return pendingPbix; }

const KEY = "pbix-converter-state";
let volatileState: Partial<ConversionState> = {};

export function getState(): ConversionState {
  if (typeof window === "undefined") return {};
  try {
    const stored = JSON.parse(sessionStorage.getItem(KEY) || "{}") as Partial<ConversionState>;
    // Purge credentials written by older builds before they can be restored
    // into application state. New access/embed tokens live in volatileState.
    const { accessToken: _accessToken, apiAccessToken: _apiAccessToken, refreshToken: _refreshToken, fabricAccessToken: _fabricAccessToken, oneLakeAccessToken: _oneLakeAccessToken, embed: _embed, ...persisted } = stored;
    if (persisted.migrationStatus) {
      const { embedToken: _statusEmbedToken, ...safeStatus } = persisted.migrationStatus;
      persisted.migrationStatus = safeStatus;
    }
    sessionStorage.setItem(KEY, JSON.stringify(persisted));
    return { ...persisted, ...volatileState };
  } catch {
    return {};
  }
}

export function setState(patch: Partial<ConversionState>) {
  const next = { ...getState(), ...patch };
  if ("accessToken" in patch || "apiAccessToken" in patch || "refreshToken" in patch || "fabricAccessToken" in patch || "oneLakeAccessToken" in patch || "embed" in patch) volatileState = { ...volatileState, ...(patch.accessToken !== undefined ? { accessToken: patch.accessToken } : {}), ...(patch.apiAccessToken !== undefined ? { apiAccessToken: patch.apiAccessToken } : {}), ...(patch.refreshToken !== undefined ? { refreshToken: patch.refreshToken } : {}), ...(patch.fabricAccessToken !== undefined ? { fabricAccessToken: patch.fabricAccessToken } : {}), ...(patch.oneLakeAccessToken !== undefined ? { oneLakeAccessToken: patch.oneLakeAccessToken } : {}), ...(patch.embed !== undefined ? { embed: patch.embed } : {}) };
  // Access and embed tokens are short-lived credentials; keep them in memory only.
  const { accessToken: _accessToken, apiAccessToken: _apiAccessToken, refreshToken: _refreshToken, fabricAccessToken: _fabricAccessToken, oneLakeAccessToken: _oneLakeAccessToken, embed: _embed, ...persisted } = next;
  if (persisted.migrationStatus) {
    const { embedToken: _statusEmbedToken, ...safeStatus } = persisted.migrationStatus;
    persisted.migrationStatus = safeStatus;
  }
  sessionStorage.setItem(KEY, JSON.stringify(persisted));
  window.dispatchEvent(new CustomEvent("converter-state"));
  return next;
}

export function useConversionState() {
  const [state, setLocal] = useState<ConversionState>({});
  useEffect(() => {
    setLocal(getState());
    const handler = () => setLocal(getState());
    window.addEventListener("converter-state", handler);
    return () => window.removeEventListener("converter-state", handler);
  }, []);
  return state;
}
