// The public key the apps are built with (base64 DER), so it can be copied into the LICENCE_PUBLIC_KEY variable.
import { json } from "@/lib/http";
import { publicKeyForApps } from "@/lib/licence";

export const dynamic = "force-dynamic";

export async function GET() {
  return json({ publicKey: publicKeyForApps() });
}
