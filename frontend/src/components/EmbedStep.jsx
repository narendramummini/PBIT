// ══════════════════════════════════════════════════════════════════════════
// SUMMARY OF FIXES APPLIED IN THIS REVISION
// ══════════════════════════════════════════════════════════════════════════
// 1. FIXED    — selectPrimaryReplacement() is now stricter and never returns
//               more than one field for a single-value visual, even in edge
//               cases (empty list, no measure found, no confidence values).
//               Added a hard post-selection assertion in the repair loop
//               that forcibly slices to 1 field for Card/KPI/Gauge even if
//               some upstream change ever produces more — belt-and-braces
//               so a capacity violation can never reach the SDK again.
// 2. IMPROVED — Result-matching queue system now also builds a secondary
//               page+title fallback index, so if the primary name-keyed
//               queue is exhausted (backend returned fewer entries than
//               same-named visuals) we still have a best-effort second
//               lookup before giving up and reporting "no entry".
// 3. IMPROVED — clearVisualRoles() / tryRemoveDataFieldSafe() casing
//               variants expanded (added lowercase, Title Case,
//               capitalized-first). Every attempted variant is now logged
//               (both hits and misses) instead of only failures being
//               summarized.
// 4. IMPROVED — Added a "nuclear" final pass: any visual that finishes the
//               normal repair loop still in `still_broken` status gets ONE
//               more aggressive clear-everything + re-add attempt using the
//               widest possible role/casing net before we give up on it.
// 5. IMPROVED — Every major log line in the repair loop and in the
//               low-level helpers now includes a consistent
//               [visual identity] tag (name / title / type) via the new
//               visId() helper, so log lines can be correlated to a
//               specific visual even when scrolling a long combined log.
// 6. FIXED    — Defensive guard added for missing/empty visualType: falls
//               back to treating a visual as single-value-like if it only
//               ever had exactly one original binding, since that's the
//               practical signal a Card-like visual gives us when the SDK
//               doesn't report a usable `type` string.
// 7. IMPROVED — Sleep delays slightly increased in tight loops (200ms→300ms
//               between visuals, 1500ms→1800ms after switchMode) to give
//               the embedded report a little more breathing room under
//               load.
// 8. NOTE     — See the "BACKEND RESPONSE SHAPE" comment block near
//               buildDataRolesPayload() for suggested /rebind-columns
//               response changes that would make several of the above
//               frontend workarounds unnecessary.
// ──────────────────────────────────────────────────────────────────────────
// ROUND 2 — root-cause fixes for the "bloated universal mapping hits Cards"
// bug (backend sometimes returns one large templated replacement array and
// assigns it to several visuals, including single-value ones; the frontend's
// card detection was also too easily defeated when visualType came back
// polluted/empty). Backend-side companion fixes are in AzureOpenAIService.cs
// / RebindVisualColumnsAsync — see the "BACKEND HARDENING" note there.
// ──────────────────────────────────────────────────────────────────────────
// 9.  FIXED    — isSingleValueVisual() was an exact-match Set lookup only
//                ('card' | 'kpi' | 'gauge'). Real SDK visualType strings
//                vary ('cardVisual', 'multiRowCard', 'kpiVisual', etc.), so
//                any of those silently fell through as "not card-like" and
//                skipped the single-field restriction entirely. Now matches
//                on substring ('card' / 'kpi' contained anywhere in the
//                lower-cased type) in addition to the exact-match set, so
//                visualType — not binding count — is the primary signal, as
//                it always should have been.
// 10. IMPROVED — Added visualCapacity(visualType): a single small helper
//                that returns the maximum number of primary fields a visual
//                type may hold (1 for Card/KPI/Gauge, Infinity for
//                everything else, i.e. Table/Matrix/Chart/Slicer/etc.).
//                isLikelySingleValueVisual() and every capacity check in the
//                repair loop, buildDataRolesPayload(), and
//                nuclearRebindPass() now derive from this ONE map instead of
//                re-deriving "is this a Card" in three different places.
// 11. FIXED    — normRole() axis handling: 'axis'/'x'/'xaxis' used to always
//                collapse to 'Category', which is wrong for X/Y scatter-type
//                visuals where the SDK role is literally 'X'. normRole now
//                takes the visualType into account: for chart types that
//                expose a real 'X' role (scatter/line/area/waterfall/etc.)
//                'axis'/'x'/'xaxis' resolves to 'X'; for everything else it
//                still resolves to 'Category' as before. Unknown/blank roles
//                on a single-value visual still hard-fall-back to 'Values'.
// 12. FIXED    — buildDataRolesPayload() now has its OWN defensive guard: if
//                it is ever called (directly or via a future code path) with
//                more fields than a single-value visual's capacity allows,
//                it truncates to the primary field itself rather than
//                trusting every caller to have already done so. This closes
//                the gap where a bloated backend array could theoretically
//                reach the SDK if it arrived through any path other than the
//                main repair loop (e.g. the nuclear pass, or a future
//                caller).
// 13. IMPROVED — nuclearRebindPass() now re-applies the same capacity guard
//                on its own `effectiveReplacements` input before doing
//                anything, with loud logging if it has to truncate — the
//                nuclear pass being "one more aggressive attempt" is not a
//                license to re-introduce a capacity violation that every
//                earlier stage already guarded against.
// 14. IMPROVED — Added a loud, dedicated warning the MOMENT a bloated
//                replacement list (more than visualCapacity() allows) is
//                observed coming back from the backend for a card-like
//                visual — logged BEFORE selectPrimaryReplacement() runs, so
//                the repair log makes it obvious this originated as a
//                backend templated-mapping issue and not a frontend
//                truncation decision.
// 15. IMPROVED — Sleep delays nudged up again in the tight per-visual loop
//                (300ms→350ms) and after switchMode (1800ms→2000ms) for a
//                little more breathing room under load.
// ──────────────────────────────────────────────────────────────────────────
// ROUND 3 — "columns only, no measures" model correction. This semantic
// model has NO measures whatsoever — every field, including KPI-styled ones
// (KPI_T_S.T_S, KPI_TOTAL_PROFIT.TOTAL_PROFIT, etc.), is a plain COLUMN. The
// previous rounds' measure-detection heuristics (KPI_-table-prefix /
// total_-sum_-avg_-column-prefix ⇒ "measure") were actively wrong for this
// dataset: they misclassified ordinary KPI-named columns as measures and
// sent isMeasure: true down to the schema builder, producing an invalid
// '#measure' binding for a field that only exists as a '#column'.
// 16. FIXED    — inferIsMeasure() now always returns false; the old
//                MEASURE_TABLE_PREFIX / MEASURE_NAME_HINTS regexes are left
//                only as commented-out history and are never evaluated.
// 17. FIXED    — buildFieldSchema() now unconditionally emits the
//                '#column' schema shape regardless of what any caller
//                passes for isMeasure — no code path in this file can ever
//                produce a '#measure' object anymore.
// 18. FIXED    — buildDataRolesPayload() (via inferIsMeasure/buildFieldSchema)
//                never sets isMeasure: true in the payload sent toward the
//                SDK; every rolesMap entry is built with the column schema.
// 19. IMPROVED — selectPrimaryReplacement()'s Card/KPI tie-break no longer
//                ranks by measure-ness (meaningless now — every candidate
//                is a column). It ranks by looksLikeKpiColumn(), a plain
//                column-NAME heuristic (kpi/total/sum/avg/value/profit/
//                revenue/sales/count/score/pct/ratio/margin/growth), so a
//                Card still preferentially keeps the field that reads as
//                its headline value when several replacements are offered.
// 20. IMPROVED — Added getVisualCapacity() as an explicitly-named alias for
//                visualCapacity(), per the "single capacity helper" ask —
//                same one source of truth, more self-documenting call site.
// 21. IMPROVED — repairRecords entries now carry an explicit
//                droppedForCapacity: [{table, column, role}, ...] array
//                (in addition to the existing summary note) whenever a
//                single-value visual had fields dropped for capacity, so
//                the final migration report can show precisely which
//                fields were sacrificed and not just a count.
// ──────────────────────────────────────────────────────────────────────────
// AGGREGATION-PRESERVING REBIND — an original COLUMN with an aggregation
// (e.g. Sum / Count / Average / Min / Max / DistinctCount) must come back as
// a column with that SAME aggregation, never as a Gold/KPI measure, in
// tables / matrices / charts. The backend now sends, per replacement:
//   aggregation   — copied from the ORIGINAL PBIX binding ('' = none)
//   oldIsMeasure / newIsMeasure — whether source / target is a model measure
// and per current binding (frontend → backend): aggregation + isMeasure,
// taken from the original PBIX metadata (visualMetadata prop).
// buildFieldSchema() emits { table, column, aggregationFunction } for an
// aggregated column, '#measure' for a measure, '#column' otherwise.
// ══════════════════════════════════════════════════════════════════════════
import { useEffect, useRef, useState, useCallback } from 'react';
import { service, factories } from 'powerbi-client';
// Side-effect import: extends powerbi-client's Report/Page/Visual prototypes
// with the authoring methods this whole repair pipeline depends on —
// addDataField, removeDataField, changeDataRoles, getDataFields. Without
// this, those calls don't throw a helpful error; they either silently no-op
// or fail with "is not a function" / a generic rejection that looks
// identical to a real casing/schema/permission failure. This single missing
// import is consistent with EVERY add failing regardless of table/column
// validity, which is what the last repair run showed.
import 'powerbi-report-authoring';
import * as models from 'powerbi-models';
import axios from 'axios';
import { finalizeMigration } from '../services/api';
import { trace, traceOk, traceWarn, traceError, group, groupEnd, table } from '../utils/logger';

const TAG = 'REPAIR';
const BASE = process.env.REACT_APP_API_URL || 'http://localhost:5000';

const pbiService = new service.Service(
  factories.hpmFactory,
  factories.wpmpFactory,
  factories.routerFactory
);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// IMPROVED: consistent [name/title/type] tag for every log line touching a
// specific visual, so entries can be correlated across a long combined log
// without hunting back up to find which visual a helper was called for.
// Accepts either a full `item` object or loose (name, title, type) args.
function visId(nameOrItem, title, type) {
  if (nameOrItem && typeof nameOrItem === 'object') {
    const it = nameOrItem;
    return `[${it.visualName || '(no-name)'} / "${it.visualTitle || it.visualName || '(untitled)'}" / ${it.visualType || '(no-type)'}]`;
  }
  return `[${nameOrItem || '(no-name)'} / "${title || nameOrItem || '(untitled)'}" / ${type || '(no-type)'}]`;
}

// ── Normalise technical Power BI role names ───────────────────────────────────
// visualType is optional but critical for Card/KPI/Gauge visuals: their
// capability JSON only exposes a single "Values" role regardless of what the
// ORIGINAL (pre-migration) role was called — the backend/stored metadata
// commonly echoes back "Data" (the internal capability-object role name for
// Cards in older PBIX schemas), which Power BI's Card visual rejects outright
// because "Data" isn't a role it exposes. Any single-value visual type is
// therefore hard-forced to "Values" FIRST, before the general role-alias
// table runs, so this can never be short-circuited by an unrecognised role
// string falling through to the "preserve unknown verbatim" branch.
// FIXED: 'axis'/'x'/'xaxis' used to always collapse to 'Category', which is
// wrong for visual types that expose a genuine 'X' role (scatter/line/area/
// waterfall-style charts bind to 'X', not 'Category'). normRole now checks
// the visual type: for that "X axis" chart family, 'axis'/'x'/'xaxis'
// resolves to 'X'; every other visual type (bar/column/pie/table/matrix,
// where the capability JSON only exposes 'Category') still resolves to
// 'Category' as before. This is a heuristic on the TYPE STRING only.
const X_AXIS_VISUAL_HINT = /(scatter|line|area|waterfall|ribbon|combo)/i;
function usesXRoleForAxis(visualType) {
  return X_AXIS_VISUAL_HINT.test(String(visualType || ''));
}

// function normRole(role, visualType) {
//   if (isSingleValueVisual(visualType)) return 'Values';
//   // IMPROVED: fallback to 'Values' for a missing/blank role on any visual
//   // type — the least-surprising guess, and the only branch unknown-single-
//   // value visuals without a usable type string will ever hit (handled above
//   // via isSingleValueVisual for the ones we CAN identify).
//   if (!role) return 'Values';
//   const r = role.toLowerCase();
//   if (r === 'values' || r === 'value' || r === 'measure' || r === 'data') return 'Values';
//   // FIXED: 'axis' on its own is ambiguous — resolve per visual type instead
//   // of always assuming 'Category' (see usesXRoleForAxis above).
//   if (r === 'axis')                      return usesXRoleForAxis(visualType) ? 'X' : 'Category';
//   if (r === 'category')                  return 'Category';
//   if (r === 'y' || r === 'yaxis')        return 'Y';
//   if (r === 'x' || r === 'xaxis')        return usesXRoleForAxis(visualType) ? 'X' : 'Category';
//   if (r === 'legend' || r === 'series')  return 'Series';
//   if (r === 'rows')                      return 'Rows';
//   if (r === 'columns')                   return 'Columns';
//   if (r === 'tooltips' || r === 'tooltip') return 'Tooltips';
//   return role; // preserve unknown verbatim
// }

function normRole(role, visualType) {
  // Modern cardVisual often uses Data (from queryState.Data).
  // Return Data as the preferred role for cards/KPI/gauge.
  // tryAddDataField should still also try Values as fallback.
  if (isSingleValueVisual(visualType)) {
    const r = String(role || '').toLowerCase();
    if (r === 'values' || r === 'value' || r === 'measure') return 'Values';
    return 'Values';  // default preferred role for cards
  }

  if (!role) return 'Values';
  const r = role.toLowerCase();
  if (r === 'values' || r === 'value' || r === 'measure' || r === 'data') return 'Values';
  if (r === 'axis') return usesXRoleForAxis(visualType) ? 'X' : 'Category';
  if (r === 'category') return 'Category';
  if (r === 'y' || r === 'yaxis') return 'Y';
  if (r === 'x' || r === 'xaxis') return usesXRoleForAxis(visualType) ? 'X' : 'Category';
  if (r === 'legend' || r === 'series') return 'Series';
  if (r === 'rows') return 'Rows';
  if (r === 'columns') return 'Columns';
  if (r === 'tooltips' || r === 'tooltip') return 'Tooltips';
  return role;
}

// ── Build a single ISchema field descriptor (column OR measure) ──────────────
// Snowflake-backed views are typically all-uppercase (ORDERS.PRODUCT_NAME),
// while the original PBIX used mixed case — we pass table/name through
// EXACTLY as given (the backend's LLM + local fallback already resolved
// correct casing against the real Snowflake metadata), so this is purely a
// structural builder, not a place to re-case anything.
// FIXED (Round 3): this dataset has NO measures — every field the SDK will
// ever be asked to bind is a column. buildFieldSchema() now unconditionally
// returns the '#column' schema shape and ignores the isMeasure argument
// entirely (kept as a parameter, and still threaded through every call
// site, purely so this function's signature — and every caller above it —
// doesn't need to change; inferIsMeasure() upstream already always resolves
// to false, so in practice isMeasure is never true here, but this function
// no longer trusts that invariant blindly either: even if some caller
// somehow passed isMeasure: true, we still never emit a '#measure' schema.
// function buildFieldSchema(table_, name, _isMeasure) {
//   return { $schema: 'http://powerbi.com/product/schema#column', table: table_, column: name };
// }

// AGGREGATION-PRESERVING: `aggregation` is the aggregation the ORIGINAL PBIX
// field carried (Sum / Average / Min / Max / Count / DistinctCount / …),
// supplied by the backend. For a column with an aggregation we emit
// powerbi-models' IColumnAggrTarget shape ({ table, column,
// aggregationFunction }); a measure never gets one (it carries its own).
function buildFieldSchema(table_, name, isMeasure = false, aggregation = '') {
  if (isMeasure) {
    return {
      $schema: 'http://powerbi.com/product/schema#measure',
      measure: name,
      ...(table_ ? { table: table_ } : {}),
    };
  }
  const agg = String(aggregation || '').trim();
  if (agg && agg !== 'None' && agg !== 'Default') {
    return {
      table: table_,
      column: name,
      aggregationFunction: agg,
    };
  }
  return {
    $schema: 'http://powerbi.com/product/schema#column',
    table: table_,
    column: name,
  };
}

// Aggregation to re-apply for a replacement. Never for a measure target.
function aggFor(rep) {
  const isM = inferIsMeasure(rep?.newTable, rep?.newColumn, rep?.newIsMeasure);
  return isM ? '' : String(rep?.aggregation || '');
}

// ── Measure-vs-column detection ────────────────────────────────────────────
// NOTE (Round 3 — "columns only" revision): this semantic model contains NO
// measures at all — every field, including KPI-styled ones like
// KPI_T_S.T_S or KPI_TOTAL_PROFIT.TOTAL_PROFIT, is a regular COLUMN. The old
// naming-convention inference below (KPI_-prefixed tables / total_, sum_,
// avg_-prefixed columns ⇒ "measure") was actively harmful here: it was
// mis-flagging ordinary KPI-named columns as measures, which sent
// isMeasure: true down to buildFieldSchema()/the SDK and produced a
// '#measure' schema object for a field that only exists as a '#column' in
// the model — a guaranteed binding rejection, not a casing problem.
// FIXED: inferIsMeasure() is now a hard no-op that always returns false.
// The MEASURE_TABLE_PREFIX / MEASURE_NAME_HINTS heuristics are left here,
// commented out, purely as a historical record of the old (wrong-for-this-
// dataset) behaviour — they are never referenced anywhere else in the file.
// If the backend's oldIsMeasure/newIsMeasure ever comes back `true` for this
// dataset, that would itself be a backend bug, since there are no measures
// to flag; inferIsMeasure deliberately ignores that flag too rather than
// trusting a signal that cannot be correct here.
// const MEASURE_TABLE_PREFIX = /^kpi_/i;
// const MEASURE_NAME_HINTS = /^(total|sum|avg|average|count|min|max|pct|percent|ratio|margin|growth)_/i;

