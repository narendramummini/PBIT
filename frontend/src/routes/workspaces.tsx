// import { createFileRoute, useNavigate } from "@tanstack/react-router";
// import { useEffect, useMemo, useState } from "react";
// import { FileUp, Search, FolderOpen, FileText, ArrowRight, Clock, User, Loader2 } from "lucide-react";
// import { Button } from "@/components/ui/button";
// import { Input } from "@/components/ui/input";
// import { AppShell } from "@/components/app-shell";
// import { getState, setState } from "@/lib/conversion-store";

// const BACKEND = "http://localhost:8000";

// export const Route = createFileRoute("/workspaces")({
//   head: () => ({
//     meta: [
//       { title: "Power BI Workspaces · PowerBI Accelerator" },
//       { name: "description", content: "Browse Power BI workspaces and pick a report to convert." },
//     ],
//   }),
//   component: WorkspacesPage,
// });

// function WorkspacesPage() {
//   const navigate = useNavigate();
//   const [workspaceList, setWorkspaceList] = useState<any[]>([]);
//   const [loading, setLoading] = useState(true);
//   const [error, setError] = useState<string | null>(null);
//   const [activeWs, setActiveWs] = useState<string | null>(null);
//   const [wsQuery, setWsQuery] = useState("");
//   const [selectedReport, setSelectedReport] = useState<string | null>(null);
//   const [uploadedName, setUploadedName] = useState<string | null>(null);

//   useEffect(() => {
//     const { accessToken } = getState();
//     if (!accessToken) {
//       navigate({ to: "/" });
//       return;
//     }
//     fetch(`${BACKEND}/workspaces`, {
//       headers: { Authorization: `Bearer ${accessToken}` },
//       credentials: "include",
//     })
//       .then((r) => {
//         if (!r.ok) throw new Error(`HTTP ${r.status}`);
//         return r.json();
//       })
//       .then((data) => {
//         const list = data.workspaces ?? [];
//         setWorkspaceList(list);
//         if (list.length > 0) setActiveWs(list[0].id);
//       })
//       .catch((e) => setError(e.message))
//       .finally(() => setLoading(false));
//   }, []);

//   const filteredWs = useMemo(
//     () => workspaceList.filter((w) => w.name.toLowerCase().includes(wsQuery.toLowerCase())),
//     [wsQuery, workspaceList],
//   );

//   const ws = workspaceList.find((w) => w.id === activeWs);
//   const wsReports: any[] = ws?.reports ?? [];

//   const onFile = (file?: File) => {
//     if (!file) return;
//     setUploadedName(file.name);
//     setSelectedReport("uploaded");
//   };

//  const handleContinue = () => {
//   if (!selectedReport || !ws) return;

//   const isUploaded = selectedReport === "uploaded";
//   const reportName = isUploaded
//     ? uploadedName ?? "Uploaded.pbix"
//     : wsReports.find((r) => r.id === selectedReport)?.name ?? "Report";

//   setState({
//     selectedReportId: selectedReport,
//     selectedReportName: reportName,
//     selectedWorkspace: ws.name,
//   });

//   // Only clone when an existing workspace report was picked — an
//   // uploaded .pbix has no report_id to clone, it goes through the
//   // extract/parse pipeline instead.
//   if (!isUploaded) {
//     fetch(`${BACKEND}/workspaces/${ws.id}/reports/${selectedReport}/clone`, {
//       method: "POST",
//       headers: { "Content-Type": "application/json" },
//       credentials: "include",
//       body: JSON.stringify({ name: `${reportName} - Clone` }),
//     })
//       .then((r) => r.json())
//       .then((data) => console.log("Clone response:", data))
//       .catch((err) => console.error("Clone failed:", err));
//   }

//   // Navigate right away — don't wait for the clone to finish.
//   navigate({ to: "/configure" });
// };

//   if (loading) {
//     return (
//       <AppShell step={0}>
//         <div className="min-h-[60vh] grid place-items-center">
//           <div className="flex items-center gap-2 text-muted-foreground">
//             <Loader2 className="size-4 animate-spin" />
//             <span className="text-sm">Loading workspaces…</span>
//           </div>
//         </div>
//       </AppShell>
//     );
//   }

