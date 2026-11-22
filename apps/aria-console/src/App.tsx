import { useEffect, useMemo, useState } from "react";
import { SHOWCASE_ACTS } from "./data/showcaseScenario";
import { detectCmrRuntime } from "./lib/cmrRuntime";
import type {
  CmrRuntimeInfo,
  LiveMeetingState,
  MeetingInsightItem,
} from "./types/aria";
import { TopCommandBar } from "./components/TopCommandBar";
import { PresenceStage } from "./components/PresenceStage";
import { SpecialistSkillsStrip } from "./components/SpecialistSkillsStrip";
import { IntelligenceStream } from "./components/IntelligenceStream";
import { AccountabilityStudio } from "./components/AccountabilityStudio";
import { SpatialEvidenceCanvas } from "./components/SpatialEvidenceCanvas";
import { FinOpsTelemetryDrawer } from "./components/FinOpsTelemetryDrawer";
import { CommandPalette } from "./components/CommandPalette";

function resolveTrustedApiHost(rawHost: string | null): string {
  if (!rawHost) return window.location.origin;
  try {
    const parsed = new URL(rawHost);
    if (parsed.origin === window.location.origin) {
      return parsed.origin;
    }
    const isLocalhost =
      parsed.protocol === "http:" &&
      (parsed.hostname === "localhost" || parsed.hostname === "127.0.0.1");
    const isTrustedCloudHost =
      parsed.protocol === "https:" &&
      parsed.hostname.endsWith(".cloudapp.azure.com");
    return isLocalhost || isTrustedCloudHost
      ? parsed.origin
      : window.location.origin;
  } catch {
    return window.location.origin;
  }
}

