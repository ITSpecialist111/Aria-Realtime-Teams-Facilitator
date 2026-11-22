import { useState } from "react";
import {
  Check,
  CheckCircle2,
  ClipboardCheck,
  Clock,
  HelpCircle,
  ListChecks,
  ParkingSquare,
  User,
} from "lucide-react";
import type { CmrRuntimeInfo, LiveMeetingState } from "../types/aria";

interface AccountabilityStudioProps {
  state: LiveMeetingState;
  cmr: CmrRuntimeInfo;
}

export function AccountabilityStudio({
  state,
  cmr,
}: AccountabilityStudioProps) {
  const [tab, setTab] = useState<"actions" | "decisions" | "open">("actions");
  const [copied, setCopied] = useState(false);

  const openCount = state.questions.length + state.parked.length;

  function copyRecapMarkdown() {
    const md = [
      `# ${state.subject}`,
      ``,
      `## Executive Summary`,
      state.summary,
      ``,
      `## Locked Decisions (${state.decisions.length})`,
      ...state.decisions.map((d) => `- ${d}`),
      ``,
      `## Assigned Actions (${state.actions.length})`,
      ...state.actions.map(
        (a) => `- **${a.owner}**: ${a.task}${a.due ? ` _(Due: ${a.due})_` : ""}`,
      ),
    ].join("\n");

    void navigator.clipboard?.writeText(md);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 2200);
  }

  return (
    <aside
      className="w-full lg:w-[308px] xl:w-[356px] shrink-0 flex flex-col border-t lg:border-t-0 lg:border-l lg:overflow-hidden"
      style={{
        backgroundColor: "var(--bg-surface)",
        borderColor: "var(--border-hairline)",
      }}
    >
      {/* Studio Header & Segmented Tabs */}
      <div
        className="p-3.5 border-b space-y-2.5"
        style={{ borderColor: "var(--border-hairline)" }}
      >
        <div className="flex items-center justify-between">
          <span
            className="text-[11px] font-mono-tabular uppercase tracking-wider font-semibold"
            style={{ color: "var(--text-muted)" }}
          >
            Accountability Studio
          </span>
          <button
            type="button"
            onClick={copyRecapMarkdown}
            className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-[11px] font-medium border cursor-pointer"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
              color: copied ? "var(--accent-emerald)" : "var(--text-secondary)",
            }}
          >
            {copied ? (
              <>
                <Check className="w-3 h-3" />
                Copied Recap
              </>
            ) : (
              <>
                <ClipboardCheck className="w-3 h-3" />
                Copy Recap
              </>
            )}
          </button>
        </div>

        <div
          className="grid grid-cols-3 rounded-lg p-0.5 border text-xs"
          style={{
            backgroundColor: "var(--bg-elevated)",
            borderColor: "var(--border-hairline)",
          }}
        >
          <button
            type="button"
            onClick={() => setTab("actions")}
            className="py-1.5 rounded-md font-medium cursor-pointer"
            style={{
              backgroundColor:
                tab === "actions" ? "var(--bg-surface)" : "transparent",
              color:
                tab === "actions" ? "var(--text-primary)" : "var(--text-muted)",
            }}
          >
            Actions ({state.actions.length})
          </button>
          <button
            type="button"
            onClick={() => setTab("decisions")}
            className="py-1.5 rounded-md font-medium cursor-pointer"
            style={{
              backgroundColor:
                tab === "decisions" ? "var(--bg-surface)" : "transparent",
              color:
                tab === "decisions"
                  ? "var(--text-primary)"
                  : "var(--text-muted)",
            }}
          >
            Decisions ({state.decisions.length})
          </button>
          <button
            type="button"
            onClick={() => setTab("open")}
            className="py-1.5 rounded-md font-medium cursor-pointer"
            style={{
              backgroundColor:
                tab === "open" ? "var(--bg-surface)" : "transparent",
              color:
                tab === "open" ? "var(--text-primary)" : "var(--text-muted)",
            }}
          >
            Open ({openCount})
          </button>
        </div>
      </div>

      {/* Tab Body */}
      <div className="flex-1 overflow-y-auto p-3.5 space-y-2.5">
        {tab === "actions" &&
          (state.actions.length === 0 ? (
            <p className="text-xs py-6 text-center" style={{ color: "var(--text-muted)" }}>
              No action items recorded yet.
            </p>
          ) : (
            state.actions.map((act, idx) => (
              <div
                key={`${act.owner}-${idx}`}
                className="rounded-xl border p-3 space-y-1.5"
                style={{
                  backgroundColor: "var(--bg-elevated)",
                  borderColor: "var(--border-hairline)",
                }}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="inline-flex items-center gap-1.5 text-xs font-semibold">
                    <ListChecks
                      className="w-3.5 h-3.5 shrink-0"
                      style={{ color: "var(--accent-cobalt)" }}
                    />
                    {act.owner}
                  </span>
                  {act.due && (
                    <span
                      className="inline-flex items-center gap-1 text-[10px] font-mono-tabular px-1.5 py-0.5 rounded border"
                      style={{
                        backgroundColor: "var(--bg-surface)",
                        borderColor: "var(--border-hairline)",
                        color: "var(--text-secondary)",
                      }}
                    >
                      <Clock className="w-2.5 h-2.5" />
                      {act.due}
                    </span>
                  )}
                </div>
                <p
                  className="text-xs leading-relaxed"
                  style={{ color: "var(--text-secondary)" }}
                >
                  {act.task}
                </p>
              </div>
            ))
          ))}

        {tab === "decisions" &&
          (state.decisions.length === 0 ? (
            <p className="text-xs py-6 text-center" style={{ color: "var(--text-muted)" }}>
              No locked decisions yet.
            </p>
          ) : (
            state.decisions.map((dec) => (
              <div
                key={dec}
                className="rounded-xl border p-3 flex items-start gap-2.5"
                style={{
                  backgroundColor: "var(--bg-elevated)",
                  borderColor: "var(--border-hairline)",
                }}
              >
                <CheckCircle2
                  className="w-4 h-4 mt-0.5 shrink-0"
                  style={{ color: "var(--accent-emerald)" }}
                />
                <p className="text-xs leading-relaxed">{dec}</p>
              </div>
            ))
          ))}

        {tab === "open" && (
          <div className="space-y-3">
            {state.questions.length > 0 && (
              <div className="space-y-1.5">
                <span
                  className="text-[10px] font-mono-tabular uppercase tracking-wider block"
                  style={{ color: "var(--text-muted)" }}
                >
                  Unresolved Questions
                </span>
                {state.questions.map((q) => (
                  <div
                    key={q}
                    className="rounded-xl border p-3 flex items-start gap-2 text-xs"
                    style={{
                      backgroundColor: "var(--bg-elevated)",
                      borderColor: "var(--border-hairline)",
                    }}
                  >
                    <HelpCircle
                      className="w-3.5 h-3.5 mt-0.5 shrink-0"
                      style={{ color: "var(--accent-amber)" }}
                    />
                    <span>{q}</span>
                  </div>
                ))}
              </div>
            )}

            {state.parked.length > 0 && (
              <div className="space-y-1.5">
                <span
                  className="text-[10px] font-mono-tabular uppercase tracking-wider block"
                  style={{ color: "var(--text-muted)" }}
                >
                  Parking Lot
                </span>
                {state.parked.map((p) => (
                  <div
                    key={p}
                    className="rounded-xl border p-3 flex items-start gap-2 text-xs"
                    style={{
                      backgroundColor: "var(--bg-elevated)",
                      borderColor: "var(--border-hairline)",
                    }}
                  >
                    <ParkingSquare
                      className="w-3.5 h-3.5 mt-0.5 shrink-0"
                      style={{ color: "var(--text-muted)" }}
                    />
                    <span style={{ color: "var(--text-secondary)" }}>{p}</span>
                  </div>
                ))}
              </div>
            )}

            {openCount === 0 && (
              <p className="text-xs py-6 text-center" style={{ color: "var(--text-muted)" }}>
                All open questions resolved.
              </p>
            )}
          </div>
        )}
      </div>

      {/* Copilot Managed Runtime Context & Agent 365 Governance Footer */}
      <div
        className="p-3 border-t text-[11px] space-y-1"
        style={{
          backgroundColor: "var(--bg-elevated)",
          borderColor: "var(--border-hairline)",
          color: "var(--text-secondary)",
        }}
      >
        <div className="flex items-center justify-between">
          <span className="inline-flex items-center gap-1.5 font-medium">
            <User className="w-3 h-3" style={{ color: "var(--accent-cobalt)" }} />
            {cmr.userFullName ?? "Entra Agent 365 Teammate"}
          </span>
          <span className="font-mono-tabular text-[10px]" style={{ color: "var(--text-muted)" }}>
            {cmr.hostedInCmr ? "CMR Sandbox" : "OTel · S2S"}
          </span>
        </div>
        <p className="text-[10px]" style={{ color: "var(--text-muted)" }}>
          Recap dispatches via Graph (`saveToSentItems: false`) · Follow-up invites via `onlineMeetings`.
        </p>
      </div>
    </aside>
  );
}
