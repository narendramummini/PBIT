import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { Snowflake, Loader2, ArrowRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { AppShell } from "@/components/app-shell";
import { getPendingPbix, getState, setState } from "@/lib/conversion-store";
import { startMigration } from "@/lib/migration-api";
import { acquireFabricAndOneLakeTokens } from "@/lib/microsoft-delegated-auth";

export const Route = createFileRoute("/configure")({ component: ConfigurePage });

function ConfigurePage() {
  const navigate = useNavigate();
  const initialState = getState();
  const [form, setForm] = useState({ account: "", warehouse: "", database: "", role: "", username: "", patToken: "", tenantId: initialState.tenantId ?? "", clientId: initialState.clientId ?? "", azureOpenAIEndpoint: "", azureOpenAIDeployment: "gpt-4o", xmlaEndpoint: "" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const update = (key: keyof typeof form, value: string) => setForm((current) => ({ ...current, [key]: value }));
  const start = async () => {
    const file = getPendingPbix();
    const state = getState();
    if (!file) { setError("Choose a PBIX file on the workspaces page first."); return; }
    if (!form.account || !form.username || !form.patToken || !form.tenantId || !form.clientId || !state.selectedWorkspaceId) {
      setError("Complete the required Snowflake and Power BI fields, and select a workspace."); return;
    }
    if (!state.apiAccessToken) { setError("Your Microsoft sign-in has expired. Sign in again before starting the migration."); return; }
    setBusy(true); setError("");
    try {
      const { fabricAccessToken, oneLakeAccessToken, lakehouseSqlAccessToken } = await acquireFabricAndOneLakeTokens(form.tenantId, form.clientId, state.microsoftEmail);
      const result = await startMigration(file,
        { account: form.account, username: form.username, patToken: form.patToken, warehouse: form.warehouse, database: form.database, role: form.role },
        { tenantId: form.tenantId, clientId: form.clientId, clientSecret: "", workspaceId: state.selectedWorkspaceId, azureOpenAIEndpoint: form.azureOpenAIEndpoint, azureOpenAIKey: "", azureOpenAIDeployment: form.azureOpenAIDeployment, xmlaEndpoint: form.xmlaEndpoint, userAccessToken: "", fabricAccessToken, oneLakeAccessToken, lakehouseSqlAccessToken });
      setState({ migrationSessionId: result.sessionId, availableSchemas: result.schemas ?? [], visualBindings: result.visualBindings ?? [], migrationStatus: { status: result.status ?? "awaiting_schema_selection" }, snowflake: { accountUrl: form.account, warehouse: form.warehouse, database: form.database, schema: "", username: form.username } });
      navigate({ to: "/pipeline" });
    } catch (e) { setError(e instanceof Error ? e.message : "Migration initialization failed"); }
    finally { setBusy(false); }
  };
  const field = (label: string, key: keyof typeof form, secret = false) => <Field key={key} label={label}><Input type={secret ? "password" : "text"} value={form[key]} onChange={(e) => update(key, e.target.value)} autoComplete="off" /></Field>;
  return <AppShell step={1}><div className="mx-auto max-w-[760px] px-6 py-8">
    <div className="mb-6"><h1 className="text-2xl font-semibold tracking-tight">Snowflake destination</h1><p className="text-sm text-muted-foreground mt-1">Credentials are sent to the migration API for this request and are not saved in browser storage.</p></div>
    <div className="rounded-lg border bg-card"><div className="p-5 border-b flex items-center gap-3"><div className="size-9 rounded-md bg-[#29B5E8]/10 text-[#29B5E8] grid place-items-center"><Snowflake className="size-5" /></div><div className="font-semibold">Snowflake account and migration settings</div></div>
      <div className="p-6 grid sm:grid-cols-2 gap-5">
        {field("Account identifier", "account")} {field("Username", "username")} {field("Programmatic Access Token", "patToken", true)} {field("Warehouse", "warehouse")} {field("Database", "database")} {field("Role (optional)", "role")}
        {field("Power BI tenant ID", "tenantId")} {field("Power BI application client ID", "clientId")} {field("XMLA endpoint", "xmlaEndpoint")}
        {field("Azure OpenAI endpoint", "azureOpenAIEndpoint")} {field("Azure OpenAI deployment", "azureOpenAIDeployment")}
      </div>
      {error && <div role="alert" className="mx-6 mb-4 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">{error}</div>}
      <div className="px-6 py-4 border-t flex items-center justify-end"><Button onClick={start} disabled={busy}>{busy && <Loader2 className="size-4 animate-spin"/>}{busy ? "Uploading and publishing PBIX…" : "Start migration"}<ArrowRight className="size-4"/></Button></div>
    </div></div></AppShell>;
}

function Field({ label, children }: { label: string; children: React.ReactNode }) { return <div><Label className="text-xs font-medium text-muted-foreground mb-1.5 block">{label}</Label>{children}</div>; }
