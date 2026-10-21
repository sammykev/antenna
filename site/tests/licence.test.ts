import { generateKeyPairSync } from "crypto";
import { beforeAll, describe, expect, it } from "vitest";
import { afterGrant, afterPayment, entitlement } from "../lib/entitlement";
import { approveLink, pollLink, refresh, startLink } from "../lib/devices";
import { readLicence, signLicence } from "../lib/licence";
import { normaliseCode } from "../lib/codes";
import { parseReference, redeem, validSignature } from "../lib/paystack";
import { MemoryStore } from "../lib/store";
import type { Account, Accounts } from "../lib/users";
import { createHmac } from "crypto";

const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const PUB = publicKey.export({ type: "spki", format: "pem" }).toString();

beforeAll(() => {
  process.env.LICENCE_PRIVATE_KEY = privateKey.export({ type: "pkcs8", format: "pem" }).toString();
});

class FakeAccounts implements Accounts {
  map = new Map<string, Account>();
  async get(id: string) { return this.map.get(id) ?? null; }
  async update(id: string, meta: object) { const a = this.map.get(id)!; a.meta = { ...a.meta, ...meta }; }
}

const day = 86_400_000;

describe("licences", () => {
  it("signs and verifies, and rejects tampering", () => {
    const token = signLicence({ v: 1, sub: "user_1", email: "a@b", plan: "trial", until: 10, refresh: 20, device: "dev-12345", iat: 1 });
    expect(readLicence(token, PUB)?.plan).toBe("trial");
    const [body, sig] = token.split(".");
    const forged = Buffer.from(Buffer.from(body, "base64url").toString().replace("trial", "yearly")).toString("base64url");
    expect(readLicence(`${forged}.${sig}`, PUB)).toBeNull();
    const other = generateKeyPairSync("ec", { namedCurve: "P-256" }).publicKey.export({ type: "spki", format: "pem" }).toString();
    expect(readLicence(token, other)).toBeNull();
  });
});

describe("entitlement", () => {
  const now = new Date("2026-10-21T12:00:00Z");
  it("trial, then nothing, then paid; paying early keeps the days", () => {
    expect(entitlement({ antenna_trial_ends: new Date(now.getTime() + day).toISOString() }, now).plan).toBe("trial");
    expect(entitlement({ antenna_trial_ends: new Date(now.getTime() - day).toISOString() }, now).active).toBe(false);
    const paid = afterPayment({}, "monthly", now);
    expect(entitlement(paid, now).plan).toBe("monthly");
    const again = afterPayment(paid, "yearly", now);
    expect(new Date(again.antenna_expires!).getTime() - now.getTime()).toBe((30 + 365) * day);
    expect(entitlement({ ...again, antenna_blocked: true }, now).active).toBe(false);
    expect(entitlement(afterGrant({}, 10, now), now).plan).toBe("granted");
  });
});

describe("device sign-in", () => {
  it("links a device, starts the trial, refreshes, and stops after removal", async () => {
    const store = new MemoryStore();
    const accounts = new FakeAccounts();
    accounts.map.set("user_1", { id: "user_1", email: "me@x", createdAt: 0, meta: {} });
    const now = new Date();

    const started = await startLink(store, { deviceId: "phone-123456", name: "Pixel", platform: "android" });
    if (!("deviceCode" in started) || !started.deviceCode || !started.userCode) throw new Error("no code");
    expect(await pollLink(store, accounts, started.deviceCode, now)).toEqual({ pending: true });
    expect(await approveLink(store, accounts, "user_1", started.userCode.toLowerCase().replace("-", " "), now)).toMatchObject({ ok: true });
    const polled = await pollLink(store, accounts, started.deviceCode, now);
    if (!("token" in polled) || !polled.token) throw new Error("no token");
    const licence = readLicence(polled.token, PUB)!;
    expect(licence.plan).toBe("trial");
    expect(licence.device).toBe("phone-123456");
    expect(licence.until - Math.floor(now.getTime() / 1000)).toBe(7 * 86400);
    expect(await pollLink(store, accounts, started.deviceCode, now)).toEqual({ error: "expired" });  // only once

    const refreshed = await refresh(store, accounts, polled.token, PUB, now);
    expect("token" in refreshed).toBe(true);
    await store.removeDevice("user_1", "phone-123456");
    expect(await refresh(store, accounts, polled.token, PUB, now)).toEqual({ error: "signed_out" });
  });

  it("limits devices", async () => {
    const store = new MemoryStore();
    const accounts = new FakeAccounts();
    accounts.map.set("user_1", { id: "user_1", email: "me@x", createdAt: 0, meta: {} });
    for (let i = 0; i < 4; i++) {
      const s = await startLink(store, { deviceId: `device-${i}-xxxx` });
      if (!("userCode" in s) || !s.userCode) throw new Error();
      const r = await approveLink(store, accounts, "user_1", s.userCode);
      expect("ok" in r).toBe(i < 3);
    }
  });

  it("normalises typed codes", () => expect(normaliseCode(" abcd 2345 ")).toBe("ABCD-2345"));
});

describe("payments", () => {
  const now = new Date("2026-10-21T12:00:00Z");
  const ref = "antenna_user_abc_monthly_x1y2";

  it("redeems once, and only the right amount", async () => {
    const store = new MemoryStore();
    const accounts = new FakeAccounts();
    accounts.map.set("user_abc", { id: "user_abc", email: "me@x", createdAt: 0, meta: {} });
    const used: string[] = [];
    const tx = { status: "success", reference: ref, amount: 300000, currency: "NGN", paid_at: now.toISOString() };
    const go = (t: object) => redeem(store, accounts, ref, t, now, async () => used, async (_u, r) => { used.push(r); });

    expect(await go({ ...tx, amount: 100 })).toMatchObject({ ok: false, error: "amount_mismatch" });
    expect(await go(tx)).toMatchObject({ ok: true, plan: "monthly" });
    expect(await go(tx)).toMatchObject({ ok: false, error: "reference_already_used" });
    expect((await store.payments()).length).toBe(1);
    expect(parseReference("antenna_user_abc_lifetime_1")).toBeNull();
  });

  it("checks webhook signatures", () => {
    const body = '{"event":"charge.success"}';
    const sig = createHmac("sha512", "sk_test").update(body).digest("hex");
    expect(validSignature(body, sig, "sk_test")).toBe(true);
    expect(validSignature(body, sig, "other")).toBe(false);
  });
});
