"use client";
// Your plan and the devices signed in to Antenna.
import { useCallback, useEffect, useState } from "react";
import Link from "next/link";

type Device = { id: string; name: string; platform: string; linkedAt: string; lastSeen: string };
type Account = { email: string; entitlement: { plan: string; until: string | null; active: boolean }; devices: Device[] };

const planName = (p: string) => ({ trial: "Free trial", monthly: "Monthly", yearly: "Yearly", granted: "Complimentary", none: "No active plan" } as Record<string, string>)[p] ?? p;

export default function AccountPage() {
  const [account, setAccount] = useState<Account | null>(null);
  const load = useCallback(() => fetch("/api/account").then(r => r.json()).then(setAccount), []);
  useEffect(() => { load(); }, [load]);

  async function remove(id: string) {
    await fetch(`/api/account?device=${encodeURIComponent(id)}`, { method: "DELETE" });
    load();
  }

  if (!account) return <p className="pt-10 text-muted">Loading…</p>;
  const e = account.entitlement;
  return (
    <div className="space-y-6 pt-8">
      <h1 className="text-3xl font-bold">Account</h1>
      <div className="card space-y-2">
        <p className="text-muted">{account.email}</p>
        <p className="text-xl font-semibold">{planName(e.plan)}</p>
        {e.until && <p>{e.active ? "Until" : "Ended"} {new Date(e.until).toLocaleString()}</p>}
        <Link className="btn" href="/pricing">{e.active && e.plan !== "trial" ? "Extend" : "Subscribe"}</Link>
      </div>
      <div className="card space-y-3">
        <h2 className="text-lg font-semibold">Devices</h2>
        {account.devices.length === 0 && <p className="text-muted">No apps signed in yet. In Antenna, choose Sign in and enter the code at <Link className="underline" href="/link">/link</Link>.</p>}
        {account.devices.map(d => (
          <div key={d.id} className="flex items-center justify-between gap-4 border-t border-white/10 pt-3">
            <div>
              <p className="font-medium">{d.name} <span className="text-sm text-muted">({d.platform})</span></p>
              <p className="text-sm text-muted">Last seen {new Date(d.lastSeen).toLocaleString()}</p>
            </div>
            <button className="btn-quiet" onClick={() => remove(d.id)}>Sign out</button>
          </div>
        ))}
      </div>
    </div>
  );
}
