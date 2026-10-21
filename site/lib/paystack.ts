// Redeeming a Paystack payment for a plan, with BetIQ's checks: Paystack's own record of the payment (never the
// browser's word), the amount, the payer, the age, and each reference counting once.
import { createHmac, timingSafeEqual } from "crypto";
import { afterPayment } from "./entitlement";
import { PLANS, planKobo, type Plan } from "./pricing";
import type { Store } from "./store";
import type { Accounts } from "./users";

export type PaystackTx = {
  status?: string; reference?: string; amount?: number | string; currency?: string;
  paid_at?: string; paidAt?: string; metadata?: unknown;
};

/** Paystack signs each webhook body with HMAC-SHA512 of the secret key. */
export function validSignature(rawBody: string, signature: string | null, secret: string): boolean {
  if (!signature || !secret) return false;
  const expected = Buffer.from(createHmac("sha512", secret).update(rawBody).digest("hex"));
  const given = Buffer.from(signature);
  return expected.length === given.length && timingSafeEqual(expected, given);
}

export async function verifyTransaction(reference: string): Promise<PaystackTx | null> {
  const res = await fetch(`https://api.paystack.co/transaction/verify/${encodeURIComponent(reference)}`, {
    headers: { Authorization: `Bearer ${process.env.PAYSTACK_SECRET_KEY}` },
  });
  const data = await res.json().catch(() => null);
  if (!data?.status || !data.data) {
    console.error("[paystack] verify failed:", data?.message);
    return null;
  }
  return data.data as PaystackTx;
}

/** References are "antenna_<userId>_<plan>_<random>", made by the checkout. */
export const referenceFor = (userId: string, plan: Plan, random: string) => `antenna_${userId}_${plan}_${random}`;

export function parseReference(reference: string): { userId: string; plan: Plan } | null {
  const m = /^antenna_(user_[A-Za-z0-9]+)_(monthly|yearly)_[A-Za-z0-9]+$/.exec(reference);
  return m ? { userId: m[1], plan: m[2] as Plan } : null;
}

export function paysFor(tx: PaystackTx, plan: Plan): boolean {
  return tx.status === "success" && tx.currency === "NGN" && Number(tx.amount) >= planKobo(plan);
}

export const MAX_PAYMENT_AGE_HOURS = 48;

export function isFresh(tx: PaystackTx, now = new Date()): boolean {
  const at = new Date(tx.paid_at ?? tx.paidAt ?? "");
  return !isNaN(at.getTime()) && now.getTime() - at.getTime() <= MAX_PAYMENT_AGE_HOURS * 3_600_000;
}

export type Redeemed = { ok: true; plan: Plan; expires: string } | { ok: false; error: string };

/** Checks a payment Paystack reports for `reference` and, if it's good and new, extends the account. */
export async function redeem(store: Store, accounts: Accounts, reference: string, tx: PaystackTx | null, now = new Date(),
                             usedReferences: (userId: string) => Promise<string[]>, markUsed: (userId: string, ref: string) => Promise<void>): Promise<Redeemed> {
  const parsed = parseReference(reference);
  if (!parsed) return { ok: false, error: "not_an_antenna_payment" };
  if (!tx || tx.reference !== reference) return { ok: false, error: "payment_not_verified" };
  if (!paysFor(tx, parsed.plan)) return { ok: false, error: "amount_mismatch" };
  if (!isFresh(tx, now)) return { ok: false, error: "payment_too_old" };
  if ((await usedReferences(parsed.userId)).includes(reference)) return { ok: false, error: "reference_already_used" };
  const account = await accounts.get(parsed.userId);
  if (!account) return { ok: false, error: "no_account" };

  const meta = afterPayment(account.meta, parsed.plan, now);
  await markUsed(parsed.userId, reference);
  await accounts.update(parsed.userId, meta);
  await store.addPayment({ userId: parsed.userId, email: account.email, plan: parsed.plan, ngn: PLANS[parsed.plan].ngn, reference, at: now.toISOString() });
  return { ok: true, plan: parsed.plan, expires: meta.antenna_expires! };
}
