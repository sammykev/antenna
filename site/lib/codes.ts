// Sign-in codes: the app shows a short code, the user types it at /link while signed in.
import { randomBytes, randomInt } from "crypto";

// No 0/O, 1/I/L: easy to read off a TV or phone and type.
const ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

export function userCode(): string {
  const pick = () => Array.from({ length: 4 }, () => ALPHABET[randomInt(ALPHABET.length)]).join("");
  return `${pick()}-${pick()}`;
}

/** Tidies what was typed: "abcd 1234", "ABCD1234" → "ABCD-1234". */
export function normaliseCode(input: string): string {
  const s = input.toUpperCase().replace(/[^A-Z0-9]/g, "");
  return s.length === 8 ? `${s.slice(0, 4)}-${s.slice(4)}` : s;
}

export const deviceCode = () => randomBytes(32).toString("base64url");

/** How long a code shown in the app stays usable. */
export const CODE_SECONDS = 600;