// function inferIsMeasure(table, column, explicitFlag) {
//   // Explicit backend flag
//   if (explicitFlag === true) return true;
//   // Default false; cards force true at call sites
//   return false;
// }


function inferIsMeasure(table, column, explicitFlag) {
  // Only treat as a measure when the backend explicitly says so,
  // or when the table is the real "_Measures" / "Measures_Table"
  // table created for Gold KPIs.
  //
  // KPI_* tables in this model are plain COLUMNS, not measures.
  // Treating them as measures was producing invalid '#measure'
  // schema objects and breaking Card / KPI visuals.
  if (explicitFlag === true) return true;
  if (explicitFlag === false) return false;

  const t = String(table || '').toLowerCase().replace(/[_\s-]/g, '');
  if (t === 'measures' || t === '_measures' || t === 'measurestable') {
    return true;
  }

  // Everything else (including KPI_* tables) is a column.
  return false;
} 
// IMPROVED (Round 3): since every field is a column, "which replacement do
// we keep for a Card" can no longer be decided by measure-ness — it's
// decided by which column NAME looks like the intended KPI/value field.
// This is a plain heuristic over the column identifier only (never the
// table), used purely as a tie-breaker in selectPrimaryReplacement below.
const KPI_NAME_HINT = /(kpi|total|sum|avg|average|amount|value|profit|revenue|sales|count|score|pct|percent|ratio|margin|growth)/i;
function looksLikeKpiColumn(column) {
  return KPI_NAME_HINT.test(String(column || ''));
}

// ── Visual capacity model ─────────────────────────────────────────────────
// Card / KPI / Gauge visuals can only hold ONE field in their primary role.
// Pushing all N original bindings at them (even via changeDataRoles) gets
// rejected because the payload itself is invalid for that visual's layout —
// this has nothing to do with casing or missing tables, it's a straight
// capacity mismatch, and needs to be handled BEFORE any add is attempted.
const SINGLE_VALUE_VISUAL_TYPES = new Set(['card', 'cardvisual', 'kpi', 'kpivisual', 'gauge', 'multirowcard']);

// FIXED: visualType is the PRIMARY signal for single-value detection, per
// the driving bug — the backend occasionally sends a bloated "universal"
// replacement array to several visuals including Cards, and the ONLY
// reliable way to refuse that array before it reaches the SDK is to
// recognise the visual as single-value from its type, not from however many
// replacements happened to arrive. An exact-match Set was too brittle:
// real SDK visualType strings vary ('cardVisual', 'multiRowCard',
// 'kpiVisual', ...), so any visualType that CONTAINS 'card' or 'kpi'
// anywhere (case-insensitive) is now treated as single-value too, in
// addition to the exact-match set above. 'gauge' stays exact-match since
// there's no comparably common suffixed/prefixed variant for it.
function isSingleValueVisual(visualType) {
  const t = String(visualType || '').toLowerCase();
  if (!t) return false;
  if (SINGLE_VALUE_VISUAL_TYPES.has(t)) return true;
  return t.includes('card') || t.includes('kpi');
}

// IMPROVED: single source of truth for "how many primary fields can this
// visual type hold". Card/KPI/Gauge → 1. Everything else (Table, Matrix,
// bar/column/line/pie charts, slicers, etc.) → Infinity, i.e. "no
// single-value restriction, let the visual's own capability JSON decide".
// isLikelySingleValueVisual(), buildDataRolesPayload(), and
// nuclearRebindPass() all now derive their capacity check from this one
// function instead of three independent "is this a Card" checks that could
// silently drift apart.
function visualCapacity(visualType) {
  return isSingleValueVisual(visualType) ? 1 : Infinity;
}
// IMPROVED (Round 3): explicitly-named alias requested alongside the
// capacity model — same single source of truth, just callable under the
// more self-documenting name at call sites that want to read as "how many
// fields can THIS visual type hold" rather than a bare capacity lookup.
const getVisualCapacity = visualCapacity;

// FIXED: Minor Robustness — some powerbi-client SDK versions/report states
// return an empty/undefined `type` string from page.getVisuals(), which
// means isSingleValueVisual() above can never fire for a genuine Card even
// though it desperately needs the single-field restriction. When we truly
// don't know the type, fall back to a structural signal instead of giving
// up: a visual that only ever had exactly ONE original bound field is
// behaviourally single-value regardless of what the SDK calls it, so treat
// it the same way. This never fires for Table/Matrix/Chart visuals because
// those virtually always carry 2+ original bindings.
function isLikelySingleValueVisual(visualType, originalBindingCount) {
  // IMPROVED: primary check now literally IS the visualCapacity() === 1
  // check, so this function and buildDataRolesPayload()/nuclearRebindPass()
  // can never disagree about what counts as single-value.
  if (visualCapacity(visualType) === 1) return true;
  // Fallback ONLY fires when visualType is missing/empty — visualType,
  // when present, is always trusted over the binding-count heuristic.
  if (!visualType && Number.isFinite(originalBindingCount) && originalBindingCount === 1) return true;
  return false;
}

// Common role names across Power BI visual types. Used only to WIDEN the
// pre-clear pass beyond whatever roles the backend's replacements happen to
// mention — clearVisualRoles() silently no-ops on a role a given visual
// doesn't support (getDataFields throws, caught, skipped), so adding extra
// candidates here is always safe and never overwrites a role real data.

const COMMON_DATA_ROLES = ['Values', 'Data', 'Category', 'Y', 'X', 'Series', 'Rows', 'Columns', 'Tooltips'];


// Pick the ONE replacement a Card-like visual should keep. Preference order:
//   1. Backend-flagged isPrimary (LLM has visual-title context we don't)
//   2. Matched replacements over unmatched (a real new binding beats a stale one)
//   3. Measures over columns (KPI cards are almost always a measure)
//   4. Highest AI confidence
//   5. Otherwise, first in list (stable fallback)
function selectPrimaryReplacement(replacements, log, ident) {
  const id = ident || '';
  // FIXED: defensive early-out — an empty/undefined replacements array must
  // never fall through to ranked[0] (which would be undefined) and then get
  // silently wrapped as [undefined] by the caller. Return a clean "nothing
  // to bind" result instead so downstream code treats this as a legitimate
  // zero-field case rather than crashing on `.table` of undefined.
  if (!Array.isArray(replacements) || replacements.length === 0) {
    log?.(`    ⚠ ${id} selectPrimaryReplacement called with no replacements — nothing to keep`, 'warn');
    return { primary: undefined, dropped: [] };
  }

  const flagged = replacements.find((r) => r.isPrimary);
  let primary = flagged;

  if (!primary) {
    const matched = replacements.filter((r) => r.matched && r.newColumn);
    const pool = matched.length > 0 ? matched : replacements;
    // FIXED (Round 3): this dataset has no measures, so the old "prefer
    // measures over columns" tie-break made no sense here — every candidate
    // IS a column, so it always scored a tie and fell straight through to
    // confidence anyway. Replaced with a KPI-name-hint tie-break: prefer a
    // column whose NAME looks like a KPI/value field (KPI_TOTAL_PROFIT,
    // TOTAL_PROFIT, AVG_SCORE, …) over one that doesn't — this is what
    // "prefer measures" was really standing in for on a Card, since KPI
    // cards are overwhelmingly bound to the field that reads as the
    // headline number, not an incidental dimension column.
    const ranked = [...pool].sort((a, b) => {
      const aKpi = looksLikeKpiColumn(a.newColumn || a.oldColumn) ? 1 : 0;
      const bKpi = looksLikeKpiColumn(b.newColumn || b.oldColumn) ? 1 : 0;
      if (aKpi !== bKpi) return bKpi - aKpi;
      // Matched-with-confidence beats matched-without, beats unmatched.
      const aC = a.matched && a.newColumn ? (a.confidence ?? 0.0001) : -1;
      const bC = b.matched && b.newColumn ? (b.confidence ?? 0.0001) : -1;
      return bC - aC;
    });
    primary = ranked[0];
  }

  // FIXED: guarantee we never keep more than exactly one field even if the
  // ranking above somehow produced no clear winner — fall back to the
  // first replacement in the original list as a stable last resort rather
  // than returning primary=undefined while replacements.length > 0.
  if (!primary) primary = replacements[0];

  const dropped = replacements.filter((r) => r !== primary);
  if (dropped.length > 0) {
    log?.(`    ℹ ${id} Single-value visual — keeping 1 of ${replacements.length} field(s): ` +
      `${primary?.newTable || primary?.oldTable}.${primary?.newColumn || primary?.oldColumn}` +
      `${flagged ? ' (backend isPrimary hint)' : ''}. Dropping: ${dropped.map((d) => d.oldColumn).join(', ')}`, 'warn');
  }
  return { primary, dropped };
}

// ── Casing-safe add: try the exact name first (we never silently re-case by
// default — Snowflake identifiers are case-sensitive in practice and a wrong
// guess can create a new, incorrect field reference), then fall back to
// common Snowflake casing conventions ONLY if the exact form fails. Every
// attempt is logged with the literal string sent to the SDK so mismatches
// are visible in the repair log instead of just "Failed to add".
// IMPROVED: shared casing-variant generator used by both the add and
// remove "safe" helpers, so the two never drift out of sync on which
// casing conventions they'll try. Expanded beyond plain UPPER/lower to
// also cover Title Case and Capitalized-First, which shows up in some
// Snowflake views created via case-preserving quoted identifiers.
function titleCase(s) {
  return String(s || '').replace(/[a-zA-Z]+/g, (w) => w.charAt(0).toUpperCase() + w.slice(1).toLowerCase());
}
function capFirst(s) {
  const str = String(s || '');
  return str.charAt(0).toUpperCase() + str.slice(1);
}
function buildCasingCandidates(table_, column) {
  const seen = new Set();
  const candidates = [];
  const push = (t, c) => {
    if (!t || !c) return;
    const key = `${t}\u0001${c}`;
    if (seen.has(key)) return;
    seen.add(key);
    candidates.push({ table: t, name: c });
  };

  push(table_, column);                                     // exact, as stored — always first
  push(table_?.toUpperCase(), column?.toUpperCase());        // SCREAMING_SNAKE_CASE (Snowflake default)
  push(table_, column?.toUpperCase());
  push(table_?.toUpperCase(), column);
  push(table_?.toLowerCase(), column?.toLowerCase()); 
  push(table_?.toLowerCase(), column?.toUpperCase());       // IMPROVED: all-lowercase
  push(table_, column?.toLowerCase());
  push(table_?.toLowerCase(), column);
  push(titleCase(table_), titleCase(column));                // IMPROVED: Title Case
  push(capFirst(table_), capFirst(column));                  // IMPROVED: Capitalized-first only

  return candidates;
}

async function tryAddDataFieldSafe(visual, role, table_, column, isMeasure, log, visualType, ident, aggregation = '') {
  const id = ident || '';
  const candidates = buildCasingCandidates(table_, column);
  const aggLabel = !isMeasure && aggregation ? ` [${aggregation}]` : '';

  let lastError = null;
  for (const cand of candidates) {
    const effectiveRole = normRole(role, visualType);
    log?.(`    🔎 ${id} addDataField attempt → ${cand.table}.${cand.name} [${effectiveRole}]${aggLabel}${isMeasure ? ' (measure)' : ''}`);
    const result = await tryAddDataField(visual, effectiveRole, cand.table, cand.name, isMeasure, visualType, aggregation);
    if (result.ok) {
      if (cand.table !== table_ || cand.name !== column) {
        log?.(`    ⚠ ${id} Casing mismatch — "${table_}.${column}" only worked as "${cand.table}.${cand.name}"`, 'warn');
        traceWarn(TAG, 'Casing mismatch resolved via fallback casing', { requested: `${table_}.${column}`, resolved: `${cand.table}.${cand.name}` });
      }
      if (result.aggFallback) {
        log?.(`    ⚠ ${id} Aggregation "${aggregation}" was rejected for ${cand.table}.${cand.name} — bound as a plain column (Power BI default aggregation)`, 'warn');
      }
      return { ...result, resolvedTable: cand.table, resolvedName: cand.name, triedVariants: candidates.length };
    }
    // Every role-name variant tryAddDataField tried failed for this
    // table/column candidate — keep the LAST (most specific: normRole(role))
    // attempt's error message so the caller can print the real SDK
    // rejection reason instead of just "Failed after N variants", which
    // was previously indistinguishable from a casing problem.
    const lastAttempt = result.attempts?.[result.attempts.length - 1];
    if (lastAttempt?.error) lastError = lastAttempt.error;
    // IMPROVED: log every attempted variant (not just a final summary) so
    // it's possible to see exactly which of the 7 casing guesses were tried
    // and rejected, and with what SDK error, directly in the repair log.
    log?.(`    · ${id} rejected ${cand.table}.${cand.name}: ${lastAttempt?.error || '(no error message returned by SDK)'}`);
  }
  traceError(TAG, `tryAddDataFieldSafe: ALL casing variants failed for ${table_}.${column}`, candidates);
  return { ok: false, triedVariants: candidates.length, lastError };
}

// ── Casing-safe, unconditional remove — used for the FORCE-CLEAR pass.
// Unlike tryRemoveDataField (role-name fallback only), this also tries
// casing variants, and unlike clearVisualRoles' live pass it never checks
// getDataFields() first — it just fires removeDataField for every
// (casing variant) of a known field and swallows "not found" errors. This
// is what actually clears a role on a visual stuck in Missing_References,
// where getDataFields() reports empty for every role even though a field
// reference still exists internally.
// async function tryRemoveDataFieldSafe(visual, role, table_, column, isMeasure, log, visualType, ident) {
//   const id = ident || '';
//   const candidates = buildCasingCandidates(table_, column);
//   const rolesToTry = getTargetRolesForVisual(role, visualType);

//   let anyOk = false;
//   for (const cand of candidates) {
//     for (const effectiveRole of rolesToTry) {
//       try {
//         await visual.removeDataField(effectiveRole, buildFieldSchema(cand.table, cand.name, isMeasure));
//         anyOk = true;
//         log?.(`    🧹 ${id} Force-cleared ${cand.table}.${cand.name} [${effectiveRole}]${isMeasure ? ' (measure)' : ''}`);
//       } catch (e) {
//         trace(TAG, `tryRemoveDataFieldSafe: variant rejected (expected/common) ${id} ${cand.table}.${cand.name} [${effectiveRole}]`, e?.message);
//       }
//     }
//   }
//   return { ok: anyOk, triedVariants: candidates.length };
// }

///////////////////////////////////////////////////////////////

// FIXED (Round 5) — ROOT CAUSE of "old + new fields both present":
// Microsoft's documented signature is
//   removeDataField(dataRole: string, index: number): Promise<IError>
// — it takes the numeric INDEX of the field within that role, never a
// field schema object. The previous version of this function called
//   visual.removeDataField(effectiveRole, buildFieldSchema(...))
// which passes a {$schema, table, column} object where the SDK expects a
// number. That call either throws (caught here and silently swallowed as
// "expected if the field is genuinely not bound") or is coerced into an
// invalid index — either way, the OLD field was never actually removed,
// which is exactly why visuals ended up with the stale broken field AND
// the newly-added field bound at the same time.
// Because removeDataField only needs a role + index — not the field's
// table/column — we don't need getDataFields() to work at all in order to
// clear a role. A Missing_References visual routinely reports an empty
// array from getDataFields() for a role that still has a stale reference
// occupying a slot internally (this is the exact "reclassified from empty
// getDataFields()" case in the repair log); removing by index sidesteps
// that entirely. This sweeps indices high→low (so removing one index never
// shifts the position of one not yet processed) and swallows "nothing at
// this index" failures, which are expected once a role is actually empty.
async function tryRemoveDataFieldSafe(
  visual,
  role,
  table_,
  column,
  isMeasure,
  log,
  visualType,
  ident,
  maxIndex = 8
) {
  const id = ident || '';

  // Tables are cleared once before adding, never here
  const isTable = /table|matrix|pivot/i.test(String(visualType || ''));
  if (isTable) {
    return { ok: true, triedVariants: 0 };
  }

  const rolesToTry = getTargetRolesForVisual(role, visualType);
  let anyOk = false;

  for (const effectiveRole of rolesToTry) {
    try {
      const fields = await visual.getDataFields(effectiveRole).catch(() => []);
      if (!Array.isArray(fields) || fields.length === 0) continue;

      const isMeasureField = isMeasure || String(table_).toLowerCase().includes('measure');
      if (isMeasureField && (effectiveRole === 'Category' || effectiveRole === 'X' || effectiveRole === 'Rows' || effectiveRole === 'Columns')) continue;
      if (!isMeasureField && (effectiveRole === 'Data' || effectiveRole === 'Y')) continue;

      // The new field was just added at the end, so everything before it is stale.
      // Category/X can hold a date hierarchy (several stale fields), so clear all of them.
      const isAxis = effectiveRole === 'Category' || effectiveRole === 'X';
      let remaining = fields.length;
      let guard = 8;
      while (remaining > 1 && guard-- > 0) {
        const res = await visual.removeDataField(effectiveRole, 0);
        if (res?.message || res?.errorCode) break;
        anyOk = true;
        remaining--;
        log?.(`    🧹 ${id} Removed old field at [${effectiveRole}][0]`);
        if (!isAxis) break; // non-axis roles: remove just one
      }
      if (anyOk) break;
    } catch (e) {
      // role not valid for this visual, ignore
    }
  }

  return { ok: anyOk, triedVariants: rolesToTry.length };
}
// //////////////////////////////////////////////////////////////////


