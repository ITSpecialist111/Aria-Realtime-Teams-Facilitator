import { useState } from "react";
import {
  Activity,
  Calculator,
  ChevronDown,
  Cpu,
  DollarSign,
  Server,
  Sparkles,
  X,
} from "lucide-react";
import type { FinOpsSnapshot } from "../types/aria";

interface FinOpsTelemetryDrawerProps {
  open: boolean;
  onClose: () => void;
  finops: FinOpsSnapshot;
  avatarVideoActive: boolean;
}

export function FinOpsTelemetryDrawer({
  open,
  onClose,
  finops,
  avatarVideoActive,
}: FinOpsTelemetryDrawerProps) {
  const [expandedRow, setExpandedRow] = useState<string | null>("voice-live");

  // Interactive Enterprise TCO Calculator state (defaults calibrated to 100-seat enterprise deployment)
  const [seats, setSeats] = useState(100);
  const [meetingsPerWeek, setMeetingsPerWeek] = useState(120);
  const [avgMinutes, setAvgMinutes] = useState(35);
  const [nightlyDealloc, setNightlyDealloc] = useState(true);
  const [avatarPolicy, setAvatarPolicy] = useState<
    "aura-only" | "exec-15" | "always"
  >("aura-only");

  if (!open) return null;

  const monthlyMinutes = meetingsPerWeek * 4.33 * avgMinutes;
  const coreAiMonthly = monthlyMinutes * 0.018; // $0.0108 Voice Live + $0.0022 Scribe + $0.0050 Work IQ
  const vmMonthly = nightlyDealloc ? 58.4 : 186.15; // D4s_v5 Sweden Central 7h*22d vs 24x7
  const avatarShare =
    avatarPolicy === "always" ? 1 : avatarPolicy === "exec-15" ? 0.15 : 0;
  const avatarMonthly = monthlyMinutes * avatarShare * 0.085;
  const cmrMonthly = 0; // Hosted in M365 Copilot Managed Runtime

  const totalMonthlyUsd =
    coreAiMonthly + vmMonthly + avatarMonthly + cmrMonthly;
  const perSeatMonthlyUsd = totalMonthlyUsd / Math.max(1, seats);

  const waterfallRows = [
    {
      id: "voice-live",
      layer: "1 · Azure AI Voice Live (24kHz Duplex Audio)",
      model: finops.voiceModel,
      rateLabel: "$0.0108 / min",
      thisCallUsd: finops.voiceCostUsd,
      detail: `Streaming 24kHz PCM over WebSocket with semantic server-VAD (100ms frame batching). Live call audio: ${finops.audioMinutes.toFixed(1)} min.`,
    },
    {
      id: "scribe-router",
      layer: "2 · Async 15s Scribe, Alignment & Skill Router",
      model: finops.scribeModel,
      rateLabel: "$0.0022 / min",
      thisCallUsd: finops.scribeCostUsd,
      detail: `Runs out-of-band every 15s on compact JSON schema (~1.9k prompt / 280 completion tokens). Completed ${finops.scribePasses} passes this call with zero voice-path blocking.`,
    },
    {
      id: "work-iq",
      layer: "3 · Microsoft 365 Work IQ Semantic Grounding",
      model: "workiq_retrieve (MCP)",
      rateLabel: "$0.0050 / min",
      thisCallUsd: finops.workIqCostUsd,
      detail: `15s cooldown + query deduplication caps consumption at ~0.1 Copilot Credit per grounded retrieval (${finops.workIqCalls} retrievals this call · avg ${(finops.workIqAvgMs / 1000).toFixed(1)}s).`,
    },
    {
      id: "cmr-host",
      layer: "4 · Copilot Managed Runtime (CMR) App & OTel",
      model: "@microsoft/cmr-apps v1.0",
      rateLabel: "$0.0000 / min",
      thisCallUsd: 0,
      detail:
        "Replaces standalone Azure App Service / Container Apps hosting. Delivered inside the M365 Copilot Managed Runtime sandbox with native Entra auth and Agent 365 S2S OpenTelemetry.",
    },
    {
      id: "did-avatar",
      layer: "5 · D-ID Humanoid WebRTC Stream (Optional)",
      model: avatarVideoActive
        ? "D-ID LiveKit + Ada HD"
        : "60fps GSAP Aura (Default)",
      rateLabel: avatarVideoActive ? "$0.0850 / min" : "$0.0000 / min",
      thisCallUsd: avatarVideoActive ? finops.audioMinutes * 0.085 : 0,
      detail:
        "Zero-cost 60fps GSAP SVG Radial Voice Aura runs by default. Executive Humanoid WebRTC lip-sync (`en-GB-AdaMultilingualNeural` paired with `en-GB-Ada:DragonHDLatestNeural`) activates strictly on demand via ephemeral 1h client keys.",
    },
    {
      id: "media-vm",
      layer: "6 · Teams Real-Time Media Host (Standard_D4s_v5)",
      model: "Skype.Bots.Media RTP",
      rateLabel: nightlyDealloc ? "$58.40 / mo" : "$186.15 / mo",
      thisCallUsd: 0,
      detail:
        "Fixed-rate Windows media host for raw TCP/8445 RTP audio + NV12 video ($0.0000 marginal cost per call; accounted in Monthly TCO below). Scheduled 22:00 UTC auto-deallocation saves $127.75/mo (68.6%).",
    },
  ];

  const latencySpans = [
    { label: "Teams RTP 20ms Audio Copy", p50: "4 ms", pct: 6 },
    { label: "NV12 Live Video Tile Render", p50: "19 ms", pct: 14 },
    { label: "Async 15s Scribe JSON Pass", p50: "680 ms", pct: 42 },
    { label: "Voice Live Turn Response (p50)", p50: "1,040 ms", pct: 58 },
    {
      label: "Work IQ Semantic Retrieve (non-blocking)",
      p50: `${finops.workIqAvgMs > 0 ? finops.workIqAvgMs.toLocaleString() : "6,420"} ms`,
      pct: 88,
    },
  ];

  return (
    <div
      className="fixed inset-0 z-50 flex justify-end backdrop-blur-xs"
      style={{ backgroundColor: "rgba(8, 11, 16, 0.55)" }}
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-label="Executive FinOps, Unit Economics & Telemetry"
        onClick={(e) => e.stopPropagation()}
        className="w-full max-w-[580px] h-full border-l flex flex-col overflow-hidden shadow-2xl"
        style={{
          backgroundColor: "var(--bg-surface)",
          borderColor: "var(--border-strong)",
        }}
      >
        {/* Header */}
        <div
          className="px-5 py-4 border-b flex items-start justify-between gap-3"
          style={{ borderColor: "var(--border-hairline)" }}
        >
          <div>
            <div className="flex items-center gap-2">
              <DollarSign
                className="w-4 h-4"
                style={{ color: "var(--accent-emerald)" }}
              />
              <h2 className="text-sm font-semibold">
                Executive FinOps & Unit Economics ("What's the cost of this?")
              </h2>
            </div>
            <p className="text-xs mt-0.5" style={{ color: "var(--text-muted)" }}>
              Live call metering, 6-layer architecture cost waterfall, and enterprise TCO model.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close FinOps drawer"
            className="p-1.5 rounded-lg border cursor-pointer"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
            }}
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        {/* Scrollable Body */}
        <div className="flex-1 overflow-y-auto p-5 space-y-5">
          {/* Level 1: Headline KPI Strip */}
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-2.5">
            <div
              className="rounded-xl border p-3"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <span
                className="text-[10px] font-mono-tabular uppercase"
                style={{ color: "var(--text-muted)" }}
              >
                Core Voice + IQ
              </span>
              <div className="text-lg font-semibold font-mono-tabular mt-0.5">
                $0.018<span className="text-xs font-normal">/m</span>
              </div>
              <span
                className="text-[10px]"
                style={{ color: "var(--accent-emerald)" }}
              >
                $0.81 per 45m call
              </span>
            </div>

            <div
              className="rounded-xl border p-3"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <span
                className="text-[10px] font-mono-tabular uppercase"
                style={{ color: "var(--text-muted)" }}
              >
                {seats}-Seat TCO
              </span>
              <div className="text-lg font-semibold font-mono-tabular mt-0.5">
                ${perSeatMonthlyUsd.toFixed(2)}
                <span className="text-xs font-normal">/usr</span>
              </div>
              <span
                className="text-[10px]"
                style={{ color: "var(--text-secondary)" }}
              >
                ${totalMonthlyUsd.toFixed(0)}/mo total
              </span>
            </div>

            <div
              className="rounded-xl border p-3"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <span
                className="text-[10px] font-mono-tabular uppercase"
                style={{ color: "var(--text-muted)" }}
              >
                VM Dealloc Save
              </span>
              <div
                className="text-lg font-semibold font-mono-tabular mt-0.5"
                style={{
                  color: nightlyDealloc
                    ? "var(--accent-emerald)"
                    : "var(--text-muted)",
                }}
              >
                {nightlyDealloc ? "68.6%" : "0.0%"}
              </div>
              <span
                className="text-[10px]"
                style={{ color: "var(--text-secondary)" }}
              >
                {nightlyDealloc ? "22:00 UTC auto-stop" : "24×7 always-on"}
              </span>
            </div>

            <div
              className="rounded-xl border p-3"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <span
                className="text-[10px] font-mono-tabular uppercase"
                style={{ color: "var(--text-muted)" }}
              >
                CMR App Host
              </span>
              <div className="text-lg font-semibold font-mono-tabular mt-0.5">
                $0.00
              </div>
              <span
                className="text-[10px]"
                style={{ color: "var(--accent-cobalt)" }}
              >
                Replaces App Service
              </span>
            </div>
          </div>

          {/* Level 2: 6-Layer Expandable Unit-Economics Waterfall */}
          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <span
                className="inline-flex items-center gap-1.5 text-[11px] font-mono-tabular uppercase tracking-wider font-semibold"
                style={{ color: "var(--text-muted)" }}
              >
                <Cpu className="w-3.5 h-3.5" />
                6-Layer Unit-Economics Waterfall (Click to drill down)
              </span>
              <span
                className="text-xs font-mono-tabular"
                style={{ color: "var(--text-secondary)" }}
              >
                This call: $
                {(
                  finops.totalLiveCostUsd +
                  (avatarVideoActive ? finops.audioMinutes * 0.085 : 0)
                ).toFixed(4)}
              </span>
            </div>

            <div className="space-y-1.5">
              {waterfallRows.map((row) => {
                const isOpen = expandedRow === row.id;
                return (
                  <div
                    key={row.id}
                    className="rounded-xl border overflow-hidden"
                    style={{
                      backgroundColor: "var(--bg-elevated)",
                      borderColor: isOpen
                        ? "var(--border-strong)"
                        : "var(--border-hairline)",
                    }}
                  >
                    <button
                      type="button"
                      onClick={() =>
                        setExpandedRow(isOpen ? null : row.id)
                      }
                      className="w-full px-3.5 py-2.5 flex items-center justify-between gap-2 text-left text-xs cursor-pointer"
                    >
                      <div className="min-w-0">
                        <div className="font-medium truncate">{row.layer}</div>
                        <div
                          className="text-[11px] font-mono-tabular"
                          style={{ color: "var(--text-muted)" }}
                        >
                          {row.model}
                        </div>
                      </div>
                      <div className="flex items-center gap-2.5 shrink-0 font-mono-tabular">
                        <span className="font-semibold">{row.rateLabel}</span>
                        <ChevronDown
                          className={`w-3.5 h-3.5 transition-transform ${
                            isOpen ? "rotate-180" : ""
                          }`}
                          style={{ color: "var(--text-muted)" }}
                        />
                      </div>
                    </button>
                    {isOpen && (
                      <div
                        className="px-3.5 pb-3 pt-2 border-t text-xs leading-relaxed flex items-start justify-between gap-3"
                        style={{
                          borderColor: "var(--border-hairline)",
                          backgroundColor: "var(--bg-surface)",
                          color: "var(--text-secondary)",
                        }}
                      >
                        <p>{row.detail}</p>
                        <span
                          className="font-mono-tabular text-[11px] shrink-0 px-2 py-0.5 rounded border"
                          style={{
                            borderColor: "var(--border-hairline)",
                            color: "var(--text-primary)",
                          }}
                        >
                          ${row.thisCallUsd.toFixed(4)}
                        </span>
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>

          {/* Level 3: Interactive Enterprise Monthly TCO Simulator */}
          <div
            className="rounded-xl border p-4 space-y-3.5"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
            }}
          >
            <div className="flex items-center justify-between">
              <span className="inline-flex items-center gap-1.5 text-xs font-semibold">
                <Calculator
                  className="w-3.5 h-3.5"
                  style={{ color: "var(--accent-cobalt)" }}
                />
                Interactive Enterprise Monthly TCO Simulator
              </span>
              <span className="font-mono-tabular text-sm font-semibold">
                ${totalMonthlyUsd.toFixed(2)} / mo
              </span>
            </div>

            <div className="space-y-2.5 text-xs">
              <div>
                <div className="flex justify-between mb-1">
                  <span style={{ color: "var(--text-secondary)" }}>
                    Licensed Organisation Seats
                  </span>
                  <span className="font-mono-tabular font-semibold">
                    {seats} seats
                  </span>
                </div>
                <input
                  type="range"
                  min={25}
                  max={500}
                  step={25}
                  value={seats}
                  onChange={(e) => setSeats(Number(e.target.value))}
                  className="w-full accent-blue-600 cursor-pointer"
                />
              </div>

              <div>
                <div className="flex justify-between mb-1">
                  <span style={{ color: "var(--text-secondary)" }}>
                    Facilitated Meetings / Week
                  </span>
                  <span className="font-mono-tabular font-semibold">
                    {meetingsPerWeek} meetings/wk ({Math.round(monthlyMinutes).toLocaleString()} min/mo)
                  </span>
                </div>
                <input
                  type="range"
                  min={20}
                  max={400}
                  step={10}
                  value={meetingsPerWeek}
                  onChange={(e) => setMeetingsPerWeek(Number(e.target.value))}
                  className="w-full accent-blue-600 cursor-pointer"
                />
              </div>

              <div>
                <div className="flex justify-between mb-1">
                  <span style={{ color: "var(--text-secondary)" }}>
                    Average Meeting Duration
                  </span>
                  <span className="font-mono-tabular font-semibold">
                    {avgMinutes} min
                  </span>
                </div>
                <input
                  type="range"
                  min={15}
                  max={90}
                  step={5}
                  value={avgMinutes}
                  onChange={(e) => setAvgMinutes(Number(e.target.value))}
                  className="w-full accent-blue-600 cursor-pointer"
                />
              </div>

              <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
                <label className="inline-flex items-center gap-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={nightlyDealloc}
                    onChange={(e) => setNightlyDealloc(e.target.checked)}
                    className="rounded accent-blue-600"
                  />
                  <span>
                    Business-hours VM schedule (saves{" "}
                    <strong className="font-mono-tabular">$127.75/mo</strong>{" "}
                    vs 24×7)
                  </span>
                </label>

                <select
                  value={avatarPolicy}
                  onChange={(e) =>
                    setAvatarPolicy(
                      e.target.value as "aura-only" | "exec-15" | "always",
                    )
                  }
                  aria-label="Avatar streaming tier"
                  className="rounded-md border px-2 py-1 text-xs"
                  style={{
                    backgroundColor: "var(--bg-surface)",
                    borderColor: "var(--border-hairline)",
                    color: "var(--text-primary)",
                  }}
                >
                  <option value="aura-only">GSAP Voice Aura ($0/min)</option>
                  <option value="exec-15">D-ID on Exec Bridges (15%)</option>
                  <option value="always">D-ID Humanoid Always-On (100%)</option>
                </select>
              </div>
            </div>

            <div
              className="rounded-lg border p-3 flex items-center justify-between text-xs"
              style={{
                backgroundColor: "var(--bg-surface)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <span className="inline-flex items-center gap-1.5">
                <Sparkles
                  className="w-3.5 h-3.5"
                  style={{ color: "var(--accent-emerald)" }}
                />
                Effective Per-User Monthly Run-Rate
              </span>
              <span className="font-mono-tabular text-sm font-semibold">
                ${perSeatMonthlyUsd.toFixed(2)} / seat / mo
              </span>
            </div>
          </div>

          {/* Level 4: Agent 365 OpenTelemetry Latency Spans */}
          <div className="space-y-2">
            <span
              className="inline-flex items-center gap-1.5 text-[11px] font-mono-tabular uppercase tracking-wider font-semibold"
              style={{ color: "var(--text-muted)" }}
            >
              <Activity className="w-3.5 h-3.5" />
              Agent 365 OpenTelemetry Pipeline Latency (p50)
            </span>

            <div
              className="rounded-xl border p-3.5 space-y-2.5"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              {latencySpans.map((span) => (
                <div key={span.label} className="space-y-1 text-xs">
                  <div className="flex justify-between">
                    <span style={{ color: "var(--text-secondary)" }}>
                      {span.label}
                    </span>
                    <span className="font-mono-tabular font-medium">
                      {span.p50}
                    </span>
                  </div>
                  <div
                    className="h-1.5 rounded-full overflow-hidden"
                    style={{ backgroundColor: "var(--bg-subtle)" }}
                  >
                    <div
                      className="h-full rounded-full"
                      style={{
                        width: `${span.pct}%`,
                        backgroundColor: "var(--accent-cobalt)",
                      }}
                    />
                  </div>
                </div>
              ))}

              <div
                className="pt-2 border-t flex items-center gap-2 text-[11px]"
                style={{
                  borderColor: "var(--border-hairline)",
                  color: "var(--text-muted)",
                }}
              >
                <Server className="w-3.5 h-3.5 shrink-0" />
                <span>
                  Work IQ & 15s Scribe execute on non-blocking background tasks so realtime voice turn latency stays ~1.0s.
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
