import { useEffect, useRef, useState } from "react";
import gsap from "gsap";
import type {
  AgentManager,
  StreamingState as StreamingStateType,
} from "@d-id/client-sdk";
import {
  Camera,
  Network,
  Square,
  TrendingDown,
  TrendingUp,
  UserCheck,
  Video,
  Volume2,
  Waves,
} from "lucide-react";
import type { LiveMeetingState } from "../types/aria";

interface PresenceStageProps {
  state: LiveMeetingState;
  spokenBriefing: string;
  apiBaseUrl: string;
  liveKey: string | null;
  isLiveConnected: boolean;
  onOpenCanvas: () => void;
  onAvatarActiveChange: (active: boolean) => void;
}

export function PresenceStage({
  state,
  spokenBriefing,
  apiBaseUrl,
  liveKey,
  isLiveConnected,
  onOpenCanvas,
  onAvatarActiveChange,
}: PresenceStageProps) {
  const [stageMode, setStageMode] = useState<"halo" | "humanoid">("halo");
  const [didStatus, setDidStatus] = useState<
    "idle" | "connecting" | "connected" | "speaking" | "unconfigured" | "error"
  >("idle");
  const [didNote, setDidNote] = useState<string>(
    "60fps GSAP Radial Voice Aura · en-GB-SoniaNeural",
  );
  const [localSpeaking, setLocalSpeaking] = useState(false);
  const [liveSpeechText, setLiveSpeechText] = useState<string | null>(null);

  const videoRef = useRef<HTMLVideoElement | null>(null);
  const managerRef = useRef<AgentManager | null>(null);
  const srcObjectRef = useRef<MediaStream | null>(null);

  const ringOuterRef = useRef<SVGCircleElement | null>(null);
  const ringMidRef = useRef<SVGCircleElement | null>(null);
  const ringInnerRef = useRef<SVGCircleElement | null>(null);

  const effectiveSpeaking =
    state.speaking || localSpeaking || didStatus === "speaking";
  const effectiveSearching = Boolean(state.searching);

  useEffect(() => {
    onAvatarActiveChange(
      stageMode === "humanoid" &&
        (didStatus === "connected" || didStatus === "speaking"),
    );
  }, [stageMode, didStatus, onAvatarActiveChange]);

  // 60fps GSAP quickSetter radial aura driven by state (speaking / searching / listening)
  useEffect(() => {
    const outer = ringOuterRef.current;
    const mid = ringMidRef.current;
    const inner = ringInnerRef.current;
    if (!outer || !mid || !inner) return;

    const prefersReduced = window.matchMedia(
      "(prefers-reduced-motion: reduce)",
    ).matches;
    if (prefersReduced) return;

    const setOuterScale = gsap.quickSetter(outer, "scale");
    const setMidScale = gsap.quickSetter(mid, "scale");
    const setInnerScale = gsap.quickSetter(inner, "scale");

    gsap.set([outer, mid, inner], { transformOrigin: "50% 50%" });

    const ticker = (time: number) => {
      const speed = effectiveSpeaking ? 6.8 : effectiveSearching ? 4.0 : 1.7;
      const amp = effectiveSpeaking ? 0.14 : effectiveSearching ? 0.08 : 0.035;
      setOuterScale(1 + Math.sin(time * speed) * amp);
      setMidScale(1 + Math.sin(time * speed * 1.25 + 1.1) * (amp * 0.85));
      setInnerScale(1 + Math.cos(time * speed * 1.5 + 0.4) * (amp * 0.65));
    };

    gsap.ticker.add(ticker);
    return () => {
      gsap.ticker.remove(ticker);
    };
  }, [effectiveSpeaking, effectiveSearching]);

  const mountedRef = useRef(true);
  const connectingRef = useRef(false);

  // Cleanup D-ID WebRTC session + Web Speech on unmount
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      window.speechSynthesis?.cancel();
      const mgr = managerRef.current;
      managerRef.current = null;
      void mgr?.disconnect().catch(() => undefined);
    };
  }, []);

  function switchToHalo() {
    setStageMode("halo");
    const mgr = managerRef.current;
    managerRef.current = null;
    srcObjectRef.current = null;
    if (videoRef.current) {
      videoRef.current.srcObject = null;
      videoRef.current.removeAttribute("src");
    }
    setDidStatus("idle");
    setDidNote("60fps GSAP Radial Voice Aura · en-GB-SoniaNeural");
    if (mgr) {
      void mgr.disconnect().catch(() => undefined);
    }
  }

  async function connectHumanoidAvatar() {
    setStageMode("humanoid");
    if (connectingRef.current) {
      return;
    }
    if (
      managerRef.current &&
      (didStatus === "connected" || didStatus === "speaking")
    ) {
      return;
    }

    connectingRef.current = true;
    setDidStatus("connecting");
    setDidNote("Minting domain-scoped D-ID WebRTC session...");

    try {
      const headers: Record<string, string> = {
        "Content-Type": "application/json",
      };
      if (liveKey) {
        headers["X-Live-Key"] = liveKey;
      }

      const resp = await fetch(`${apiBaseUrl}/api/avatar/session`, {
        method: "POST",
        headers,
        body: JSON.stringify({
          callId: state.callId,
          origin: window.location.origin,
        }),
      });

      if (!resp.ok) {
        if (!mountedRef.current) return;
        setDidStatus("unconfigured");
        setDidNote(
          "D-ID host key not active yet — using 60fps GSAP Voice Aura ($0/min). Run Set-DidAvatarKey.ps1 to enable WebRTC video.",
        );
        return;
      }

      const data = (await resp.json()) as {
        configured?: boolean;
        agentId?: string;
        clientKey?: string;
        message?: string;
      };

      if (!data.configured || !data.agentId || !data.clientKey) {
        if (!mountedRef.current) return;
        setDidStatus("unconfigured");
        setDidNote(
          data.message ??
            "D-ID API key not set on host — running zero-cost 60fps GSAP Voice Aura.",
        );
        return;
      }

      const { createAgentManager, StreamingState, StreamType } = await import(
        "@d-id/client-sdk"
      );

      const manager = await createAgentManager(data.agentId, {
        auth: { type: "key", clientKey: data.clientKey },
        callbacks: {
          onSrcObjectReady(stream: MediaStream) {
            if (!mountedRef.current) return;
            srcObjectRef.current = stream;
            if (videoRef.current) {
              videoRef.current.srcObject = stream;
              void videoRef.current.play().catch(() => undefined);
            }
          },
          onVideoStateChange(videoState: StreamingStateType) {
            if (!mountedRef.current) return;
            if (videoState === StreamingState.Start) {
              setDidStatus("speaking");
              if (
                videoRef.current &&
                srcObjectRef.current &&
                videoRef.current.srcObject !== srcObjectRef.current
              ) {
                videoRef.current.src = "";
                videoRef.current.srcObject = srcObjectRef.current;
                void videoRef.current.play().catch(() => undefined);
              }
            } else {
              setDidStatus("connected");
              if (
                manager.getStreamType() === StreamType.Legacy &&
                videoRef.current &&
                manager.agent.idle_video
              ) {
                videoRef.current.srcObject = null;
                videoRef.current.src = manager.agent.idle_video;
                videoRef.current.loop = true;
                void videoRef.current.play().catch(() => undefined);
              }
            }
          },
          onError(err: Error) {
            if (!mountedRef.current) return;
            setDidStatus("error");
            setDidNote(`D-ID stream fallback: ${err.message}`);
          },
        },
      });

      if (!mountedRef.current) {
        void manager.disconnect().catch(() => undefined);
        return;
      }

      managerRef.current = manager;
      await manager.connect();

      if (!mountedRef.current || managerRef.current !== manager) {
        void manager.disconnect().catch(() => undefined);
        return;
      }

      setDidStatus("connected");
      setDidNote("D-ID WebRTC Live · Microsoft en-GB-SoniaNeural");
    } catch {
      if (!mountedRef.current) return;
      setDidStatus("unconfigured");
      setDidNote(
        "D-ID endpoint offline in standalone preview — 60fps GSAP Voice Aura active.",
      );
    } finally {
      connectingRef.current = false;
    }
  }

  async function handleSpeakSummary() {
    let textToSpeak = spokenBriefing;

    if (isLiveConnected && liveKey) {
      try {
        const resp = await fetch(
          `${apiBaseUrl}/api/live/${encodeURIComponent(state.callId)}/summary`,
          {
            method: "POST",
            headers: {
              "X-Live-Key": liveKey,
            },
          },
        );
        if (resp.ok) {
          const payload = (await resp.json()) as { speech?: string };
          if (payload.speech) textToSpeak = payload.speech;
        }
      } catch {
        // Fallback to local summary.
      }
    }

    setLiveSpeechText(textToSpeak);

    if (
      stageMode === "humanoid" &&
      managerRef.current &&
      (didStatus === "connected" || didStatus === "speaking")
    ) {
      window.speechSynthesis?.cancel();
      setLocalSpeaking(false);
      try {
        if (didStatus === "speaking" && managerRef.current.isInterruptAvailable()) {
          managerRef.current.interrupt();
        }
        setDidStatus("speaking");
        await managerRef.current.speak({ type: "text", input: textToSpeak });
        return;
      } catch {
        // Fall through to browser neural TTS + GSAP aura only if D-ID speak threw.
      }
    }

    // Crisp Web Speech fallback synchronized with the 60fps GSAP radial voice aura
    if (typeof window.speechSynthesis !== "undefined") {
      window.speechSynthesis.cancel();
      const utter = new SpeechSynthesisUtterance(textToSpeak);
      const voices = window.speechSynthesis.getVoices();
      const british =
        voices.find((v) => v.name.includes("Sonia")) ??
        voices.find((v) => v.lang === "en-GB");
      if (british) utter.voice = british;
      utter.rate = 1.03;
      utter.onstart = () => setLocalSpeaking(true);
      utter.onend = () => setLocalSpeaking(false);
      utter.onerror = () => setLocalSpeaking(false);
      setLocalSpeaking(true);
      window.speechSynthesis.speak(utter);
    } else {
      setLocalSpeaking(true);
      globalThis.setTimeout(() => setLocalSpeaking(false), 6000);
    }
  }

  function handleInterrupt() {
    window.speechSynthesis?.cancel();
    setLocalSpeaking(false);
    setLiveSpeechText(null);
    if (managerRef.current && managerRef.current.isInterruptAvailable()) {
      try {
        managerRef.current.interrupt();
      } catch {
        // Ignore if stream already stopped.
      }
    }
  }

  const auraStroke = effectiveSearching
    ? "var(--accent-violet)"
    : effectiveSpeaking
      ? "var(--accent-cobalt)"
      : "var(--accent-emerald)";

  return (
    <aside
      className="w-full lg:w-[308px] shrink-0 flex flex-col gap-3 p-3.5 border-b lg:border-b-0 lg:border-r overflow-y-auto"
      style={{
        backgroundColor: "var(--bg-surface)",
        borderColor: "var(--border-hairline)",
      }}
    >
      {/* E2: Aria Humanoid Avatar & 60fps GSAP Voice Aura Stage */}
      <div
        className={`relative rounded-xl border p-3.5 overflow-hidden transition-colors ${
          effectiveSpeaking || effectiveSearching
            ? "stage-beam-active stage-beam-motion"
            : ""
        }`}
        style={{
          backgroundColor: "var(--bg-elevated)",
          borderColor: "var(--border-hairline)",
        }}
      >
        <div className="flex items-center justify-between gap-2 mb-2.5">
          <div className="flex items-center gap-1.5">
            <span
              className="h-2 w-2 rounded-full"
              style={{ backgroundColor: auraStroke }}
            />
            <span className="text-[11px] font-semibold uppercase tracking-wider font-mono-tabular">
              {effectiveSearching
                ? "Searching Work IQ"
                : effectiveSpeaking
                  ? "Aria Speaking"
                  : "Aria Listening"}
            </span>
          </div>

          {/* Stage Mode Switch: Voice Aura vs D-ID Humanoid */}
          <div
            className="inline-flex rounded-md p-0.5 border text-[10px] font-medium"
            style={{
              backgroundColor: "var(--bg-surface)",
              borderColor: "var(--border-hairline)",
            }}
          >
            <button
              type="button"
              onClick={switchToHalo}
              className="px-2 py-0.5 rounded flex items-center gap-1 cursor-pointer"
              style={{
                backgroundColor:
                  stageMode === "halo" ? "var(--bg-elevated)" : "transparent",
                color:
                  stageMode === "halo"
                    ? "var(--text-primary)"
                    : "var(--text-muted)",
              }}
            >
              <Waves className="w-3 h-3" />
              Aura
            </button>
            <button
              type="button"
              onClick={() => void connectHumanoidAvatar()}
              className="px-2 py-0.5 rounded flex items-center gap-1 cursor-pointer"
              style={{
                backgroundColor:
                  stageMode === "humanoid"
                    ? "var(--bg-elevated)"
                    : "transparent",
                color:
                  stageMode === "humanoid"
                    ? "var(--text-primary)"
                    : "var(--text-muted)",
              }}
            >
              <Video className="w-3 h-3" />
              D-ID
            </button>
          </div>
        </div>

        {/* Visual Stage Viewport */}
        <div
          className="relative h-48 rounded-lg border flex items-center justify-center pb-6 overflow-hidden"
          style={{
            backgroundColor: "var(--bg-canvas)",
            borderColor: "var(--border-hairline)",
          }}
        >
          {/* 60fps GSAP Radial Voice Aura (always mounted as seamless zero-latency layer) */}
          <svg
            viewBox="0 0 200 200"
            className="w-36 h-36 select-none"
            aria-label="Aria radial voice aura"
          >
            <circle
              ref={ringOuterRef}
              cx="100"
              cy="100"
              r="68"
              fill="none"
              stroke={auraStroke}
              strokeOpacity="0.24"
              strokeWidth="1.25"
              strokeDasharray="4 4"
            />
            <circle
              ref={ringMidRef}
              cx="100"
              cy="100"
              r="48"
              fill="none"
              stroke={auraStroke}
              strokeOpacity="0.48"
              strokeWidth="1.5"
            />
            <circle
              ref={ringInnerRef}
              cx="100"
              cy="100"
              r="28"
              fill={auraStroke}
              fillOpacity="0.16"
              stroke={auraStroke}
              strokeWidth="2"
            />
            <circle cx="100" cy="100" r="6" fill={auraStroke} />
          </svg>

          {/* D-ID WebRTC Video Stream Layer */}
          <video
            ref={videoRef}
            playsInline
            className={`absolute inset-0 w-full h-full object-cover transition-opacity duration-300 ${
              stageMode === "humanoid" &&
              (didStatus === "connected" || didStatus === "speaking")
                ? "opacity-100"
                : "opacity-0 pointer-events-none"
            }`}
          />

          <div
            className="absolute bottom-2 left-2.5 right-2.5 flex items-center justify-between text-[10px] font-mono-tabular px-2 py-1 rounded border backdrop-blur-xs"
            style={{
              backgroundColor:
                "color-mix(in oklch, var(--bg-surface) 84%, transparent)",
              borderColor: "var(--border-hairline)",
              color: "var(--text-secondary)",
            }}
          >
            <span className="truncate">{didNote}</span>
          </div>
        </div>

        {/* Live Caption / Active Utterance */}
        {(liveSpeechText || state.caption || state.searching) && (
          <p
            className="mt-2.5 text-xs leading-relaxed line-clamp-3"
            style={{ color: "var(--text-secondary)" }}
          >
            {state.searching
              ? `Querying M365 Work IQ: "${state.searching}"`
              : (liveSpeechText ?? state.caption)}
          </p>
        )}

        {/* Executive Spoken Summary & Interrupt Controls */}
        <div className="mt-3 flex items-center gap-2">
          <button
            type="button"
            onClick={() => void handleSpeakSummary()}
            className="flex-1 inline-flex items-center justify-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium border transition-colors cursor-pointer"
            style={{
              backgroundColor: "var(--bg-surface)",
              borderColor: "var(--border-strong)",
              color: "var(--text-primary)",
            }}
          >
            <Volume2
              className="w-3.5 h-3.5"
              style={{ color: "var(--accent-cobalt)" }}
            />
            Speak 20s Summary
          </button>

          {effectiveSpeaking && (
            <button
              type="button"
              onClick={handleInterrupt}
              className="inline-flex items-center gap-1 rounded-lg px-2.5 py-1.5 text-xs font-medium border cursor-pointer"
              style={{
                backgroundColor:
                  "color-mix(in oklch, var(--accent-crimson) 14%, transparent)",
                borderColor: "var(--accent-crimson)",
                color: "var(--accent-crimson)",
              }}
            >
              <Square className="w-3 h-3" />
              Stop
            </button>
          )}
        </div>
      </div>

      {/* Room Alignment & Voice Balance Card */}
      <div
        className="rounded-xl border p-3.5"
        style={{
          backgroundColor: "var(--bg-elevated)",
          borderColor: "var(--border-hairline)",
        }}
      >
        <div className="flex items-center justify-between mb-1.5">
          <span
            className="text-[11px] font-semibold uppercase tracking-wider font-mono-tabular"
            style={{ color: "var(--text-secondary)" }}
          >
            Room Alignment
          </span>
          {state.alignment !== null && (
            <span className="inline-flex items-center gap-1 text-sm font-semibold font-mono-tabular">
              {state.trend > 0 ? (
                <TrendingUp
                  className="w-3.5 h-3.5"
                  style={{ color: "var(--accent-emerald)" }}
                />
              ) : state.trend < 0 ? (
                <TrendingDown
                  className="w-3.5 h-3.5"
                  style={{ color: "var(--accent-amber)" }}
                />
              ) : null}
              {state.alignment}%
            </span>
          )}
        </div>

        {state.alignment !== null && (
          <div
            className="h-1.5 rounded-full overflow-hidden mb-2"
            style={{ backgroundColor: "var(--bg-subtle)" }}
          >
            <div
              className="h-full rounded-full transition-all duration-500"
              style={{
                width: `${state.alignment}%`,
                backgroundColor:
                  state.alignment >= 80
                    ? "var(--accent-emerald)"
                    : state.alignment >= 60
                      ? "var(--accent-cobalt)"
                      : "var(--accent-amber)",
              }}
            />
          </div>
        )}

        {state.alignmentNote && (
          <p
            className="text-xs leading-snug mb-3"
            style={{ color: "var(--text-secondary)" }}
          >
            {state.alignmentNote}
          </p>
        )}

        {/* Speaker Talk-Time Balance */}
        <div
          className="pt-2.5 border-t space-y-1.5"
          style={{ borderColor: "var(--border-hairline)" }}
        >
          <div className="flex items-center justify-between text-[11px]">
            <span
              className="inline-flex items-center gap-1 font-mono-tabular uppercase tracking-wider"
              style={{ color: "var(--text-muted)" }}
            >
              <UserCheck className="w-3 h-3" />
              Voice Share
            </span>
            <span
              className="text-[10px] font-mono-tabular"
              style={{ color: "var(--text-muted)" }}
            >
              {state.voices.length} participants
            </span>
          </div>
          {state.voices.map((v) => (
            <div key={v.name} className="space-y-0.5">
              <div className="flex justify-between text-xs">
                <span className="truncate">{v.name}</span>
                <span
                  className="font-mono-tabular"
                  style={{ color: "var(--text-secondary)" }}
                >
                  {v.share}%
                </span>
              </div>
              <div
                className="h-1 rounded-full overflow-hidden"
                style={{ backgroundColor: "var(--bg-subtle)" }}
              >
                <div
                  className="h-full rounded-full transition-all duration-500"
                  style={{
                    width: `${v.share}%`,
                    backgroundColor: "var(--accent-cobalt)",
                  }}
                />
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* E7 Trigger: Spatial Evidence & Whiteboard Topology Canvas */}
      <button
        type="button"
        onClick={onOpenCanvas}
        data-testid="open-spatial-canvas"
        className="w-full text-left rounded-xl border p-3.5 transition-colors cursor-pointer hover:opacity-95"
        style={{
          backgroundColor: "var(--bg-elevated)",
          borderColor: "var(--border-hairline)",
        }}
      >
        <div className="flex items-center justify-between mb-1">
          <span className="inline-flex items-center gap-1.5 text-xs font-semibold">
            <Network
              className="w-3.5 h-3.5"
              style={{ color: "var(--accent-violet)" }}
            />
            Spatial Evidence Canvas
          </span>
          <span
            className="text-[10px] font-mono-tabular px-1.5 py-0.5 rounded border"
            style={{
              backgroundColor: "var(--bg-surface)",
              borderColor: "var(--border-hairline)",
              color: "var(--text-secondary)",
            }}
          >
            {state.restricted
              ? "Guest Shield"
              : `${state.visuals.length} capture · topology`}
          </span>
        </div>
        <p className="text-xs leading-snug" style={{ color: "var(--text-secondary)" }}>
          {state.visuals[0]
            ? `${state.visuals[0].title} — click to unfold interactive topology & camera evidence.`
            : "Inspect whiteboard captures, Work IQ SLA clauses, and decision links."}
        </p>
        {state.visuals[0] && (
          <div
            className="mt-2 inline-flex items-center gap-1 text-[11px] font-medium"
            style={{ color: "var(--accent-cobalt)" }}
          >
            <Camera className="w-3 h-3" />
            Captured from {state.visuals[0].capturedFrom ?? state.visuals[0].source}
          </div>
        )}
      </button>
    </aside>
  );
}
