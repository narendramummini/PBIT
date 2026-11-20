import React, { useState } from 'react';
import SignInGate from './components/SignInGate';
import UploadStep from './components/UploadStep';
import SchemaStep from './components/SchemaStep';
import EmbedStep  from './components/EmbedStep';
import { initSession, selectSchemas } from './services/api';
import { trace, traceError } from './utils/logger';
import './App.css';

const TAG = 'APP';
const STEPS = ['Upload & Publish', 'Select Schemas', 'Repair & Validate'];

// eslint-disable-next-line no-console
console.log(
  '%cPBIX Migrator — console tracing enabled. Run window.__pbixDump() at any time to export the full trace log as JSON.',
  'color:#0078d4;font-weight:bold;font-size:12px'
);

export default function App() {
  const [step, setStep]       = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError]     = useState(null);

  // ── Auth: must complete before anything else is reachable ──────────────
  // { pbiToken, fabricToken, oneLakeToken, account, tenantId, clientId }
  const [auth, setAuth] = useState(null);

  // Session data
  const [sessionId, setSessionId]         = useState(null);
  const [schemas, setSchemas]             = useState([]);
  const [visualBindings, setVisualBindings] = useState([]);
  const [embedData, setEmbedData]         = useState(null);

  // ── Step 1: Upload PBIX + publish → extract visual bindings + schemas ──────
  const handleInitDone = async (payload) => {
    trace(TAG, 'STEP 0 → 1: handleInitDone() starting', { fileName: payload?.pbixFile?.name });
    setError(null);
    setLoading(true);
    try {
      const result = await initSession(payload);
      // result = { sessionId, reportId, datasetId, schemas, visualBindings, status }
      trace(TAG, 'STEP 0 → 1: initSession() succeeded, advancing to Select Schemas', result);
      setSessionId(result.sessionId);
      setSchemas(result.schemas || []);
      setVisualBindings(result.visualBindings || []);
      setStep(1);
    } catch (err) {
      traceError(TAG, 'STEP 0 → 1: handleInitDone() failed, staying on Upload step', err);
      setError(err.message);
    } finally {
      setLoading(false);
    }
  };

  // ── Step 2: Schema selection → clean dataset → load all SF views → embed ───
  const handleSchemasDone = async (selectedSchemas) => {
    trace(TAG, 'STEP 1 → 2: handleSchemasDone() starting', { sessionId, selectedSchemas });
    setError(null);
    setLoading(true);
    try {
      const result = await selectSchemas({ sessionId, selectedSchemas });
      // result = MigrationStatusResponse (status=completed, embedUrl, embedToken, ...)
      trace(TAG, 'STEP 1 → 2: selectSchemas() succeeded, advancing to Repair & Validate', {
        ...result, embedToken: result.embedToken ? `[REDACTED, ${result.embedToken.length} chars]` : null,
      });
      setEmbedData(result);
      setStep(2);
    } catch (err) {
      traceError(TAG, 'STEP 1 → 2: handleSchemasDone() failed, staying on Select Schemas step', err);
      setError(err.message);
    } finally {
      setLoading(false);
    }
  };

  if (!auth) {
    return (
      <div className="app">
        <header className="app-header">
          <div className="header-inner">
            <div className="logo">
              <span className="logo-icon">⬡</span>
              <span className="logo-text">PBIX<b>Migrator</b></span>
            </div>
            <p className="tagline">Power BI → Snowflake DirectQuery Migration Engine</p>
          </div>
        </header>
        <main className="main-content">
          <SignInGate onSignedIn={setAuth} />
        </main>
      </div>
    );
  }

  return (
    <div className="app">
      <header className="app-header">
        <div className="header-inner">
          <div className="logo">
            <span className="logo-icon">⬡</span>
            <span className="logo-text">PBIX<b>Migrator</b></span>
          </div>
          <p className="tagline">Power BI → Snowflake DirectQuery Migration Engine</p>
        </div>
        <div className="signed-in-as">Signed in as {auth.account?.username}</div>
      </header>

      <nav className="stepper">
        {STEPS.map((label, i) => (
          <div key={label} className={`stepper-item ${i === step ? 'active' : i < step ? 'done' : ''}`}>
            <div className="stepper-dot">{i < step ? '✓' : i + 1}</div>
            <span className="stepper-label">{label}</span>
            {i < STEPS.length - 1 && <div className="stepper-line" />}
          </div>
        ))}
      </nav>

      {error && (
        <div className="error-banner">
          <span>⚠ {error}</span>
          <button onClick={() => setError(null)}>✕</button>
        </div>
      )}

      <main className="main-content">
        {step === 0 && (
          <UploadStep
            auth={auth}
            onDone={handleInitDone}
            loading={loading}
            setLoading={setLoading}
            setError={setError}
          />
        )}
        {step === 1 && (
          <SchemaStep
            schemas={schemas}
            visualBindings={visualBindings}
            onDone={handleSchemasDone}
            loading={loading}
            setLoading={setLoading}
            setError={setError}
          />
        )}
        {step === 2 && embedData && (
          <EmbedStep
            sessionId={sessionId}
            embedUrl={embedData.embedUrl}
            embedToken={embedData.embedToken}
            reportId={embedData.reportId}
            datasetId={embedData.datasetId}
            unmappedTables={embedData.unmappedTables}
            tablesLoaded={embedData.tablesLoaded}
            tablesTotal={embedData.tablesTotal}
            visualMetadata={embedData.visualMetadata}
          />
        )}
      </main>

      <footer className="app-footer">
        User (delegated) auth · Power BI Publish · XMLA Binding Extraction · Snowflake → Lakehouse → DirectQuery
      </footer>
    </div>
  );
}