// Adds (or, negative, removes) days of access: comps, refunds, support.
import { currentAdmin } from "@/lib/admin";
import { afterGrant } from "@/lib/entitlement";
import { body, json } from "@/lib/http";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  const admin = await currentAdmin();
  if (!admin) return json({ error: "forbidden" }, 403);
  const { userId, days } = await body(req);
  if (typeof userId !== "string" || typeof days !== "number" || !Number.isFinite(days) || Math.abs(days) > 3650)
    return json({ error: "bad_request" }, 400);
  const account = await accounts().get(userId);
  if (!account) return json({ error: "no_account" }, 404);
  const meta = afterGrant(account.meta, days);
  await accounts().update(userId, meta);
  console.log(`[admin] ${admin} gave ${userId} ${days} day(s), now until ${meta.antenna_expires}`);
  return json({ ok: true, until: meta.antenna_expires });
}