//   if (error) {
//     return (
//       <AppShell step={0}>
//         <div className="min-h-[60vh] grid place-items-center">
//           <div className="text-center">
//             <p className="text-sm text-destructive font-medium">Failed to load workspaces</p>
//             <p className="text-xs text-muted-foreground mt-1">{error}</p>
//             <button onClick={() => navigate({ to: "/" })} className="mt-4 text-sm underline">
//               Sign in again
//             </button>
//           </div>
//         </div>
//       </AppShell>
//     );
//   }

//   return (
//     <AppShell step={0}>
//       <div className="mx-auto max-w-[1400px] px-6 py-8">
//         <div className="mb-6">
//           <h1 className="text-2xl font-semibold tracking-tight">Select a Power BI report</h1>
//           <p className="text-sm text-muted-foreground mt-1">
//             Choose a report from a workspace or upload a PBIX file.
//           </p>
//         </div>

//         <div className="grid lg:grid-cols-[280px_1fr] gap-6">
//           <aside className="rounded-lg border bg-card h-fit lg:sticky lg:top-20">
//             <div className="p-3 border-b">
//               <div className="relative">
//                 <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 size-4 text-muted-foreground" />
//                 <Input
//                   value={wsQuery}
//                   onChange={(e) => setWsQuery(e.target.value)}
//                   placeholder="Search workspaces"
//                   className="pl-8 h-9"
//                 />
//               </div>
//             </div>
//             <div className="p-2 max-h-[600px] overflow-auto">
//               <div className="px-2 py-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
//                 Workspaces ({filteredWs.length})
//               </div>
//               {filteredWs.map((w) => {
//                 const active = w.id === activeWs;
//                 return (
//                   <button
//                     key={w.id}
//                     onClick={() => { setActiveWs(w.id); setSelectedReport(null); }}
//                     className={`w-full text-left px-2.5 py-2 rounded-md flex items-start gap-2.5 transition-colors ${
//                       active ? "bg-accent" : "hover:bg-muted"
//                     }`}
//                   >
//                     <FolderOpen className={`size-4 mt-0.5 shrink-0 ${active ? "text-primary" : "text-muted-foreground"}`} />
//                     <div className="min-w-0 flex-1">
//                       <div className="text-sm font-medium truncate">{w.name}</div>
//                       <div className="text-xs text-muted-foreground truncate">
//                         {w.reports?.length ?? 0} reports
//                       </div>
//                     </div>
//                   </button>
//                 );
//               })}
//             </div>
//           </aside>

//           <section>
//             <div className="rounded-lg border bg-card">
//               <div className="p-5 border-b flex items-center justify-between flex-wrap gap-3">
//                 <div>
//                   <div className="text-xs text-muted-foreground">Workspace</div>
//                   <div className="font-semibold">{ws?.name ?? ""}</div>
//                   <div className="text-xs text-muted-foreground mt-0.5">{ws?.description ?? ""}</div>
//                 </div>
//                 <label className="cursor-pointer">
//                   <input type="file" accept=".pbix" className="hidden" onChange={(e) => onFile(e.target.files?.[0])} />
//                   <span className="inline-flex items-center gap-2 px-3 py-2 rounded-md border bg-background hover:bg-accent text-sm font-medium">
//                     <FileUp className="size-4" /> Upload PBIX
//                   </span>
//                 </label>
//               </div>

//               {uploadedName && (
//                 <div className="px-5 py-3 border-b bg-accent/50 flex items-center gap-3">
//                   <div className="size-8 rounded-md bg-primary/10 text-primary grid place-items-center">
//                     <FileUp className="size-4" />
//                   </div>
//                   <div className="flex-1 min-w-0">
//                     <div className="text-sm font-medium truncate">{uploadedName}</div>
//                     <div className="text-xs text-muted-foreground">Just uploaded · ready to convert</div>
//                   </div>
//                   <button
//                     onClick={() => setSelectedReport("uploaded")}
//                     className={`px-3 py-1.5 rounded-md text-xs font-medium ${
//                       selectedReport === "uploaded" ? "bg-primary text-primary-foreground" : "bg-background border"
//                     }`}
//                   >
//                     {selectedReport === "uploaded" ? "Selected" : "Select"}
//                   </button>
//                 </div>
//               )}

