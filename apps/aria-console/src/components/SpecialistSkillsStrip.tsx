import { useState } from "react";
import { CheckCircle2, ChevronDown, Circle, Layers } from "lucide-react";
import type { SkillDetail } from "../types/aria";

interface SpecialistSkillsStripProps {
  skills: SkillDetail[];
  tracked: Record<string, string[]>;
}

export function SpecialistSkillsStrip({
  skills,
  tracked,
}: SpecialistSkillsStripProps) {
  const [selectedSkillName, setSelectedSkillName] = useState<string | null>(
    null,
  );
  const selectedSkill =
    skills.find((s) => s.name === selectedSkillName) ?? null;

  return (
    <div
      className="border-b px-4 py-2.5"
      style={{
        backgroundColor: "var(--bg-surface)",
        borderColor: "var(--border-hairline)",
      }}
    >
      <div className="flex items-center gap-2 flex-wrap">
        <span
          className="inline-flex items-center gap-1 text-[11px] font-mono-tabular uppercase tracking-wider mr-1"
          style={{ color: "var(--text-muted)" }}
        >
          <Layers className="w-3.5 h-3.5" />
          Skills ({skills.length})
        </span>

        {skills.map((skill) => {
          const coveredCount = skill.checklist.filter((c) => c.covered).length;
          const totalCount = skill.checklist.length;
          const isSelected = skill.name === selectedSkillName;

          return (
            <button
              key={skill.name}
              type="button"
              onClick={() =>
                setSelectedSkillName(isSelected ? null : skill.name)
              }
              className={`inline-flex items-center gap-2 rounded-full px-3 py-1 text-xs font-medium border transition-colors cursor-pointer ${
                skill.dynamic ? "skill-sweep-once" : ""
              }`}
              style={{
                backgroundColor: isSelected
                  ? "color-mix(in oklch, var(--accent-cobalt) 15%, transparent)"
                  : "var(--bg-elevated)",
                borderColor: isSelected
                  ? "var(--accent-cobalt)"
                  : "var(--border-hairline)",
                color: "var(--text-primary)",
              }}
            >
              <span
                className="h-1.5 w-1.5 rounded-full"
                style={{
                  backgroundColor: skill.dynamic
                    ? "var(--accent-violet)"
                    : "var(--accent-cobalt)",
                }}
              />
              <span>{skill.label}</span>
              {totalCount > 0 && (
                <span
                  className="text-[10px] font-mono-tabular"
                  style={{
                    color:
                      coveredCount === totalCount
                        ? "var(--accent-emerald)"
                        : "var(--text-muted)",
                  }}
                >
                  {coveredCount}/{totalCount}
                </span>
              )}
              <ChevronDown
                className={`w-3 h-3 transition-transform ${
                  isSelected ? "rotate-180" : ""
                }`}
                style={{ color: "var(--text-muted)" }}
              />
            </button>
          );
        })}
      </div>

      {/* Unfolded SKILL.md Checklist & Tracked Categories Drawer */}
      {selectedSkill && (
        <div
          className="mt-2.5 rounded-xl border p-3.5 grid grid-cols-1 md:grid-cols-2 gap-4"
          style={{
            backgroundColor: "var(--bg-elevated)",
            borderColor: "var(--border-hairline)",
          }}
        >
          <div>
            <div className="flex items-center justify-between mb-1">
              <span className="text-xs font-semibold">
                {selectedSkill.label} · SKILL.md Checklist
              </span>
              <span
                className="text-[10px] font-mono-tabular px-1.5 py-0.5 rounded border"
                style={{
                  borderColor: "var(--border-hairline)",
                  color: "var(--text-muted)",
                }}
              >
                {selectedSkill.dynamic ? "Auto-activated mid-call" : "Baseline invite skill"}
              </span>
            </div>
            <p
              className="text-xs mb-2.5"
              style={{ color: "var(--text-secondary)" }}
            >
              {selectedSkill.description}
            </p>
            <ul className="space-y-1.5">
              {selectedSkill.checklist.map((c) => (
                <li
                  key={c.item}
                  className="flex items-start gap-2 text-xs leading-snug"
                >
                  {c.covered ? (
                    <CheckCircle2
                      className="w-3.5 h-3.5 mt-0.5 shrink-0"
                      style={{ color: "var(--accent-emerald)" }}
                    />
                  ) : (
                    <Circle
                      className="w-3.5 h-3.5 mt-0.5 shrink-0"
                      style={{ color: "var(--accent-amber)" }}
                    />
                  )}
                  <span
                    style={{
                      color: c.covered
                        ? "var(--text-primary)"
                        : "var(--text-secondary)",
                    }}
                  >
                    {c.item}
                  </span>
                </li>
              ))}
            </ul>
          </div>

          <div
            className="md:border-l md:pl-4 space-y-2"
            style={{ borderColor: "var(--border-hairline)" }}
          >
            <span
              className="text-[11px] font-mono-tabular uppercase tracking-wider block"
              style={{ color: "var(--text-muted)" }}
            >
              Structured Skill Registers
            </span>
            {selectedSkill.tracks.map((cat) => {
              const items = tracked[cat] ?? [];
              return (
                <div
                  key={cat}
                  className="rounded-lg border p-2.5"
                  style={{
                    backgroundColor: "var(--bg-surface)",
                    borderColor: "var(--border-hairline)",
                  }}
                >
                  <div className="flex items-center justify-between text-xs font-medium mb-1">
                    <span>{cat}</span>
                    <span
                      className="font-mono-tabular text-[11px]"
                      style={{ color: "var(--text-muted)" }}
                    >
                      {items.length}
                    </span>
                  </div>
                  {items.length > 0 ? (
                    <ul className="space-y-1">
                      {items.map((entry) => (
                        <li
                          key={entry}
                          className="text-xs"
                          style={{ color: "var(--text-secondary)" }}
                        >
                          {entry}
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <p
                      className="text-xs"
                      style={{ color: "var(--text-muted)" }}
                    >
                      Listening for {cat.toLowerCase()}...
                    </p>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
