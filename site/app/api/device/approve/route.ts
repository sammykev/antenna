// The signed-in user typed the code the app shows.
import { auth } from "@clerk/nextjs/server";
import { approveLink } from "@/lib/devices";
import { body, json, result } from "@/lib/http";
import { store } from "@/lib/store";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  const { userId } = await auth();
  if (!userId) return json({ error: "not_signed_in" }, 401);
  const code = (await body(req)).code;
  return result(await approveLink(store(), accounts(), userId, typeof code === "string" ? code : ""));
}
