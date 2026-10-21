// Makes the licence signing key. Put LICENCE_PRIVATE_KEY in Vercel (keep it secret), and the public key in the
// antenna repo's Actions variable LICENCE_PUBLIC_KEY so the apps are built with it.
import { generateKeyPairSync } from "crypto";

const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const pem = privateKey.export({ type: "pkcs8", format: "pem" }).toString();
console.log("LICENCE_PRIVATE_KEY (Vercel → Settings → Environment Variables), on one line:\n");
console.log(pem.trim().replace(/\n/g, "\\n"));
console.log("\nLICENCE_PUBLIC_KEY (GitHub → antenna → Settings → Secrets and variables → Actions → Variables):\n");
console.log(publicKey.export({ type: "spki", format: "der" }).toString("base64"));
