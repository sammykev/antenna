// The app starts signing in: it shows the user code and polls with the device code.
import { startLink } from "@/lib/devices";
import { body, result } from "@/lib/http";
import { store } from "@/lib/store";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  return result(await startLink(store(), await body(req)));
}
