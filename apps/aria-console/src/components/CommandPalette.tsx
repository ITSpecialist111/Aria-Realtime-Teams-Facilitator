import { Command } from "cmdk";
import {
  DollarSign,
  Moon,
  Network,
  Play,
  ShieldAlert,
  Sun,
  X,
} from "lucide-react";
import { SHOWCASE_ACTS } from "../data/showcaseScenario";

interface CommandPaletteProps {
  open: boolean;
  onClose: () => void;
  onOpenFinOps: () => void;
  onOpenCanvas: () => void;
  onToggleGuestShield: () => void;
  onToggleDarkMode: () => void;
  darkMode: boolean;
  onSelectAct: (actId: number) => void;
}

export function CommandPalette({
  open,
  onClose,
  onOpenFinOps,
  onOpenCanvas,
  onToggleGuestShield,
  onToggleDarkMode,
  darkMode,
  onSelectAct,
}: CommandPaletteProps) {
  if (!open) return null;

  function runAndClose(fn: () => void) {
    fn();
    onClose();
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center pt-[14vh] p-4 backdrop-blur-xs"
      style={{ backgroundColor: "rgba(8, 11, 16, 0.55)" }}
      onClick={onClose}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="w-full max-w-lg rounded-2xl border overflow-hidden shadow-2xl"
        style={{
          backgroundColor: "var(--bg-surface)",
          borderColor: "var(--border-strong)",
        }}
      >
        <Command label="Aria Executive Command Menu">
          <div
            className="flex items-center border-b px-3.5"
            style={{ borderColor: "var(--border-hairline)" }}
          >
            <Command.Input
              autoFocus
              placeholder="Jump to cost breakdown, spatial canvas, DLP shield, or scenario act..."
              className="w-full py-3 text-sm bg-transparent outline-none"
              style={{ color: "var(--text-primary)" }}
            />
            <button
              type="button"
              onClick={onClose}
              className="p-1 rounded cursor-pointer"
              style={{ color: "var(--text-muted)" }}
            >
              <X className="w-4 h-4" />
            </button>
          </div>

          <Command.List className="max-h-80 overflow-y-auto p-2 text-xs">
            <Command.Empty
              className="py-6 text-center"
              style={{ color: "var(--text-muted)" }}
            >
              No matching command found.
            </Command.Empty>

            <Command.Group
              heading="Executive Drill-Downs"
              className="px-2 py-1 text-[10px] font-mono-tabular uppercase tracking-wider"
              style={{ color: "var(--text-muted)" }}
            >
              <Command.Item
                onSelect={() => runAndClose(onOpenFinOps)}
                className="flex items-center gap-2.5 px-2.5 py-2 rounded-lg cursor-pointer data-[selected=true]:bg-blue-600/15"
                style={{ color: "var(--text-primary)" }}
              >
                <DollarSign
                  className="w-4 h-4"
                  style={{ color: "var(--accent-emerald)" }}
                />
                <span>
                  What's the cost of this? — Unfold FinOps Waterfall & 100-Seat TCO
                </span>
              </Command.Item>

              <Command.Item
                onSelect={() => runAndClose(onOpenCanvas)}
                className="flex items-center gap-2.5 px-2.5 py-2 rounded-lg cursor-pointer data-[selected=true]:bg-blue-600/15"
                style={{ color: "var(--text-primary)" }}
              >
                <Network
                  className="w-4 h-4"
                  style={{ color: "var(--accent-violet)" }}
                />
                <span>Open Spatial Evidence & Whiteboard Topology Canvas</span>
              </Command.Item>

              <Command.Item
                onSelect={() => runAndClose(onToggleGuestShield)}
                className="flex items-center gap-2.5 px-2.5 py-2 rounded-lg cursor-pointer data-[selected=true]:bg-blue-600/15"
                style={{ color: "var(--text-primary)" }}
              >
                <ShieldAlert
                  className="w-4 h-4"
                  style={{ color: "var(--accent-amber)" }}
                />
                <span>Toggle External Guest DLP Shield (Withhold M365 Docs)</span>
              </Command.Item>

              <Command.Item
                onSelect={() => runAndClose(onToggleDarkMode)}
                className="flex items-center gap-2.5 px-2.5 py-2 rounded-lg cursor-pointer data-[selected=true]:bg-blue-600/15"
                style={{ color: "var(--text-primary)" }}
              >
                {darkMode ? (
                  <Sun className="w-4 h-4" />
                ) : (
                  <Moon className="w-4 h-4" />
                )}
                <span>
                  Switch Editorial Theme to{" "}
                  {darkMode ? "Warm Alabaster Paper" : "Deep Obsidian"}
                </span>
              </Command.Item>
            </Command.Group>

            <Command.Group
              heading="Capstone Scenario Progression"
              className="px-2 py-1 mt-1 text-[10px] font-mono-tabular uppercase tracking-wider"
              style={{ color: "var(--text-muted)" }}
            >
              {SHOWCASE_ACTS.map((act) => (
                <Command.Item
                  key={act.id}
                  onSelect={() => runAndClose(() => onSelectAct(act.id))}
                  className="flex items-center gap-2.5 px-2.5 py-2 rounded-lg cursor-pointer data-[selected=true]:bg-blue-600/15"
                  style={{ color: "var(--text-primary)" }}
                >
                  <Play
                    className="w-3.5 h-3.5"
                    style={{ color: "var(--accent-cobalt)" }}
                  />
                  <span>{act.label}</span>
                </Command.Item>
              ))}
            </Command.Group>
          </Command.List>
        </Command>
      </div>
    </div>
  );
}
