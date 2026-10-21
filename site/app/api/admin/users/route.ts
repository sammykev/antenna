// ?q= searches by email; ?id= returns one account with its devices.
import { currentAdmin } from "@/lib/admin";
import { allUsers } from "@/lib/adminData";
import { json } from "@/lib/http";
import { store } from "@/lib/store";

export const dynamic = "force-dynamic";

export async function GET(req: Request) {
  if (!(await currentAdmin())) return json({ error: "forbidden" }, 403);
  const url = new URL(req.url);
  const id = url.searchParams.get("id");
  if (id) return json({ devices: await store().devices(id) });
  const q = url.searchParams.get("q")?.trim() || undefined;
  const users = await allUsers(q);
  return json({ users: users.slice(0, 500), total: users.length });
}
