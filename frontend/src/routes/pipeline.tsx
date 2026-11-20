import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { CheckCircle2, Loader2, ArrowRight, AlertCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { AppShell } from "@/components/app-shell";
import { setState, useConversionState } from "@/lib/conversion-store";
import { migrationStatus, selectSchemas } from "@/lib/migration-api";

export const Route = createFileRoute("/pipeline")({ component: PipelinePage });

function PipelinePage() {
  const navigate = useNavigate();
  const state = useConversionState();
  const schemas = state.availableSchemas ?? [];
  const [selected, setSelected] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [status, setStatus] = useState<any>(state.migrationStatus ?? { status: "awaiting_schema_selection" });
  const sessionId = state.migrationSessionId;

  useEffect(() => {
    if (!sessionId || ["completed", "validated", "failed", "error"].includes(String(status.status).toLowerCase())) return;
    let active = true;
    const poll = async () => {
      try {
        const latest = await migrationStatus(sessionId);
        if (!active) return;
        setStatus(latest);
        setState({ migrationStatus: latest });
      } catch (e) {
        if (active) setError(e instanceof Error ? e.message : "Unable to check migration status");
      }
    };
    const timer = window.setInterval(poll, 2500);
    void poll();
    return () => { active = false; window.clearInterval(timer); };
  }, [sessionId, status.status]);

  const submit = async () => {
    if (!sessionId || selected.length === 0) return;
    setBusy(true);
    setError("");
    try {
      const result = await selectSchemas(sessionId, selected);
      setStatus(result);
      setState({ migrationStatus: result, embed: { embedUrl: result.embedUrl, embedToken: result.embedToken, reportId: result.reportId, datasetId: result.datasetId } });
    } catch (e) {
      setError(e instanceof Error ? e.message : "Schema selection failed");
    } finally {
      setBusy(false);
    }
  };

  const terminal = ["completed", "validated", "failed", "error"].includes(String(status.status).toLowerCase());
  const progress = status.tablesTotal ? Math.round((status.tablesLoaded / status.tablesTotal) * 100) : terminal ? 100 : status.status === "rebuilding_dataset" ? 55 : status.status === "loading_views" ? 25 : 0;
  const issueList = status.unmappedTables?.length > 0 && <div className="mt-4 text-sm"><h3 className="font-medium">{status.status === "awaiting_schema_selection" ? "Reconciliation details" : "Unmapped tables"}</h3><ul className="list-disc pl-5 mt-2">{status.unmappedTables.map((t: any, i: number) => <li key={`${t.tableName}-${i}`}>{t.tableName}: {t.reason}{t.unmappedColumns?.length ? ` Missing: ${t.unmappedColumns.join(", ")}` : ""}</li>)}</ul></div>;

  return <AppShell step={2}><div className="mx-auto max-w-[1400px] px-6 py-8">
    <div className="mb-6"><h1 className="text-2xl font-semibold tracking-tight">{terminal && status.status !== "failed" ? "Migration ready for validation" : "Migration pipeline"}</h1><p className="text-sm text-muted-foreground mt-1">{state.selectedReportName ?? "Power BI report"} - Session {sessionId?.slice(0, 8) ?? "missing"}</p></div>
    <div className="mb-6"><div className="h-2 bg-muted rounded-full overflow-hidden"><div className="h-full bg-primary transition-all" style={{ width: `${progress}%` }}/></div><div className="mt-2 flex justify-between text-xs text-muted-foreground"><span>{status.status ?? "Initializing"}</span><span>{status.tablesLoaded ?? 0} / {status.tablesTotal ?? "-"} tables</span></div></div>
    {!sessionId ? <div role="alert" className="rounded-lg border p-5">No migration session was created. Return to Workspaces and upload a PBIX file.</div> : status.status === "awaiting_schema_selection" ? <section className="rounded-lg border bg-card p-5"><h2 className="font-semibold">Select Snowflake schemas</h2><p className="text-sm text-muted-foreground mt-1">Choose schemas to load and map into the Power BI model.</p>{status.errorMessage && <div role="alert" className="mt-4 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">{status.errorMessage}</div>}{issueList}<div className="my-5 grid sm:grid-cols-2 lg:grid-cols-3 gap-2">{schemas.map((schema) => <label key={schema} className="flex items-center gap-2 rounded-md border p-3 text-sm"><input type="checkbox" checked={selected.includes(schema)} onChange={() => setSelected((prev) => prev.includes(schema) ? prev.filter((x) => x !== schema) : [...prev, schema])}/>{schema}</label>)}</div>{schemas.length === 0 && <p className="text-sm text-muted-foreground">The API returned no available schemas.</p>}<Button onClick={submit} disabled={busy || selected.length === 0}>{busy && <Loader2 className="size-4 animate-spin"/>}{busy ? "Loading schemas and rebuilding dataset..." : "Load selected schemas"}</Button></section> : <section className="rounded-lg border bg-card p-5"><div className="flex items-center gap-2 font-semibold">{status.status === "failed" || status.status === "error" ? <AlertCircle className="size-5 text-destructive"/> : terminal ? <CheckCircle2 className="size-5 text-success"/> : <Loader2 className="size-5 animate-spin text-primary"/>}{status.status ?? "Working"}</div><p className="mt-2 text-sm text-muted-foreground">{status.errorMessage ?? (terminal ? "The backend migration operation returned." : "Waiting for the migration service. This page checks backend status automatically.")}</p>{issueList}{terminal && status.status !== "failed" && <Button className="mt-5" onClick={() => navigate({ to: "/results" })}>Review results and embedded report <ArrowRight className="size-4"/></Button>}</section>}
    {error && <div role="alert" className="mt-4 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">{error}<button className="ml-3 underline" onClick={() => setError("")}>Dismiss</button></div>}
  </div></AppShell>;
}
