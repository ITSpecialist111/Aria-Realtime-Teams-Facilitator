import { useEffect, useMemo, useState } from "react";
import {
  Camera,
  Maximize2,
  Move,
  RotateCcw,
  ShieldAlert,
  X,
  ZoomIn,
  ZoomOut,
} from "lucide-react";
import {
  TOPOLOGY_EDGES,
  TOPOLOGY_NODES_INITIAL,
} from "../data/showcaseScenario";
import type {
  MeetingInsightItem,
  TopologyEdge,
  TopologyNode,
  VisualCapture,
} from "../types/aria";

interface SpatialEvidenceCanvasProps {
  open: boolean;
  onClose: () => void;
  visuals: VisualCapture[];
  insights: MeetingInsightItem[];
  decisions: string[];
  restricted: boolean;
  apiBaseUrl: string;
  callId: string;
  liveKey: string | null;
  isLiveConnected: boolean;
}

const NODE_WIDTH = 272;
const NODE_HEIGHT = 136;

export function SpatialEvidenceCanvas({
  open,
  onClose,
  visuals,
  insights,
  decisions,
  restricted,
  apiBaseUrl,
  callId,
  liveKey,
  isLiveConnected,
}: SpatialEvidenceCanvasProps) {
  const computedGraph = useMemo<{
    nodes: TopologyNode[];
    edges: TopologyEdge[];
  }>(() => {
    if (restricted) {
      const decNodes: TopologyNode[] = decisions.slice(0, 2).map((d, i) => ({
        id: `dec-${i}`,
        kind: "decision",
        badge: `LOCKED DECISION · ${i + 1}`,
        title: d.length > 48 ? `${d.slice(0, 48)}...` : d,
        detail: d,
        meta: "External Guest Shield Active · Internal M365 citations hidden",
        x: 80 + i * 340,
        y: 140,
      }));
      return { nodes: decNodes, edges: [] };
    }

    if (!isLiveConnected) {
      return { nodes: TOPOLOGY_NODES_INITIAL, edges: TOPOLOGY_EDGES };
    }

    const liveNodes: TopologyNode[] = [];
    const liveEdges: TopologyEdge[] = [];

    if (visuals[0]) {
      liveNodes.push({
        id: "live-cap-0",
        kind: "capture",
        badge: "CAMERA / SCREEN CAPTURE",
        title: visuals[0].title,
        detail: visuals[0].summary,
        meta: `Captured from ${visuals[0].capturedFrom ?? visuals[0].source}`,
        x: 64,
        y: 140,
      });
    }

    insights.slice(-2).forEach((ins, i) => {
      const id = `live-ins-${i}`;
      liveNodes.push({
        id,
        kind: ins.kind === "knowledge" ? "playbook" : "risk",
        badge:
          ins.kind === "knowledge" ? "WORK IQ · M365" : "LIVE FACILITATION",
        title: ins.title,
        detail: ins.text,
        meta: ins.citation ?? ins.at,
        x: 416,
        y: 60 + i * 210,
      });
      if (visuals[0]) {
        liveEdges.push({
          from: "live-cap-0",
          to: id,
          label: "Corroborates context",
        });
      }
    });

    if (decisions[0]) {
      liveNodes.push({
        id: "live-dec-0",
        kind: "decision",
        badge: "LOCKED DECISION",
        title:
          decisions[0].length > 48
            ? `${decisions[0].slice(0, 48)}...`
            : decisions[0],
        detail: decisions[0],
        meta: "Recorded in live meeting log",
        x: 824,
        y: 158,
      });
      liveNodes
        .filter((n) => n.id.startsWith("live-ins-"))
        .forEach((n) => {
          liveEdges.push({
            from: n.id,
            to: "live-dec-0",
            label: "Informs decision",
          });
        });
    }

    return { nodes: liveNodes, edges: liveEdges };
  }, [restricted, isLiveConnected, visuals, insights, decisions]);

  const [offsets, setOffsets] = useState<Record<string, { x: number; y: number }>>({});
  const [zoom, setZoom] = useState(1);
  const [selectedId, setSelectedId] = useState<string>("cap-whiteboard");
  const [liveBlobUrl, setLiveBlobUrl] = useState<string | null>(null);
  const [dragState, setDragState] = useState<{
    id: string;
    startX: number;
    startY: number;
    origX: number;
    origY: number;
  } | null>(null);

  const latestVisual = restricted ? null : (visuals[0] ?? null);

  useEffect(() => {
    if (!open || !isLiveConnected || !latestVisual || !liveKey || restricted) {
      setLiveBlobUrl(null);
      return;
    }

    let objectUrl: string | null = null;
    let cancelled = false;

    async function loadCapture() {
      try {
        const resp = await fetch(
          `${apiBaseUrl}/api/live/${encodeURIComponent(callId)}/visuals/${latestVisual!.index}`,
          { headers: { "X-Live-Key": liveKey! } },
        );
        if (!resp.ok || cancelled) return;
        const blob = await resp.blob();
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setLiveBlobUrl(objectUrl);
      } catch {
        // Ignore transient image load error.
      }
    }

    void loadCapture();
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [open, isLiveConnected, latestVisual, liveKey, restricted, apiBaseUrl, callId]);

  if (!open) return null;

  const nodes: TopologyNode[] = computedGraph.nodes.map((n) =>
    offsets[n.id] ? { ...n, x: offsets[n.id].x, y: offsets[n.id].y } : n,
  );
  const edges = computedGraph.edges;
  const selectedNode = nodes.find((n) => n.id === selectedId) ?? nodes[0] ?? null;

  function onPointerDownNode(e: React.PointerEvent, node: TopologyNode) {
    e.stopPropagation();
    setSelectedId(node.id);
    setDragState({
      id: node.id,
      startX: e.clientX,
      startY: e.clientY,
      origX: node.x,
      origY: node.y,
    });
  }

  function onPointerMoveCanvas(e: React.PointerEvent) {
    if (!dragState) return;
    const dx = (e.clientX - dragState.startX) / zoom;
    const dy = (e.clientY - dragState.startY) / zoom;
    setOffsets((prev) => ({
      ...prev,
      [dragState.id]: {
        x: Math.max(16, Math.min(920, Math.round(dragState.origX + dx))),
        y: Math.max(16, Math.min(380, Math.round(dragState.origY + dy))),
      },
    }));
  }

  function onPointerUpCanvas() {
    setDragState(null);
  }

  function resetLayout() {
    setOffsets({});
    setZoom(1);
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4 sm:p-6 backdrop-blur-xs"
      style={{ backgroundColor: "rgba(8, 11, 16, 0.62)" }}
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-label="Spatial Evidence & Topology Canvas"
        onClick={(e) => e.stopPropagation()}
        className="w-full max-w-6xl h-[84vh] rounded-2xl border flex flex-col overflow-hidden shadow-2xl"
        style={{
          backgroundColor: "var(--bg-surface)",
          borderColor: "var(--border-strong)",
        }}
      >
        {/* Canvas Toolbar */}
        <div
          className="px-5 py-3.5 border-b flex items-center justify-between gap-3"
          style={{ borderColor: "var(--border-hairline)" }}
        >
          <div>
            <div className="flex items-center gap-2">
              <span className="text-sm font-semibold">
                Spatial Evidence & Incident Topology Canvas
              </span>
              <span
                className="text-[10px] font-mono-tabular px-2 py-0.5 rounded-full border"
                style={{
                  backgroundColor: "var(--bg-elevated)",
                  borderColor: "var(--border-hairline)",
                  color: "var(--text-secondary)",
                }}
              >
                Drag nodes · Inspect camera & M365 links
              </span>
            </div>
            <p className="text-xs mt-0.5" style={{ color: "var(--text-muted)" }}>
              Maps visual camera/screen captures directly to Work IQ policy clauses and locked room decisions.
            </p>
          </div>

          <div className="flex items-center gap-1.5">
            <button
              type="button"
              onClick={() => setZoom((z) => Math.max(0.65, +(z - 0.15).toFixed(2)))}
              aria-label="Zoom out"
              className="p-1.5 rounded-lg border cursor-pointer"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <ZoomOut className="w-3.5 h-3.5" />
            </button>
            <span className="text-xs font-mono-tabular px-1.5">
              {Math.round(zoom * 100)}%
            </span>
            <button
              type="button"
              onClick={() => setZoom((z) => Math.min(1.45, +(z + 0.15).toFixed(2)))}
              aria-label="Zoom in"
              className="p-1.5 rounded-lg border cursor-pointer"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <ZoomIn className="w-3.5 h-3.5" />
            </button>
            <button
              type="button"
              onClick={resetLayout}
              title="Reset layout"
              className="p-1.5 rounded-lg border cursor-pointer"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <RotateCcw className="w-3.5 h-3.5" />
            </button>
            <button
              type="button"
              onClick={onClose}
              aria-label="Close Spatial Canvas"
              className="ml-2 p-1.5 rounded-lg border cursor-pointer"
              style={{
                backgroundColor: "var(--bg-elevated)",
                borderColor: "var(--border-hairline)",
              }}
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>

        {/* Interactive Canvas + Right Evidence Inspector */}
        <div className="flex-1 flex flex-col lg:flex-row overflow-hidden">
          <div
            onPointerMove={onPointerMoveCanvas}
            onPointerUp={onPointerUpCanvas}
            onPointerLeave={onPointerUpCanvas}
            className="flex-1 relative overflow-auto select-none"
            style={{
              backgroundColor: "var(--bg-canvas)",
              backgroundImage:
                "radial-gradient(var(--border-hairline) 1px, transparent 1px)",
              backgroundSize: "20px 20px",
            }}
          >
            <div
              style={{
                width: "1160px",
                height: "520px",
                transform: `scale(${zoom})`,
                transformOrigin: "top left",
              }}
              className="relative"
            >
              {/* Cubic Bezier Evidence Edges */}
              <svg
                width="1160"
                height="520"
                className="absolute inset-0 pointer-events-none"
              >
                {edges.map((edge) => {
                  const from = nodes.find((n) => n.id === edge.from);
                  const to = nodes.find((n) => n.id === edge.to);
                  if (!from || !to) return null;
                  const x1 = from.x + NODE_WIDTH;
                  const y1 = from.y + NODE_HEIGHT / 2;
                  const x2 = to.x;
                  const y2 = to.y + NODE_HEIGHT / 2;
                  const cx = (x1 + x2) / 2;
                  const d = `M ${x1} ${y1} C ${cx} ${y1}, ${cx} ${y2}, ${x2} ${y2}`;
                  return (
                    <g key={`${edge.from}-${edge.to}`}>
                      <path
                        d={d}
                        fill="none"
                        stroke="var(--border-strong)"
                        strokeWidth="1.75"
                      />
                      <circle cx={x2} cy={y2} r="3.5" fill="var(--accent-cobalt)" />
                      <text
                        x={cx}
                        y={(y1 + y2) / 2 - 8}
                        textAnchor="middle"
                        fill="var(--text-muted)"
                        fontSize="10"
                      >
                        {edge.label}
                      </text>
                    </g>
                  );
                })}
              </svg>

              {/* Draggable Topology Evidence Cards */}
              {nodes.map((node) => {
                const isSelected = selectedNode?.id === node.id;
                const accent =
                  node.kind === "capture"
                    ? "var(--accent-cobalt)"
                    : node.kind === "playbook"
                      ? "var(--accent-violet)"
                      : node.kind === "risk"
                        ? "var(--accent-amber)"
                        : "var(--accent-emerald)";

                return (
                  <div
                    key={node.id}
                    onPointerDown={(e) => onPointerDownNode(e, node)}
                    style={{
                      left: `${node.x}px`,
                      top: `${node.y}px`,
                      width: `${NODE_WIDTH}px`,
                      backgroundColor: "var(--bg-surface)",
                      borderColor: isSelected ? accent : "var(--border-hairline)",
                    }}
                    className="absolute rounded-xl border p-3.5 shadow-sm cursor-grab active:cursor-grabbing transition-shadow"
                  >
                    <div className="flex items-center justify-between gap-2 mb-1">
                      <span
                        className="text-[10px] font-mono-tabular font-semibold uppercase"
                        style={{ color: accent }}
                      >
                        {node.badge}
                      </span>
                      <Move
                        className="w-3 h-3"
                        style={{ color: "var(--text-muted)" }}
                      />
                    </div>
                    <div className="text-xs font-semibold leading-snug mb-1">
                      {node.title}
                    </div>
                    <p
                      className="text-[11px] leading-snug line-clamp-3"
                      style={{ color: "var(--text-secondary)" }}
                    >
                      {node.detail}
                    </p>
                  </div>
                );
              })}
            </div>
          </div>

          {/* Right Inspector: Captured Whiteboard / Camera Frame + Key Points */}
          <div
            className="w-full lg:w-[340px] border-t lg:border-t-0 lg:border-l p-4 overflow-y-auto space-y-3.5"
            style={{
              backgroundColor: "var(--bg-elevated)",
              borderColor: "var(--border-hairline)",
            }}
          >
            {restricted && (
              <div
                className="flex items-start gap-2 rounded-lg p-3 text-xs border"
                style={{
                  backgroundColor:
                    "color-mix(in oklch, var(--accent-amber) 12%, transparent)",
                  borderColor:
                    "color-mix(in oklch, var(--accent-amber) 45%, transparent)",
                  color: "var(--accent-amber)",
                }}
              >
                <ShieldAlert className="w-4 h-4 shrink-0 mt-0.5" />
                <span>
                  External Guest Shield active — internal Work IQ policy nodes and camera captures are withheld.
                </span>
              </div>
            )}

            {selectedNode ? (
              <div>
                <span
                  className="text-[10px] font-mono-tabular uppercase tracking-wider"
                  style={{ color: "var(--text-muted)" }}
                >
                  Selected Evidence Node
                </span>
                <h3 className="text-sm font-semibold mt-0.5">
                  {selectedNode.title}
                </h3>
                <p
                  className="text-xs leading-relaxed mt-1.5"
                  style={{ color: "var(--text-secondary)" }}
                >
                  {selectedNode.detail}
                </p>
                <div
                  className="mt-2 text-[11px] font-mono-tabular"
                  style={{ color: "var(--text-muted)" }}
                >
                  {selectedNode.meta}
                </div>
              </div>
            ) : (
              <p className="text-xs" style={{ color: "var(--text-muted)" }}>
                No topology nodes recorded yet for this session.
              </p>
            )}

            {latestVisual && (
              <div
                className="pt-3 border-t space-y-2"
                style={{ borderColor: "var(--border-hairline)" }}
              >
                <div className="flex items-center justify-between text-xs font-semibold">
                  <span className="inline-flex items-center gap-1.5">
                    <Camera
                      className="w-3.5 h-3.5"
                      style={{ color: "var(--accent-cobalt)" }}
                    />
                    Vision Capture Breakdown
                  </span>
                  <Maximize2
                    className="w-3 h-3"
                    style={{ color: "var(--text-muted)" }}
                  />
                </div>

                {liveBlobUrl ? (
                  <img
                    src={liveBlobUrl}
                    alt={latestVisual.title}
                    className="w-full rounded-lg border object-cover"
                    style={{ borderColor: "var(--border-hairline)" }}
                  />
                ) : (
                  /* Crisp Schematic Render of the Captured Ring B Cutover Whiteboard */
                  <div
                    className="rounded-lg border p-3"
                    style={{
                      backgroundColor: "var(--bg-surface)",
                      borderColor: "var(--border-hairline)",
                    }}
                  >
                    <svg viewBox="0 0 280 120" className="w-full h-28">
                      <rect
                        x="10"
                        y="42"
                        width="68"
                        height="36"
                        rx="6"
                        fill="none"
                        stroke="var(--accent-cobalt)"
                        strokeWidth="1.5"
                      />
                      <text x="44" y="63" textAnchor="middle" fontSize="9" fill="currentColor">
                        St Jude PACS
                      </text>
                      <path
                        d="M 78 50 Q 140 12 202 50"
                        fill="none"
                        stroke="var(--accent-crimson)"
                        strokeWidth="1.5"
                        strokeDasharray="4 3"
                      />
                      <text x="140" y="22" textAnchor="middle" fontSize="8" fill="var(--accent-crimson)">
                        ONF Span #14 (DARK 2.4km)
                      </text>
                      <path
                        d="M 78 70 Q 140 108 202 70"
                        fill="none"
                        stroke="var(--accent-emerald)"
                        strokeWidth="2"
                      />
                      <text x="140" y="106" textAnchor="middle" fontSize="8" fill="var(--accent-emerald)">
                        Ring B MPLS · 10Gbps LIVE
                      </text>
                      <rect
                        x="202"
                        y="42"
                        width="68"
                        height="36"
                        rx="6"
                        fill="none"
                        stroke="var(--accent-emerald)"
                        strokeWidth="1.5"
                      />
                      <text x="236" y="63" textAnchor="middle" fontSize="9" fill="currentColor">
                        Core Vault
                      </text>
                    </svg>
                  </div>
                )}

                <ul className="space-y-1">
                  {latestVisual.keyPoints.map((kp) => (
                    <li
                      key={kp}
                      className="text-xs leading-snug flex items-start gap-1.5"
                      style={{ color: "var(--text-secondary)" }}
                    >
                      <span style={{ color: "var(--accent-cobalt)" }}>•</span>
                      <span>{kp}</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
