// What an account may use: its trial, a paid plan, or time an admin granted. Kept in Clerk publicMetadata
// under antenna_* keys (so a Clerk instance can be shared with another product without clashing).
import { PLANS, TRIAL_DAYS, type Plan } from "./pricing";

export type AntennaMeta = {
  antenna_trial_ends?: string;
  antenna_plan?: Plan | "granted";
  antenna_expires?: string;
  antenna_blocked?: boolean;
};

export type Entitlement = { plan: string; until: Date | null; active: boolean; trialEnds: Date | null };

const date = (v: unknown) => {
  const d = typeof v === "string" ? new Date(v) : null;
  return d && !isNaN(d.getTime()) ? d : null;
};

export function entitlement(meta: AntennaMeta, now = new Date()): Entitlement {
  const trialEnds = date(meta.antenna_trial_ends);
  const paidUntil = date(meta.antenna_expires);
  if (meta.antenna_blocked) return { plan: "none", until: null, active: false, trialEnds };
  if (paidUntil && paidUntil > now) return { plan: meta.antenna_plan ?? "monthly", until: paidUntil, active: true, trialEnds };
  if (trialEnds && trialEnds > now) return { plan: "trial", until: trialEnds, active: true, trialEnds };
  return { plan: "none", until: paidUntil ?? trialEnds, active: false, trialEnds };
}

/** The trial starts the first time the account signs an app in. */
export function trialStart(meta: AntennaMeta, now = new Date()): AntennaMeta | null {
  if (meta.antenna_trial_ends) return null;
  return { antenna_trial_ends: new Date(now.getTime() + TRIAL_DAYS * 86_400_000).toISOString() };
}

/** Paying extends from the current paid expiry (so paying early never loses days), else from now. */
export function afterPayment(meta: AntennaMeta, plan: Plan, now = new Date()): AntennaMeta {
  const current = date(meta.antenna_expires);
  const from = current && current > now ? current : now;
  return { antenna_plan: plan, antenna_expires: new Date(from.getTime() + PLANS[plan].days * 86_400_000).toISOString() };
}

/** An admin adds days (or takes them away with a negative number). */
export function afterGrant(meta: AntennaMeta, days: number, now = new Date()): AntennaMeta {
  const current = date(meta.antenna_expires);
  const from = current && current > now ? current : now;
  return { antenna_plan: meta.antenna_plan && meta.antenna_plan !== "granted" && current && current > now ? meta.antenna_plan : "granted",
    antenna_expires: new Date(from.getTime() + days * 86_400_000).toISOString() };
}
