export type PresenceMode = "listening" | "thinking" | "speaking";

export interface SkillChecklistItem {
  item: string;
  covered: boolean;
}

export interface SkillDetail {
  name: string;
  label: string;
  description: string;
  dynamic: boolean;
  at: string;
  checklist: SkillChecklistItem[];
  tracks: string[];
}

export interface AgendaItemState {
  title: string;
  minutes: number | null;
  spent: number;
}

export interface AgendaState {
  items: AgendaItemState[];
  current: number;
  currentElapsed: number;
  done: number[];
}

export interface MilestoneItem {
  label: string;
  at: number;
  kind: "topic" | "decision" | "risk" | "skill";
}

export interface VoiceShare {
  name: string;
  share: number;
}

export interface ActionRecord {
  owner: string;
  task: string;
  due?: string | null;
}

export interface MeetingInsightItem {
  id: string;
  kind: "knowledge" | "risk" | "nudge" | "skill" | "visual";
  severity: "info" | "medium" | "high";
  title: string;
  text: string;
  citation?: string | null;
  at: string;
  elapsedSec: number;
  skillRule?: string;
  linkedOutcome?: string;
}

export interface VisualCapture {
  index: number;
  title: string;
  summary: string;
  keyPoints: string[];
  source: string;
  capturedFrom?: string | null;
  at: string;
  imageUrl?: string;
}

export interface TranscriptTurnItem {
  at: string;
  speaker: string;
  text: string;
  agent: boolean;
}

export interface FinOpsSnapshot {
  voiceModel: string;
  scribeModel: string;
  audioMinutes: number;
  scribePasses: number;
  workIqCalls: number;
  workIqAvgMs: number;
  voiceCostUsd: number;
  scribeCostUsd: number;
  workIqCostUsd: number;
  totalLiveCostUsd: number;
  costPerMinuteUsd: number;
}

export interface LiveMeetingState {
  callId: string;
  agent: string;
  subject: string;
  restricted: boolean;
  presence: PresenceMode;
  speaking: boolean;
  caption?: string | null;
  elapsed: number;
  remaining: number | null;
  alignment: number | null;
  alignmentNote?: string | null;
  trend: number;
  voices: VoiceShare[];
  skills: SkillDetail[];
  uncovered: string[];
  agenda: AgendaState;
  milestones: MilestoneItem[];
  summary: string;
  decisions: string[];
  actions: ActionRecord[];
  questions: string[];
  parked: string[];
  tracked: Record<string, string[]>;
  visuals: VisualCapture[];
  finops: FinOpsSnapshot;
  insights: MeetingInsightItem[];
  searching?: string | null;
  transcript: TranscriptTurnItem[];
}

export interface CmrRuntimeInfo {
  hostedInCmr: boolean;
  appId?: string;
  environmentId?: string;
  sessionId?: string;
  userFullName?: string;
  tenantId?: string;
}

export interface TopologyNode {
  id: string;
  kind: "capture" | "playbook" | "risk" | "decision";
  badge: string;
  title: string;
  detail: string;
  meta: string;
  x: number;
  y: number;
}

export interface TopologyEdge {
  from: string;
  to: string;
  label: string;
}
