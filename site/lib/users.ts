// Accounts live in Clerk; this is the slice of it the licence logic needs, so tests can stand in for Clerk.
import type { AntennaMeta } from "./entitlement";

export type Account = { id: string; email: string; createdAt: number; meta: AntennaMeta };

export interface Accounts {
  get(userId: string): Promise<Account | null>;
  update(userId: string, meta: Partial<AntennaMeta>): Promise<void>;
}

export class ClerkAccounts implements Accounts {
  private async client() {
    const { clerkClient } = await import("@clerk/nextjs/server");
    return clerkClient();
  }

  async get(userId: string): Promise<Account | null> {
    try {
      const user = await (await this.client()).users.getUser(userId);
      return {
        id: user.id,
        email: user.primaryEmailAddress?.emailAddress ?? user.emailAddresses[0]?.emailAddress ?? "",
        createdAt: user.createdAt,
        meta: (user.publicMetadata ?? {}) as AntennaMeta,
      };
    } catch {
      return null;
    }
  }

  async update(userId: string, meta: Partial<AntennaMeta>) {
    await (await this.client()).users.updateUserMetadata(userId, { publicMetadata: meta });
  }
}

let shared: Accounts | null = null;
export const accounts = (): Accounts => (shared ??= new ClerkAccounts());
