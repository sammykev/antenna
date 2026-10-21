// What the admin dashboard shows: every account's plan, and the totals.
import { clerkClient } from "@clerk/nextjs/server";
import { entitlement, type AntennaMeta } from "./entitlement";
import type { Payment } from "./store";

export type AdminUser = {
  id: string; email: string; createdAt: number; lastSignIn: number | null;
  plan: string; active: boolean; until: string | null; trialEnds: string | null; blocked: boolean;
};

/** Accounts, newest first. Clerk pages at 500; plenty for the dashboard. */
export async function allUsers(query?: string, max = 5000): Promise<AdminUser[]> {
  const client = await clerkClient();
  const out: AdminUser[] = [];
  for (let offset = 0; offset < max; offset += 500) {
    const page = await client.users.getUserList({ limit: 500, offset, orderBy: "-created_at", ...(query ? { query } : {}) });
    for (const u of page.data) {
      const meta = (u.publicMetadata ?? {}) as AntennaMeta;
      const e = entitlement(meta);
      out.push({
        id: u.id, email: u.primaryEmailAddress?.emailAddress ?? u.emailAddresses[0]?.emailAddress ?? "",
        createdAt: u.createdAt, lastSignIn: u.lastSignInAt ?? null, plan: e.plan, active: e.active,
        until: e.until?.toISOString() ?? null, trialEnds: e.trialEnds?.toISOString() ?? null, blocked: !!meta.antenna_blocked,
      });
    }
    if (page.data.length < 500) break;
  }
  return out;
}

export function overview(users: AdminUser[], payments: Payment[], now = new Date()) {
  const month = now.toISOString().slice(0, 7);
  const paid = users.filter(u => u.active && u.plan !== "trial");
  return {
    users: users.length,
    signedUpThisWeek: users.filter(u => now.getTime() - u.createdAt < 7 * 86_400_000).length,
    inTrial: users.filter(u => u.plan === "trial").length,
    paying: paid.filter(u => u.plan === "monthly" || u.plan === "yearly").length,
    granted: paid.filter(u => u.plan === "granted").length,
    lapsed: users.filter(u => !u.active && u.trialEnds).length,
    revenueThisMonth: payments.filter(p => p.at.startsWith(month)).reduce((s, p) => s + p.ngn, 0),
    revenueTotal: payments.reduce((s, p) => s + p.ngn, 0),
    paymentsThisMonth: payments.filter(p => p.at.startsWith(month)).length,
  };
}
