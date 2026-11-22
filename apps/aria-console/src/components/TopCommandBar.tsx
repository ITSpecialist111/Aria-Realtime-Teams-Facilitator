import {
  Command,
  Moon,
  Play,
  Radio,
  ShieldAlert,
  ShieldCheck,
  Sparkles,
  Sun,
} from "lucide-react";
import type { CmrRuntimeInfo, LiveMeetingState } from "../types/aria";
import { SHOWCASE_ACTS } from "../data/showcaseScenario";

interface TopCommandBarProps {
  state: LiveMeetingState;
  cmr: CmrRuntimeInfo;
  isLiveConnected: boolean;
  activeAct: number;
  onSelectAct: (actId: number) => void;
  autoAdvancing: boolean;
  onToggleAutoAdvance: () => void;
  scrubMaxSec: number | null;
  onScrubMilestone: (atSec: number | null) => void;
  onToggleGuestShield: () => void;
  onOpenFinOps: () => void;
  onOpenCommandPalette: () => void;
  darkMode: boolean;
  onToggleDarkMode: () => void;
  avatarVideoActive: boolean;
}

function formatClock(sec: number): string {
  const m = Math.floor(sec / 60);
  const s = sec % 60;
  return `${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
}

export function TopCommandBar({
  state,
  cmr,
  isLiveConnected,
  activeAct,
  onSelectAct,
  autoAdvancing,
  onToggleAutoAdvance,
  scrubMaxSec,
  onScrubMilestone,
  onToggleGuestShield,
  onOpenFinOps,
  onOpenCommandPalette,
  darkMode,
  onToggleDarkMode,
  avatarVideoActive,
}: TopCommandBarProps) {
  const totalPlannedMin = Math.max(
    1,
    state.agenda.items.reduce((acc, i) => acc + (i.minutes ?? 3), 0),
  );
  const totalSec = Math.max(totalPlannedMin * 60, state.elapsed + 60);
  const effectivePerMin =
    state.finops.costPerMinuteUsd + (avatarVideoActive ? 0.085 : 0);
  const effectiveCallCost =
    state.finops.totalLiveCostUsd +
    (avatarVideoActive ? state.finops.audioMinutes * 0.085 : 0);

  return (
    <header
      className="flex flex-col border-b select-none"
      style={{
        backgroundColor: "var(--bg-surface)",
        borderColor: "var(--border-hairline)",
      }}
    >
      {/* Row 1: Primary Identity, CMR Host Badge, Cost Pill, Controls */}
      <div className="flex items-center justify-between gap-3 px-4 h-13">
        <div className="flex items-center gap-3 min-w-0">
          <div className="flex items-center gap-2 shrink-0">
            <span
              className="inline-flex h-2 w-2 rounded-full"
              style={{
                backgroundColor: isLiveConnected
                  ? "var(--accent-emerald)"
                  : "var(--accent-cobalt)",
              }}
            />
            <span className="text-xs font-semibold tracking-wider uppercase font-mono-tabular">
              {state.agent}
            </span>
          </div>

          <span
            className="hidden sm:inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-[11px] font-medium border shrink-0"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
              color: "var(--text-secondary)",
            }}
            title={
              cmr.hostedInCmr
                ? `Hosted inside Microsoft Copilot Managed Runtime (App ${cmr.appId})`
                : "Copilot Managed Runtime SDK active · Ready for App Player or Direct Teams Companion"
            }
          >
            <Sparkles className="w-3 h-3" style={{ color: "var(--accent-cobalt)" }} />
            {cmr.hostedInCmr ? "CMR Managed App · Live" : "CMR Runtime SDK · v1.0"}
          </span>

          {isLiveConnected && (
            <span
              className="inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-[11px] font-semibold font-mono-tabular border shrink-0"
              style={{
                backgroundColor:
                  "color-mix(in oklch, var(--accent-emerald) 14%, transparent)",
                borderColor:
                  "color-mix(in oklch, var(--accent-emerald) 45%, transparent)",
                color: "var(--accent-emerald)",
              }}
            >
              ● LIVE TEAMS CALL
            </span>
          )}

          <h1
            className="text-sm font-medium truncate"
            style={{ color: "var(--text-primary)" }}
          >
            {state.subject}
          </h1>
        </div>

        <div className="flex items-center gap-2 shrink-0">
          {/* Scenario / Live Switcher */}
          {!isLiveConnected && (
            <div
              className="hidden xl:flex items-center rounded-lg p-0.5 border"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              {SHOWCASE_ACTS.map((act) => {
                const active = act.id === activeAct;
                return (
                  <button
                    key={act.id}
                    type="button"
                    onClick={() => onSelectAct(act.id)}
                    className="px-2.5 py-1 rounded-md text-[11px] font-medium transition-colors cursor-pointer"
                    style={{
                      backgroundColor: active ? "var(--bg-surface)" : "transparent",
                      color: active ? "var(--text-primary)" : "var(--text-muted)",
                      boxShadow: active ? "0 1px 2px rgba(0,0,0,0.08)" : "none",
                    }}
                  >
                    {act.shortLabel}
                  </button>
                );
              })}
              <button
                type="button"
                onClick={onToggleAutoAdvance}
                title="Auto-play 3-act crisis progression"
                className="ml-1 px-2 py-1 rounded-md text-[11px] font-medium flex items-center gap-1 cursor-pointer"
                style={{
                  color: autoAdvancing ? "var(--accent-emerald)" : "var(--text-secondary)",
                }}
              >
                {autoAdvancing ? <Radio className="w-3 h-3" /> : <Play className="w-3 h-3" />}
                {autoAdvancing ? "Playing" : "Replay"}
              </button>
            </div>
          )}

          {/* External Guest DLP Shield Toggle */}
          <button
            type="button"
            onClick={onToggleGuestShield}
            title="External attendee DLP boundary: hides internal M365 Work IQ citations and org tools when an external guest joins"
            className="inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1.5 text-xs font-medium border transition-colors cursor-pointer"
            style={{
              backgroundColor: state.restricted
                ? "color-mix(in oklch, var(--accent-amber) 14%, transparent)"
                : "var(--bg-elevated)",
              borderColor: state.restricted
                ? "color-mix(in oklch, var(--accent-amber) 45%, transparent)"
                : "var(--border-hairline)",
              color: state.restricted ? "var(--accent-amber)" : "var(--text-secondary)",
            }}
          >
            {state.restricted ? (
              <>
                <ShieldAlert className="w-3.5 h-3.5" />
                <span className="hidden md:inline">Guest Shield Active</span>
              </>
            ) : (
              <>
                <ShieldCheck className="w-3.5 h-3.5" style={{ color: "var(--accent-emerald)" }} />
                <span className="hidden md:inline">Internal · M365 Grounded</span>
              </>
            )}
          </button>

          {/* E8 Collapsed Executive FinOps & Latency Pill ("What's the cost of this?") */}
          <button
            type="button"
            onClick={onOpenFinOps}
            data-testid="finops-pill"
            className="inline-flex items-center gap-2 rounded-lg px-3 py-1.5 text-xs font-mono-tabular border transition-all hover:opacity-95 cursor-pointer"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-strong)",
              color: "var(--text-primary)",
            }}
            title="Click to unfold Executive FinOps Waterfall & 100-Seat Monthly TCO"
          >
            <span
              className="inline-block h-1.5 w-1.5 rounded-full"
              style={{ backgroundColor: "var(--accent-emerald)" }}
            />
            <span style={{ color: "var(--text-secondary)" }}>1.04s</span>
            <span style={{ color: "var(--border-strong)" }}>·</span>
            <span className="font-semibold">
              ${effectiveCallCost.toFixed(2)} call
            </span>
            <span
              className="hidden sm:inline text-[11px]"
              style={{ color: "var(--text-muted)" }}
            >
              (${effectivePerMin.toFixed(3)}/m)
            </span>
          </button>

          {/* Theme Toggle (Obsidian / Warm Alabaster Paper) */}
          <button
            type="button"
            onClick={onToggleDarkMode}
            aria-label="Toggle Paper / Obsidian theme"
            className="inline-flex items-center justify-center w-8 h-8 rounded-lg border transition-colors cursor-pointer"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
              color: "var(--text-secondary)",
            }}
            title={darkMode ? "Switch to Warm Alabaster Paper" : "Switch to Deep Obsidian"}
          >
            {darkMode ? <Sun className="w-3.5 h-3.5" /> : <Moon className="w-3.5 h-3.5" />}
          </button>

          {/* Command Palette Trigger */}
          <button
            type="button"
            onClick={onOpenCommandPalette}
            aria-label="Open Command Palette"
            className="inline-flex items-center gap-1 rounded-lg px-2 py-1.5 text-xs font-mono-tabular border cursor-pointer"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
              color: "var(--text-secondary)",
            }}
          >
            <Command className="w-3 h-3" />
            <span>K</span>
          </button>
        </div>
      </div>

      {/* Row 2: E3 Proportional Agenda Pacing + Clickable Milestone Scrub Rail */}
      <div
        className="flex items-center gap-4 px-4 py-2 border-t text-xs"
        style={{
          backgroundColor: "var(--bg-canvas)",
          borderColor: "var(--border-hairline)",
        }}
      >
        <div className="flex items-center gap-2 shrink-0 font-mono-tabular">
          <span className="font-semibold" style={{ color: "var(--text-primary)" }}>
            {formatClock(state.elapsed)}
          </span>
          {state.remaining !== null && (
            <span style={{ color: "var(--text-muted)" }}>
              / -{formatClock(Math.max(0, state.remaining))}
            </span>
          )}
        </div>

        {/* Proportional Segmented Agenda Bar */}
        <div className="flex-1 flex flex-col gap-1.5 min-w-0">
          <div className="flex items-center gap-1 w-full">
            {state.agenda.items.map((item, idx) => {
              const weight = Math.max(1, item.minutes ?? 3);
              const isCurrent = idx === state.agenda.current;
              const isDone = state.agenda.done.includes(idx);
              const planned = item.minutes ?? 3;
              const overrun = item.spent > planned;
              const fillPct = isDone
                ? 100
                : isCurrent
                  ? Math.min(100, Math.round((item.spent / planned) * 100))
                  : Math.min(100, Math.round((item.spent / planned) * 100));

              return (
                <div
                  key={item.title}
                  style={{ flexGrow: weight, flexBasis: 0 }}
                  className="min-w-0 group"
                  title={`${item.title} — ${item.spent.toFixed(1)}m spent / ${planned}m planned`}
                >
                  <div className="flex items-center justify-between gap-1 mb-0.5">
                    <span
                      className="truncate text-[11px]"
                      style={{
                        color: isCurrent
                          ? "var(--text-primary)"
                          : "var(--text-muted)",
                        fontWeight: isCurrent ? 600 : 400,
                      }}
                    >
                      {idx + 1}. {item.title}
                    </span>
                    <span
                      className="text-[10px] font-mono-tabular shrink-0 hidden lg:inline"
                      style={{
                        color: overrun ? "var(--accent-amber)" : "var(--text-muted)",
                      }}
                    >
                      {item.spent.toFixed(1)}/{planned}m
                    </span>
                  </div>
                  <div
                    className="h-1.5 rounded-full overflow-hidden"
                    style={{ backgroundColor: "var(--bg-subtle)" }}
                  >
                    <div
                      className="h-full rounded-full transition-all duration-500"
                      style={{
                        width: `${fillPct}%`,
                        backgroundColor: overrun
                          ? "var(--accent-amber)"
                          : isDone
                            ? "var(--accent-emerald)"
                            : "var(--accent-cobalt)",
                      }}
                    />
                  </div>
                </div>
              );
            })}
          </div>

          {/* Clickable Milestone Timeline Chips */}
          {state.milestones.length > 0 && (
            <div className="flex items-center gap-1.5 overflow-x-auto pb-0.5">
              <span
                className="text-[10px] uppercase tracking-wider font-mono-tabular shrink-0"
                style={{ color: "var(--text-muted)" }}
              >
                Milestones:
              </span>
              {state.milestones.map((ms) => {
                const selected = scrubMaxSec === ms.at;
                const pct = Math.min(98, Math.round((ms.at / totalSec) * 100));
                return (
                  <button
                    key={`${ms.label}-${ms.at}`}
                    type="button"
                    onClick={() => onScrubMilestone(selected ? null : ms.at)}
                    title={`Filter Intelligence Stream up to ${formatClock(ms.at)} (${pct}% of timeline)`}
                    className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] border transition-colors shrink-0 cursor-pointer"
                    style={{
                      backgroundColor: selected
                        ? "color-mix(in oklch, var(--accent-cobalt) 18%, transparent)"
                        : "var(--bg-surface)",
                      borderColor: selected
                        ? "var(--accent-cobalt)"
                        : "var(--border-hairline)",
                      color: selected ? "var(--text-primary)" : "var(--text-secondary)",
                    }}
                  >
                    <span
                      className="h-1.5 w-1.5 rounded-full"
                      style={{
                        backgroundColor:
                          ms.kind === "skill"
                            ? "var(--accent-violet)"
                            : ms.kind === "risk"
                              ? "var(--accent-amber)"
                              : "var(--accent-emerald)",
                      }}
                    />
                    <span className="font-mono-tabular text-[10px]" style={{ color: "var(--text-muted)" }}>
                      {formatClock(ms.at)}
                    </span>
                    <span>{ms.label}</span>
                  </button>
                );
              })}
              {scrubMaxSec !== null && (
                <button
                  type="button"
                  onClick={() => onScrubMilestone(null)}
                  className="text-[11px] underline px-1.5 cursor-pointer shrink-0"
                  style={{ color: "var(--accent-cobalt)" }}
                >
                  Reset filter
                </button>
              )}
            </div>
          )}
        </div>
      </div>
    </header>
  );
}
