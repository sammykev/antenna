// Plans: the single source for the pricing page, the checkout and the server-side Paystack amount check.

export type Plan = "monthly" | "yearly";

export const PLANS: Record<Plan, { name: string; ngn: number; days: number }> = {
  monthly: { name: "Monthly", ngn: 3000, days: 30 },
  yearly: { name: "Yearly", ngn: 30000, days: 365 },
};

/** Days of full access a new account gets before it has to subscribe. */
export const TRIAL_DAYS = 7;

/** How many phones and PCs one account can sign in on. */
export const MAX_DEVICES = 3;

/** Paystack charges in kobo (1 NGN = 100 kobo). */
export const planKobo = (plan: Plan) => PLANS[plan].ngn * 100;

/** "₦3,000 / month" */
export const planLabel = (plan: Plan) =>
  `₦${PLANS[plan].ngn.toLocaleString("en-US")} / ${plan === "monthly" ? "month" : "year"}`;

export const isPlan = (v: unknown): v is Plan => v === "monthly" || v === "yearly";