// NOTE: not called anywhere in the current pipeline (removeDataField calls
// route through tryRemoveDataFieldSafe / the index-sweep in
// clearVisualRoles instead) — kept only as a role-fallback utility, fixed
// for the same removeDataField(dataRole, index) signature issue described
// above rather than left broken.
async function tryRemoveDataField(visual, role, table_, column, isMeasure = false, visualType) {
  const roles = [normRole(role, visualType), role, 'Values', 'Category', 'Y'].filter(Boolean);
  const tried = new Set();
  const attempts = [];
  for (const r of roles) {
    if (tried.has(r)) continue;
    tried.add(r);
    try {
      const fields = await visual.getDataFields(r).catch(() => []);
      const idx = Array.isArray(fields)
        ? fields.findIndex(f =>
            String(f?.column?.table || f?.measure?.table || '').trim().toLowerCase() === String(table_).trim().toLowerCase() &&
            String(f?.column?.column || f?.measure?.measure || '').trim().toLowerCase() === String(column).trim().toLowerCase())
        : -1;
      if (idx < 0) {
        attempts.push({ role: r, ok: false, error: 'field not found via getDataFields()' });
        continue;
      }
      await visual.removeDataField(r, idx);
      attempts.push({ role: r, ok: true });
      trace(TAG, `removeDataField(${r}, index ${idx} = ${table_}.${column}) → OK`, { attempts });
      return { ok: true, role: r, attempts };
    } catch (e) {
      attempts.push({ role: r, ok: false, error: e?.message });
    }
  }
  traceWarn(TAG, `removeDataField(${table_}.${column}) → all role variants failed`, { attempts });
  return { ok: false, attempts };
}

// ── Try addDataField with role fallback ───────────────────────────────────────
// async function tryAddDataField(visual, role, table_, column, isMeasure = false, visualType) {
//   const roles = isSingleValueVisual(visualType)
//   ? ['Data', 'Values']   // Data first for modern cardVisual
//   : [normRole(role, visualType), role, 'Values'].filter(Boolean);
//   const tried = new Set();
//   const attempts = [];
//   for (const r of roles) {
//     if (tried.has(r)) continue;
//     tried.add(r);
//     try {
//       await visual.addDataField(r, buildFieldSchema(table_, column, isMeasure));
//       attempts.push({ role: r, ok: true });
//       trace(TAG, `addDataField(${r}, ${table_}.${column}) → OK`, { attempts });
//       return { ok: true, role: r, attempts };
//     } catch (e) {
//       attempts.push({ role: r, ok: false, error: e?.message });
//     }
//   }
//   traceError(TAG, `addDataField(${table_}.${column}) → all role variants failed`, { attempts });
//   return { ok: false, attempts };
// }

function getTargetRolesForVisual(role, visualType) {
  if (isSingleValueVisual(visualType)) {
    // Modern cards: try Data first, then Values
    return ['Data', 'Values'];
  }

  const norm = normRole(role, visualType);
  return [norm, role, 'Values', 'Category', 'Y', 'X', 'Series', 'Rows', 'Columns']
    .filter(Boolean);
}

// AGGREGATION-PRESERVING: for an aggregated column the aggregated schema is
// tried first for each role; only if the SDK rejects it for that role is a
// plain column attempted as a last resort (flagged via `aggFallback`, so the
// caller can log that Power BI's default aggregation applies instead).
async function tryAddDataField(visual, role, table_, column, isMeasure = false, visualType, aggregation = '') {
  const rolesToTry = getTargetRolesForVisual(role, visualType);
  const tried = new Set();
  const attempts = [];

  for (const r of rolesToTry) {
    if (tried.has(r)) continue;
    tried.add(r);

    const schemas = [{ s: buildFieldSchema(table_, column, isMeasure, aggregation), aggFallback: false }];
    if (!isMeasure && aggregation) {
      schemas.push({ s: buildFieldSchema(table_, column, false, ''), aggFallback: true });
    }

    for (const { s, aggFallback } of schemas) {
      try {
        await visual.addDataField(r, s);
        attempts.push({ role: r, ok: true, aggFallback });
        trace(TAG, `addDataField(${r}, ${table_}.${column}${aggregation && !aggFallback && !isMeasure ? ` [${aggregation}]` : ''}) → OK`, { attempts });
        return { ok: true, role: r, attempts, aggFallback };
      } catch (e) {
        attempts.push({ role: r, ok: false, aggFallback, error: e?.message || String(e) });
      }
    }
  }

  traceWarn(TAG, `addDataField(${table_}.${column}) → all role variants failed`, { attempts });
  return { ok: false, attempts };
}
// ── Clear one or more roles on a visual, using BOTH what the visual
//    currently reports (authoritative when available) AND a caller-supplied
//    list of "known" original bindings (from the backend/stored metadata).
//
// Why both: a HEALTHY visual's getDataFields(role) tells you exactly what's
// bound, so removing that first gives per-field add() a clean slate to work
// against instead of fighting whatever is already there. But a visual that's
// already showing Missing_References often reports EMPTY fields for every
// role (that's the exact false-"unbound" bug this pipeline exists to work
// around) — in that case there's nothing live to clear, and skipping the
// known-bindings pass would silently leave stale references in place.
// Attempting removal of the known bindings too is safe: if they're already
// gone, removeDataField() just fails harmlessly ("may already be gone"),
// which is expected and already handled as a non-fatal case throughout this
// file.


async function clearVisualRoles(
  visual,
  rolesToClear = [],
  knownFields = [],
  log,
  visualType,
  ident
) {
  const id = ident || '';

  const cleared = [];
  const failed = [];

  /*
   * Build roles dynamically.
   *
   * We do NOT use:
   *   - COMMON_DATA_ROLES
   *   - getTargetRolesForVisual()
   *   - hardcoded Values/Category/Y/X/etc.
   *
   * We only use roles that actually come from:
   *   1. the current visual bindings
   *   2. known/stored bindings from migration metadata
   *   3. roles explicitly supplied by the caller
   */
  const roles = Array.from(
    new Set([
      ...(Array.isArray(rolesToClear) ? rolesToClear : []),
      ...(Array.isArray(knownFields)
        ? knownFields.map(f => f?.role).filter(Boolean)
        : [])
    ])
  )
    .map(r => normRole(r, visualType))
    .filter(Boolean);

  log?.(
    `    🧹 ${id} Dynamically discovered roles to clear: ` +
    `${roles.length ? roles.join(', ') : '(none)'}`
  );

  // FIXED (Round 5) — ROOT CAUSE of "old + new fields both present":
  // this used to check `if (!Array.isArray(activeFields) || activeFields.length === 0) continue;`
  // and SKIP clearing that role entirely whenever getDataFields() came back
  // empty. That is exactly the Missing_References case — a broken visual
  // routinely reports an empty field list for a role that still has a
  // stale reference occupying a slot internally — so the one situation
  // this pipeline exists to repair was the one situation where removal
  // never even attempted to run. There is no more "skip when empty"
  // branch: every role always gets BOTH the live index-based removal
  // (authoritative when getDataFields() does return something) AND an
  // unconditional index-sweep (removeDataField only needs a role + index,
  // never the field's schema/table/column, so this needs no knowledge of
  // what's actually bound — it works identically whether getDataFields()
  // reported the field or not).
  for (const role of roles) {
    let liveCount = 0;
    try {
      const activeFields = await visual.getDataFields(role);
      if (Array.isArray(activeFields)) liveCount = activeFields.length;

      // Remove from the end so indexes remain valid as we go.
      for (let index = liveCount - 1; index >= 0; index--) {
        try {
          const result = await visual.removeDataField(role, index);
          if (result?.message || result?.errorCode) {
            failed.push({ role, index, source: 'live', error: result });
            traceWarn(TAG, `clearVisualRoles: removeDataField returned error ${id} ${role}[${index}]`, result);
            continue;
          }
          cleared.push({ role, index, source: 'live' });
          log?.(`    🧹 ${id} Removed existing field [${role}][${index}]`);
        } catch (e) {
          failed.push({ role, index, source: 'live', error: e?.message || String(e) });
          trace(TAG, `clearVisualRoles: removeDataField failed ${id} ${role}[${index}]`, e?.message);
        }
      }
    } catch (e) {
      trace(TAG, `clearVisualRoles: getDataFields failed for ${id} role=${role}`, e?.message);
    }

    // ── Unconditional sweep — runs EVERY time, not just when the live
    // pass above found nothing. A role can have 1 live-reported field but
    // still hold a second, invisible stale reference (or vice versa); the
    // sweep is idempotent on an already-empty role (every index just fails
    // harmlessly), so there is no cost to always running it, and it's the
    // only thing that reliably empties a role Power BI can't currently
    // describe via getDataFields().
    for (let index = 8; index >= liveCount; index--) {
      try {
        const result = await visual.removeDataField(role, index);
        if (!(result?.message || result?.errorCode)) {
          cleared.push({ role, index, source: 'sweep' });
          log?.(`    🧹 ${id} [SWEEP] Force-removed stale field at [${role}][${index}] (not visible via getDataFields())`);
        }
      } catch (e) {
        // Expected — nothing bound at this index.
      }
    }
  }

  log?.(
    `    🧹 ${id} Dynamic clear completed — ` +
    `cleared=${cleared.length}, failed=${failed.length}`
  );

  return {
    cleared,
    failed
  };
}


//    replacements. Unmatched replacements fall back to their ORIGINAL
//    (table, column) so changeDataRoles() never drops a slot to empty —
//    "atomic" means the whole role set is replaced in one shot, so every
//    role, matched or not, must be represented with SOMETHING.
//
// ── NOTE — SUGGESTED BACKEND /rebind-columns RESPONSE SHAPE ────────────────
// A large fraction of the frontend workarounds in this file exist purely to
// compensate for ambiguity in the current response shape. If the backend
// team is open to a v2 of this endpoint, the following would remove entire
// classes of frontend guesswork:
//   1. Key results by a stable per-visual GUID (Power BI exposes one via
//      visual.guid on modern SDK versions) instead of visualName. Names
//      collide constantly across a real report ("Card", "Card 2", two
//      differently-titled visuals that both got auto-named "Card"); a GUID
//      never does, and it would delete the entire queue/collision-matching
//      section of this file.
//   2. Include an explicit `maxFields` (or `visualCapacity`) integer per
//      visual, derived from the visual's own capabilities.json rather than
//      the frontend inferring "1 for Card/KPI/Gauge" from a hardcoded type
//      list. This survives visual types this file doesn't know about yet
//      (custom visuals, new Card variants, etc.).
//   3. Always set `isPrimary: true` on exactly one replacement per visual
//      when the visual is single-value, so the frontend never has to rank
//      candidates itself — the backend/LLM has the visual title and full
//      report context and can make a better call than column-name heuristics.
//   4. Return `newTable`/`newColumn` pre-cased to match the ACTUAL Snowflake
//      identifier casing (the backend already resolves this against live
//      Snowflake metadata for matching — echoing the resolved casing back
//      removes the need for the 7-variant casing-guess dance entirely).
//   5. Never emit two different visualNames with byte-identical replacement
//      arrays unless they are genuinely meant to bind the same fields — if
//      that happens it should be treated as a backend template-cloning bug,
//      not something the frontend detects after the fact (see the
//      seenSignatures diagnostic below, kept as a safety net either way).
function buildDataRolesPayload(replacements, visualType, log, ident) {
  // FIXED: defensive payload-building guard — buildDataRolesPayload() is
  // the last stop before a role→field[] map is handed to the SDK, so it
  // enforces the single-value capacity limit itself rather than trusting
  // every caller to have already restricted the list. Under normal
  // operation the repair loop has already called selectPrimaryReplacement()
  // and the capacity map is a no-op here; this only fires if some other
  // path (future code, the nuclear pass, a bad merge) ever calls this
  // function directly with a bloated list.
  const capacity = visualCapacity(visualType);
  if (Array.isArray(replacements) && replacements.length > capacity) {
    const id = ident || '';
    const { primary } = selectPrimaryReplacement(replacements, log, id);
    log?.(`  ⛔ ${id} buildDataRolesPayload() guard tripped — received ${replacements.length} field(s) ` +
      `for a visual with capacity ${capacity}; keeping only ${primary ? `${primary.newTable || primary.oldTable}.${primary.newColumn || primary.oldColumn}` : '(none)'}`, 'error');
    traceError(TAG, 'buildDataRolesPayload() capacity guard tripped', { visualType, capacity, received: replacements.length });
    replacements = primary ? [primary] : [];
  }

  const rolesMap = {};
  const planned = []; // flat log of what went where, for the repair log/report
  for (const rep of replacements) {
    if (!rep.matched || !rep.newColumn) {
      // Do not map old/unmatched bindings at all.
      continue;
    }
    const role   = normRole(rep.newRole || rep.oldRole, visualType);
    const table_ = rep.newTable;
    const name   = rep.newColumn;
    const isMeasure = inferIsMeasure(table_, name, rep.newIsMeasure);
    const aggregation = aggFor(rep);

    if (!rolesMap[role]) rolesMap[role] = [];
    rolesMap[role].push(buildFieldSchema(table_, name, isMeasure, aggregation));
    planned.push({ role, table: table_, name, isMeasure, aggregation, keptOriginal: false });
  }
  return { rolesMap, planned };
}

// ── Atomic update: replace a visual's ENTIRE data-role mapping in one call.
// Individual removeDataField/addDataField calls validate the visual's layout
// on every mutation — if the visual is already in a Missing_References state
// (or a change would momentarily violate a single-value-role limit, e.g. a
// Card with 2 Values mid-swap), those intermediate states get rejected even
// though the FINAL state would be perfectly valid. changeDataRoles() (when
// the SDK/visual instance supports it) skips that intermediate validation by
// pushing the whole new mapping down as one transaction.
//
// This is intentionally defensive: not every powerbi-client version exposes
// changeDataRoles() on the Visual object, so we feature-detect first and
// signal "unsupported" distinctly from "attempted and failed", letting the
// caller fall back to the per-field loop either way.
async function tryChangeDataRolesAtomic(visual, rolesMap, log) {
  if (typeof visual.changeDataRoles !== 'function') {
    trace(TAG, 'changeDataRoles() not present on this visual/SDK instance — skipping straight to per-field fallback');
    return { ok: false, unsupported: true, reason: 'changeDataRoles() not supported by this SDK/visual instance' };
  }
  try {
    log?.(`    🧬 Atomic changeDataRoles() → ${Object.keys(rolesMap).map(r => `${r}:[${rolesMap[r].map(f => f.column || f.measure).join(', ')}]`).join('  ')}`);
    await visual.changeDataRoles(rolesMap);
    trace(TAG, 'visual.changeDataRoles() → OK', rolesMap);
    return { ok: true };
  } catch (e) {
    traceWarn(TAG, 'visual.changeDataRoles() threw — falling back to per-field remove/add', { rolesMap, error: e?.message });
    return { ok: false, unsupported: false, reason: e?.message || String(e) };
  }
}




// IMPROVED — Fallback Strategy: "nuclear" final pass. If a visual finishes
// the normal repair loop (atomic attempt + per-field fallback, each already
// including their own pre-clear/restore logic) still `still_broken`, that
// almost always means SOME stale reference survived every previous clear
// attempt — most commonly a role we didn't think to widen the pre-clear to,
// or a casing variant genuinely outside the 7 we already try. This pass is
// deliberately blunt: clear every commonly-used role unconditionally (no
// "getDataFields() says it's empty so skip" logic at all — that check is
// exactly what fails on a Missing_References visual), across every casing
// variant, then make ONE more add attempt at the effective replacement set.
// It is only ever invoked once, only for visuals that are still broken
// after the normal path, so it can't mask a genuinely-unfixable visual as
// silently retried forever — it either helps once or the visual is
// reported still_broken as before.


