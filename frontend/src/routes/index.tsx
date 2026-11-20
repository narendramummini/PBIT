import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/")({
  head: () => ({
    meta: [
      { title: "Sign in · PowerBI Accelerator" },
      {
        name: "description",
        content: "Sign in with Microsoft to convert Power BI semantic models to Snowflake.",
      },
    ],
  }),
  component: LoginPage,
});

function LoginPage() {
  const handleSignIn = () => {
    const workspaceApi = (import.meta.env.VITE_WORKSPACE_API_URL as string | undefined) ?? "http://localhost:8000";
    window.location.href = `${workspaceApi.replace(/\/$/, "")}/login`;
  };

  return (
    <div className="min-h-screen grid place-items-center bg-background p-8">
      <div className="w-full max-w-sm text-center">
        <div className="flex items-center justify-center gap-2 mb-10">
          <div className="size-10 rounded-lg bg-primary text-primary-foreground grid place-items-center">
            <MicrosoftLogo className="size-5" />
          </div>
        </div>

        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          PowerBI Accelerator
        </h1>

        <div className="mt-8">
          <Button
            onClick={handleSignIn}
            className="w-full h-11 text-sm font-medium"
          >
            <MicrosoftLogo className="size-4 mr-2" />
            Continue with Microsoft
          </Button>
        </div>
      </div>
    </div>
  );
}

function Button({ onClick, className, children }: {
  onClick: () => void;
  className?: string;
  children: React.ReactNode;
}) {
  return (
    <button onClick={onClick} className={`inline-flex items-center justify-center rounded-md bg-primary text-primary-foreground hover:bg-primary/90 px-4 py-2 text-sm font-medium ${className}`}>
      {children}
    </button>
  );
}

function MicrosoftLogo({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 23 23" className={className} aria-hidden>
      <rect x="1" y="1" width="10" height="10" fill="#F25022" />
      <rect x="12" y="1" width="10" height="10" fill="#7FBA00" />
      <rect x="1" y="12" width="10" height="10" fill="#00A4EF" />
      <rect x="12" y="12" width="10" height="10" fill="#FFB900" />
    </svg>
  );
}
