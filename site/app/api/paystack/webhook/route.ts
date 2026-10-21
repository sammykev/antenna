// Paystack's charge.success webhook: counts a payment even when the browser never reported it.
// Set the URL in Paystack Dashboard → Settings → API Keys & Webhooks: https://<site>/api/paystack/webhook
import { json } from "@/lib/http";
import { parseReference, redeem, validSignature, verifyTransaction } from "@/lib/paystack";
import { markUsed, usedReferences } from "@/lib/references";
import { store } from "@/lib/store";
import { accounts } from "@/lib/users";

export const dynamic = "force-dynamic";

export async function POST(req: Request) {
  const raw = await req.text();
  if (!validSignature(raw, req.headers.get("x-paystack-signature"), process.env.PAYSTACK_SECRET_KEY || ""))
    return json({ error: "bad_signature" }, 401);
  let event: { event?: string; data?: { reference?: string } };
  try { event = JSON.parse(raw); } catch { return json({ error: "bad_body" }, 400); }
  const reference = event.data?.reference;
  if (event.event !== "charge.success" || !reference || !parseReference(reference)) return json({ ok: true, ignored: true });

  const tx = await verifyTransaction(reference);
  if (!tx) return json({ error: "verify_failed" }, 502);  // Paystack retries
  const done = await redeem(store(), accounts(), reference, tx, new Date(), usedReferences, markUsed);
  // Already redeemed by the browser is fine.
  return json({ ok: true, redeemed: done.ok });
}