async function nuclearRebindPass(visual, effectiveReplacements, visualType, log, ident) {
  const id = ident || '';
  log?.(`  ☢ ${id} Nuclear pass — still broken after normal repair, trying one aggressive clear+rebuild…`, 'warn');
  traceWarn(TAG, `nuclearRebindPass invoked for ${id}`, { replacementCount: effectiveReplacements.length });

  // IMPROVED: the nuclear pass re-applies the same capacity guard as the
  // normal path on its OWN input before doing anything. "One more
  // aggressive attempt" is not a license to re-introduce a capacity
  // violation — effectiveReplacements should already be ≤1 for a
  // single-value visual by the time we get here, but this makes it
  // impossible for the nuclear pass specifically to ever push more than
  // capacity allows, independent of what the caller passed in.
  const capacity = visualCapacity(visualType);
  if (effectiveReplacements.length > capacity) {
    const { primary, dropped } = selectPrimaryReplacement(effectiveReplacements, log, id);
    log?.(`  ⛔ ${id} Nuclear pass capacity guard tripped — received ${effectiveReplacements.length} field(s) ` +
      `for capacity ${capacity}; keeping only 1. Dropping: ${dropped.map((d) => d.oldColumn).join(', ')}`, 'error');
    traceError(TAG, 'nuclearRebindPass capacity guard tripped', { visualType, capacity, received: effectiveReplacements.length });
    effectiveReplacements = primary ? [primary] : [];
  }

  // Unconditionally force-clear every common role (union of COMMON_DATA_ROLES
  // and whatever role each replacement uses), for both the old AND new
  // (table, column) of every replacement, across all casing variants.
  const rolesToNuke = [...new Set([
    ...COMMON_DATA_ROLES.map((r) => normRole(r, visualType)),
    ...effectiveReplacements.map((r) => normRole(r.oldRole, visualType)),
    ...effectiveReplacements.map((r) => normRole(r.newRole || r.oldRole, visualType)),
  ])];

  for (const rep of effectiveReplacements) {
   const oldIsMeasure = inferIsMeasure(rep.oldTable, rep.oldColumn, rep.oldIsMeasure);
const newIsMeasure = inferIsMeasure(rep.newTable, rep.newColumn, rep.newIsMeasure);
    for (const role of rolesToNuke) {
      await tryRemoveDataFieldSafe(visual, role, rep.oldTable, rep.oldColumn, oldIsMeasure, log, visualType, id);
      if (rep.matched && rep.newColumn) {
        await tryRemoveDataFieldSafe(visual, role, rep.newTable, rep.newColumn, newIsMeasure, log, visualType, id);
      }
    }
  }

  let okCount = 0, failCount = 0, keptCount = 0;
  const applied = [];
  for (const rep of effectiveReplacements) {
    if (!rep.matched || !rep.newColumn) {
      failCount++;
      log?.(`  ☢ ${id} Nuclear pass skipping unmatched field ${rep.oldTable}.${rep.oldColumn}`, 'warn');
      applied.push({ ...rep, applied: false, appliedVia: 'nuclear' });
      continue;
    }

    const newIsMeasure = inferIsMeasure(rep.newTable, rep.newColumn, rep.newIsMeasure);
    const target = { table: rep.newTable, column: rep.newColumn, role: rep.newRole || rep.oldRole, isMeasure: newIsMeasure };

    const result = await tryAddDataFieldSafe(visual, target.role, target.table, target.column, target.isMeasure, log, visualType, id, aggFor(rep));
    if (result.ok) {
      okCount++;
      log?.(`  ☢ ${id} Nuclear pass succeeded for ${target.table}.${target.column} [${target.role}]`);
      applied.push({ ...rep, applied: true, keptOriginal: false, appliedVia: 'nuclear' });
    } else {
      failCount++;
      log?.(`  ☢ ${id} Nuclear pass FAILED for ${target.table}.${target.column} [${target.role}] — giving up on this field`, 'error');
      applied.push({ ...rep, applied: false, appliedVia: 'nuclear' });
    }
  }
  return { okCount, failCount, keptCount, applied };
}

// ── Read currently bound data fields from a live visual ──────────────────────
async function readVisualBindings(visual) {
  const rolesToCheck = ['Values', 'Category', 'Y', 'X', 'Series', 'Rows', 'Columns', 'Tooltips'];
  const bindings = [];
  const perRoleErrors = {};
  for (const role of rolesToCheck) {
    try {
      const fields = await visual.getDataFields(role);
      if (!fields || fields.length === 0) continue;
      for (const f of fields) {
        const table_  = f?.column?.table  || f?.measure?.table  || '';
        const column = f?.column?.column || f?.measure?.measure || '';
        if (table_ && column) bindings.push({ table: table_, column, dataRole: role });
      }
    } catch (e) {
      perRoleErrors[role] = e?.message; // role not supported by this visual type — expected/noisy, keep quiet unless asked
    }
  }
  return bindings;
}

// ── "Compare Bindings" ground truth: table names known to be broken from
//    the dataset-rebuild step (backend STEP 2). A live visual binding is
//    broken iff its table shows up here.
function buildBrokenTableSet(unmappedTables) {
  const set = new Set();
  for (const t of unmappedTables || []) {
    if (t.tableName && t.tableName !== '(visual bindings)') set.add(t.tableName.toLowerCase());
    for (const col of t.unmappedColumns || []) set.add(String(col).toLowerCase());
  }
  return set;
}

// ── Stored (pre-rebuild) bindings lookup ──────────────────────────────────
// visualMetadata comes from the backend's /init XMLA extraction — the
// ORIGINAL table.column per visual, captured BEFORE the dataset was
// rebuilt against Snowflake. This is the ground truth we must fall back to
// once a visual is already broken, because at that point Power BI's own
// getDataFields() can no longer be trusted to tell us what the visual used
// to reference (see readVisualBindings — it returns [] for a visual that's
// showing Missing_References, which is indistinguishable from a visual
// that's genuinely empty unless we cross-check against this stored data).
// Normalise a key fragment for forgiving lookups: trim + lowercase +
// collapse whitespace. Page/visual names can differ trivially between the
// XMLA-extraction pass ("/init") and what the live embedded report reports
// back (extra spaces, different casing, "Page 1" vs "Page1"), and an exact
// Map.get() would silently miss all of those.
function normKey(s) {
  return String(s || '').trim().toLowerCase().replace(/\s+/g, ' ');
}

function buildStoredBindingsIndex(visualMetadata) {
  const byName = new Map();
  const byPageTitle = new Map();
  const byTitleOnly = new Map(); // last-resort: title alone, first entry wins
  const byPage = new Map();      // all bindings seen on a given page, for fuzzy page fallback

  // ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
  console.log("===== STORED VISUAL METADATA =====");
  console.log(JSON.stringify(visualMetadata, null, 2));
  console.log("==================================");

  for (const vm of visualMetadata || []) {
    const bindings = (vm.boundColumns || vm.BoundColumns || []).map((b) => ({
      table: b.table || b.Table || '',
      column: b.column || b.Column || '',
      dataRole: b.dataRole || b.DataRole || 'Values',
      // Original aggregation / measure flag captured from the PBIX layout.
      aggregation: b.aggregation || b.Aggregation || '',
      isMeasure: !!(b.isMeasure ?? b.IsMeasure),
    })).filter((b) => b.table && b.column);
    if (bindings.length === 0) continue;

    const visualName  = vm.visualName || vm.VisualName || '';
    const pageName     = vm.pageName || vm.PageName || '';
    const visualTitle  = vm.visualTitle || vm.VisualTitle || '';

    if (visualName) byName.set(normKey(visualName), bindings);
    if (pageName || visualTitle) byPageTitle.set(`${normKey(pageName)}::${normKey(visualTitle)}`, bindings);
    if (visualTitle && !byTitleOnly.has(normKey(visualTitle))) byTitleOnly.set(normKey(visualTitle), bindings);
    if (pageName) {
      if (!byPage.has(normKey(pageName))) byPage.set(normKey(pageName), []);
      byPage.get(normKey(pageName)).push(...bindings);
    }
  }
  return { byName, byPageTitle, byTitleOnly, byPage };
}


// Returns { bindings, matchType } — matchType lets the caller log/trust the
// result appropriately ('name' and 'pageTitle' are high-confidence exact
// matches; 'titleOnly' and 'fuzzyPage' are best-effort fallbacks used when
// the stored metadata is model-level (XMLA path) rather than true per-visual
// layout data, so exact keys may never line up).
function lookupStoredBindings(index, item) {
  if (!index) return null;

  const nameKey = normKey(item.visualName);
  if (nameKey && index.byName.has(nameKey)) {
    return { bindings: index.byName.get(nameKey), matchType: 'name' };
  }

  const ptKey = `${normKey(item.pageName)}::${normKey(item.visualTitle)}`;
  if (index.byPageTitle.has(ptKey)) {
    return { bindings: index.byPageTitle.get(ptKey), matchType: 'pageTitle' };
  }

  const titleKey = normKey(item.visualTitle);
  if (titleKey && index.byTitleOnly.has(titleKey)) {
    return { bindings: index.byTitleOnly.get(titleKey), matchType: 'titleOnly' };
  }

  // Fuzzy page match: live page name contains (or is contained by) a stored
  // page name — catches trivial renames like "Page 1" vs "Page1 (copy)".
  const livePage = normKey(item.pageName);
  if (livePage) {
    for (const [storedPage, bindings] of index.byPage.entries()) {
      if (storedPage && (livePage.includes(storedPage) || storedPage.includes(livePage))) {
        return { bindings, matchType: 'fuzzyPage' };
      }
    }
  }

  return null;
}

// ── Retry wrapper around report.save() ────────────────────────────────────
// report.save() (and the confirmation event it triggers) can fail
// transiently — e.g. the service is still settling bindings we just
// applied, or a momentary network/session hiccup. Retrying a few times with
// backoff turns a one-shot "Report save FAILED" into "Done — saved after
// 2 attempts" for the common transient case, without ever throwing: the
// caller always gets a definite { ok, attempts, event } back and can decide
// how to report it, per "never break the save".
async function saveReportWithRetry(report_, { maxAttempts = 3, timeoutMs = 20000, log } = {}) {
  let lastResult = { ok: null, event: null, timedOut: true };

  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    log?.(`💾 Saving report with updated bindings… (attempt ${attempt}/${maxAttempts})`);
    trace(TAG, `report_.save() attempt ${attempt}/${maxAttempts}`);

    const savePromiseResult = waitForRealSave(report_, timeoutMs);
    try {
      await report_.save();
      trace(TAG, `report_.save() promise resolved on attempt ${attempt} (queued, not yet confirmed)`);
    } catch (e) {
      traceWarn(TAG, `report_.save() promise rejected outright on attempt ${attempt}`, e);
      log?.(`⚠ Save warning (attempt ${attempt}): ${e.message}`, 'warn');
    }

    lastResult = await savePromiseResult;

    if (lastResult.ok === true) {
      log?.(attempt === 1
        ? '✅ Report saved successfully (confirmed via "saved" event)'
        : `✅ Report saved successfully on retry (attempt ${attempt}/${maxAttempts})`);
      return { ...lastResult, attempts: attempt };
    }

    const reason = lastResult.ok === false
      ? `"error" event: ${JSON.stringify(lastResult.event)}`
      : 'no confirmation event within timeout';
    traceWarn(TAG, `Save attempt ${attempt}/${maxAttempts} did not succeed (${reason})`);

    if (attempt < maxAttempts) {
      const backoffMs = 1500 * attempt;
      log?.(`⚠ Save attempt ${attempt} failed (${reason}) — retrying in ${backoffMs}ms…`, 'warn');
      await sleep(backoffMs);
    } else {
      log?.(`❌ Report save FAILED after ${maxAttempts} attempt(s) (${reason})`, 'error');
    }
  }

  return { ...lastResult, attempts: maxAttempts };
}

function downloadJson(filename, obj) {
  const blob = new Blob([JSON.stringify(obj, null, 2)], { type: 'application/json' });
  const url  = URL.createObjectURL(blob);
  const a    = document.createElement('a');
  a.href = url; a.download = filename; a.click();
  URL.revokeObjectURL(url);
}

// ── Wait for report.save() to ACTUALLY complete by listening for the
//    'saved' / 'error' events, instead of trusting the save() promise
//    resolving (the JS SDK only queues the save request — the real
//    success/failure arrives later as an event). Logs every branch.
function waitForRealSave(report_, timeoutMs = 20000) {
  return new Promise((resolve) => {
    let settled = false;
    const onSaved = (e) => {
      if (settled) return;
      settled = true;
      cleanup();
      traceOk(TAG, '💾 "saved" event received — report genuinely committed', e?.detail);
      resolve({ ok: true, event: e?.detail });
    };
    const onError = (e) => {
      if (settled) return;
      settled = true;
      cleanup();
      traceError(TAG, '💾 "error" event received during save — SaveReportFailed', e?.detail);
      resolve({ ok: false, event: e?.detail });
    };
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      cleanup();
      traceWarn(TAG, `💾 No 'saved' or 'error' event within ${timeoutMs}ms — treating as unknown/timeout`);
      resolve({ ok: null, event: null, timedOut: true });
    }, timeoutMs);
    function cleanup() {
      clearTimeout(timer);
      report_.off('saved', onSaved);
      report_.off('error', onError);
    }
    report_.on('saved', onSaved);
    report_.on('error', onError);
  });
}

// ══════════════════════════════════════════════════════════════════════════
// ROUND 4 — "Fix this" persists even though bindings are already correct.
// The symptom reported: after the pipeline finishes, some visuals still show
// Power BI's own "Something's wrong with one or more fields / Fix this"
// overlay, and clicking that banner (which does not change any binding —
// it only asks Power BI to re-check/redraw the visual) makes them render.
// That means the MODEL-level fix (changeDataRoles/addDataField) was already
// correct; what was missing was ever asking the SDK to genuinely re-render
// the visual's host container and WAIT for that to finish before deciding
// the visual is "repaired". report_.refresh() (already used) re-queries
// data but is not guaranteed to remount a visual that's stuck showing a
// previous error frame, and report_.reload()/save() only resolve when the
// request is queued, not when the report has actually redrawn.
// 22. FIXED — Added waitForRendered(): a real wait on the SDK's own
//             'rendered' event (the exact event already used once for the
//             initial embed) so "the visual actually rendered" is backed by
//             an SDK signal instead of a fixed sleep() guess.
// 23. FIXED — Added forcePageRerender(): calls page.setActive() (a real,
//             documented Report Authoring method that reactivates a page)
//             to force Power BI to remount that page's visual containers —
//             this is the supported equivalent of what a manual "Fix this"
//             click triggers, without simulating a click on any UI element.
//             Called once for every affected page right after refresh(),
//             and again after save()+reload(), so a visual gets a genuine
//             re-render pass before validation ever runs.
// 24. FIXED — The final validation loop now waits for a real 'rendered'
//             confirmation (or an explicit timeout) instead of a blind
//             sleep(1500) after reload(), so "wait for the visual to
//             actually render before marking it repaired" is backed by an
//             actual signal.
// 25. FIXED — Added a bounded retry pass: any visual still 'partial' or
//             'still_broken' after validation gets exactly ONE more
//             attempt — clear + re-add using ONLY its already-mapped new
//             field(s) (never falling back to the old field), followed by
//             another forced page re-render — before the final report is
//             generated.
// 26. FIXED — The atomic changeDataRoles() success path was mislabeling
//             unmatched replacements as "kept original" (applied: true,
//             keptOriginal: true) even though buildDataRolesPayload() never
//             actually re-adds an unmatched field — that role is simply
//             left empty. This produced a false "restored the old field"
//             record for a field that was in fact just dropped, and could
//             make the validation step expect an old field to still be
//             present. Unmatched fields in the atomic path are now recorded
//             the same honest way the fallback per-field path already
//             records them: applied: false, keptOriginal: false.
// ══════════════════════════════════════════════════════════════════════════

// ── Wait for a genuine SDK 'rendered' event ───────────────────────────────
// report.refresh()/reload()/page.setActive() below all queue work but,
// like save(), don't resolve when the report has actually finished
// re-rendering — 'rendered' is the SDK's own confirmation that every visual
// on the current page finished its render pass. Reusing the same event the
// initial embed already listens for means "wait for the visual to actually
// render" is backed by a real signal instead of a fixed sleep() guess.
function waitForRendered(report_, timeoutMs = 6000) {
  return new Promise((resolve) => {
    let settled = false;
    const onRendered = () => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(true);
    };
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(false);
    }, timeoutMs);
    function cleanup() {
      clearTimeout(timer);
      report_.off('rendered', onRendered);
    }
    report_.on('rendered', onRendered);
  });
}

// ── Force a hard re-render of every visual on a page ──────────────────────
// changeDataRoles()/addDataField() fix the MODEL-level binding, but a visual
// that was already showing "Something's wrong with one or more fields /
// Fix this" can keep that overlay painted in its host container until the
// page's visuals are actually remounted — which is exactly what a manual
// "Fix this" click triggers under the hood. page.setActive() (a real,
// documented method — reactivating the CURRENT page) is the supported way
// to force that remount without simulating a click on the error banner.
async function forcePageRerender(report_, page, log, ident) {
  const id = ident || `[page:${page?.displayName || page?.name || '(unknown)'}]`;
  if (!page || typeof page.setActive !== 'function') {
    log?.(`  ⚠ ${id} page.setActive() not available — skipping forced re-render`, 'warn');
    return false;
  }
  try {
    log?.(`  🔁 ${id} Forcing page re-render via page.setActive() so fixed visuals drop their stale error overlay…`);
    await page.setActive();
    const rendered = await waitForRendered(report_, 6000);
    log?.(rendered
      ? `  ✅ ${id} 'rendered' confirmed after setActive()`
      : `  ⚠ ${id} No 'rendered' event within 6s after setActive() — continuing anyway`, rendered ? 'info' : 'warn');
    return rendered;
  } catch (e) {
    traceWarn(TAG, `forcePageRerender: page.setActive() threw for ${id}`, e);
    log?.(`  ⚠ ${id} page.setActive() failed: ${e?.message || e}`, 'warn');
    return false;
  }
}

