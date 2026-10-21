// The device sign-in flow and licence refreshes, independent of Next.js so it can be tested end to end.
import { CODE_SECONDS, deviceCode as newDeviceCode, normaliseCode, userCode as newUserCode } from "./codes";
import { entitlement, trialStart } from "./entitlement";
import { REFRESH_SECONDS, readLicence, signLicence, type LicencePayload } from "./licence";
import { MAX_DEVICES } from "./pricing";
import type { Store } from "./store";
import type { Account, Accounts } from "./users";

const clean = (s: unknown, max: number) => (typeof s === "string" ? s.trim().slice(0, max) : "");

/** The app asks to sign in: it gets a secret device code to poll with and a short code for the user to type. */
export async function startLink(store: Store, body: { deviceId?: unknown; name?: unknown; platform?: unknown }) {
  const deviceId = clean(body.deviceId, 100);
  if (deviceId.length < 8) return { error: "bad_device" as const };
  const deviceCode = newDeviceCode();
  const userCode = newUserCode();
  await store.putCode(deviceCode, userCode, {
    deviceId, name: clean(body.name, 80) || "Device", platform: clean(body.platform, 20) || "unknown", status: "pending",
  }, CODE_SECONDS);
  return { deviceCode, userCode, expiresIn: CODE_SECONDS, interval: 5 };
}

/** The signed-in user typed the code at /link. */
export async function approveLink(store: Store, accounts: Accounts, userId: string, typed: string, now = new Date()) {
  const found = await store.codeByUser(normaliseCode(typed));
  if (!found || found.code.status !== "pending") return { error: "unknown_code" as const };
  const devices = await store.devices(userId);
  const known = devices.some(d => d.id === found.code.deviceId);
  if (!known && devices.length >= MAX_DEVICES) return { error: "too_many_devices" as const, devices };

  const account = await accounts.get(userId);
  if (!account) return { error: "no_account" as const };
  const trial = trialStart(account.meta, now);
  if (trial) await accounts.update(userId, trial);
  await store.saveDevice(userId, {
    id: found.code.deviceId, name: found.code.name, platform: found.code.platform,
    linkedAt: devices.find(d => d.id === found.code.deviceId)?.linkedAt ?? now.toISOString(), lastSeen: now.toISOString(),
  });
  await store.approve(found.deviceCode, userId);
  return { ok: true as const, name: found.code.name };
}

/** The app polls until the user has typed the code; then it gets its first licence (once). */
export async function pollLink(store: Store, accounts: Accounts, deviceCode: unknown, now = new Date()) {
  if (typeof deviceCode !== "string") return { error: "bad_request" as const };
  const code = await store.codeByDevice(deviceCode);
  if (!code) return { error: "expired" as const };
  if (code.status !== "approved" || !code.userId) return { pending: true as const };
  await store.dropCode(deviceCode);
  const account = await accounts.get(code.userId);
  if (!account) return { error: "no_account" as const };
  return { token: licenceFor(account, code.deviceId, now) };
}

/**
 * An app brings its current licence (signed by us, possibly expired) and gets a fresh one, as long as the device is
 * still linked. That's how payments, grants and removals reach the apps.
 */
export async function refresh(store: Store, accounts: Accounts, token: unknown, publicKeyPem: string, now = new Date()) {
  const old = typeof token === "string" ? readLicence(token, publicKeyPem) : null;
  if (!old) return { error: "bad_licence" as const };
  const devices = await store.devices(old.sub);
  const device = devices.find(d => d.id === old.device);
  if (!device) return { error: "signed_out" as const };
  const account = await accounts.get(old.sub);
  if (!account) return { error: "signed_out" as const };
  await store.saveDevice(old.sub, { ...device, lastSeen: now.toISOString() });
  return { token: licenceFor(account, old.device, now) };
}

export function licenceFor(account: Account, deviceId: string, now = new Date()): string {
  const e = entitlement(account.meta, now);
  const nowS = Math.floor(now.getTime() / 1000);
  const payload: LicencePayload = {
    v: 1, sub: account.id, email: account.email, plan: e.plan, device: deviceId, iat: nowS,
    until: e.active && e.until ? Math.floor(e.until.getTime() / 1000) : nowS,
    // While a trial or plan runs, check back at least every 14 days; once it's over, as soon as the app opens.
    refresh: nowS + (e.active ? REFRESH_SECONDS : 3600),
  };
  return signLicence(payload);
}
