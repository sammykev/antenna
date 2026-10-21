// Licences the apps check offline: "<base64url(JSON payload)>.<base64url(ECDSA P-256 SHA-256 DER signature)>",
// signed over the first part. The Windows (Antenna.Core/Licensing) and Android (licence/Licence.kt) apps verify
// them with the public key, so this format must stay in step with theirs.
import { createPrivateKey, createPublicKey, sign, verify } from "crypto";

export type LicencePayload = {
  v: 1;
  /** Clerk user id */
  sub: string;
  email: string;
  /** "trial", "monthly", "yearly", "granted" (by an admin) or "none" (trial over, not subscribed) */
  plan: string;
  /** Access ends (unix seconds). */
  until: number;
  /** The app must refresh by then (unix seconds), so revoked access and new payments reach it. */
  refresh: number;
  device: string;
  iat: number;
};

/** How long an app may go without reaching the site before it has to refresh. */
export const REFRESH_SECONDS = 14 * 24 * 3600;

const b64url = (b: Buffer) => b.toString("base64url");

function privateKey() {
  const pem = process.env.LICENCE_PRIVATE_KEY;
  if (!pem) throw new Error("LICENCE_PRIVATE_KEY is not set");
  return createPrivateKey(pem.replace(/\\n/g, "\n"));
}

export function signLicence(payload: LicencePayload, key = privateKey()): string {
  const body = b64url(Buffer.from(JSON.stringify(payload), "utf8"));
  const signature = sign("sha256", Buffer.from(body, "ascii"), key);  // DER by default
  return `${body}.${b64url(signature)}`;
}

/** The payload if the signature is good (whatever the dates say), else null. */
export function readLicence(token: string, publicKeyPem: string): LicencePayload | null {
  const [body, signature, extra] = token.split(".");
  if (!body || !signature || extra !== undefined) return null;
  try {
    const ok = verify("sha256", Buffer.from(body, "ascii"), createPublicKey(publicKeyPem), Buffer.from(signature, "base64url"));
    if (!ok) return null;
    const payload = JSON.parse(Buffer.from(body, "base64url").toString("utf8"));
    return payload?.v === 1 && typeof payload.sub === "string" && typeof payload.device === "string" ? payload : null;
  } catch {
    return null;
  }
}

export function publicKeyPem(): string {
  return createPublicKey(privateKey()).export({ type: "spki", format: "pem" }).toString();
}

/** The public key as the apps take it: base64 of the DER SubjectPublicKeyInfo (one line). */
export function publicKeyForApps(): string {
  return (createPublicKey(privateKey()).export({ type: "spki", format: "der" }) as Buffer).toString("base64");
}