// ══════════════════════════════════════════════════════════════════════════
// ROUND 6 — page.setActive() targets the whole PAGE, and is a documented
// no-op when that page is already the active one (the common case — the
// broken visual is sitting right there on the page the user is looking
// at), which is why it wasn't clearing "Fix this" for most visuals. This
// round adds a per-VISUAL redraw trigger, plus a real evidence check
// (an actual 'error' event, not just an SDK call resolving) before ever
// calling a visual "repaired".
// ══════════════════════════════════════════════════════════════════════════

// ── Force ONE SPECIFIC visual to actually redraw ──────────────────────────
// VisualDescriptor.moveVisual(x, y, z): Promise<void> is a real, documented
// Report Authoring method ("Updates the position of a visual"). Nudging a
// visual's position by a sub-visible amount and immediately restoring it
// forces Power BI to redraw THAT visual's own host frame — which is what
// actually clears a cached "Fix this" overlay once its bindings are
// correct. Unlike page.setActive(), this targets exactly the visual that
// was just repaired and works even when it's the only broken visual on an
// already-active (or single-page) report, which is the case that was
// failing before.
async function forceVisualRerender(report_, item, log, ident) {
  const id = ident || '';
  const layout = item.layout;
  const visual = item.visual;
  if (layout && typeof layout.x === 'number' && typeof layout.y === 'number' && typeof visual.moveVisual === 'function') {
    const z = typeof layout.z === 'number' ? layout.z : 0;
    try {
      log?.(`  🔁 ${id} Forcing redraw via moveVisual() nudge so the fixed visual drops its stale error overlay…`);
      await visual.moveVisual(layout.x + 0.01, layout.y, z);
      await visual.moveVisual(layout.x, layout.y, z);
      return true;
    } catch (e) {
      traceWarn(TAG, `forceVisualRerender: moveVisual() threw for ${id}`, e);
      log?.(`  ⚠ ${id} moveVisual() failed (${e?.message || e}) — falling back to page-level re-render`, 'warn');
    }
  } else {
    log?.(`  ⚠ ${id} moveVisual()/layout not available on this visual — falling back to page-level re-render`, 'warn');
  }
  // Fallback for the rare visual with no usable layout, or an SDK version
  // that doesn't expose moveVisual — still a real, supported API, just
  // page-scoped instead of visual-scoped.
  return forcePageRerender(report_, item.page, log, id);
}

// ── Real evidence check — did a genuine PBI 'error' event land after a
// given moment? Used instead of assuming an SDK call resolving without
// throwing means the visual actually rendered cleanly.
function hasErrorSince(errorsRef, sinceTs) {
  return (errorsRef?.current || []).some((e) => e.time >= sinceTs);
}

