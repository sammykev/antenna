"use client";
// Plans and the Paystack checkout (inline popup, as on BetIQ). The server re-checks every payment with Paystack.
import { useState } from "react";
import { Show, SignInButton, useUser } from "@clerk/nextjs";
import { MAX_DEVICES, PLANS, TRIAL_DAYS, planKobo, planLabel, type Plan } from "@/lib/pricing";

export default function Pricing() {
  const { user } = useUser();
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function buy(plan: Plan) {
    if (!user) return;
    setBusy(true);
    setStatus(null);
    const { default: PaystackPop } = await import("@paystack/inline-js");
    const reference = `antenna_${user.id}_${plan}_${Math.random().toString(36).slice(2, 12)}`;
    new PaystackPop().newTransaction({
      key: process.env.NEXT_PUBLIC_PAYSTACK_PUBLIC_KEY!,
      email: user.primaryEmailAddress?.emailAddress ?? "",
      amount: planKobo(plan),
      currency: "NGN",
      reference,
      metadata: { userId: user.id, plan },
      onSuccess: async () => {
        setStatus("Payment received, unlocking…");
        const res = await fetch("/api/subscribe", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ reference }) });
        const data = await res.json().catch(() => ({}));
        setBusy(false);
        setStatus(res.ok
          ? `Done: Antenna is unlocked until ${new Date(data.expires).toLocaleDateString()}. Your apps pick it up within a minute (or tap Refresh in the app).`
          : data.error === "reference_already_used"
            ? "That payment has already been applied to your account."
            : "We couldn't confirm the payment yet. If you were charged it will be applied within a few minutes.");
      },
      onCancel: () => { setBusy(false); setStatus("Payment cancelled."); },
    });
  }

  return (
    <div className="space-y-8 pt-8">
      <div>
        <h1 className="text-3xl font-bold">Pricing</h1>
        <p className="text-muted">Every account starts with {TRIAL_DAYS} days free. One plan covers up to {MAX_DEVICES} devices (Windows and Android).</p>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        {(Object.keys(PLANS) as Plan[]).map(plan => (
          <div key={plan} className="card space-y-3">
            <h2 className="text-xl font-semibold">{PLANS[plan].name}</h2>
            <p className="text-2xl font-bold">{planLabel(plan)}</p>
            {plan === "yearly" && <p className="text-sm text-muted">Two months free compared with monthly.</p>}
            <Show when="signed-in"><button className="btn w-full" disabled={busy} onClick={() => buy(plan)}>Subscribe</button></Show>
            <Show when="signed-out"><SignInButton mode="modal"><button className="btn w-full">Sign in to subscribe</button></SignInButton></Show>
          </div>
        ))}
      </div>
      {status && <p className="card">{status}</p>}
      <p className="text-sm text-muted">Paying again before your plan ends adds to it: you never lose days.</p>
    </div>
  );
}
