import { useLayoutEffect, useRef, useState } from "react";
import gsap from "gsap";
import { Flip } from "gsap/Flip";
import {
  AlertTriangle,
  BookOpen,
  Camera,
  ChevronRight,
  FileText,
  MessageSquareText,
  ShieldAlert,
  Sparkles,
} from "lucide-react";
import type {
  MeetingInsightItem,
  TranscriptTurnItem,
} from "../types/aria";

gsap.registerPlugin(Flip);

interface IntelligenceStreamProps {
  summary: string;
  insights: MeetingInsightItem[];
  transcript: TranscriptTurnItem[];
  restricted: boolean;
  scrubMaxSec: number | null;
}

function formatClock(sec: number): string {
  const m = Math.floor(sec / 60);
  const s = sec % 60;
  return `${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
}

export function IntelligenceStream({
  summary,
  insights,
  transcript,
  restricted,
  scrubMaxSec,
}: IntelligenceStreamProps) {
  const [activeTab, setActiveTab] = useState<"intelligence" | "transcript">(
    "intelligence",
  );
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const listRef = useRef<HTMLDivElement | null>(null);
  const flipStateRef = useRef<Flip.FlipState | null>(null);

  const visibleInsights = insights.filter((item) => {
    if (restricted && item.kind === "knowledge") return false;
    if (scrubMaxSec !== null && item.elapsedSec > scrubMaxSec) return false;
    return true;
  });

  function toggleCard(id: string) {
    const prefersReduced = window.matchMedia(
      "(prefers-reduced-motion: reduce)",
    ).matches;
    if (!prefersReduced && listRef.current) {
      flipStateRef.current = Flip.getState(
        listRef.current.querySelectorAll("[data-flip-card]"),
      );
    }
    setExpandedId((prev) => (prev === id ? null : id));
  }

  useLayoutEffect(() => {
    if (!flipStateRef.current || !listRef.current) return;
    const state = flipStateRef.current;
    flipStateRef.current = null;

    const ctx = gsap.context(() => {
      Flip.from(state, {
        duration: 0.38,
        ease: "power3.inOut",
        nested: true,
        prune: true,
      });

      // Animate Swap S2 3-node SVG provenance trace stroke-draw on newly expanded card
      if (expandedId && listRef.current) {
        const paths = listRef.current.querySelectorAll<SVGPathElement>(
          `[data-trace-for="${expandedId}"] path`,
        );
        paths.forEach((p) => {
          const len = p.getTotalLength?.() || 120;
          gsap.fromTo(
            p,
            { strokeDasharray: len, strokeDashoffset: len },
            { strokeDashoffset: 0, duration: 0.55, ease: "power2.out" },
          );
        });
      }
    }, listRef);

    return () => ctx.revert();
  }, [expandedId, activeTab]);

  return (
    <section className="flex-1 min-w-0 flex flex-col overflow-hidden">
      {/* Executive Situation Brief + Stream Mode Switch */}
      <div
        className="px-4 py-3 border-b flex flex-col gap-2"
        style={{
          backgroundColor: "var(--bg-surface)",
          borderColor: "var(--border-hairline)",
        }}
      >
        <div className="flex items-center justify-between gap-3">
          <span
            className="text-[11px] font-mono-tabular uppercase tracking-wider font-semibold"
            style={{ color: "var(--text-muted)" }}
          >
            Executive Synthesis & Grounded Intelligence
          </span>

          <div
            className="inline-flex rounded-lg p-0.5 border text-xs"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
            }}
          >
            <button
              type="button"
              onClick={() => setActiveTab("intelligence")}
              className="px-2.5 py-1 rounded-md font-medium cursor-pointer"
              style={{
                backgroundColor:
                  activeTab === "intelligence"
                    ? "var(--bg-surface)"
                    : "transparent",
                color:
                  activeTab === "intelligence"
                    ? "var(--text-primary)"
                    : "var(--text-muted)",
              }}
            >
              Intelligence ({visibleInsights.length})
            </button>
            <button
              type="button"
              onClick={() => setActiveTab("transcript")}
              className="px-2.5 py-1 rounded-md font-medium inline-flex items-center gap-1 cursor-pointer"
              style={{
                backgroundColor:
                  activeTab === "transcript"
                    ? "var(--bg-surface)"
                    : "transparent",
                color:
                  activeTab === "transcript"
                    ? "var(--text-primary)"
                    : "var(--text-muted)",
              }}
            >
              <MessageSquareText className="w-3 h-3" />
              Transcript ({transcript.length})
            </button>
          </div>
        </div>

        {summary && (
          <p
            className="text-xs sm:text-[13px] leading-relaxed"
            style={{ color: "var(--text-secondary)" }}
          >
            {summary}
          </p>
        )}

        {restricted && (
          <div
            className="flex items-center gap-2 rounded-lg px-3 py-2 text-xs border"
            style={{
              backgroundColor:
                "color-mix(in oklch, var(--accent-amber) 12%, transparent)",
              borderColor:
                "color-mix(in oklch, var(--accent-amber) 45%, transparent)",
              color: "var(--accent-amber)",
            }}
          >
            <ShieldAlert className="w-4 h-4 shrink-0" />
            <span>
              External attendee detected — internal Microsoft 365 Work IQ documents, SharePoint playbooks, and mailbox citations are automatically withheld.
            </span>
          </div>
        )}
      </div>

      {/* Main Scrollable Stream */}
      <div ref={listRef} className="flex-1 overflow-y-auto p-4 space-y-2">
        {activeTab === "intelligence" ? (
          visibleInsights.length === 0 ? (
            <div
              className="rounded-xl border p-8 text-center text-xs"
              style={{
                backgroundColor: "var(--bg-surface)",
                borderColor: "var(--border-hairline)",
                color: "var(--text-muted)",
              }}
            >
              Listening for operational decisions, risks, and Microsoft 365 grounding opportunities...
            </div>
          ) : (
            visibleInsights.map((item) => {
              const isOpen = expandedId === item.id;
              const toneColor =
                item.kind === "knowledge"
                  ? "var(--accent-violet)"
                  : item.severity === "high"
                    ? "var(--accent-crimson)"
                    : item.kind === "visual"
                      ? "var(--accent-cobalt)"
                      : "var(--accent-emerald)";

              const KindIcon =
                item.kind === "knowledge"
                  ? BookOpen
                  : item.kind === "visual"
                    ? Camera
                    : item.severity === "high"
                      ? AlertTriangle
                      : Sparkles;

              return (
                <article
                  key={item.id}
                  data-flip-card
                  data-flip-id={item.id}
                  className="rounded-xl border transition-colors overflow-hidden"
                  style={{
                    backgroundColor: "var(--bg-surface)",
                    borderColor: isOpen
                      ? "var(--border-strong)"
                      : "var(--border-hairline)",
                  }}
                >
                  {/* Calm 52px Single-Line Scannable Header */}
                  <button
                    type="button"
                    onClick={() => toggleCard(item.id)}
                    aria-expanded={isOpen}
                    className="w-full min-h-[52px] px-3.5 py-2.5 flex items-center justify-between gap-3 text-left cursor-pointer"
                  >
                    <div className="flex items-center gap-2.5 min-w-0">
                      <span
                        className="font-mono-tabular text-[11px] shrink-0"
                        style={{ color: "var(--text-muted)" }}
                      >
                        {item.elapsedSec > 0
                          ? formatClock(item.elapsedSec)
                          : item.at}
                      </span>
                      <span
                        className="w-6 h-6 rounded-md flex items-center justify-center shrink-0"
                        style={{
                          backgroundColor: `color-mix(in oklch, ${toneColor} 14%, transparent)`,
                          color: toneColor,
                        }}
                      >
                        <KindIcon className="w-3.5 h-3.5" />
                      </span>
                      <span className="text-[13px] font-medium truncate">
                        {item.title}
                      </span>
                    </div>

                    <div className="flex items-center gap-2 shrink-0">
                      {item.citation && (
                        <span
                          className="hidden sm:inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-[11px] border max-w-[210px] truncate"
                          style={{
                            backgroundColor: "var(--bg-elevated)",
                            borderColor: "var(--border-hairline)",
                            color: "var(--text-secondary)",
                          }}
                        >
                          <FileText className="w-3 h-3 shrink-0" />
                          <span className="truncate">{item.citation}</span>
                        </span>
                      )}
                      <ChevronRight
                        className={`w-4 h-4 transition-transform duration-200 ${
                          isOpen ? "rotate-90" : ""
                        }`}
                        style={{ color: "var(--text-muted)" }}
                      />
                    </div>
                  </button>

                  {/* GSAP Flip Unfolded Evidence & Swap S2 3-Node Provenance Trace */}
                  {isOpen && (
                    <div
                      className="px-4 pb-4 pt-2 border-t space-y-3"
                      style={{
                        borderColor: "var(--border-hairline)",
                        backgroundColor: "var(--bg-elevated)",
                      }}
                    >
                      <p
                        className="text-xs sm:text-[13px] leading-relaxed"
                        style={{ color: "var(--text-primary)" }}
                      >
                        {item.text}
                      </p>

                      {/* Swap S2: Crisp 3-Node SVG Provenance Trace */}
                      <div
                        data-trace-for={item.id}
                        className="rounded-lg border p-3"
                        style={{
                          backgroundColor: "var(--bg-surface)",
                          borderColor: "var(--border-hairline)",
                        }}
                      >
                        <div
                          className="text-[10px] font-mono-tabular uppercase tracking-wider mb-2"
                          style={{ color: "var(--text-muted)" }}
                        >
                          Grounded Provenance Chain
                        </div>

                        <div className="grid grid-cols-1 sm:grid-cols-[1fr_44px_1fr_44px_1fr] items-center gap-2 text-xs">
                          <div
                            className="rounded-md border px-2.5 py-2"
                            style={{
                              backgroundColor: "var(--bg-elevated)",
                              borderColor: "var(--border-hairline)",
                            }}
                          >
                            <div
                              className="text-[10px] font-mono-tabular uppercase"
                              style={{ color: "var(--accent-violet)" }}
                            >
                              1 · Primary Source
                            </div>
                            <div className="font-medium truncate mt-0.5">
                              {item.citation ?? "Live Meeting Context"}
                            </div>
                          </div>

                          <svg
                            viewBox="0 0 44 16"
                            className="hidden sm:block w-11 h-4"
                          >
                            <path
                              d="M 2 8 L 40 8 M 34 3 L 40 8 L 34 13"
                              fill="none"
                              stroke="var(--accent-cobalt)"
                              strokeWidth="1.5"
                              strokeLinecap="round"
                              strokeLinejoin="round"
                            />
                          </svg>

                          <div
                            className="rounded-md border px-2.5 py-2"
                            style={{
                              backgroundColor: "var(--bg-elevated)",
                              borderColor: "var(--border-hairline)",
                            }}
                          >
                            <div
                              className="text-[10px] font-mono-tabular uppercase"
                              style={{ color: "var(--accent-cobalt)" }}
                            >
                              2 · Skill Lens
                            </div>
                            <div className="font-medium truncate mt-0.5">
                              {item.skillRule ?? "Autonomous Facilitation"}
                            </div>
                          </div>

                          <svg
                            viewBox="0 0 44 16"
                            className="hidden sm:block w-11 h-4"
                          >
                            <path
                              d="M 2 8 L 40 8 M 34 3 L 40 8 L 34 13"
                              fill="none"
                              stroke="var(--accent-emerald)"
                              strokeWidth="1.5"
                              strokeLinecap="round"
                              strokeLinejoin="round"
                            />
                          </svg>

                          <div
                            className="rounded-md border px-2.5 py-2"
                            style={{
                              backgroundColor: "var(--bg-elevated)",
                              borderColor: "var(--border-hairline)",
                            }}
                          >
                            <div
                              className="text-[10px] font-mono-tabular uppercase"
                              style={{ color: "var(--accent-emerald)" }}
                            >
                              3 · Room Outcome
                            </div>
                            <div className="font-medium truncate mt-0.5">
                              {item.linkedOutcome ?? "Surfaced on Live Board"}
                            </div>
                          </div>
                        </div>
                      </div>
                    </div>
                  )}
                </article>
              );
            })
          )
        ) : (
          /* Attributed Transcript Stream */
          <div className="space-y-2">
            {transcript.map((turn, idx) => (
              <div
                key={`${turn.at}-${idx}`}
                className="rounded-xl border px-3.5 py-2.5 text-xs"
                style={{
                  backgroundColor: turn.agent
                    ? "color-mix(in oklch, var(--accent-cobalt) 8%, var(--bg-surface))"
                    : "var(--bg-surface)",
                  borderColor: turn.agent
                    ? "color-mix(in oklch, var(--accent-cobalt) 35%, var(--border-hairline))"
                    : "var(--border-hairline)",
                }}
              >
                <div className="flex items-center justify-between mb-1">
                  <span className="font-semibold">{turn.speaker}</span>
                  <span
                    className="font-mono-tabular text-[11px]"
                    style={{ color: "var(--text-muted)" }}
                  >
                    {turn.at}
                  </span>
                </div>
                <p
                  className="leading-relaxed"
                  style={{ color: "var(--text-secondary)" }}
                >
                  {turn.text}
                </p>
              </div>
            ))}
          </div>
        )}
      </div>
    </section>
  );
}
