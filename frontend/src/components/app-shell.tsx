import { Link, useRouterState } from "@tanstack/react-router";
import { Database, LogOut, Snowflake } from "lucide-react";
import type { ReactNode } from "react";
import { useConversionState } from "@/lib/conversion-store";

const steps = [
  { to: "/workspaces", label: "Source" },
  { to: "/configure", label: "Destination" },
  { to: "/pipeline", label: "Pipeline" },
  { to: "/results", label: "Results" },
] as const;

export function AppShell({ children, step }: { children: ReactNode; step?: number }) {
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const state = useConversionState();
  const activeIndex = step ?? steps.findIndex((s) => pathname.startsWith(s.to));

  return (
    <div className="min-h-screen bg-background flex flex-col">
      <header className="border-b bg-card sticky top-0 z-30">
        <div className="mx-auto max-w-[1400px] px-6 h-14 flex items-center justify-between">
          <Link to="/workspaces" className="flex items-center gap-2 font-semibold tracking-tight">
            <div className="size-7 rounded-md bg-primary text-primary-foreground grid place-items-center">
              <Snowflake className="size-4" />
            </div>
            <span>PowerBI Accelerator</span>
            <span className="text-xs font-normal text-muted-foreground hidden sm:inline ml-1">
              Converter
            </span>
          </Link>

          <nav className="hidden md:flex items-center gap-1">
            {steps.map((s, i) => {
              const active = activeIndex === i;
              const done = activeIndex > i;
              return (
                <Link
                  key={s.to}
                  to={s.to}
                  className={`flex items-center gap-2 px-3 py-1.5 rounded-md text-sm transition-colors ${
                    active
                      ? "bg-accent text-accent-foreground font-medium"
                      : "text-muted-foreground hover:text-foreground"
                  }`}
                >
                  <span
                    className={`size-5 grid place-items-center rounded-full text-[11px] font-semibold ${
                      done
                        ? "bg-success text-success-foreground"
                        : active
                          ? "bg-primary text-primary-foreground"
                          : "bg-muted text-muted-foreground"
                    }`}
                  >
                    {done ? "✓" : i + 1}
                  </span>
                  {s.label}
                </Link>
              );
            })}
          </nav>

          <div className="flex items-center gap-3">
            <div className="hidden sm:flex items-center gap-2 text-sm">
              <Database className="size-4 text-muted-foreground" />
              <span className="text-muted-foreground">
                {state.user?.email ?? "demo@contoso.com"}
              </span>
            </div>
            <Link
              to="/"
              className="flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground"
            >
              <LogOut className="size-4" />
              <span className="hidden sm:inline">Sign out</span>
            </Link>
          </div>
        </div>
      </header>

      <main className="flex-1">{children}</main>
    </div>
  );
}
