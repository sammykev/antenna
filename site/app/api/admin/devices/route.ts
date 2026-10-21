// Signs one of an account's devices out.
import { currentAdmin } from "@/lib/admin";
import { body, json } from "@/lib/http";
import { store } from "@/lib/store";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  if (!(await currentAdmin())) return json({ error: "forbidden" }, 403);
  const { userId, deviceId } = await body(req);
  if (typeof userId !== "string" || typeof deviceId !== "string") return json({ error: "bad_request" }, 400);
  await store().removeDevice(userId, deviceId);
  return json({ ok: true });
}
