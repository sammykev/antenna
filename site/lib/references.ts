// Paystack references already redeemed, per account, in Clerk privateMetadata (as in BetIQ).
import { clerkClient } from "@clerk/nextjs/server";

const MAX = 100;

export async function usedReferences(userId: string): Promise<string[]> {
  const user = await (await clerkClient()).users.getUser(userId);
  const used = user.privateMetadata?.antenna_references;
  return Array.isArray(used) ? (used as string[]) : [];
}

export async function markUsed(userId: string, reference: string) {
  const used = await usedReferences(userId);
  await (await clerkClient()).users.updateUserMetadata(userId, { privateMetadata: { antenna_references: [...used, reference].slice(-MAX) } });
}
