import { getContext } from "@microsoft/cmr-apps/app";
import { getUser } from "@microsoft/cmr-apps/auth";
import { initializeLogger } from "@microsoft/cmr-apps/telemetry";
import type { CmrRuntimeInfo } from "../types/aria";

function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T | null> {
  return new Promise((resolve) => {
    const timer = window.setTimeout(() => resolve(null), ms);
    promise
      .then((val) => {
        window.clearTimeout(timer);
        resolve(val);
      })
      .catch(() => {
        window.clearTimeout(timer);
        resolve(null);
      });
  });
}

/**
 * Probes the Microsoft Copilot Managed Runtime SDK (`@microsoft/cmr-apps`).
 * Resolves full host/app/user identity when running inside the CMR App Player
 * (`play.managedapps.cloud.microsoft` or `ms app dev` local gateway), and degrades
 * cleanly when opened directly from a live Teams meeting link (`/live/{callId}`).
 */
export async function detectCmrRuntime(): Promise<CmrRuntimeInfo> {
  // CMR App Player and `ms app dev` localGateway always host the app inside a sandboxed iframe.
  // Skipping the postMessage probe in top-level standalone windows avoids handshake timeout noise.
  let insideHostFrame = false;
  try {
    insideHostFrame = window.self !== window.top;
  } catch {
    insideHostFrame = true;
  }

  if (!insideHostFrame) {
    return { hostedInCmr: false };
  }

  try {
    initializeLogger({
      logMetric: () => {
        // Receives structured session & network metrics streamed from the CMR host.
      },
    });
  } catch {
    // Outside CMR host iframe.
  }

  const [ctx, user] = await Promise.all([
    withTimeout(getContext(), 900),
    withTimeout(getUser(), 900),
  ]);

  if (ctx?.app?.appId) {
    return {
      hostedInCmr: true,
      appId: ctx.app.appId,
      environmentId: ctx.app.environmentId,
      sessionId: ctx.host?.sessionId,
      userFullName: user?.fullName,
      tenantId: user?.tenantId,
    };
  }

  return {
    hostedInCmr: false,
  };
}
