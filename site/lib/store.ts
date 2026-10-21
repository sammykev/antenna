// Short-lived sign-in codes, each account's devices and the payments log, in Upstash Redis. Tests use the in-memory
// store, so the API logic runs without a database.
import { Redis } from "@upstash/redis";

export type Device = { id: string; name: string; platform: string; linkedAt: string; lastSeen: string };
export type Payment = { userId: string; email: string; plan: string; ngn: number; reference: string; at: string };
export type PendingCode = { deviceId: string; name: string; platform: string; status: "pending" | "approved"; userId?: string };

export interface Store {
  putCode(deviceCode: string, userCode: string, code: PendingCode, seconds: number): Promise<void>;
  codeByUser(userCode: string): Promise<{ deviceCode: string; code: PendingCode } | null>;
  codeByDevice(deviceCode: string): Promise<PendingCode | null>;
  approve(deviceCode: string, userId: string): Promise<void>;
  dropCode(deviceCode: string): Promise<void>;
  devices(userId: string): Promise<Device[]>;
  saveDevice(userId: string, device: Device): Promise<void>;
  removeDevice(userId: string, deviceId: string): Promise<void>;
  addPayment(p: Payment): Promise<void>;
  payments(limit?: number): Promise<Payment[]>;
}

export class RedisStore implements Store {
  constructor(private r = Redis.fromEnv()) {}

  async putCode(deviceCode: string, userCode: string, code: PendingCode, seconds: number) {
    await this.r.set(`dc:${deviceCode}`, code, { ex: seconds });
    await this.r.set(`uc:${userCode}`, deviceCode, { ex: seconds });
  }
  async codeByUser(userCode: string) {
    const deviceCode = await this.r.get<string>(`uc:${userCode}`);
    const code = deviceCode ? await this.r.get<PendingCode>(`dc:${deviceCode}`) : null;
    return deviceCode && code ? { deviceCode, code } : null;
  }
  codeByDevice(deviceCode: string) {
    return this.r.get<PendingCode>(`dc:${deviceCode}`);
  }
  async approve(deviceCode: string, userId: string) {
    const code = await this.codeByDevice(deviceCode);
    if (code) await this.r.set(`dc:${deviceCode}`, { ...code, status: "approved", userId }, { keepTtl: true });
  }
  async dropCode(deviceCode: string) {
    await this.r.del(`dc:${deviceCode}`);
  }
  async devices(userId: string) {
    const all = await this.r.hgetall<Record<string, Device>>(`devices:${userId}`);
    return Object.values(all ?? {}).sort((a, b) => a.linkedAt.localeCompare(b.linkedAt));
  }
  async saveDevice(userId: string, device: Device) {
    await this.r.hset(`devices:${userId}`, { [device.id]: device });
  }
  async removeDevice(userId: string, deviceId: string) {
    await this.r.hdel(`devices:${userId}`, deviceId);
  }
  async addPayment(p: Payment) {
    await this.r.lpush("payments", p);
    await this.r.ltrim("payments", 0, 9999);
  }
  payments(limit = 1000) {
    return this.r.lrange<Payment>("payments", 0, limit - 1);
  }
}

export class MemoryStore implements Store {
  codes = new Map<string, PendingCode>();
  users = new Map<string, string>();
  deviceMap = new Map<string, Map<string, Device>>();
  paymentList: Payment[] = [];

  async putCode(deviceCode: string, userCode: string, code: PendingCode) {
    this.codes.set(deviceCode, code);
    this.users.set(userCode, deviceCode);
  }
  async codeByUser(userCode: string) {
    const deviceCode = this.users.get(userCode);
    const code = deviceCode ? this.codes.get(deviceCode) : undefined;
    return deviceCode && code ? { deviceCode, code } : null;
  }
  async codeByDevice(deviceCode: string) {
    return this.codes.get(deviceCode) ?? null;
  }
  async approve(deviceCode: string, userId: string) {
    const code = this.codes.get(deviceCode);
    if (code) this.codes.set(deviceCode, { ...code, status: "approved", userId });
  }
  async dropCode(deviceCode: string) {
    this.codes.delete(deviceCode);
  }
  async devices(userId: string) {
    return [...(this.deviceMap.get(userId)?.values() ?? [])];
  }
  async saveDevice(userId: string, device: Device) {
    if (!this.deviceMap.has(userId)) this.deviceMap.set(userId, new Map());
    this.deviceMap.get(userId)!.set(device.id, device);
  }
  async removeDevice(userId: string, deviceId: string) {
    this.deviceMap.get(userId)?.delete(deviceId);
  }
  async addPayment(p: Payment) {
    this.paymentList.unshift(p);
  }
  async payments(limit = 1000) {
    return this.paymentList.slice(0, limit);
  }
}

let shared: Store | null = null;
export const store = (): Store => (shared ??= new RedisStore());
