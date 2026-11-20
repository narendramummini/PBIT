// ─────────────────────────────────────────────────────────────────────────
// Central console logging helper.
//
// Goal: every meaningful thing that happens in the frontend (API calls,
// embed lifecycle events, per-visual field read/write, etc.) gets printed
// to the browser console in a consistent, greppable format, AND is kept
// in an in-memory ring buffer (window.__PBIX_LOGS) so it can be dumped /
// copy-pasted for a bug report even after the fact.
//
// Usage:
//   import { trace, group, groupEnd } from '../utils/logger';
//   trace('API', 'POST /api/migration/init', { payload });
// ─────────────────────────────────────────────────────────────────────────

const RING_BUFFER_MAX = 2000;
if (typeof window !== 'undefined' && !window.__PBIX_LOGS) {
  window.__PBIX_LOGS = [];
  // Handy in devtools: window.__pbixDump() copies the whole trace as JSON.
  window.__pbixDump = () => {
    const json = JSON.stringify(window.__PBIX_LOGS, null, 2);
    console.log(json);
    return json;
  };
}

function timestamp() {
  const d = new Date();
  return d.toISOString().split('T')[1].replace('Z', '');
}

function record(tag, level, msg, data) {
  const entry = { t: timestamp(), tag, level, msg, data };
  if (typeof window !== 'undefined') {
    window.__PBIX_LOGS.push(entry);
    if (window.__PBIX_LOGS.length > RING_BUFFER_MAX) window.__PBIX_LOGS.shift();
  }
  return entry;
}

const styles = {
  info:  'color:#5b8def;font-weight:bold',
  warn:  'color:#e0a800;font-weight:bold',
  error: 'color:#e03131;font-weight:bold',
  ok:    'color:#2f9e44;font-weight:bold',
};

// trace(): single-line log, always visible, includes an expandable data blob.
export function trace(tag, msg, data, level = 'info') {
  record(tag, level, msg, data);
  const prefix = `%c[${timestamp()}] [${tag}]`;
  if (data !== undefined) {
    console.log(prefix, styles[level] || styles.info, msg, data);
  } else {
    console.log(prefix, styles[level] || styles.info, msg);
  }
}

export const traceOk    = (tag, msg, data) => trace(tag, msg, data, 'ok');
export const traceWarn  = (tag, msg, data) => trace(tag, msg, data, 'warn');
export const traceError = (tag, msg, data) => trace(tag, msg, data, 'error');

// group(): collapsible section for a multi-step block (e.g. one visual's
// repair). Always call groupEnd() when done — use try/finally.
export function group(tag, title, data) {
  record(tag, 'group', title, data);
  console.groupCollapsed(`%c[${timestamp()}] [${tag}] ${title}`, styles.info);
  if (data !== undefined) console.log(data);
}

export function groupEnd() {
  console.groupEnd();
}

// table(): pretty-print an array of objects as a console.table, plus record it.
export function table(tag, title, rows) {
  record(tag, 'table', title, rows);
  console.log(`%c[${timestamp()}] [${tag}] ${title}`, styles.info);
  if (Array.isArray(rows) && rows.length > 0) console.table(rows);
}

// time(): wraps an async function, logging start/end/duration/error.
export async function timeIt(tag, label, fn) {
  const start = performance.now();
  trace(tag, `▶ START ${label}`);
  try {
    const result = await fn();
    const ms = (performance.now() - start).toFixed(1);
    traceOk(tag, `✔ DONE ${label} (${ms}ms)`, result);
    return result;
  } catch (e) {
    const ms = (performance.now() - start).toFixed(1);
    traceError(tag, `✘ FAILED ${label} (${ms}ms): ${e.message}`, e);
    throw e;
  }
}
