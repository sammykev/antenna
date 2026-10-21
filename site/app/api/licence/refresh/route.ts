// Apps swap their licence for a fresh one: new payments, grants and removed devices reach them this way.
import { refresh } from "@/lib/devices";
import { body, result } from "@/lib/http";
import { publicKeyPem } from "@/lib/licence";
import { store } from "@/lib/store";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  return result(await refresh(store(), accounts(), (await body(req)).token, publicKeyPem()));
}