//               <div className="p-5 grid sm:grid-cols-2 xl:grid-cols-3 gap-3">
//                 {wsReports.length === 0 && (
//                   <p className="text-sm text-muted-foreground col-span-3">No reports in this workspace.</p>
//                 )}
//                 {wsReports.map((r) => {
//                   const active = selectedReport === r.id;
//                   return (
//                     <button
//                       key={r.id}
//                       onClick={() => setSelectedReport(r.id)}
//                       className={`text-left rounded-lg border p-4 transition-all ${
//                         active ? "border-primary ring-2 ring-primary/20 bg-accent/40" : "hover:border-foreground/20 hover:shadow-sm bg-background"
//                       }`}
//                     >
//                       <div className="flex items-start justify-between gap-2">
//                         <div className="size-8 rounded-md bg-primary/10 text-primary grid place-items-center">
//                           <FileText className="size-4" />
//                         </div>
//                         {active && <span className="text-[10px] font-semibold uppercase tracking-wider text-primary">Selected</span>}
//                       </div>
//                       <div className="mt-3 text-sm font-medium leading-snug">{r.name}</div>
//                       <div className="mt-3 grid grid-cols-2 gap-y-1.5 text-xs text-muted-foreground">
//                         <div className="flex items-center gap-1.5">
//                           <Clock className="size-3" /> {r.modifiedDateTime ?? r.lastModified ?? "—"}
//                         </div>
//                         <div className="flex items-center gap-1.5">
//                           <User className="size-3" /> {r.webUrl ? "Power BI" : "—"}
//                         </div>
//                       </div>
//                     </button>
//                   );
//                 })}
//               </div>
//             </div>

//             <div className="mt-6 flex items-center justify-between">
//               <div className="text-sm text-muted-foreground">
//                 {selectedReport ? "1 report selected" : "Select a report to continue"}
//               </div>
//               <Button onClick={handleContinue} disabled={!selectedReport} size="lg">
//                 Continue <ArrowRight className="size-4" />
//               </Button>
//             </div>
//           </section>
//         </div>
//       </div>
//     </AppShell>
//   );
// }

