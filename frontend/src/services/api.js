import axios from 'axios';
import { trace, traceOk, traceError } from '../utils/logger';

const BASE = process.env.REACT_APP_API_URL || 'http://localhost:5000';
const api  = axios.create({ baseURL: BASE });

// ── Global request/response tracing ────────────────────────────────────────
// Every single HTTP call the frontend makes is printed to console: method,
// url, request body, response body (or error body), and duration. This is
// the single most useful log when diagnosing "frontend never received the
// mapping JSON"-type bugs, because you can see EXACTLY what the backend
// sent back for /rebind-columns, /finalize, etc.
api.interceptors.request.use((config) => {
  config.metadata = { start: performance.now() };
  trace('HTTP →', `${config.method?.toUpperCase()} ${config.url}`, {
    baseURL: config.baseURL,
    params:  config.params,
    data:    config.data instanceof FormData ? '[FormData — see below]' : config.data,
  });
  if (config.data instanceof FormData) {
    const entries = {};
    for (const [k, v] of config.data.entries()) {
      entries[k] = v instanceof File ? `[File ${v.name}, ${v.size} bytes]` : v;
    }
    trace('HTTP →', `${config.method?.toUpperCase()} ${config.url} — FormData contents`, entries);
  }
  return config;
});

api.interceptors.response.use(
  (response) => {
    const ms = (performance.now() - (response.config.metadata?.start || 0)).toFixed(1);
    traceOk('HTTP ←', `${response.status} ${response.config.method?.toUpperCase()} ${response.config.url} (${ms}ms)`, response.data);
    return response;
  },
  (error) => {
    const cfg = error.config || {};
    const ms = (performance.now() - (cfg.metadata?.start || 0)).toFixed(1);
    traceError(
      'HTTP ✘',
      `${error.response?.status ?? 'NETWORK'} ${cfg.method?.toUpperCase()} ${cfg.url} (${ms}ms)`,
      { requestData: cfg.data, responseData: error.response?.data, message: error.message }
    );
    return Promise.reject(error);
  }
);

export function extractError(err) {
  const serverMsg = err?.response?.data?.error;
  if (serverMsg) return serverMsg;
  if (err?.message) return err.message;
  return 'Unknown error';
}

// Step 1: Upload PBIX + configs → publish to PBI → extract visual bindings + schemas
export async function initSession({ pbixFile, snowflakeConfig, powerBIConfig }) {
  trace('API', 'initSession() called', {
    fileName: pbixFile?.name, fileSize: pbixFile?.size,
    snowflakeConfig: { ...snowflakeConfig, password: snowflakeConfig?.password ? '[REDACTED]' : undefined },
    powerBIConfig:   { ...powerBIConfig,   clientSecret: powerBIConfig?.clientSecret ? '[REDACTED]' : undefined },
  });
  const fd = new FormData();
  fd.append('pbixFile', pbixFile);
  fd.append('snowflakeConfigJson', JSON.stringify(snowflakeConfig));
  fd.append('powerBIConfigJson',   JSON.stringify(powerBIConfig));
  try {
    const { data } = await api.post('/api/migration/init', fd, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    trace('API', 'initSession() resolved', {
      sessionId: data.sessionId, reportId: data.reportId, datasetId: data.datasetId,
      schemaCount: data.schemas?.length, visualBindingCount: data.visualBindings?.length,
      visualMetadataCount: data.visualMetadata?.length, status: data.status,
    });
    return data; // { sessionId, reportId, datasetId, schemas, visualBindings, status }
  } catch (err) {
    traceError('API', 'initSession() failed', err);
    throw new Error(extractError(err));
  }
}

// Step 2: Select schemas → clean dataset → load all SF views → auto-bind → embed
export async function selectSchemas({ sessionId, selectedSchemas }) {
  trace('API', 'selectSchemas() called', { sessionId, selectedSchemas });
  try {
    const { data } = await api.post('/api/migration/select-schemas', { sessionId, selectedSchemas });
    trace('API', 'selectSchemas() resolved', {
      status: data.status, tablesLoaded: data.tablesLoaded, tablesTotal: data.tablesTotal,
      unmappedTableCount: data.unmappedTables?.length, hasEmbedToken: !!data.embedToken,
    });
    return data; // MigrationStatusResponse { status, embedUrl, embedToken, tablesLoaded, ... }
  } catch (err) {
    traceError('API', 'selectSchemas() failed', err);
    throw new Error(extractError(err));
  }
}

// Utility: list workspaces
export async function listWorkspaces(pbiConfig) {
  try {
    const { data } = await api.post('/api/migration/workspaces', pbiConfig);
    return data;
  } catch (err) {
    traceError('API', 'listWorkspaces() failed', err);
    throw new Error(extractError(err));
  }
}

export async function getStatus(sessionId) {
  try {
    const { data } = await api.get(`/api/migration/status/${sessionId}`);
    return data;
  } catch (err) {
    traceError('API', 'getStatus() failed', err);
    throw new Error(extractError(err));
  }
}

// Fetch valid Snowflake view/column map + embed info for a session
export async function getViewTables(sessionId) {
  try {
    const { data } = await api.get(`/api/migration/viewtables/${sessionId}`);
    return data; // { views: { viewName: [col1, col2, ...] }, reportId, datasetId, embedUrl, embedToken, visualMetadata }
  } catch (err) {
    traceError('API', 'getViewTables() failed', err);
    throw new Error(extractError(err));
  }
}

// Step 4: Visual Repair Layer finished → send outcomes → get back the
// Validation & Migration Report
export async function finalizeMigration(sessionId, visuals) {
  trace('API', 'finalizeMigration() called', { sessionId, visualCount: visuals?.length, visuals });
  try {
    const { data } = await api.post(`/api/migration/finalize/${sessionId}`, { visuals });
    trace('API', 'finalizeMigration() resolved', data);
    return data; // MigrationReport
  } catch (err) {
    traceError('API', 'finalizeMigration() failed', err);
    throw new Error(extractError(err));
  }
}

export async function getReport(sessionId) {
  try {
    const { data } = await api.get(`/api/migration/report/${sessionId}`);
    return data;
  } catch (err) {
    traceError('API', 'getReport() failed', err);
    throw new Error(extractError(err));
  }
}
