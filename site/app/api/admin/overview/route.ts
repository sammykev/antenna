import { currentAdmin } from "@/lib/admin";
import { allUsers, overview } from "@/lib/adminData";
import { json } from "@/lib/http";
import { store } from "@/lib/store";

export const dynamic = "force-dynamic";

export async function GET() {
  if (!(await currentAdmin())) return json({ error: "forbidden" }, 403);
  const [users, payments] = await Promise.all([allUsers(), store().payments()]);
  return json({ stats: overview(users, payments), payments: payments.slice(0, 50) });
}