import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useMemo, useState } from "react";
import { FileUp, Search, FolderOpen, FileText, ArrowRight, Clock, User, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { AppShell } from "@/components/app-shell";
import { getState, setPendingPbix, setState } from "@/lib/conversion-store";
import { authorizedFetch } from "@/lib/migration-api";

const BACKEND = ((import.meta.env.VITE_WORKSPACE_API_URL as string | undefined) ?? "http://localhost:8000").replace(/\/$/, "");

export const Route = createFileRoute("/workspaces")({
  head: () => ({
    meta: [
      { title: "Power BI Workspaces · PowerBI Accelerator" },
      { name: "description", content: "Browse Power BI workspaces and pick a report to convert." },
    ],
  }),
  component: WorkspacesPage,
});

function WorkspacesPage() {
  const navigate = useNavigate();
  const [workspaceList, setWorkspaceList] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [activeWs, setActiveWs] = useState<string | null>(null);
  const [wsQuery, setWsQuery] = useState("");
  const [selectedReport, setSelectedReport] = useState<string | null>(null);
  const [uploadedFile, setUploadedFile] = useState<File | null>(null);
  const [uploadedName, setUploadedName] = useState<string | null>(null);

  useEffect(() => {
    const { apiAccessToken } = getState();
    if (!apiAccessToken) {
      navigate({ to: "/" });
      return;
    }
    authorizedFetch(BACKEND, "/workspaces", {
      headers: { Authorization: `Bearer ${apiAccessToken}` },
      credentials: "include",
    })
      .then((r) => {
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        return r.json();
      })
      .then((data) => {
        const list = data.workspaces ?? [];
        setWorkspaceList(list);
        if (list.length > 0) setActiveWs(list[0].id);
      })
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  const filteredWs = useMemo(
    () => workspaceList.filter((w) => w.name.toLowerCase().includes(wsQuery.toLowerCase())),
    [wsQuery, workspaceList],
  );

  const ws = workspaceList.find((w) => w.id === activeWs);
  const wsReports: any[] = ws?.reports ?? [];

  const onFile = (file?: File) => {
    if (!file) return;
    setUploadedFile(file);
    setUploadedName(file.name);
    setSelectedReport("uploaded");
  };

  const handleContinue = async () => {
    if (!selectedReport || !ws) return;

    const isUploaded = selectedReport === "uploaded";
    const reportName = isUploaded
      ? uploadedName ?? "Uploaded.pbix"
      : wsReports.find((r) => r.id === selectedReport)?.name ?? "Report";

    if (!isUploaded || !uploadedFile) {
      setError("Select a Power BI report file (.pbix) to start a migration. Existing report cloning does not create a migration session.");
      return;
    }
    const apiAccessToken = getState().apiAccessToken;
    try {
      const selected = await authorizedFetch(BACKEND, "/workspaces/select", { method: "POST", headers: { "Content-Type": "application/json", Authorization: `Bearer ${apiAccessToken ?? ""}` }, body: JSON.stringify({ workspace_id: ws.id }) });
      if (!selected.ok) throw new Error("Workspace selection failed");
    } catch (e) { setError(e instanceof Error ? e.message : "Workspace selection failed"); return; }
    setPendingPbix(uploadedFile);
    setState({
      selectedReportId: selectedReport,
      selectedReportName: reportName,
      selectedWorkspace: ws.name,
      selectedWorkspaceId: ws.id,
    });
    navigate({ to: "/configure" });
  };

  if (loading) {
    return (
      <AppShell step={0}>
        <div className="min-h-[60vh] grid place-items-center">
          <div className="flex items-center gap-2 text-muted-foreground">
            <Loader2 className="size-4 animate-spin" />
            <span className="text-sm">Loading workspaces…</span>
          </div>
        </div>
      </AppShell>
    );
  }

  if (error) {
    return (
      <AppShell step={0}>
        <div className="min-h-[60vh] grid place-items-center">
          <div className="text-center">
            <p className="text-sm text-destructive font-medium">Failed to load workspaces</p>
            <p className="text-xs text-muted-foreground mt-1">{error}</p>
            <button onClick={() => navigate({ to: "/" })} className="mt-4 text-sm underline">
              Sign in again
            </button>
          </div>
        </div>
      </AppShell>
    );
  }

  return (
    <AppShell step={0}>
      <div className="mx-auto max-w-[1400px] px-6 py-8">
        <div className="mb-6">
          <h1 className="text-2xl font-semibold tracking-tight">Select a Power BI report</h1>
          <p className="text-sm text-muted-foreground mt-1">
            Choose a report from a workspace or upload a PBIX file.
          </p>
        </div>

        <div className="grid lg:grid-cols-[280px_1fr] gap-6">
          <aside className="rounded-lg border bg-card h-fit lg:sticky lg:top-20">
            <div className="p-3 border-b">
              <div className="relative">
                <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 size-4 text-muted-foreground" />
                <Input
                  value={wsQuery}
                  onChange={(e) => setWsQuery(e.target.value)}
                  placeholder="Search workspaces"
                  className="pl-8 h-9"
                />
              </div>
            </div>
            <div className="p-2 max-h-[600px] overflow-auto">
              <div className="px-2 py-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                Workspaces ({filteredWs.length})
              </div>
              {filteredWs.map((w) => {
                const active = w.id === activeWs;
                return (
                  <button
                    key={w.id}
                    onClick={() => { setActiveWs(w.id); setSelectedReport(null); }}
                    className={`w-full text-left px-2.5 py-2 rounded-md flex items-start gap-2.5 transition-colors ${
                      active ? "bg-accent" : "hover:bg-muted"
                    }`}
                  >
                    <FolderOpen className={`size-4 mt-0.5 shrink-0 ${active ? "text-primary" : "text-muted-foreground"}`} />
                    <div className="min-w-0 flex-1">
                      <div className="text-sm font-medium truncate">{w.name}</div>
                      <div className="text-xs text-muted-foreground truncate">
                        {w.reports?.length ?? 0} reports
                      </div>
                    </div>
                  </button>
                );
              })}
            </div>
          </aside>

          <section>
            <div className="rounded-lg border bg-card">
              <div className="p-5 border-b flex items-center justify-between flex-wrap gap-3">
                <div>
                  <div className="text-xs text-muted-foreground">Workspace</div>
                  <div className="font-semibold">{ws?.name ?? ""}</div>
                  <div className="text-xs text-muted-foreground mt-0.5">{ws?.description ?? ""}</div>
                </div>
                <label className="cursor-pointer">
                  <input type="file" accept=".pbix" className="hidden" onChange={(e) => onFile(e.target.files?.[0])} />
                  <span className="inline-flex items-center gap-2 px-3 py-2 rounded-md border bg-background hover:bg-accent text-sm font-medium">
                    <FileUp className="size-4" /> Upload PBIX
                  </span>
                </label>
              </div>

              {uploadedName && (
                <div className="px-5 py-3 border-b bg-accent/50 flex items-center gap-3">
                  <div className="size-8 rounded-md bg-primary/10 text-primary grid place-items-center">
                    <FileUp className="size-4" />
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="text-sm font-medium truncate">{uploadedName}</div>
                    <div className="text-xs text-muted-foreground">Just uploaded · ready to convert</div>
                  </div>
                  <button
                    onClick={() => setSelectedReport("uploaded")}
                    className={`px-3 py-1.5 rounded-md text-xs font-medium ${
                      selectedReport === "uploaded" ? "bg-primary text-primary-foreground" : "bg-background border"
                    }`}
                  >
                    {selectedReport === "uploaded" ? "Selected" : "Select"}
                  </button>
                </div>
              )}

              <div className="p-5 grid sm:grid-cols-2 xl:grid-cols-3 gap-3">
                {wsReports.length === 0 && (
                  <p className="text-sm text-muted-foreground col-span-3">No reports in this workspace.</p>
                )}
                {wsReports.map((r) => {
                  const active = selectedReport === r.id;
                  return (
                    <button
                      key={r.id}
                      onClick={() => setSelectedReport(r.id)}
                      className={`text-left rounded-lg border p-4 transition-all ${
                        active ? "border-primary ring-2 ring-primary/20 bg-accent/40" : "hover:border-foreground/20 hover:shadow-sm bg-background"
                      }`}
                    >
                      <div className="flex items-start justify-between gap-2">
                        <div className="size-8 rounded-md bg-primary/10 text-primary grid place-items-center">
                          <FileText className="size-4" />
                        </div>
                        {active && <span className="text-[10px] font-semibold uppercase tracking-wider text-primary">Selected</span>}
                      </div>
                      <div className="mt-3 text-sm font-medium leading-snug">{r.name}</div>
                      <div className="mt-3 grid grid-cols-2 gap-y-1.5 text-xs text-muted-foreground">
                        <div className="flex items-center gap-1.5">
                          <Clock className="size-3" /> {r.modifiedDateTime ?? r.lastModified ?? "—"}
                        </div>
                        <div className="flex items-center gap-1.5">
                          <User className="size-3" /> {r.webUrl ? "Power BI" : "—"}
                        </div>
                      </div>
                    </button>
                  );
                })}
              </div>
            </div>

            <div className="mt-6 flex items-center justify-between">
              <div className="text-sm text-muted-foreground">
                {selectedReport ? "1 report selected" : "Select a report to continue"}
              </div>
              <Button onClick={handleContinue} disabled={!selectedReport} size="lg">
                Continue <ArrowRight className="size-4" />
              </Button>
            </div>
          </section>
        </div>
      </div>
    </AppShell>
  );
}