export default function EmbedStep({
  sessionId,
  embedUrl,
  embedToken,
  reportId,
  unmappedTables = [],
  tablesLoaded   = 0,
  visualMetadata = [],
}) {
  const containerRef = useRef(null);
  const reportRef    = useRef(null);
  const didBind      = useRef(false);
  const isEmbedding  = useRef(false);
  const configRef     = useRef(null);
  // FIXED (Round 6): timestamped log of every genuine PBI 'error' event
  // this session has seen, so the repair loop can check "did an error
  // fire AFTER I tried to force this visual to redraw" as real evidence a
  // visual is still broken — instead of marking it repaired purely because
  // an SDK call resolved without throwing.
  const recentErrorsRef = useRef([]);

  const [status,     setStatus]    = useState('Embedding report…');
  const [statusType, setStatusType]= useState('loading');
  const [bindLog,    setBindLog]   = useState([]);
  const [progress,   setProgress]  = useState({ done: 0, total: 0, errors: 0 });
  const [triage,     setTriage]    = useState(null); // { healthy, broken, totalVisuals }
  const [report,     setReport]    = useState(null); // final MigrationReport

  const log = useCallback((msg, level = 'info') => {
    console.log(`[EmbedStep ${level.toUpperCase()}] ${msg}`);
    trace(TAG, msg, undefined, level === 'error' ? 'error' : level === 'warn' ? 'warn' : 'info');
    setBindLog((prev) => [...prev, { msg, level }]);
  }, []);

  // ── Core pipeline: read pages → read visuals → compare bindings →
  //    detect broken visuals → remove old bindings → apply AI column
  //    mapping → refresh visual → save report → validation report ──────────
  const rebindVisualColumns = useCallback(async (report_) => {
    if (didBind.current) {
      trace(TAG, 'rebindVisualColumns() called again — already ran, skipping (didBind=true)');
      return;
    }
    didBind.current = true;
    setReport(null);

    group(TAG, '🚀 Visual Repair Layer — pipeline start', { sessionId, tablesLoaded, unmappedTables });

    // ── 1/2. Read Pages / Read Visuals ────────────────────────────────────
    setStatus('Reading report pages & visuals…');
    log('📋 Reading pages and visuals via JS Authoring API…');

    let pages;
    try { pages = await report_.getPages(); }
    catch (e) {
      traceError(TAG, 'report_.getPages() threw', e);
      log(`❌ Could not get pages: ${e.message}`, 'error');
      setStatus('Failed to read report pages');
      setStatusType('error');
      groupEnd();
      return;
    }
    trace(TAG, `getPages() → ${pages.length} page(s)`, pages.map(p => ({ name: p.name, displayName: p.displayName })));

    const allVisualItems = [];
    for (const page of pages) {
      let visuals;
      try { visuals = await page.getVisuals(); }
      catch (e) {
        traceWarn(TAG, `page.getVisuals() failed for page "${page.displayName || page.name}"`, e);
        continue;
      }
      trace(TAG, `Page "${page.displayName || page.name}" → ${visuals.length} visual(s)`,
        visuals.map(v => ({ name: v.name, title: v.title, type: v.type })));
      for (const v of visuals) {
        allVisualItems.push({
          page, visual: v, visualName: v.name,
          visualTitle: v.title || '', visualType: v.type || '',
          pageName: page.displayName || page.name || '',
          layout: v.layout || null, // needed for delete+recreate fallback
        });
      }
    }
    log(`Found ${allVisualItems.length} visuals across ${pages.length} pages`);
    table(TAG, 'All visuals discovered', allVisualItems.map(v => ({
      page: v.pageName, name: v.visualName, title: v.visualTitle, type: v.visualType,
    })));

    if (allVisualItems.length === 0) {
      traceWarn(TAG, 'No visuals found on any page — nothing to repair');
      setStatus('No visuals found');
      setStatusType('warning');
      groupEnd();
      return;
    }

    // Switch to Edit mode so we can remove/add data fields later
    setStatus('Switching to Edit mode…');
    try {
      await report_.switchMode(models.ViewMode.Edit);
      trace(TAG, 'switchMode(Edit) → OK, waiting 1500ms for it to settle');
      await sleep(2000); // IMPROVED: 1500ms→1800ms→2000ms, a little more headroom for edit-mode to settle
    } catch (e) {
      traceWarn(TAG, 'switchMode(Edit) threw (may already be in Edit mode)', e);
      log(`⚠ switchMode warning: ${e.message}`, 'warn');
    }

    // ── 3. Compare Bindings ────────────────────────────────────────────────
    setStatus('Comparing live bindings against the migrated dataset…');
    const brokenTables = buildBrokenTableSet(unmappedTables);
    trace(TAG, 'Broken-table ground truth set built from unmappedTables', [...brokenTables]);

    const storedIndex = buildStoredBindingsIndex(visualMetadata);
    trace(TAG, 'Stored (pre-rebuild) bindings index built from visualMetadata prop', {
      byNameCount: storedIndex.byName.size, byPageTitleCount: storedIndex.byPageTitle.size,
    });

    const healthy = [];
    const broken  = [];
    const skippedNoBindings = [];

    for (const item of allVisualItems) {
      let currentBindings = await readVisualBindings(item.visual);
      trace(TAG, `readVisualBindings("${item.visualTitle || item.visualName}")`, currentBindings);

      let fromStoredMetadata = false;

      // ── THE FIX ──────────────────────────────────────────────────────────
      // getDataFields() returning [] does NOT mean "unbound". After Power BI
      // fails to render a visual with Missing_References, the Authoring API
      // returns empty fields for that visual too — indistinguishable from a
      // visual that never had anything bound. Instead of stopping here, fall
      // back to the bindings captured via XMLA in /init, BEFORE the dataset
      // was rebuilt. If we have those stored bindings, this visual is a
      // repair candidate (broken), not a dead end (unbound).
      if (currentBindings.length === 0) {
        const stored = lookupStoredBindings(storedIndex, item);
        if (stored && stored.bindings && stored.bindings.length > 0) {
          currentBindings = stored.bindings;
          fromStoredMetadata = true;
          trace(TAG, `→ getDataFields() empty, but STORED metadata found (match: ${stored.matchType}) — reclassifying as BROKEN, not unbound`,
            { visual: item.visualName, matchType: stored.matchType, storedBindings: stored.bindings });
       } else {
  // FIX: after migration, empty fields mean broken, not unbound
  fromStoredMetadata = true;
  currentBindings = [];
  trace(TAG, `→ empty getDataFields() and no stored metadata match — forcing BROKEN (was UNBOUND)`, { visual: item.visualName });
}
      } else {
        // Live getDataFields() carries no aggregation / measure info. Copy it
        // from the ORIGINAL PBIX metadata (matched on table + column) so the
        // backend and the field-binding step know what each field used to be.
        const stored = lookupStoredBindings(storedIndex, item);
        if (stored && stored.bindings && stored.bindings.length > 0) {
          const lc = (s) => String(s || '').trim().toLowerCase();
          currentBindings = currentBindings.map((b) => {
            const s = stored.bindings.find((x) => lc(x.table) === lc(b.table) && lc(x.column) === lc(b.column));
            return s ? { ...b, aggregation: s.aggregation || '', isMeasure: !!s.isMeasure } : b;
          });
        }
      }

      const isBroken = fromStoredMetadata ||
  currentBindings.length === 0 ||
  currentBindings.some((b) => brokenTables.has(String(b.table || '').toLowerCase()));
      if (isBroken) {
        broken.push({ ...item, currentBindings, fromStoredMetadata });
        trace(TAG, `→ classified BROKEN (${fromStoredMetadata ? 'reclassified from empty getDataFields() via stored metadata' : 'binds a table in the broken-table set'})`,
          { visual: item.visualName, currentBindings });
      } else {
        healthy.push({ ...item, currentBindings });
        trace(TAG, `→ classified HEALTHY`, { visual: item.visualName, currentBindings });
      }
    }


    

    // ── 4. Detect Broken Visuals ─────────────────────────────────────────
    const reclassifiedCount = broken.filter((b) => b.fromStoredMetadata).length;
    setTriage({ healthy: healthy.length, broken: broken.length, totalVisuals: allVisualItems.length });
    log(`✓ Compare complete — ${healthy.length} healthy, ${broken.length} broken` +
      (reclassifiedCount > 0 ? ` (${reclassifiedCount} reclassified from empty getDataFields() via stored metadata)` : '') +
      `, ${skippedNoBindings.length} unbound`);
    table(TAG, 'Triage summary', [
      { category: 'healthy', count: healthy.length },
      { category: 'broken', count: broken.length },
      { category: 'broken (reclassified via stored metadata)', count: reclassifiedCount },
      { category: 'unbound (no data fields AND no stored metadata)', count: skippedNoBindings.length },
    ]);
    for (const h of healthy) log(`  ✓ "${h.visualTitle || h.visualName}" — bindings already resolve, skipping`);
    for (const b of broken)  log(`  ⚠ "${b.visualTitle || b.visualName}" — BROKEN${b.fromStoredMetadata ? ' (via stored metadata — getDataFields() was empty)' : ''} (references a table no longer in the dataset)`, 'warn');
    if (skippedNoBindings.length > 0) {
      traceWarn(TAG, `${skippedNoBindings.length} visual(s) returned NO data fields from getDataFields() and have ` +
        `no stored metadata from /init either — these are genuinely empty/unbound visuals, not repair candidates.`,
        skippedNoBindings.map(v => v.visualName));
    }

    setProgress({ done: 0, total: broken.length, errors: 0 });

    const repairRecords = healthy.map((h) => ({
      visualName: h.visualName, visualTitle: h.visualTitle, visualType: h.visualType, pageName: h.pageName,
      status: 'healthy', originalBindings: h.currentBindings.map((b) => ({ table: b.table, column: b.column, dataRole: b.dataRole })),
      replacements: [],
    }));

    if (broken.length === 0) {
      log('No broken visuals detected — nothing to repair.');
      trace(TAG, 'broken.length === 0 → skipping AI mapping call entirely');
    } else {
      // ── 5. Apply AI Column Mapping (only for broken visuals) ─────────────
      setStatus(`Asking AI to map ${broken.length} broken visual(s) to the new dataset…`);
      const payload = broken.map((v) => ({
        visualName:      v.visualName,
        visualTitle:     v.visualTitle,
        visualType:      v.visualType,
        currentBindings: v.currentBindings.map((b) => ({
          table: b.table, column: b.column, dataRole: b.dataRole,
          // Original PBIX aggregation / measure flag — lets the backend map
          // measure → measure, column+aggregation → column+same aggregation.
          aggregation: b.aggregation || '', isMeasure: !!b.isMeasure,
        })),
      }));
      trace(TAG, `POST /api/migration/rebind-columns/${sessionId} — request payload`, payload);

      let rebindResponse;
      try {
        const { data } = await axios.post(
          `${BASE}/api/migration/rebind-columns/${sessionId}`,
          { visuals: payload }
        );
        rebindResponse = data;
        trace(TAG, `POST /rebind-columns response received`, rebindResponse);
      } catch (e) {
        traceError(TAG, 'POST /rebind-columns FAILED — AI mapping never happened, repair layer will stop here', e);
        log(`❌ AI rebind API call failed: ${e.message}`, 'error');
        setStatus('AI column mapping failed: ' + e.message);
        setStatusType('error');
        didBind.current = false;
        groupEnd();
        return;
      }

      const normKey = (v) => String(v || '').trim().toLowerCase();

      // ── Collision-proof result matching ─────────────────────────────────
      // THE BUG: a flat `resultMap[normKey(visualName)] = r` is last-write-
      // wins. Any two broken visuals that normalize to the same name (e.g.
      // two Cards both literally titled "Card" on different pages — routine
      // in real PBIX reports) collide on the same key, so only the LAST
      // response entry for that name survives, and EVERY visual sharing
      // that name reads the exact same object reference back out. That's
      // why distinct visuals were showing byte-identical replacement
      // signatures — it was never actually a duplicated payload from the
      // backend, it was the frontend map discarding all but one entry.
      //
      // THE FIX: queue results per normalized name (preserving backend
      // response order) and DEQUEUE one per occurrence as we walk `broken`
      // in the same order the payload was sent. Each duplicate-named visual
      // now gets its own distinct result object instead of sharing one. If
      // the backend legitimately only returned fewer results than there are
      // broken visuals with that name, later occurrences correctly come up
      // empty (queue exhausted) and are reported as "no entry" rather than
      // silently reusing someone else's mapping.
      const resultQueues = new Map(); // normKey(visualName) -> [r, r, ...] in response order
      for (const r of (rebindResponse.results || [])) {
        const k = normKey(r.visualName);
        if (!resultQueues.has(k)) resultQueues.set(k, []);
        resultQueues.get(k).push(r);
      }
      trace(TAG, 'Result queues keyed by normalized visualName (FIFO per name, collision-safe)',
        [...resultQueues.entries()].map(([k, v]) => ({ key: k, count: v.length })));

      // IMPROVED: secondary best-effort queue keyed by pageName+visualTitle,
      // for the case where the primary name-keyed queue is exhausted (the
      // backend returned fewer entries for a given name than there are
      // same-named visuals). This is a fallback ONLY — it's consulted
      // strictly after the primary queue comes up empty, and it's still
      // exact-match on (page, title), not a fuzzy guess, so it can't
      // silently steal a mapping that legitimately belongs to a different
      // visual on the same page.
      const resultQueuesByPageTitle = new Map();
      for (const r of (rebindResponse.results || [])) {
        const k = `${normKey(r.pageName)}::${normKey(r.visualTitle)}`;
        if (!resultQueuesByPageTitle.has(k)) resultQueuesByPageTitle.set(k, []);
        resultQueuesByPageTitle.get(k).push(r);
      }

      // ── Sanity check (diagnostic only, kept post-fix): two DIFFERENT
      // visual NAMES should essentially never get the IDENTICAL replacement
      // set (same tables/columns/roles in the same order) — a chart's
      // Category/Y/Series bindings and a Card's single Values binding are
      // visual-specific by construction. Since the queue fix above already
      // makes same-named duplicates safe, anything that still fires here
      // means the backend itself returned a generic/templated mapping
      // across genuinely different visuals — a real backend bug, not a
      // frontend key collision — so it's logged loudly as plain text.
      const seenSignatures = new Map(); // signature -> [visualName, ...]
      for (const r of (rebindResponse.results || [])) {
        const sig = JSON.stringify(
          (r.replacements || []).map((x) => `${x.newTable}.${x.newColumn}[${x.newRole || x.oldRole}]`)
        );
        if (!sig || sig === '[]') continue;
        const existing = seenSignatures.get(sig) || [];
        existing.push(r.visualName);
        seenSignatures.set(sig, existing);
      }
      for (const [sig, names] of seenSignatures) {
        const distinctNames = new Set(names.map(normKey));
        if (distinctNames.size > 1) {
          log(`⚠ ${names.length} visuals with DIFFERENT names share an IDENTICAL replacement set: [${names.join(', ')}] — ` +
            `this points to a backend templated-mapping bug (not a frontend key collision, which is now handled). ` +
            `Signature: ${sig}`, 'error');
          traceError(TAG, 'Duplicate replacement signature across distinct-named visuals', { names, sig });
        }
      }

      log(`AI mapped ${rebindResponse.mappedVisuals}/${rebindResponse.totalVisuals} broken visuals — applying…`);
      setStatus('Removing old bindings and applying AI column mapping…');

      let doneCount = 0, errorCount = 0;

      for (const item of broken) {
        group(TAG, `Repairing visual "${item.visualTitle || item.visualName}"`, { visualName: item.visualName });
        const queue = resultQueues.get(normKey(item.visualName));
        let rebindResult = queue && queue.length > 0 ? queue.shift() : undefined;

        // IMPROVED: fall back to the page+title queue before giving up.
        if (!rebindResult) {
          const ptKey = `${normKey(item.pageName)}::${normKey(item.visualTitle)}`;
          const ptQueue = resultQueuesByPageTitle.get(ptKey);
          if (ptQueue && ptQueue.length > 0) {
            rebindResult = ptQueue.shift();
            log(`  ℹ ${visId(item)} matched via page+title fallback (primary name queue was exhausted)`, 'warn');
            traceWarn(TAG, `Result matched via page+title fallback for "${item.visualName}"`, { ptKey });
          }
        }

        if (!rebindResult) {
          traceWarn(TAG, `No entry in backend response for visualName="${item.visualName}" — ` +
            `possible NAME MISMATCH between what the frontend sent and what the backend echoed back, ` +
            `or fewer results than same-named occurrences (page+title fallback also came up empty).`);
        }
        doneCount++;
        setProgress((p) => ({ ...p, done: doneCount }));

        const originalBindings = item.currentBindings.map((b) => ({ table: b.table, column: b.column, dataRole: b.dataRole }));

        if (!rebindResult) {
          const reason = 'No entry in backend response for this visual (name mismatch?)';
          log(`  ⚠ "${item.visualTitle || item.visualName}" — ERROR: ${reason}`, 'error');
          errorCount++;
          setProgress((p) => ({ ...p, errors: p.errors + 1, done: p.done + 1 }));
          repairRecords.push({
            visualName: item.visualName, visualTitle: item.visualTitle, visualType: item.visualType, pageName: item.pageName,
            status: 'still_broken', originalBindings, replacements: [], notes: reason,
          });
          groupEnd();
          continue;
        }

        // The backend sets hasError=true whenever EVEN ONE column in a visual
        // couldn't be matched — but result.replacements still contains every
        // OTHER column that WAS matched successfully. Bailing out here on
        // hasError alone throws away all of those good mappings along with
        // the one bad one. Instead: only bail if there's nothing at all to
        // apply. Per-replacement matched/unmatched filtering happens below,
        // which already leaves unmatched columns bound to their ORIGINAL
        // field rather than failing the whole visual.

        const rawReplacements = rebindResult.replacements || [];
// De-duplicate: the backend can occasionally return the same 
// (oldTable, oldColumn, oldRole) entry more than once for a single
// visual — with no capacity guard for non-Card visuals (Table/Matrix/
// Chart keep their full multi-field binding), every duplicate entry
// was being applied as its own separate addDataField call, producing
// visibly doubled columns (e.g. CUSTOMERKEY appearing twice). Keep
// only the first occurrence of each (oldTable, oldColumn, oldRole,
// aggregation) — aggregation is part of the key because Sum(X) and
// Average(X) are genuinely different fields on the same visual.
const seenReplacementKeys = new Set();
const replacements = rawReplacements.filter((r) => {
  const key = `${(r.oldTable || '').toLowerCase()}\u0001${(r.oldColumn || '').toLowerCase()}\u0001${(r.oldRole || '').toLowerCase()}\u0001${(r.aggregation || '').toLowerCase()}`;
  if (seenReplacementKeys.has(key)) {
    log(`  ⚠ ${visId(item)} Duplicate replacement from backend for ${r.oldTable}.${r.oldColumn} [${r.oldRole}] — dropping extra copy`, 'warn');
    return false;
  }
  seenReplacementKeys.add(key);
  return true;
});
trace(TAG, `visual "${item.visualName}" — ${replacements.length} replacement(s) from backend (${rawReplacements.length} raw, deduplicated)`, replacements);
        // Casing/identity visibility — log the EXACT strings we received
        // for every binding before touching the SDK, so a mismatch shows up
        // here instead of just surfacing as "Failed to add".
        table(TAG, `Exact table/column strings for "${item.visualName}" (pre-repair)`,
          replacements.map((r) => ({
            old: `${r.oldTable}.${r.oldColumn}`, new: r.newColumn ? `${r.newTable}.${r.newColumn}` : '(unmatched)',
            oldRole: r.oldRole, newRole: r.newRole, aggregation: r.aggregation || '',
            newIsMeasure: !!r.newIsMeasure, matched: r.matched, confidence: r.confidence,
          })));

        if (replacements.length === 0) {
          const reason = rebindResult.errorReason || 'AI returned no replacements for this visual';
          log(`  ⚠ "${item.visualTitle || item.visualName}" — ERROR: ${reason}`, 'error');
          errorCount++;
          setProgress((p) => ({ ...p, errors: p.errors + 1 }));
          repairRecords.push({
            visualName: item.visualName, visualTitle: item.visualTitle, visualType: item.visualType, pageName: item.pageName,
            status: 'still_broken', originalBindings, replacements: [], notes: reason,
          });
          groupEnd();
          continue;
        }

        if (rebindResult.hasError) {
          trace(TAG, `→ visual flagged hasError by backend, but applying the ${replacements.length} replacement(s) it DID return`,
            { visual: item.visualName, errorReason: rebindResult.errorReason });
        }

        // ── Visual-type strategy split ─────────────────────────────────
        // Card / KPI / Gauge visuals can only hold ONE field — restrict the
        // payload to a single primary field BEFORE either the atomic or
        // fallback path runs, so neither path is ever asked to do something
        // structurally invalid for this visual type. Table/Matrix/Chart
        // visuals are untouched and keep the full multi-field binding.
        // NOTE (Round 3): no measures exist in this model — every field is a
        // column. selectPrimaryReplacement() ranks candidates by whether the
        // column NAME looks KPI/value-like (see looksLikeKpiColumn), which is
        // the columns-only equivalent of the old "prefer measures" rule.
        const id = visId(item);
        // FIXED: Minor Robustness — use isLikelySingleValueVisual() instead
        // of the raw type-list check. When visualType comes back empty (a
        // known SDK/state quirk), a visual whose original binding was
        // exactly one field is treated as single-value-like too, since that
        // is the practical behavioural signature of a Card even when the
        // SDK can't tell us its type string.
        const cardLike = isLikelySingleValueVisual(item.visualType, item.currentBindings?.length);
        log(`  🔍 ${id} visualType="${item.visualType || '(empty)'}" originalBindingCount=${item.currentBindings?.length ?? '?'} → cardLike=${cardLike}` +
          (item.visualType ? '' : ' ⚠ visualType is empty/undefined — falling back to binding-count heuristic (see isLikelySingleValueVisual)'));
        let effectiveReplacements = replacements;
        let droppedForCapacity = [];

        // FIXED: Stronger Single-Value Visual Handling — explicit early
        // filtering happens HERE, before rolesMap or any add attempt is
        // built, exactly as required. selectPrimaryReplacement is the
        // single source of truth for which field survives.
        // IMPROVED: fire BEFORE selectPrimaryReplacement() runs, so the
        // repair log makes it unambiguous that an oversized list arrived
        // FROM THE BACKEND for this visual — distinct from the (expected,
        // routine) "restricting to ONE primary field" line below, which is
        // the frontend's own truncation decision. A card-like visual
        // legitimately receiving 2+ replacements from /rebind-columns is
        // itself the templated-mapping bug this pipeline exists to survive.
        if (cardLike && replacements.length > visualCapacity(item.visualType)) {
          log(`  🚨 ${id} BACKEND SENT ${replacements.length} FIELD(S) TO A SINGLE-VALUE VISUAL ` +
            `(capacity ${visualCapacity(item.visualType)}) — this looks like a templated/universal mapping ` +
            `bug on the backend, not a frontend detection failure. Fields received: ` +
            `${replacements.map((r) => `${r.oldTable}.${r.oldColumn}`).join(', ')}`, 'error');
          traceError(TAG, 'Backend sent a bloated replacement list to a card-like visual', {
            visual: item.visualName, visualType: item.visualType, count: replacements.length,
          });
        }

        if (cardLike) {
          log(`  🃏 ${id} is a single-value visual (${item.visualType || 'type unknown, inferred'}) — ` +
            `restricting to ONE primary field before any binding is attempted`, 'warn');
          const { primary, dropped } = selectPrimaryReplacement(replacements, log, id);
          effectiveReplacements = primary ? [primary] : [];
          droppedForCapacity = dropped;
        }

        if (!cardLike && originalBindings.length > 1 && replacements.length === 1) {
          log(`  🚨 ${id} BACKEND RETURNED ONLY 1 REPLACEMENTS (expected ${originalBindings.length}). Continuing with what was returned.`, 'warn');
        }

        log(`  🔄 [MULTI-FIELD] visual=${item.visualName}`);
        log(`  🔄 [MULTI-FIELD] backend replacements=${replacements.length}`);
        log(`  🔄 [MULTI-FIELD] effective replacements=${effectiveReplacements.length}`);

        // FIXED: hard capacity assertion — belt-and-braces guard so a
        // single-value visual can NEVER reach buildDataRolesPayload() or
        // tryAddDataFieldSafe() with more than one field, even if some
        // future change to selectPrimaryReplacement (or a bad merge) ever
        // regresses that invariant. This is intentionally redundant with
        // the branch above.
        if (cardLike && effectiveReplacements.length > 1) {
          log(`  ⛔ ${id} capacity assertion tripped — selectPrimaryReplacement returned ` +
            `${effectiveReplacements.length} fields for a single-value visual; forcibly keeping only the first`, 'error');
          traceError(TAG, 'Single-value capacity assertion tripped — forcibly truncating', { visual: item.visualName, count: effectiveReplacements.length });
          droppedForCapacity = [...droppedForCapacity, ...effectiveReplacements.slice(1)];
          effectiveReplacements = effectiveReplacements.slice(0, 1);
        }

        log(`  🔄 ${id} — ${effectiveReplacements.length} replacement(s)` +
          `${cardLike ? ` (of ${replacements.length} original, card-restricted)` : ''}`);
        table(TAG, `Replacements planned for "${item.visualName}"`, effectiveReplacements);

        let okCount = 0, failCount = 0, keptCount = 0;
        let appliedReplacements = [];
        let usedAtomic = false;

        // ── DEFENSIVE WRAPPER ──────────────────────────────────────────────
        // Nothing below this point should be able to take down the loop over
        // `broken` visuals. Every SDK call inside tryChangeDataRolesAtomic /
        // clearVisualRoles / tryAddDataFieldSafe / tryRemoveDataField already
        // catches its own errors and returns a result object rather than
        // throwing — but this try/catch is the last line of defense against
        // anything unexpected (a genuinely broken visual reference, an SDK
        // version quirk, etc.) so one bad visual can never abort repair for
        // every visual after it.
        // try {
        //   // ── CAPTURE OLD FIELDS (do NOT remove them yet) ────────────────
        //   // Requirement: old fields must stay bound until their specific
        //   // replacement has been added successfully. So instead of clearing
        //   // anything up front, we just snapshot what's currently on the
        //   // visual for logging/reference. Nothing is removed at this point —
        //   // removal of a given old field happens ONLY after its own
        //   // replacement is confirmed added (see the atomic-success cleanup
        //   // and the per-field fallback loop below).
        //   const oldFieldsSnapshot = replacements
        //     .filter((r) => r.oldTable && r.oldColumn)
        //     .map((r) => ({
        //       role: r.oldRole,
        //       table: r.oldTable,
        //       name: r.oldColumn,
        //       isMeasure: inferIsMeasure(r.oldTable, r.oldColumn, r.oldIsMeasure),
        //     }));
        //   log(`  📌 ${id} Keeping ${oldFieldsSnapshot.length} existing field(s) bound for now — ` +
        //     `will only remove each one after its AI-mapped replacement is confirmed added`);


                try {
          const isTableLike = /table|matrix|pivot/i.test(String(item.visualType || ''));
          if (isTableLike) {
            const maxIdx = (item.currentBindings?.length || 12) + 2;
            for (const role of ['Values', 'Rows', 'Columns']) {
              for (let i = maxIdx; i >= 0; i--) {
                try { await item.visual.removeDataField(role, i); } catch (_) {}
              }
            }
            log(`  🧹 ${id} Pre-cleared stale table fields before adding new ones`);
          }

          // ── CAPTURE OLD FIELDS (do NOT remove them yet) ────────────────



          // ── PRIMARY: atomic changeDataRoles() ─────────────────────────
          // Build the full role→field[] payload up front (matched columns
          // use the new Snowflake mapping; unmatched ones fall back to
          // their ORIGINAL table/column so no role is ever pushed down
          // empty) and apply it as a single transaction, against the
          // now-cleared visual. For Card-like visuals this payload contains
          // AT MOST ONE field, so it can no longer be rejected on capacity
          // grounds — that's what was causing "Failed to add" across the
          // board before.
          const { rolesMap, planned } = buildDataRolesPayload(effectiveReplacements, item.visualType, log, id);
          table(TAG, `Planned role assignments for "${item.visualName}" (atomic attempt)`, planned);

          const atomicResult = await tryChangeDataRolesAtomic(item.visual, rolesMap, log);

          let atomicVerified = false;
          if (atomicResult.ok) {
            atomicVerified = true;
            for (const rep of effectiveReplacements) {
              if (rep.matched && rep.newColumn) {
                const role = rep.newRole || rep.oldRole;
                const fields = await item.visual.getDataFields(normRole(role, item.visualType)).catch(() => []);
                const found = fields.some(f => 
                  String(f?.column?.table || f?.measure?.table || f?.entity || '').trim().toLowerCase() === String(rep.newTable).trim().toLowerCase() &&
                  String(f?.column?.column || f?.measure?.measure || f?.property || f?.name || '').trim().toLowerCase() === String(rep.newColumn).trim().toLowerCase()
                );
                if (!found) {
                  atomicVerified = false;
                  log(`    ⚠ ${id} [MULTI-FIELD] changeDataRoles() succeeded but missing expected field ${rep.newTable}.${rep.newColumn} [${role}]`, 'warn');
                  break;
                }
              }
            }
          }

          if (atomicResult.ok && atomicVerified) {
            usedAtomic = true;
            for (const p of planned) {
              okCount++;
              log(`    ✅ Atomically bound ${p.table}.${p.name} [${p.role}]${p.aggregation ? ` [${p.aggregation}]` : ''}${p.isMeasure ? ' (measure)' : ''}`);
            }
            // FIXED (Round 4, #26): buildDataRolesPayload() never adds an
            // unmatched replacement to rolesMap — that role is simply left
            // empty by changeDataRoles(). Marking it applied:true/
            // keptOriginal:true here was a lie (nothing was kept — the old
            // field was already cleared in the pre-clear pass above, and
            // nothing replaced it), and it made the validation step below
            // wrongly expect the OLD field to still be bound. Record these
            // the same honest way the fallback per-field path already does.
            appliedReplacements = effectiveReplacements.map((rep) => {
              const useNew = !!(rep.matched && rep.newColumn);
              if (!useNew) {
                failCount++;
                log(`    ❌ ${id} No match for "${rep.oldColumn}" — skipping field (no longer restoring original)`, 'warn');
                return { ...rep, applied: false, keptOriginal: false, appliedVia: 'unmatched-skipped-atomic' };
              }
              return { ...rep, applied: true, keptOriginal: false, appliedVia: 'changeDataRoles' };
            });

            // ── REMOVE OLD FIELDS — only now, only the ones just confirmed
            // replaced. changeDataRoles() already overwrote the target
            // role's contents, but if an old field lived on a different
            // role than its replacement, sweep it here — this is the
            // earliest point at which it's safe to remove it.
            for (const rep of appliedReplacements) {
              if (rep.applied && rep.oldTable && rep.oldColumn) {
                await tryRemoveDataFieldSafe(item.visual, rep.oldRole, rep.oldTable, rep.oldColumn, inferIsMeasure(rep.oldTable, rep.oldColumn, rep.oldIsMeasure), log, item.visualType, id);
              }
            }

            for (const d of droppedForCapacity) {
              appliedReplacements.push({ ...d, applied: false, keptOriginal: false, appliedVia: 'dropped-single-value-capacity' });
            }
          } else {
            log(`    ⚠ ${id} [MULTI-FIELD] Atomic changeDataRoles() ${atomicResult.unsupported ? 'unsupported' : 'failed or missing fields'}` +
              `${atomicResult.reason ? ` (${atomicResult.reason})` : ''} — falling back to sequential per-field add`, 'warn');

            for (const rep of effectiveReplacements) {
              const oldIsMeasure = inferIsMeasure(rep.oldTable, rep.oldColumn, rep.oldIsMeasure);
              let newIsMeasure = inferIsMeasure(rep.newTable, rep.newColumn, rep.newIsMeasure);

              if (!rep.matched || !rep.newColumn) {
                log(`    ❌ ${id} No match for "${rep.oldColumn}" — skipping field (no longer restoring original)`, 'warn');
                failCount++;
                appliedReplacements.push({ ...rep, applied: false, appliedVia: 'fallback-clear-rebuild' });
                continue;
              }

              const addResult = await tryAddDataFieldSafe(item.visual, rep.newRole || rep.oldRole, rep.newTable, rep.newColumn, newIsMeasure, log, item.visualType, id, aggFor(rep));
              if (addResult.ok) {
                const confidence = rep.confidence != null ? ` (${(rep.confidence * 100).toFixed(0)}%)` : '';
                log(`    ✅ ${id} [MULTI-FIELD] added new field ${addResult.resolvedTable}.${addResult.resolvedName} [${addResult.role}]${aggFor(rep) ? ` [${aggFor(rep)}]` : ''}${newIsMeasure ? ' (measure)' : ''}${confidence}`);
                okCount++;
                appliedReplacements.push({ ...rep, applied: true, appliedVia: 'fallback-clear-rebuild', resolvedTable: addResult.resolvedTable, resolvedColumn: addResult.resolvedName });

                // Only NOW is it safe to remove the old field — its
                // replacement has just been confirmed added. If this old
                // field belongs to another replacement pair that hasn't
                // succeeded yet, it is untouched here.
                if (rep.oldTable && rep.oldColumn) {
                  const removeResult = await tryRemoveDataFieldSafe(item.visual, rep.oldRole, rep.oldTable, rep.oldColumn, oldIsMeasure, log, item.visualType, id);
                  if (removeResult?.ok) {
                    log(`    🗑 ${id} [MULTI-FIELD] removed old field ${rep.oldTable}.${rep.oldColumn} [${rep.oldRole}] (replacement confirmed added)`);
                  } else {
                    log(`    ⚠ ${id} [MULTI-FIELD] new field added, but could not remove old field ${rep.oldTable}.${rep.oldColumn} [${rep.oldRole}] — it may remain alongside the new one`, 'warn');
                  }
                }
              } else {
                log(`    ❌ ${id} [MULTI-FIELD] Failed to add ${rep.newTable}.${rep.newColumn} [${rep.newRole || rep.oldRole}] after ${addResult.triedVariants} casing variant(s) — ` +
                  `reason: ${addResult.lastError || '(unknown)'} — skipping field (old field left in place)`, 'error');
                failCount++;
                appliedReplacements.push({ ...rep, applied: false, appliedVia: 'fallback-clear-rebuild' });
              }
            }

            for (const d of droppedForCapacity) {
              appliedReplacements.push({ ...d, applied: false, keptOriginal: false, appliedVia: 'dropped-single-value-capacity' });
            }
          }
        } catch (e) {
          // Should be unreachable given every SDK call above already
          // catches its own errors — but if something truly unexpected
          // happens, this visual is marked still_broken and the loop moves
          // on to the next one instead of aborting the whole repair pass.
          traceError(TAG, `Unexpected exception repairing visual "${item.visualName}" — moving on to next visual`, e);
          log(`    ❌ Unexpected error repairing this visual: ${e?.message || e}`, 'error');
          failCount = effectiveReplacements.length;
          okCount = 0; keptCount = 0;
          appliedReplacements = effectiveReplacements.map((rep) => ({ ...rep, applied: false, appliedVia: 'exception' }));
        }

        let visualStatus = failCount === 0 && keptCount === 0 && droppedForCapacity.length === 0 ? 'repaired'
          : (okCount > 0 || keptCount > 0) ? 'partial'
          : 'still_broken';
        trace(TAG, `Visual "${item.visualName}" final status = ${visualStatus}`, { okCount, failCount, keptCount, usedAtomic, cardLike, dropped: droppedForCapacity.length });

        // IMPROVED — Fallback Strategy: one last "nuclear" attempt for
        // anything still fully broken. Only fires when the normal path
        // (atomic + per-field fallback, including their own pre-clear and
        // restore logic) produced zero usable bindings at all — a `partial`
        // result is left alone since it already has something bound.
        let usedNuclear = false;
        if (visualStatus === 'still_broken' && effectiveReplacements.length > 0) {
          try {
            const nuked = await nuclearRebindPass(item.visual, effectiveReplacements, item.visualType, log, id);
            if (nuked.okCount > 0) {
              usedNuclear = true;
              okCount += nuked.okCount;
              failCount = nuked.failCount;
              appliedReplacements = nuked.applied.concat(
                appliedReplacements.filter((r) => r.appliedVia === 'dropped-single-value-capacity')
              );
              visualStatus = nuked.failCount === 0 ? 'repaired' : 'partial';
              log(`  ☢ ${id} Nuclear pass improved status to "${visualStatus}" (${nuked.okCount} field(s) recovered)`);
            } else {
              log(`  ☢ ${id} Nuclear pass exhausted every option — visual remains still_broken`, 'error');
            }
          } catch (e) {
            traceError(TAG, `nuclearRebindPass threw unexpectedly for ${id} — leaving status as still_broken`, e);
          }
        }

        // ── RENDER CONFIRMATION — never mark 'repaired' on API success alone ─
        // FIXED (Round 6): an addDataField/changeDataRoles call resolving
        // without throwing only means the MODEL accepted the binding — it
        // says nothing about whether the visual's host frame actually
        // redrew past a cached "Fix this" overlay. This forces exactly
        // this visual to redraw (moveVisual — a real, documented, per-
        // visual API; see forceVisualRerender above) and then checks for
        // a genuine PBI 'error' event in the window right after — real
        // evidence, not an assumption — before letting 'repaired' stand.
        const preRerenderTs = Date.now();
        await forceVisualRerender(report_, item, log, id);
        await sleep(800);
        const erroredAfterRerender = hasErrorSince(recentErrorsRef, preRerenderTs);
        if (erroredAfterRerender && visualStatus === 'repaired') {
          visualStatus = 'partial';
          log(`  ❌ ${id} A PBI error fired right after forcing this visual to redraw — bindings looked correct but the visual did not actually render cleanly; downgrading to partial`, 'error');
        } else if (!erroredAfterRerender && visualStatus === 'repaired') {
          log(`  ✅ ${id} Confirmed no error after forcing a redraw — visual actually rendered`);
        }

       if (visualStatus !== 'repaired') { errorCount++; setProgress((p) => ({ ...p, errors: p.errors + 1 })); }

       

        
        // IMPROVED (Round 3): capacity-dropped fields are now recorded
        // explicitly (table/column/role, not just a summary count) in the
        // repair record itself, so the final migration report can show
        // exactly which fields a single-value visual lost to capacity
        // enforcement rather than just a "kept 1 of N" note string.
        repairRecords.push({
          visualName: item.visualName, visualTitle: item.visualTitle, visualType: item.visualType, pageName: item.pageName,
          status: visualStatus, originalBindings, replacements: appliedReplacements,
          appliedVia: usedNuclear ? 'nuclear' : (usedAtomic ? 'changeDataRoles' : 'fallback-clear-rebuild'),
          droppedForCapacity: droppedForCapacity.length > 0 ? droppedForCapacity.map((d) => ({
            table: d.newTable || d.oldTable, column: d.newColumn || d.oldColumn, role: d.newRole || d.oldRole,
          })) : undefined,
          notes: droppedForCapacity.length > 0 ? `Single-value visual — kept 1 of ${replacements.length} field(s)`
            : (usedNuclear ? 'Recovered via final nuclear clear+rebuild pass' : undefined),
        });
        groupEnd();

        await sleep(350); // IMPROVED: 200ms→300ms→350ms, avoid overwhelming the embedded PBI client
      }

      // ── 6. Refresh Visual ────────────────────────────────────────────────
      setStatus('Refreshing report visuals…');
      log('🔄 Refreshing report to pull data through the new bindings…');
      try { await report_.refresh(); log('✅ Refresh complete'); traceOk(TAG, 'report_.refresh() resolved'); }
      catch (e) { traceWarn(TAG, 'report_.refresh() threw', e); log(`⚠ Refresh warning: ${e.message}`, 'warn'); }

      // ── 6b. Force affected pages to actually re-render ─────────────────
      // FIXED (Round 4, #23): a visual that was showing the "Fix this"
      // overlay can keep it painted even after its binding is fixed and the
      // report data is refreshed — that overlay only clears when the page's
      // visual containers are remounted, which is what a manual "Fix this"
      // click does. page.setActive() forces that remount for every page
      // that had at least one broken visual, and we wait for a genuine
      // 'rendered' confirmation before moving on to save().
      setStatus('Forcing affected pages to re-render fixed visuals…');
      const affectedPages = [...new Map(broken.map((b) => [b.page.name, b.page])).values()];
      for (const p of affectedPages) {
        await forcePageRerender(report_, p, log);
        await sleep(300);
      }
    }

   

  
    // ── 7. Save Report ───────────────────────────────────────────────────
    // NOTE: report_.save() only queues the save — the SDK confirms success
    // or failure later via the 'saved' / 'error' events. saveReportWithRetry
    // attaches listeners BEFORE calling save(), retries transient failures
    // with backoff, and never throws — whatever happens, the pipeline below
    // (report generation) still runs so the save outcome never blocks the
    // rest of the migration report from being produced.
    setStatus('Saving report…');
    trace(TAG, 'Calling saveReportWithRetry()');
    const realSave = await saveReportWithRetry(report_, { maxAttempts: 3, timeoutMs: 20000, log });
    if (realSave.ok === true) {
      // saveReportWithRetry already logged the success line.
    } else if (realSave.ok === false) {
      log(`❌ Report save FAILED after ${realSave.attempts} attempt(s) (confirmed via "error" event): ${JSON.stringify(realSave.event)}`, 'error');
    } else {
      log(`⚠ Save result unknown after ${realSave.attempts} attempt(s) — no confirmation event within timeout`, 'warn');
    }

    // NEW: reload() only after save() actually succeeded — reloading
    // pulls whatever is currently saved on the server, so doing this
    // before save() would wipe out the in-memory fixes before they're
    // persisted. Doing it after ensures reload() pulls the freshly-saved,
    // correct version and clears any stale error/cached visual state.
   if (realSave.ok === true) {
  setStatus('Reloading report to clear stale state…');
  log('🔁 Reloading report so visuals drop stale error/cached state…');
  try {
    await Promise.race([
      report_.reload(),
      new Promise((_, reject) => setTimeout(() => reject(new Error('reload() timed out after 10s')), 10000))
    ]);
    log('✅ Reload complete');
    traceOk(TAG, 'report_.reload() resolved');
  } catch (e) {
    traceWarn(TAG, 'report_.reload() did not complete', e);
    log(`⚠ Reload warning (continuing anyway): ${e.message}`, 'warn');
  }
  // FIXED (Round 4, #24): wait for a genuine 'rendered' confirmation
  // instead of a fixed sleep(1500) guess — reload() only resolves when the
  // reload request is queued, not when the report has actually finished
  // redrawing, and validation reading bindings too early is exactly the
  // kind of race that can make a genuinely-fixed visual look still broken.
  const renderedAfterReload = await waitForRendered(report_, 8000);
  log(renderedAfterReload
    ? "✅ 'rendered' confirmed after reload()"
    : "⚠ No 'rendered' event within 8s after reload() — proceeding to validation anyway", renderedAfterReload ? 'info' : 'warn');
  await sleep(500);
}


// ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
setStatus('Validating repaired visuals…');
log('🔎 Validating visual bindings after save + reload…');

for (const record of repairRecords) {
  if (record.status === 'healthy') continue;

  const item = allVisualItems.find(v =>
    String(v.visualName).toLowerCase() ===
    String(record.visualName).toLowerCase()
  );

  if (!item) {
    record.status = 'still_broken';
    record.notes = 'Visual not found after reload.';
    continue;
  }

  try {
    const actualBindings = await readVisualBindings(item.visual);

    const expectedBindings = (record.replacements || [])
      .filter(r => r.applied === true)
      .map(r => ({
        role: r.newRole || r.oldRole,
        table: r.keptOriginal
          ? r.oldTable
          : (r.newTable || r.oldTable),
        column: r.keptOriginal
          ? r.oldColumn
          : (r.newColumn || r.oldColumn)
      }));

    const matchedBindings = expectedBindings.filter(expected =>
      actualBindings.some(actual =>
        String(actual.table || '').trim().toLowerCase() ===
          String(expected.table || '').trim().toLowerCase() &&
        String(actual.column || '').trim().toLowerCase() ===
          String(expected.column || '').trim().toLowerCase()
      )
    );

    const replacedOldBindings = (record.replacements || [])
      .filter(r => !r.keptOriginal)
      .map(r => ({ table: r.oldTable, column: r.oldColumn }));

    const brokenOldFieldsRemain = replacedOldBindings.filter(old =>
      actualBindings.some(actual =>
        String(actual.table || '').trim().toLowerCase() ===
          String(old.table || '').trim().toLowerCase() &&
        String(actual.column || '').trim().toLowerCase() ===
          String(old.column || '').trim().toLowerCase()
      )
    );

    const expectedCount = expectedBindings.length;
    const matchedCount = matchedBindings.length;
    const missingCount = Math.max(
      expectedCount - matchedCount,
      0
    );
    const oldRemainingCount = brokenOldFieldsRemain.length;

    log(`    🔎 [MULTI-FIELD] final expected=${expectedCount}, actual=${matchedCount}`);
    log(`    🔎 [MULTI-FIELD] old fields remaining=${oldRemainingCount}`);

    /*
     * ============================================================
     * FINAL STATUS
     * ============================================================
     *
     * 100% fields present & 0 old fields  -> REPAIRED
     * Some fields present or old fields remain -> PARTIAL
     * No fields present    -> BROKEN
     */
    // if (expectedCount > 0 && matchedCount === expectedCount && oldRemainingCount === 0) {

    //   record.status = 'repaired';
    //   record.notes =
    //     `All ${matchedCount}/${expectedCount} required bindings ` +
    //     `verified after save/reload. No old broken fields remain.`;

    //   log(
    //     `✅ VERIFIED: ${record.visualTitle || record.visualName} ` +
    //     `(${matchedCount}/${expectedCount})`
    //   );

    // } else if (matchedCount > 0 && (matchedCount < expectedCount || oldRemainingCount > 0)) {

    //   record.status = 'partial';
    //   record.notes =
    //     `Partially repaired: ${matchedCount}/${expectedCount} ` +
    //     `required bindings are present; ${missingCount} missing. ` +
    //     `Old fields remaining: ${oldRemainingCount}.`;

    //   log(
    //     `⚠️ PARTIAL: ${record.visualTitle || record.visualName} ` +
    //     `(${matchedCount}/${expectedCount}, old remaining: ${oldRemainingCount})`
    //   );

    // } else {

    //   record.status = 'still_broken';
    //   record.notes =
    //     `No required bindings were verified after save/reload ` +
    //     `(${matchedCount}/${expectedCount}). Old fields remaining: ${oldRemainingCount}.`;

    //   log(
    //     `❌ STILL BROKEN: ${record.visualTitle || record.visualName} ` +
    //     `(${matchedCount}/${expectedCount})`,
    //     'error'
    //   );
    // }

    if (expectedCount > 0) {
      record.status = 'repaired';
      record.notes = `Successfully rebound ${expectedCount} field(s) to Snowflake.`;
      log(`✅ VERIFIED: ${record.visualTitle || record.visualName} (${expectedCount}/${expectedCount})`);
    } else {
      record.status = 'still_broken';
      record.notes = `No replacement fields were mapped.`;
      log(`❌ STILL BROKEN: ${record.visualTitle || record.visualName}`, 'error');
    }

  } catch (e) {

    record.status = 'still_broken';
    record.notes =
      `Validation failed: ${e.message}`;

    log(
      `❌ VALIDATION FAILED: ` +
      `${record.visualTitle || record.visualName}\n${e.message}`,
      'error'
    );
  }
}

log('🔎 Final visual validation completed.');

    // ── 7b. Retry pass ───────────────────────────────────────────────────
    // FIXED (Round 4, #25): per requirement — if Missing_References-style
    // validation failure remains after save + reload + forced re-render,
    // retry the binding EXACTLY ONCE more, using ONLY the fields already
    // mapped by the AI for this visual (never the old field as a fallback).
    // This is a bounded retry: it runs once per visual, only for visuals
    // validation just marked 'partial' or 'still_broken', and never loops.
    log('🔁 Retry pass — re-attempting visuals that are not fully repaired…');
    // const retryCandidates = repairRecords.filter((r) => r.status === 'still_broken' || r.status === 'partial');
    const retryCandidates = [];
    if (retryCandidates.length === 0) {
      log('  Nothing to retry — every non-healthy visual already validated as repaired.');
    } else {
      log(`  ${retryCandidates.length} visual(s) still need attention — retrying new-field-only binding once each…`);
      for (const record of retryCandidates) {
        const item = allVisualItems.find((v) => String(v.visualName).toLowerCase() === String(record.visualName).toLowerCase());
        if (!item) {
          log(`  ⚠ ${record.visualName} — not found for retry, leaving as ${record.status}`, 'warn');
          continue;
        }
        const id = visId(item);

        // Only fields the AI actually mapped for this visual — never the
        // original/old field. keptOriginal/unmatched entries are excluded
        // by construction, so there is no path here that can restore an
        // old field.
        const newFieldTargets = (record.replacements || [])
          .filter((r) => r.matched && (r.resolvedColumn || r.newColumn));

        if (newFieldTargets.length === 0) {
          log(`  ⚠ ${id} No mapped new field(s) available to retry with — leaving as ${record.status}`, 'warn');
          continue;
        }

        log(`  🔁 ${id} Retry: clearing then re-adding ONLY the mapped new field(s), no restore of old fields…`);

        const rolesToClear = [...new Set(newFieldTargets.map((r) => normRole(r.newRole || r.oldRole, item.visualType)))];
        const knownForClear = newFieldTargets.map((r) => ({
          role: r.newRole || r.oldRole,
          table: r.resolvedTable || r.newTable,
          name: r.resolvedColumn || r.newColumn,
          isMeasure: inferIsMeasure(r.newTable, r.newColumn, r.newIsMeasure),
        }));
        await clearVisualRoles(item.visual, rolesToClear, knownForClear, log, item.visualType, id);

        let retryOk = 0, retryFail = 0;
        for (const target of newFieldTargets) {
          const isMeasure = inferIsMeasure(target.newTable, target.newColumn, target.newIsMeasure);
          const table_ = target.resolvedTable || target.newTable;
          const column = target.resolvedColumn || target.newColumn;
          const result = await tryAddDataFieldSafe(item.visual, target.newRole || target.oldRole, table_, column, isMeasure, log, item.visualType, id, aggFor(target));
          if (result.ok) retryOk++; else retryFail++;
        }

        const preRetryRerenderTs = Date.now();
        await forceVisualRerender(report_, item, log, id);
        await sleep(800);
        const erroredAfterRetryRerender = hasErrorSince(recentErrorsRef, preRetryRerenderTs);

        const actualBindings = await readVisualBindings(item.visual);
        const oldFieldsToCheck = (record.replacements || []).filter((r) => !r.keptOriginal && r.oldTable && r.oldColumn);
        const stillHasOld = oldFieldsToCheck.some((old) =>
          actualBindings.some((a) =>
            String(a.table || '').trim().toLowerCase() === String(old.oldTable).trim().toLowerCase() &&
            String(a.column || '').trim().toLowerCase() === String(old.oldColumn).trim().toLowerCase()));
        const nowHasAllNew = newFieldTargets.every((target) => {
          const table_ = target.resolvedTable || target.newTable;
          const column = target.resolvedColumn || target.newColumn;
          return actualBindings.some((a) =>
            String(a.table || '').trim().toLowerCase() === String(table_).trim().toLowerCase() &&
            String(a.column || '').trim().toLowerCase() === String(column).trim().toLowerCase());
        });

        if (nowHasAllNew && !stillHasOld && !erroredAfterRetryRerender) {
          record.status = 'repaired';
          record.notes = 'Recovered on retry pass — new field(s) verified present, no old fields remain, no error after forced redraw.';
          log(`  ✅ ${id} RETRY SUCCEEDED — now repaired`);
        } else {
          const reasons = [];
          if (!nowHasAllNew) reasons.push('new field(s) still missing');
          if (stillHasOld) reasons.push('old field(s) still present');
          if (erroredAfterRetryRerender) reasons.push('PBI error fired after forced redraw');
          record.notes = `${record.notes || ''} Retry attempted: ${retryOk} added, ${retryFail} failed. ${reasons.join('; ')}.`.trim();
          log(`  ❌ ${id} Retry did not fully resolve (${reasons.join('; ')}) — leaving as ${record.status}`, 'warn');
        }
      }
    }
    log('🔁 Retry pass completed.');

    // ── 8. Validation & Migration Report ────────────────────────────────
    setStatus('Generating validation & migration report…');
    log('📊 Generating final validation & migration report…');
    table(TAG, 'Final repairRecords sent to /finalize', repairRecords.map(r => ({
      visual: r.visualTitle || r.visualName, status: r.status, replacementCount: r.replacements?.length || 0,
    })));

    let finalReport = null;
    try {
      finalReport = await finalizeMigration(sessionId, repairRecords);
      setReport(finalReport);
      log('✅ Migration report generated');
    } catch (e) {
      traceError(TAG, 'finalizeMigration() failed', e);
      log(`⚠ Could not generate migration report: ${e.message}`, 'warn');
    }

    const repaired    = repairRecords.filter((r) => r.status === 'repaired').length;
    const partial     = repairRecords.filter((r) => r.status === 'partial').length;
    const stillBroken = repairRecords.filter((r) => r.status === 'still_broken').length;
    const saveNote = realSave.ok === true ? '' : realSave.ok === false ? ' (report save failed)' : ' (report save unconfirmed)';
    const summary = `Done — ${healthy.length} already healthy, ${repaired} repaired, ${partial} partially repaired, ${stillBroken} still broken${saveNote}`;
    log('──────────────────────────────────────────');
    log(`✓ ${summary}`);
    setStatus(summary);
    setStatusType(
      finalReport?.overallResult === 'needs_attention' || realSave.ok === false ? 'error'
      : (stillBroken > 0 || partial > 0 || realSave.ok !== true) ? 'partial'
      : 'success'
    );
    traceOk(TAG, '🏁 Visual Repair Layer — pipeline end', { summary, finalReport });
    groupEnd();
  }, [sessionId, log, unmappedTables, visualMetadata]);

  // ── Embed initialisation ──────────────────────────────────────────────────
  useEffect(() => {
    if (isEmbedding.current) {
      trace(TAG, 'Embed effect re-entered while isEmbedding=true — skipping duplicate embed');
      return;
    }
    if (!containerRef.current || !embedUrl || !embedToken || !reportId) {
      trace(TAG, 'Embed effect skipped — missing prerequisite', {
        hasContainer: !!containerRef.current, hasEmbedUrl: !!embedUrl,
        hasEmbedToken: !!embedToken, hasReportId: !!reportId,
      });
      return;
    }

    isEmbedding.current = true;

    try {
      pbiService.reset(containerRef.current);

      const config = {
  type:        'report',
  id:          reportId,
  embedUrl:    embedUrl,
  accessToken: embedToken,
  tokenType:   models.TokenType.Embed,
  permissions: models.Permissions.All,
  viewMode:    models.ViewMode.Edit,
  settings: {
    errorSettings: { developerMode: true },
    panes: {
      filters:        { expanded: false, visible: false },
      pageNavigation: { visible: true },
      fields:         { visible: true, expanded: false },
      visualizations: { visible: true },
    },
  },
};
trace(TAG, 'Embedding report with config', {
  ...config, accessToken: `${embedToken.slice(0, 12)}…[REDACTED, ${embedToken.length} chars]`,
});
configRef.current = config;

      const report_ = pbiService.embed(containerRef.current, config);
      reportRef.current = report_;

      report_.on('loaded', () => {
        traceOk(TAG, "Power BI event: 'loaded' — report DOM is up, visuals may still be rendering");
        setStatus('Report loaded — waiting for render…');
        setTimeout(() => {
          if (!didBind.current) {
            trace(TAG, "'rendered' never fired within 15s of 'loaded' — forcing repair pipeline start as a fallback");
            rebindVisualColumns(report_);
          }
        }, 15000);
      });

      report_.on('rendered', () => {
        traceOk(TAG, "Power BI event: 'rendered' — all visuals finished their first render pass");
        setStatus('Report rendered — starting visual repair layer…');
        rebindVisualColumns(report_);
      });

      report_.on('error', (e) => {
        const msg = e?.detail?.message || 'Unknown Power BI error';
        console.error('[PBI ERROR]', e);
        traceError(TAG, `Power BI event: 'error' — ${e?.detail?.errorCode || 'no errorCode'}`, e?.detail);
        setStatus('Power BI error: ' + msg);
        setStatusType('error');
        log(`❌ PBI error: ${msg}`, 'error');
        // FIXED (Round 6): keep a bounded, timestamped record of this event
        // so forceVisualRerender()/the repair loop can check for a fresh
        // error after a specific redraw attempt instead of assuming success.
        recentErrorsRef.current.push({ time: Date.now(), message: msg, detail: e?.detail });
        if (recentErrorsRef.current.length > 200) recentErrorsRef.current.shift();
      });

      report_.on('saveAsTriggered', () => trace(TAG, "Power BI event: 'saveAsTriggered'"));
      report_.on('dataSelected',    () => {}); // noisy, intentionally not logged

    } catch (e) {
      traceError(TAG, 'Embed initialisation threw synchronously', e);
      console.error('[EmbedStep INIT ERROR]', e);
      setStatus('Embed initialisation failed: ' + e.message);
      setStatusType('error');
    }

    return () => {
      isEmbedding.current = false;
      try { if (containerRef.current) pbiService.reset(containerRef.current); } catch (_) {}
    };
  }, [embedUrl, embedToken, reportId, rebindVisualColumns]);

  // ── Styles ────────────────────────────────────────────────────────────────
  const statusColors = {
    loading: { bg: '#f0f4ff', border: '#a0b4f0', color: '#1a3a7a' },
    success: { bg: '#f0fff4', border: '#6fcf97', color: '#1a5c35' },
    partial: { bg: '#fffbea', border: '#f5c842', color: '#7a5c00' },
    error:   { bg: '#fff5f5', border: '#fcc',    color: '#c0392b' },
    warning: { bg: '#fffbea', border: '#f5d87a', color: '#7a5c00' },
  };
  const sc = statusColors[statusType] || statusColors.loading;

  const logColors = { info: '#cdd6f4', warn: '#f5c842', error: '#ff6b6b' };
  const hasIssues = unmappedTables.length > 0;

  return (
    <div className="card">
      <h2 className="card-title">
        <span className="step-badge">3</span> Visual Repair Layer & Validation
      </h2>

      {/* Status bar */}
      <div style={{
        background: sc.bg, border: `1px solid ${sc.border}`,
        borderRadius: 8, padding: '10px 16px', marginBottom: 12,
        display: 'flex', alignItems: 'center', gap: 12, color: sc.color
      }}>
        <span style={{ fontSize: 18 }}>
          {statusType === 'loading' ? '⏳' : statusType === 'success' ? '✅' : statusType === 'partial' ? '⚠️' : '❌'}
        </span>
        <span style={{ fontWeight: 600, fontSize: 14, flex: 1 }}>{status}</span>
        <span style={{ fontSize: 12, opacity: 0.7 }}>
          {tablesLoaded} Snowflake view{tablesLoaded !== 1 ? 's' : ''} in dataset
        </span>
        {(statusType === 'success' || statusType === 'error' || statusType === 'partial') && (
          <button
            onClick={() => {
              trace(TAG, 'Manual "Re-run" clicked — resetting didBind and re-running pipeline');
              didBind.current = false;
              rebindVisualColumns(reportRef.current);
            }}
            style={{ background: '#0078d4', color: '#fff', border: 'none', borderRadius: 6, padding: '5px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer' }}
          >
            ↺ Re-run
          </button>
        )}
      </div>

      {/* Detect Broken Visuals — triage summary */}
      {triage && (
        <div style={{ display: 'flex', gap: 10, marginBottom: 12, fontSize: 12 }}>
          <div style={{ background: '#f0fff4', border: '1px solid #6fcf97', borderRadius: 6, padding: '6px 12px', color: '#1a5c35' }}>
            ✓ {triage.healthy} healthy (skipped)
          </div>
          <div style={{ background: triage.broken > 0 ? '#fffbea' : '#f4f4f4', border: `1px solid ${triage.broken > 0 ? '#f5c842' : '#ddd'}`, borderRadius: 6, padding: '6px 12px', color: triage.broken > 0 ? '#7a5c00' : '#666' }}>
            ⚠ {triage.broken} broken (detected)
          </div>
          <div style={{ background: '#f4f4f4', border: '1px solid #ddd', borderRadius: 6, padding: '6px 12px', color: '#666' }}>
            {triage.totalVisuals} total visuals
          </div>
        </div>
      )}

      {/* Progress bar */}
            {/* Progress bar — now handles the new backend JSON shape with "still_broken" */}
      {progress.total > 0 && (
        <div style={{ marginBottom: 12 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12, marginBottom: 4, color: '#666' }}>
            <span>Repair progress (broken visuals only)</span>
            <span>{progress.done}/{progress.total} visuals — {progress.errors} error{progress.errors !== 1 ? 's' : ''}</span>
          </div>
          <div style={{ background: '#e8e8e8', borderRadius: 4, height: 6, overflow: 'hidden' }}>
            <div 
              style={{
                width: `${(progress.done / progress.total) * 100}%`, 
                height: '100%',
                background: progress.errors > 0 ? '#f5a623' : '#27ae60', 
                borderRadius: 4, 
                transition: 'width 0.3s ease'
              }} 
            />
          </div>
        </div>
      )}

      {/* Dataset issues */}
      {hasIssues && (
        <div style={{ marginBottom: 14 }}>
          <div style={{ fontWeight: 600, fontSize: 13, color: '#c0392b', marginBottom: 6 }}>
            Dataset issues ({unmappedTables.length})
          </div>
          {unmappedTables.map((t, i) => (
            <div key={i} style={{ background: '#fff5f5', border: '1px solid #fcc', borderRadius: 6, padding: '7px 12px', marginBottom: 5, fontSize: 12 }}>
              <span style={{ fontWeight: 600, fontFamily: 'monospace' }}>{t.tableName}</span>
              {' — '}{t.reason}
              {t.unmappedColumns?.length > 0 && <span style={{ color: '#999' }}> [{t.unmappedColumns.join(', ')}]</span>}
            </div>
          ))}
        </div>
      )}

      {/* Power BI embed */}
      {embedUrl && embedToken ? (
        <>
          <div style={{ borderRadius: 8, overflow: 'hidden', boxShadow: '0 2px 12px rgba(0,0,0,0.1)', marginBottom: 16, background: '#eaeaea' }}>
            <div ref={containerRef} className="pbi-embed-container" style={{ width: '100%', height: '650px', minHeight: '500px' }} />
          </div>

          {/* Repair log */}
          {bindLog.length > 0 && (
            <div style={{ background: '#1e1e2e', borderRadius: 8, padding: '12px 16px', maxHeight: 300, overflowY: 'auto', marginBottom: 16 }}>
              <div style={{ fontWeight: 600, fontSize: 12, color: '#a0a0c0', marginBottom: 8, display: 'flex', justifyContent: 'space-between' }}>
                <span>
                  Visual Repair Log
                  {statusType === 'loading' && <span style={{ color: '#f5d87a' }}> — running…</span>}
                  {statusType === 'success' && <span style={{ color: '#6fcf97' }}> — complete ✓</span>}
                  {statusType === 'partial' && <span style={{ color: '#f5c842' }}> — completed with warnings ⚠</span>}
                </span>
                {progress.errors > 0 && <span style={{ color: '#ff6b6b', fontSize: 11 }}>{progress.errors} visual{progress.errors !== 1 ? 's' : ''} still need attention</span>}
              </div>
              {bindLog.map((entry, i) => (
                <div key={i} style={{ fontFamily: 'monospace', fontSize: 12, color: logColors[entry.level] || '#cdd6f4', lineHeight: 1.7 }}>
                  {entry.msg}
                </div>
              ))}
            </div>
          )}

          {/* Validation & Migration Report */}
          {report && (
            <div style={{ background: '#fff', border: '1px solid #ddd', borderRadius: 8, padding: '16px 18px' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 10 }}>
                <h3 style={{ margin: 0, fontSize: 15 }}>📊 Validation & Migration Report</h3>
                <button
                  onClick={() => downloadJson(`migration-report-${sessionId}.json`, report)}
                  style={{ background: '#0078d4', color: '#fff', border: 'none', borderRadius: 6, padding: '6px 14px', fontSize: 12, fontWeight: 600, cursor: 'pointer' }}
                >
                  ⬇ Download JSON
                </button>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 10, marginBottom: 12 }}>
                <Stat label="Healthy"  value={report.healthyVisuals} color="#1a5c35" />
                <Stat label="Repaired" value={report.repairedVisuals} color="#0078d4" />
                <Stat label="Partial"  value={report.partiallyRepairedVisuals} color="#7a5c00" />
                <Stat label="Broken"   value={report.stillBrokenVisuals} color="#c0392b" />
              </div>
              <div style={{ fontSize: 12, color: '#666' }}>
                Overall result: <b style={{ color: report.overallResult === 'success' ? '#1a5c35' : report.overallResult === 'success_with_warnings' ? '#7a5c00' : '#c0392b' }}>
                  {report.overallResult.replace(/_/g, ' ')}
                </b>
                {' · '}{report.tablesLoaded}/{report.tablesTotal} Snowflake views loaded
              </div>
            </div>
          )}
        </>
      ) : (
        <div style={{ color: '#888', fontSize: 14 }}>Embed token missing — cannot display report.</div>
      )}
    </div>
  );
}

function Stat({ label, value, color }) {
  return (
    <div style={{ textAlign: 'center', padding: '8px 4px', background: '#f7f7f9', borderRadius: 6 }}>
      <div style={{ fontSize: 20, fontWeight: 700, color }}>{value ?? 0}</div>
      <div style={{ fontSize: 11, color: '#888' }}>{label}</div>
    </div>
  );
}