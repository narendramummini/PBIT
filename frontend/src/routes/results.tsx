import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { CheckCircle2, Download, RotateCcw, Loader2, AlertCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { AppShell } from "@/components/app-shell";
import { setState, useConversionState } from "@/lib/conversion-store";
import { migrationReport } from "@/lib/migration-api";
import { VisualRepair } from "@/components/visual-repair";

export const Route = createFileRoute("/results")({ component: ResultsPage });

function ResultsPage() {
  const navigate = useNavigate();
  const state = useConversionState();
  const [report, setReport] = useState<any>(state.migrationReport ?? null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const sessionId = state.migrationSessionId;
  useEffect(() => {
    if (!sessionId) return;
    setLoading(true);
    migrationReport(sessionId).then((value) => { setReport(value); setState({ migrationReport: value }); })
      .catch(() => setReport(null))
      .finally(() => setLoading(false));
  }, [sessionId]);
  const download = () => {
    if (!report) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], { type: "application/json" }));
    const link = document.createElement("a"); link.href = url; link.download = `migration-${sessionId}.json`; link.click(); URL.revokeObjectURL(url);
  };
  const reset = () => { sessionStorage.removeItem("pbix-converter-state"); navigate({ to: "/workspaces" }); };

  return <AppShell step={3}><div className="mx-auto max-w-[1400px] px-6 py-8">
    <div className="rounded-xl border bg-card p-6 flex items-center gap-4 flex-wrap"><div className="size-12 rounded-full bg-primary/10 text-primary grid place-items-center shrink-0">{loading ? <Loader2 className="size-6 animate-spin"/> : report ? <CheckCircle2 className="size-6"/> : <AlertCircle className="size-6"/>}</div><div className="flex-1 min-w-[260px]"><div className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Backend migration report</div><h1 className="text-2xl font-semibold tracking-tight mt-0.5">{state.selectedReportName ?? "Migration results"}</h1><p className="text-sm text-muted-foreground mt-1">{report?.overallResult ?? error ?? "Results are read from the migration service."}</p></div><div className="flex gap-2"><Button variant="outline" onClick={reset}><RotateCcw className="size-4"/> Start new migration</Button><Button variant="outline" onClick={download} disabled={!report}><Download className="size-4"/> Download JSON</Button></div></div>
    {error && <div role="alert" className="mt-5 rounded-lg border border-destructive/30 bg-destructive/5 p-4 text-sm text-destructive">{error}</div>}
    {report && <><div className="mt-6 grid sm:grid-cols-2 lg:grid-cols-4 gap-3">{[["Tables loaded", `${report.tablesLoaded ?? 0} / ${report.tablesTotal ?? 0}`],["Healthy visuals", report.healthyVisuals ?? 0],["Repaired visuals", report.repairedVisuals ?? 0],["Still broken", (report.partiallyRepairedVisuals ?? 0) + (report.stillBrokenVisuals ?? 0)]].map(([label, value]) => <div key={String(label)} className="rounded-lg border bg-card p-4"><div className="text-xs text-muted-foreground">{label}</div><div className="mt-1 text-2xl font-semibold">{value}</div></div>)}</div>
      <div className="mt-6 rounded-lg border bg-card"><div className="p-4 border-b font-semibold">Visual validation details</div><div className="divide-y">{(report.visualDetails ?? []).map((v: any, i: number) => <div key={`${v.visualName}-${i}`} className="p-4 flex items-start justify-between gap-4"><div><div className="font-medium">{v.visualTitle || v.visualName}</div><div className="text-xs text-muted-foreground">{v.pageName} · {v.visualType}</div>{v.notes && <p className="mt-1 text-sm text-muted-foreground">{v.notes}</p>}</div><span className="text-xs rounded-full bg-muted px-2 py-1">{v.status}</span></div>)}</div></div>
      {report.unmappedTables?.length > 0 && <div className="mt-6 rounded-lg border bg-card p-5"><h2 className="font-semibold">Unmapped tables</h2><ul className="mt-3 space-y-2 text-sm">{report.unmappedTables.map((t: any) => <li key={t.tableName}><strong>{t.tableName}</strong>: {t.reason}</li>)}</ul></div>}</>}
    {sessionId && <VisualRepair sessionId={sessionId} embed={state.embed ?? {}} onFinalized={(value) => { setReport(value); setError(""); setState({ migrationReport: value }); }}/>}
    {!report && !loading && <div className="mt-6 rounded-lg border bg-card p-5 text-sm text-muted-foreground">The migration has not produced its final validation report. Complete validation from the embedded report before viewing final results.</div>}
  </div></AppShell>;
}

