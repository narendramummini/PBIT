import { useEffect, useRef, useState } from "react";
import { AlertCircle, CheckCircle2, Loader2, RefreshCw, Wrench } from "lucide-react";
import { Button } from "@/components/ui/button";
import { finalizeMigration, migrationReport, repairVisuals, viewTables } from "@/lib/migration-api";

type Binding = { table: string; column: string; dataRole: string; aggregation?: string; isMeasure?: boolean };
type Repair = { visualName: string; visualTitle: string; visualType: string; pageName: string; status: string; originalBindings: Binding[]; replacements: any[]; notes?: string };

const norm = (x: unknown) => String(x ?? "").trim().toLowerCase();
const repairKey = (x: Pick<Repair, "pageName" | "visualName">) => `${norm(x.pageName)}::${norm(x.visualName)}`;

export function VisualRepair({ sessionId, embed, onFinalized }: { sessionId: string; embed: { embedUrl?: string; embedToken?: string; reportId?: string }; onFinalized?: (report: any) => void }) {
  const host = useRef<HTMLDivElement>(null);
  const reportRef = useRef<any>(null);
  const sdkRef = useRef<any>(null);
  const didScan = useRef(false);
  const [stage, setStage] = useState<"loading" | "review" | "applying" | "ready" | "finalizing" | "done" | "error">("loading");
  const [message, setMessage] = useState("Loading report for visual inspection…");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [repairs, setRepairs] = useState<Repair[]>([]);
  const [approved, setApproved] = useState<Record<string, boolean>>({});
  const [report, setReport] = useState<any>(null);
  const [embedInfo, setEmbedInfo] = useState(embed);
  const [progress, setProgress] = useState("");

  useEffect(() => {
    const el = host.current;
    if (!el) return;
    let cancelled = false;
    let embedded: any;
    void Promise.all([import("powerbi-client"), import("powerbi-report-authoring"), viewTables(sessionId)]).then(([powerbi, _authoring, info]) => {
      if (cancelled) return;
      const pbi = new powerbi.service.Service(powerbi.factories.hpmFactory, powerbi.factories.wpmpFactory, powerbi.factories.routerFactory);
      sdkRef.current = { pbi, models: powerbi.models };
      const recovered = { embedUrl: embed.embedUrl || info.embedUrl, embedToken: embed.embedToken || info.embedToken, reportId: embed.reportId || info.reportId };
      setEmbedInfo(recovered);
      if (!recovered.embedUrl || !recovered.embedToken || !recovered.reportId) throw new Error("The migration service returned incomplete Power BI embed information");
      embedded = pbi.embed(el, {
        type: "report", id: recovered.reportId, embedUrl: recovered.embedUrl,
        accessToken: recovered.embedToken, tokenType: powerbi.models.TokenType.Embed,
        permissions: powerbi.models.Permissions.ReadWrite,
        settings: { panes: { filters: { visible: false }, pageNavigation: { visible: true } } },
      });
      reportRef.current = embedded;
      embedded.on("loaded", () => { if (!didScan.current) void scan(embedded); });
      embedded.on("error", (event: any) => setError(event?.detail?.message ?? "Power BI report reported an error"));
    }).catch((e) => { if (!cancelled) { setError(e instanceof Error ? e.message : "Could not load report embed information"); setStage("error"); } });
    return () => { cancelled = true; if (embedded) sdkRef.current?.pbi.reset(el); reportRef.current = null; };
  }, [sessionId, embed.embedUrl, embed.embedToken, embed.reportId]);

  async function scan(reportApi: any) {
    didScan.current = true;
    setBusy(true); setError(""); setMessage("Reading report pages and visual bindings…");
    try {
      const [pages, metadata] = await Promise.all([reportApi.getPages(), viewTables(sessionId)]);
      const stored: any[] = metadata.visualMetadata ?? [];
      const availableColumns = new Map<string, Set<string>>(Object.entries(metadata.views ?? {}).map(([table, columns]: [string, any]) => [norm(table), new Set((columns as string[]).map(norm))]));
      const items: any[] = [];
      const inspectionFailures: Repair[] = [];
      for (const page of pages) {
        let visuals: any[];
        try { visuals = await page.getVisuals(); }
        catch (e) {
          const pageName = page.displayName || page.name;
          for (const meta of stored.filter((x) => norm(x.pageName) === norm(pageName))) inspectionFailures.push({ visualName: meta.visualName, visualTitle: meta.visualTitle, visualType: meta.visualType, pageName, status: "still_broken", originalBindings: meta.boundColumns ?? [], replacements: [], notes: `Visual inspection is unsupported on this page: ${e instanceof Error ? e.message : "unknown Power BI authoring error"}` });
          setMessage(`Could not inspect visuals on page ${pageName}: ${e instanceof Error ? e.message : "unsupported page"}`); continue;
        }
        for (const visual of visuals) {
          const meta = stored.find((x) => norm(x.visualName) === norm(visual.name) && (!x.pageName || norm(x.pageName) === norm(page.displayName || page.name)));
          const sourceBindings: Binding[] = (meta?.boundColumns ?? []).map((b: any) => ({ table: b.table, column: b.column, dataRole: b.dataRole || "Values", aggregation: b.aggregation, isMeasure: b.isMeasure }));
          let liveBindings: Binding[] = [];
          let unsupported = false;
          for (const role of [...new Set(sourceBindings.map((b) => b.dataRole).filter(Boolean))]) {
            const fields = await visual.getDataFields(role).catch(() => { unsupported = true; return []; });
            liveBindings.push(...fields.map((f: any) => ({ table: f.table, column: f.column, dataRole: role, aggregation: f.aggregationFunction } )));
          }
          const bindings = liveBindings.length ? liveBindings : sourceBindings;
          const broken = unsupported || bindings.some((b) => !availableColumns.get(norm(b.table))?.has(norm(b.column))) || (liveBindings.length === 0 && sourceBindings.length > 0);
          items.push({ page, visual, visualName: visual.name, visualTitle: visual.title || meta?.visualTitle || "", visualType: visual.type || meta?.visualType || "", pageName: page.displayName || page.name, bindings, broken, unsupported });
        }
      }
      const candidates = items.filter((x) => x.broken);
      const healthy: Repair[] = items.filter((x) => !x.broken).map((x) => ({ visualName: x.visualName, visualTitle: x.visualTitle, visualType: x.visualType, pageName: x.pageName, status: "healthy", originalBindings: x.bindings, replacements: [] }));
      setMessage(`${items.length} visuals inspected: ${healthy.length} healthy, ${candidates.length} need review.`);
      if (!candidates.length) { setRepairs([...healthy, ...inspectionFailures]); setStage("ready"); return; }
      setMessage(`Requesting repair suggestions for ${candidates.length} broken visuals…`);
      const suggestions = await repairVisuals(sessionId, candidates.map((x) => ({ visualName: x.visualName, visualTitle: x.visualTitle, visualType: x.visualType, currentBindings: x.bindings })));
      const queues = new Map<string, any[]>();
      for (const result of suggestions.results ?? []) { const key = norm(result.visualName); queues.set(key, [...(queues.get(key) ?? []), result]); }
      const proposed = candidates.map((x) => {
        const result = queues.get(norm(x.visualName))?.shift();
        return { visualName: x.visualName, visualTitle: x.visualTitle, visualType: x.visualType, pageName: x.pageName, status: "still_broken", originalBindings: x.bindings, replacements: result?.replacements ?? [], notes: result?.errorReason } as Repair;
      });
      const next = [...healthy, ...proposed, ...inspectionFailures];
      setRepairs(next);
      setApproved(Object.fromEntries(proposed.map((x) => [repairKey(x), false])));
      setStage("review");
    } catch (e) { didScan.current = false; setError(e instanceof Error ? e.message : "Could not inspect or analyze report visuals"); setStage("error"); }
    finally { setBusy(false); }
  }

  async function applyApproved() {
    const reportApi = reportRef.current;
    if (!reportApi) return;
    setBusy(true); setStage("applying"); setError("");
    const pages = await reportApi.getPages();
    const outcomes = [...repairs];
    const targets = outcomes.filter((x) => (x.status === "still_broken" || x.status === "partial") && approved[repairKey(x)]);
    let done = 0;
    for (const record of targets) {
      setProgress(`Applying ${++done} of ${targets.length}: ${record.visualTitle || record.visualName}`);
      try {
        const page = pages.find((p: any) => norm(p.displayName || p.name) === norm(record.pageName));
        const visual = (await page?.getVisuals())?.find((v: any) => v.name === record.visualName);
        if (!visual) throw new Error("Visual no longer exists in the embedded report");
        const replacements = record.replacements.filter((r) => r.matched && r.newTable && r.newColumn);
        if (!replacements.length) throw new Error(record.notes || "No valid replacement fields were returned");
        await reportApi.switchMode(sdkRef.current.models.ViewMode.Edit);
        for (const role of [...new Set(record.originalBindings.map((b) => b.dataRole || "Values"))]) {
          const fields = await visual.getDataFields(role).catch(() => []);
          for (let i = fields.length - 1; i >= 0; i--) await visual.removeDataField(role, i);
        }
        for (const r of replacements) {
          const role = r.newRole || r.oldRole || "Values";
          const field = r.newIsMeasure ? { $schema: "http://powerbi.com/product/schema#measure", table: r.newTable, measure: r.newColumn } : { $schema: "http://powerbi.com/product/schema#column", table: r.newTable, column: r.newColumn, ...(r.aggregation ? { aggregationFunction: r.aggregation } : {}) };
          await visual.addDataField(role, field);
        }
        record.status = replacements.length === record.originalBindings.length ? "repaired" : "partial";
        record.notes = record.status === "repaired" ? "Approved replacements applied through the Power BI authoring API." : `Applied ${replacements.length} of ${record.originalBindings.length} possible field replacements; unmatched fields remain unresolved.`;
      } catch (e) { record.status = "still_broken"; record.notes = e instanceof Error ? e.message : "Repair failed"; }
      setRepairs([...outcomes]);
    }
    try { await reportApi.refresh(); } catch { /* status is verified against field bindings below */ }
    try { await reportApi.save(); } catch (e) { setError(`Power BI save failed: ${e instanceof Error ? e.message : "unknown error"}`); }
    for (const record of outcomes.filter((x) => x.status === "repaired" || x.status === "partial")) {
      try {
        const page = pages.find((p: any) => norm(p.displayName || p.name) === norm(record.pageName));
        const visual = (await page?.getVisuals())?.find((v: any) => v.name === record.visualName);
        const fields: any[] = [];
        for (const role of [...new Set(record.replacements.map((r) => r.newRole || r.oldRole || "Values"))]) fields.push(...await visual.getDataFields(role).catch(() => []));
        if (record.replacements.some((r) => r.matched && !fields.some((f) => norm(f.table) === norm(r.newTable) && norm(f.column || f.measure) === norm(r.newColumn)))) { record.status = "partial"; record.notes = "Power BI did not retain every proposed binding after refresh."; }
      } catch (e) { record.status = "partial"; record.notes = `Could not verify updated bindings: ${e instanceof Error ? e.message : "unknown error"}`; }
    }
    setRepairs([...outcomes]);
    setApproved((current) => ({ ...current, ...Object.fromEntries(targets.map((r) => [repairKey(r), false])) }));
    setProgress(""); setStage("ready"); setBusy(false);
  }

  async function finalize() {
    setBusy(true); setStage("finalizing"); setError(""); setMessage("Checking migration and visual repair summaries, then finalizing…");
    try {
      const final = await finalizeMigration(sessionId, repairs);
      const authoritative = await migrationReport(sessionId).catch(() => final);
      setReport(authoritative); onFinalized?.(authoritative); setStage("done"); setMessage("Migration finalized. Final report retrieved from the migration service.");
    } catch (e) { setError(e instanceof Error ? e.message : "Finalization failed. Retry is available."); setStage("ready"); }
    finally { setBusy(false); }
  }

  const pending = repairs.filter((r) => r.status === "still_broken" || r.status === "partial").length;
  return <section className="mt-6 rounded-lg border bg-card overflow-hidden">
    <div className="p-4 border-b flex items-center gap-2 font-semibold"><Wrench className="size-4"/> Visual repair and migration finalization</div>
    {sessionId && <div ref={host} className="h-[560px] w-full"/>}
    <div className="p-5 space-y-4">
      <p className="text-sm text-muted-foreground">{message}</p>
      {busy && <div className="flex items-center gap-2 text-sm"><Loader2 className="size-4 animate-spin"/>{progress || message}</div>}
      {!!error && <div role="alert" className="rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">{error}<Button className="ml-3" size="sm" variant="outline" onClick={() => { setError(""); if (reportRef.current) { didScan.current = false; void scan(reportRef.current); } else window.location.reload(); }}>Retry</Button></div>}
      {stage === "review" && <div className="space-y-3"><h3 className="font-medium">Review proposed repairs</h3>{repairs.filter((r) => r.status === "still_broken" || r.status === "partial").map((r) => <label key={repairKey(r)} className="block rounded-md border p-3 text-sm"><span className="flex items-start gap-2"><input type="checkbox" checked={!!approved[repairKey(r)]} onChange={(e) => setApproved((v) => ({ ...v, [repairKey(r)]: e.target.checked }))} disabled={!r.replacements.some((m) => m.matched && m.newTable && m.newColumn)}/><span><strong>{r.visualTitle || r.visualName}</strong> · {r.pageName}<span className="mt-2 block space-y-1">{r.replacements.length ? r.replacements.map((m, i) => <span key={i} className="block text-muted-foreground">{m.oldTable}.{m.oldColumn} → {m.matched ? `${m.newTable}.${m.newColumn}` : "No matching replacement"} ({Math.round((m.confidence ?? 0) * 100)}% confidence){m.matchNote ? ` · ${m.matchNote}` : ""}</span>) : <span className="text-destructive">{r.notes || "No suggestions returned"}</span>}</span></span></span></label>)}{repairs.some((r) => (r.status === "still_broken" || r.status === "partial") && r.replacements.some((m) => m.matched && m.newTable && m.newColumn)) && <Button onClick={() => void applyApproved()} disabled={busy || !Object.values(approved).some(Boolean)}>Apply approved repairs</Button>}<Button variant="outline" onClick={() => setStage("ready")}>Continue with unresolved visuals</Button><span className="ml-3 text-xs text-muted-foreground">Suggestions are never applied until you approve them.</span></div>}
      {stage === "ready" && <div className="rounded-md border p-4"><div className="flex items-center gap-2 font-medium">{pending ? <AlertCircle className="size-4 text-amber-600"/> : <CheckCircle2 className="size-4 text-green-600"/>}{repairs.length - pending} ready · {pending} pending repair(s)</div><div className="mt-3 space-y-2">{repairs.map((r) => <div key={`${r.pageName}/${r.visualName}`} className="flex justify-between gap-4 text-sm"><span>{r.visualTitle || r.visualName} <span className="text-muted-foreground">· {r.pageName}</span>{r.notes && <span className="block text-xs text-muted-foreground">{r.notes}</span>}</span><span>{r.status.replaceAll("_", " ")}</span></div>)}</div><div className="mt-4 flex gap-2"><Button variant="outline" disabled={busy} onClick={() => { didScan.current = false; void scan(reportRef.current); }}><RefreshCw className="size-4"/> Recheck visuals</Button>{pending > 0 && <Button variant="outline" onClick={() => setStage("review")}>Review pending suggestions</Button>}<Button disabled={busy} onClick={() => void finalize()}>Finalize migration</Button></div><p className="mt-2 text-xs text-muted-foreground">Finalization records pending visuals and keeps original model tables when broken visuals remain. You can retry after repair.</p></div>}
      {stage === "done" && report && <div className="rounded-md border p-4"><h3 className="font-semibold">{report.overallResult?.replaceAll("_", " ") ?? "Migration finalized"}</h3><p className="mt-1 text-sm text-muted-foreground">{report.healthyVisuals ?? 0} healthy · {report.repairedVisuals ?? 0} repaired · {report.partiallyRepairedVisuals ?? 0} partial · {report.stillBrokenVisuals ?? 0} still broken</p><p className="mt-1 text-xs text-muted-foreground">Report {report.reportId || embed.reportId} · Dataset {report.datasetName || report.datasetId || "—"}</p><Button className="mt-3" variant="outline" onClick={() => void finalize()}>Retry finalization</Button></div>}
      {stage === "loading" && !embedInfo.embedUrl && <p className="text-sm text-muted-foreground">Recovering Power BI embed information from the migration session…</p>}
    </div>
  </section>;
}
