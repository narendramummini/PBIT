import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { setState } from "@/lib/conversion-store";

export const Route = createFileRoute("/powerbi-auth-success")({
  component: AuthSuccessPage,
});

function AuthSuccessPage() {
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const code = params.get("code") ?? "";
    const errorCode = params.get("error");
    window.history.replaceState({}, document.title, window.location.pathname);
    if (errorCode) {
      const messages: Record<string, string> = {
        signin_cancelled: "Microsoft sign-in was cancelled. Please try again.",
        exchange_unavailable: "Microsoft sign-in could not reach the authentication service. Check that it is running and try again.",
        exchange_failed: "Microsoft could not complete sign-in. Check your account and app permissions, then try again.",
        identity_incomplete: "Microsoft did not return the account details required by this app. Please contact your administrator.",
      };
      setError(messages[errorCode] ?? "Microsoft sign-in failed. Please try again.");
      return;
    }
    if (!code) {
      setError("The sign-in response was incomplete. Please start Microsoft sign-in again.");
      return;
    }
    const workspaceApi = ((import.meta.env.VITE_WORKSPACE_API_URL as string | undefined) ?? "http://localhost:8000").replace(/\/$/, "");
    fetch(`${workspaceApi}/auth/exchange`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code }) })
      .then(async (response) => { if (!response.ok) throw new Error("Sign-in exchange failed"); return response.json(); })
      .then((data) => { setState({ user: data.user, apiAccessToken: data.api_access_token, refreshToken: data.refresh_token, tenantId: data.tenant_id, clientId: data.client_id, microsoftEmail: data.user?.email }); navigate({ to: "/workspaces" }); })
      .catch(() => setError("The sign-in handoff expired or could not be verified. Please start Microsoft sign-in again."));
  }, []);

  return (
    <div className="min-h-screen grid place-items-center bg-background">
      {error ? <div className="max-w-md space-y-3 px-6 text-center"><p className="text-destructive text-sm">{error}</p><button className="text-sm underline" onClick={() => navigate({ to: "/" })}>Return to sign in</button></div> : <p className="text-sm text-muted-foreground">Signing you in...</p>}
    </div>
  );
}


