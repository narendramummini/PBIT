import { getState, setState } from "@/lib/conversion-store";

const API_BASE = (import.meta.env.VITE_MIGRATION_API_URL as string | undefined) ?? "http://localhost:5001";
const AUTH_BASE = ((import.meta.env.VITE_WORKSPACE_API_URL as string | undefined) ?? "http://localhost:8000").replace(/\/$/, "");
let refreshInFlight: Promise<boolean> | null = null;

async function refreshAccessToken(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    const refreshToken = getState().refreshToken;
    if (!refreshToken) return false;
    try {
      const response = await fetch(`${AUTH_BASE}/auth/refresh`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refresh_token: refreshToken }),
      });
      if (!response.ok) return false;
      const data = await response.json();
      setState({ apiAccessToken: data.api_access_token, refreshToken: data.refresh_token });
      return true;
    } catch { return false; }
    finally { refreshInFlight = null; }
  })();
  return refreshInFlight;
}

export async function authorizedFetch(base: string, path: string, init?: RequestInit): Promise<Response> {
  const send = () => {
    const token = getState().apiAccessToken;
    if (!token) throw new Error("Your sign-in session has no migration API access token. Please sign in again.");
    const headers = new Headers(init?.headers);
    headers.set("Authorization", `Bearer ${token}`);
    return fetch(`${base}${path}`, { ...init, headers });
  };
  let response = await send();
  if (response.status === 401 && await refreshAccessToken()) response = await send();
  return response;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await authorizedFetch(API_BASE, path, init);
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    const diagnostics = Array.isArray(body.diagnostics) ? body.diagnostics : [];
    const detail = diagnostics.slice(0, 3).map((item: any) => {
      const columns = Array.isArray(item.unmappedColumns) && item.unmappedColumns.length
        ? ` Missing: ${item.unmappedColumns.join(", ")}.`
        : "";
      const physical = item.physicalTable ? ` Physical: ${item.physicalTable}.` : "";
      return `${item.failedMethod ? `${item.failedMethod}: ` : ""}${item.tableName ?? item.originalTable ?? "semantic model"} - ${item.reason ?? "no detail returned"}.${columns}${physical}`;
    }).join(" ");
    throw new Error([body.error ?? `Migration API returned HTTP ${response.status}`, body.failedMethod, detail].filter(Boolean).join(" "));
  }
  return body as T;
}

export type PowerBIConfig = {
  tenantId: string; clientId: string; clientSecret: string; workspaceId: string;
  azureOpenAIEndpoint: string; azureOpenAIKey: string; azureOpenAIDeployment: string;
  xmlaEndpoint: string; userAccessToken: string; fabricAccessToken: string; oneLakeAccessToken: string; lakehouseSqlAccessToken: string;
};

export function startMigration(file: File, snowflake: Record<string, unknown>, powerBI: PowerBIConfig) {
  if (!powerBI.fabricAccessToken?.trim()) throw new Error("A signed-in Microsoft Fabric token is required to start this migration.");
  if (!powerBI.oneLakeAccessToken?.trim()) throw new Error("A signed-in OneLake token is required to start this migration.");
  if (!powerBI.lakehouseSqlAccessToken?.trim()) throw new Error("A signed-in Azure SQL token is required to query the Lakehouse SQL endpoint.");
  const body = new FormData();
  body.append("pbixFile", file);
  body.append("snowflakeConfigJson", JSON.stringify(snowflake));
  body.append("powerBIConfigJson", JSON.stringify(powerBI));
  return request<any>("/api/migration/init", { method: "POST", body });
}

export function selectSchemas(sessionId: string, selectedSchemas: string[]) {
  return request<any>("/api/migration/select-schemas", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sessionId, selectedSchemas }) });
}
export function migrationStatus(sessionId: string) { return request<any>(`/api/migration/status/${encodeURIComponent(sessionId)}`); }
export function migrationReport(sessionId: string) { return request<any>(`/api/migration/report/${encodeURIComponent(sessionId)}`); }
export function viewTables(sessionId: string) { return request<any>(`/api/migration/viewtables/${encodeURIComponent(sessionId)}`); }
export function finalizeMigration(sessionId: string, visuals: unknown[]) {
  return request<any>(`/api/migration/finalize/${encodeURIComponent(sessionId)}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ visuals }) });
}
export function repairVisuals(sessionId: string, visuals: unknown[]) {
  return request<any>(`/api/migration/rebind-columns/${encodeURIComponent(sessionId)}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ visuals }) });
}
