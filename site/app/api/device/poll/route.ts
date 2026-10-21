// The app waits for the user to type its code at /link; then it gets its licence.
import { pollLink } from "@/lib/devices";
import { body, result } from "@/lib/http";
import { store } from "@/lib/store";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  return result(await pollLink(store(), accounts(), (await body(req)).deviceCode));
}
