// Admins: signed-in Clerk users listed in ADMIN_USER_IDS (comma-separated), as on BetIQ.
import { auth } from "@clerk/nextjs/server";

export const adminIds = () => new Set((process.env.ADMIN_USER_IDS || "").split(",").map(s => s.trim()).filter(Boolean));

export async function currentAdmin(): Promise<string | null> {
  try {
    const { userId } = await auth();
    return userId && adminIds().has(userId) ? userId : null;
  } catch {
    return null;
  }
}
