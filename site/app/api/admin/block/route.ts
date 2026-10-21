// Blocks (or unblocks) an account: its apps lose access at their next refresh.
import { currentAdmin } from "@/lib/admin";
import { body, json } from "@/lib/http";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  const admin = await currentAdmin();
  if (!admin) return json({ error: "forbidden" }, 403);
  const { userId, blocked } = await body(req);
  if (typeof userId !== "string" || typeof blocked !== "boolean") return json({ error: "bad_request" }, 400);
  await accounts().update(userId, { antenna_blocked: blocked });
  console.log(`[admin] ${admin} ${blocked ? "blocked" : "unblocked"} ${userId}`);
  return json({ ok: true });
}
