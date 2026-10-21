// The signed-in user's plan and devices; DELETE ?device=<id> signs a device out.
import { auth } from "@clerk/nextjs/server";
import { entitlement } from "@/lib/entitlement";
import { json } from "@/lib/http";
import { store } from "@/lib/store";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function GET() {
  const { userId } = await auth();
  if (!userId) return json({ error: "not_signed_in" }, 401);
  const account = await accounts().get(userId);
  if (!account) return json({ error: "no_account" }, 404);
  return json({ email: account.email, entitlement: entitlement(account.meta), devices: await store().devices(userId) });
}

export async function DELETE(req: Request) {
  const { userId } = await auth();
  if (!userId) return json({ error: "not_signed_in" }, 401);
  const device = new URL(req.url).searchParams.get("device");
  if (device) await store().removeDevice(userId, device);
  return json({ ok: true });
}
