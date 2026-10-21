// Writes a licence signed by this site's code with a throwaway test key, for the apps' tests
// (tests/Antenna.Core.Tests/licence-fixture.json and android/app/src/test/resources/licence-fixture.json).
import { generateKeyPairSync } from "crypto";
import { writeFileSync } from "fs";
import { signLicence } from "../lib/licence";

const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const payload = { v: 1 as const, sub: "user_test", email: "test@example.com", plan: "monthly", until: 2_000_000_000, refresh: 1_900_000_000, device: "device-fixture-1", iat: 1_800_000_000 };
const token = signLicence(payload, privateKey);
const [body, sig] = token.split(".");
const tampered = Buffer.from(Buffer.from(body, "base64url").toString().replace("monthly", "yearly!")).toString("base64url") + "." + sig;
const fixture = { publicKey: (publicKey.export({ type: "spki", format: "der" }) as Buffer).toString("base64"), token, tampered, payload };
for (const path of process.argv.slice(2)) writeFileSync(path, JSON.stringify(fixture, null, 2) + "\n");