function resolveCallCoordinates(): {
  callId: string | null;
  liveKey: string | null;
  apiBaseUrl: string;
} {
  const params = new URLSearchParams(window.location.search);
  const pathMatch = window.location.pathname.match(/\/live\/([^/]+)/);
  const callId =
    params.get("callId") ??
    (pathMatch ? decodeURIComponent(pathMatch[1]) : null);

  // Security: per-call keys travel strictly in the URL fragment, never in query parameters.
  const hashParams = new URLSearchParams(
    window.location.hash.replace(/^#/, ""),
  );
  const devShowcaseKey = import.meta.env.DEV
    ? ((import.meta.env.VITE_SHOWCASE_KEY as string | undefined) ?? null)
    : null;
  const liveKey = hashParams.get("k") ?? (callId ? null : devShowcaseKey);
  const apiBaseUrl = resolveTrustedApiHost(params.get("host"));

  return { callId, liveKey, apiBaseUrl };
}

export function App() {
  // Default to Deep Obsidian per CMR development standards; user can toggle to Warm Alabaster Paper anytime
  const [darkMode, setDarkMode] = useState(true);
  const [cmr, setCmr] = useState<CmrRuntimeInfo>({ hostedInCmr: false });

  const [activeAct, setActiveAct] = useState<number>(3);
  const [autoAdvancing, setAutoAdvancing] = useState(false);
  const [guestShieldOverride, setGuestShieldOverride] = useState<
    boolean | null
  >(null);
  const [scrubMaxSec, setScrubMaxSec] = useState<number | null>(null);

  const [liveState, setLiveState] = useState<LiveMeetingState | null>(null);
  const [finOpsOpen, setFinOpsOpen] = useState(false);
  const [canvasOpen, setCanvasOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [avatarVideoActive, setAvatarVideoActive] = useState(false);

  const coords = useMemo(() => resolveCallCoordinates(), []);

  useEffect(() => {
    document.documentElement.classList.toggle("dark", darkMode);
  }, [darkMode]);

  useEffect(() => {
    void detectCmrRuntime().then(setCmr);
  }, []);

  // Global keyboard shortcuts: Cmd/Ctrl + K for Command Palette, Escape to close drawers
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setPaletteOpen((v) => !v);
      } else if (e.key === "Escape") {
        setPaletteOpen(false);
        setFinOpsOpen(false);
        setCanvasOpen(false);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);

  // Auto-advance through the 3 showcase acts when Replay is active
  useEffect(() => {
    if (!autoAdvancing || liveState) return;
    const timer = window.setInterval(() => {
      setActiveAct((curr) => (curr >= SHOWCASE_ACTS.length ? 1 : curr + 1));
    }, 5500);
    return () => window.clearInterval(timer);
  }, [autoAdvancing, liveState]);

  // Poll live meeting state: either a specific callId from a Teams chat link, or /api/live/active in CMR / Console
  useEffect(() => {
    if (!coords.liveKey) return;
    let cancelled = false;
    const endpoint = coords.callId
      ? `${coords.apiBaseUrl}/api/live/${encodeURIComponent(coords.callId)}`
      : `${coords.apiBaseUrl}/api/live/active`;

    async function poll() {
      try {
        const resp = await fetch(endpoint, {
          headers: { "X-Live-Key": coords.liveKey! },
        });
        if (cancelled) return;
        if (resp.status === 204 || resp.status === 404) {
          setLiveState(null);
          return;
        }
        if (!resp.ok) return;
        const raw = (await resp.json()) as Partial<LiveMeetingState> & {
          insights?: Array<
            Omit<MeetingInsightItem, "id" | "elapsedSec"> & {
              id?: string;
              elapsedSec?: number;
            }
          >;
        };

        const mappedInsights: MeetingInsightItem[] = (raw.insights ?? []).map(
          (ins) => ({
            id: ins.id ?? `live-ins-${String(ins.at ?? "")}-${ins.title}`,
            kind: ins.kind ?? "nudge",
            severity: ins.severity ?? "info",
            title: ins.title,
            text: ins.text,
            citation: ins.citation,
            at:
              typeof ins.at === "string" && ins.at.includes("T")
                ? new Date(ins.at).toISOString().slice(11, 19)
                : String(ins.at ?? ""),
            elapsedSec: ins.elapsedSec ?? 0,
          }),
        );

        setLiveState({
          callId: raw.callId ?? coords.callId ?? "active",
          agent: raw.agent ?? "Aria",
          subject: raw.subject ?? "Live Microsoft Teams Session",
          restricted: Boolean(raw.restricted),
          presence: raw.presence ?? "listening",
          speaking: Boolean(raw.speaking),
          caption: raw.caption ?? null,
          elapsed: raw.elapsed ?? 0,
          remaining: raw.remaining ?? null,
          alignment: raw.alignment ?? null,
          alignmentNote: raw.alignmentNote ?? null,
          trend: raw.trend ?? 0,
          voices: raw.voices ?? [],
          skills: raw.skills ?? [],
          uncovered: raw.uncovered ?? [],
          agenda: raw.agenda ?? {
            items: [],
            current: -1,
            currentElapsed: 0,
            done: [],
          },
          milestones: raw.milestones ?? [],
          summary: raw.summary ?? "",
          decisions: raw.decisions ?? [],
          actions: raw.actions ?? [],
          questions: raw.questions ?? [],
          parked: raw.parked ?? [],
          tracked: raw.tracked ?? {},
          visuals: raw.visuals ?? [],
          finops: raw.finops ?? {
            voiceModel: "gpt-realtime-2.1",
            scribeModel: "gpt-5.4-mini",
            audioMinutes: Math.max(0.1, (raw.elapsed ?? 60) / 60),
            scribePasses: 0,
            workIqCalls: 0,
            workIqAvgMs: 0,
            voiceCostUsd: 0,
            scribeCostUsd: 0,
            workIqCostUsd: 0,
            totalLiveCostUsd: 0,
            costPerMinuteUsd: 0.018,
          },
          insights: mappedInsights,
          searching: raw.searching ?? null,
          transcript: raw.transcript ?? [],
        });
      } catch {
        // Keep last state on transient blip.
      }
    }

    void poll();
    const handle = window.setInterval(() => void poll(), 1600);
    return () => {
      cancelled = true;
      window.clearInterval(handle);
    };
  }, [coords]);

  const currentShowcase =
    SHOWCASE_ACTS.find((a) => a.id === activeAct) ?? SHOWCASE_ACTS[2];

  const effectiveState: LiveMeetingState = useMemo(() => {
    const base = liveState ?? currentShowcase.state;
    const isRestricted =
      guestShieldOverride === null ? base.restricted : guestShieldOverride;
    if (!isRestricted) {
      return {
        ...base,
        restricted: false,
      };
    }
    return {
      ...base,
      restricted: true,
      searching: null,
      visuals: [],
    };
  }, [liveState, currentShowcase, guestShieldOverride]);

  return (
    <div className="min-h-dvh lg:h-dvh w-full flex flex-col lg:overflow-hidden">
      <TopCommandBar
        state={effectiveState}
        cmr={cmr}
        isLiveConnected={Boolean(liveState)}
        activeAct={activeAct}
        onSelectAct={(id) => {
          setAutoAdvancing(false);
          setScrubMaxSec(null);
          setActiveAct(id);
        }}
        autoAdvancing={autoAdvancing}
        onToggleAutoAdvance={() => {
          if (!autoAdvancing) setActiveAct(1);
          setAutoAdvancing((v) => !v);
        }}
        scrubMaxSec={scrubMaxSec}
        onScrubMilestone={setScrubMaxSec}
        onToggleGuestShield={() =>
          setGuestShieldOverride((prev) =>
            prev === null ? !effectiveState.restricted : !prev,
          )
        }
        onOpenFinOps={() => setFinOpsOpen(true)}
        onOpenCommandPalette={() => setPaletteOpen(true)}
        darkMode={darkMode}
        onToggleDarkMode={() => setDarkMode((d) => !d)}
        avatarVideoActive={avatarVideoActive}
      />

      {/* 3-Column Swiss Clinical Editorial Workspace */}
      <main className="flex-1 min-h-0 flex flex-col lg:flex-row lg:overflow-hidden">
        <PresenceStage
          state={effectiveState}
          spokenBriefing={
            liveState ? effectiveState.summary : currentShowcase.spokenBriefing
          }
          apiBaseUrl={coords.apiBaseUrl}
          liveKey={coords.liveKey}
          isLiveConnected={Boolean(liveState)}
          onOpenCanvas={() => setCanvasOpen(true)}
          onAvatarActiveChange={setAvatarVideoActive}
        />

        <div className="flex-1 min-w-0 min-h-[420px] lg:min-h-0 flex flex-col lg:overflow-hidden">
          <SpecialistSkillsStrip
            skills={effectiveState.skills}
            tracked={effectiveState.tracked}
          />
          <IntelligenceStream
            summary={effectiveState.summary}
            insights={effectiveState.insights}
            transcript={effectiveState.transcript}
            restricted={effectiveState.restricted}
            scrubMaxSec={scrubMaxSec}
          />
        </div>

        <AccountabilityStudio state={effectiveState} cmr={cmr} />
      </main>

      {/* Progressive-Disclosure Modals & Drawers */}
      <SpatialEvidenceCanvas
        open={canvasOpen}
        onClose={() => setCanvasOpen(false)}
        visuals={effectiveState.visuals}
        insights={effectiveState.insights}
        decisions={effectiveState.decisions}
        restricted={effectiveState.restricted}
        apiBaseUrl={coords.apiBaseUrl}
        callId={effectiveState.callId}
        liveKey={coords.liveKey}
        isLiveConnected={Boolean(liveState)}
      />

      <FinOpsTelemetryDrawer
        open={finOpsOpen}
        onClose={() => setFinOpsOpen(false)}
        finops={effectiveState.finops}
        avatarVideoActive={avatarVideoActive}
      />

      <CommandPalette
        open={paletteOpen}
        onClose={() => setPaletteOpen(false)}
        onOpenFinOps={() => setFinOpsOpen(true)}
        onOpenCanvas={() => setCanvasOpen(true)}
        onToggleGuestShield={() =>
          setGuestShieldOverride((prev) =>
            prev === null ? !effectiveState.restricted : !prev,
          )
        }
        onToggleDarkMode={() => setDarkMode((d) => !d)}
        darkMode={darkMode}
        onSelectAct={(id) => {
          setAutoAdvancing(false);
          setActiveAct(id);
        }}
      />
    </div>
  );
}
