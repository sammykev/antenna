// Right after paying in the browser: check the payment with Paystack and extend the account.
import { auth } from "@clerk/nextjs/server";
import { body, json } from "@/lib/http";
import { parseReference, redeem, verifyTransaction } from "@/lib/paystack";
import { markUsed, usedReferences } from "@/lib/references";
import { store } from "@/lib/store";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  const { userId } = await auth();
  if (!userId) return json({ error: "not_signed_in" }, 401);
  const reference = (await body(req)).reference;
  if (typeof reference !== "string" || parseReference(reference)?.userId !== userId) return json({ error: "reference_not_yours" }, 403);
  const done = await redeem(store(), accounts(), reference, await verifyTransaction(reference), new Date(), usedReferences, markUsed);
  return done.ok ? json(done) : json(done, 409);
}
