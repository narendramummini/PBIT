import React, { useState } from 'react';
import { trace, traceOk, traceError } from '../utils/logger';

const TAG = 'SCHEMA';

export default function SchemaStep({ schemas, visualBindings, onDone, loading, setLoading, setError }) {
  const [selected, setSelected] = useState([]);

  const toggle = (s) =>
    setSelected(prev => prev.includes(s) ? prev.filter(x => x !== s) : [...prev, s]);

  const handleSubmit = async (e) => {
    e.preventDefault();
    if (selected.length === 0) { setError('Select at least one schema'); return; }
    trace(TAG, 'handleSubmit() → calling onDone() with selected schemas', selected);
    setError(null);
    setLoading(true);
    try {
      await onDone(selected);
      traceOk(TAG, 'handleSubmit() → onDone() completed');
    } catch (e) {
      traceError(TAG, 'handleSubmit() → onDone() threw', e);
      throw e;
    } finally {
      setLoading(false);
    }
  };

  // Unique tables referenced by visuals
  const boundTables = [...new Set((visualBindings || []).map(b => b.originalTable))].sort();

  return (
    <form className="card" onSubmit={handleSubmit}>
      <h2 className="card-title">
        <span className="step-badge">2</span> Select Snowflake Schemas
      </h2>
      <p className="hint">
        Choose one or more schemas. <strong>All views</strong> from the selected schemas will be
        loaded into the dataset as DirectQuery tables. Your existing visuals will automatically
        re-bind wherever the view names match the original table names.
      </p>

      {/* Visual bindings summary */}
      {boundTables.length > 0 && (
        <div style={{
          background: '#f0f4ff', border: '1px solid #c5d3f5',
          borderRadius: 8, padding: '10px 14px', marginBottom: 18
        }}>
          <div style={{ fontWeight: 600, fontSize: 13, marginBottom: 6, color: '#2c4a9e' }}>
            📊 Visual bindings extracted from semantic model ({boundTables.length} tables)
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
            {boundTables.map(t => (
              <span key={t} style={{
                background: '#dce8ff', borderRadius: 4, padding: '2px 8px',
                fontSize: 12, color: '#2c4a9e', fontFamily: 'monospace'
              }}>{t}</span>
            ))}
          </div>
          <div style={{ fontSize: 11, color: '#5a6a99', marginTop: 6 }}>
            Snowflake views with names matching these tables will automatically serve the visuals.
          </div>
        </div>
      )}

      <div className="schema-grid">
        {schemas.map(s => (
          <label key={s} className={`schema-chip ${selected.includes(s) ? 'selected' : ''}`}>
            <input
              type="checkbox"
              style={{ display: 'none' }}
              checked={selected.includes(s)}
              onChange={() => toggle(s)}
            />
            <span className="schema-icon">❄</span>
            {s}
            {selected.includes(s) && <span className="check">✓</span>}
          </label>
        ))}
      </div>

      <div className="schema-summary">
        {selected.length === 0
          ? 'No schemas selected'
          : `${selected.length} schema${selected.length > 1 ? 's' : ''} selected: ${selected.join(', ')}`}
      </div>

      <div style={{ background: '#fffbea', border: '1px solid #f5d87a', borderRadius: 8, padding: '10px 14px', marginBottom: 16, fontSize: 13 }}>
        ⚡ <strong>What happens next:</strong> The existing dataset semantic model will be cleaned,
        and <em>all</em> views from your selected schemas will be loaded as DirectQuery tables.
        No manual mapping required.
      </div>

      <div className="btn-row">
        <button className="btn-secondary" type="button" onClick={() => setSelected(schemas)}>
          Select All
        </button>
        <button className="btn-secondary" type="button" onClick={() => setSelected([])}>
          Clear
        </button>
        <button className="btn-primary" type="submit" disabled={loading || selected.length === 0}>
          {loading
            ? <><span className="spinner" /> Loading views &amp; rebuilding dataset…</>
            : 'Load All Views & Apply →'}
        </button>
      </div>
    </form>
  );
}
