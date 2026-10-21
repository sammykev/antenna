"use client";
// The admin dashboard: totals, revenue, payments, and each account's plan and devices, with grant, block and sign-out.
import { useCallback, useEffect, useState } from "react";

type Stats = { users: number; signedUpThisWeek: number; inTrial: number; paying: number; granted: number; lapsed: number;
  revenueThisMonth: number; revenueTotal: number; paymentsThisMonth: number };
type Payment = { userId: string; email: string; plan: string; ngn: number; reference: string; at: string };
type User = { id: string; email: string; createdAt: number; lastSignIn: number | null; plan: string; active: boolean;
  until: string | null; trialEnds: string | null; blocked: boolean };
type Device = { id: string; name: string; platform: string; linkedAt: string; lastSeen: string };

const naira = (n: number) => `₦${n.toLocaleString("en-US")}`;
const when = (v: string | number | null) => (v ? new Date(v).toLocaleString() : "—");

async function post(path: string, body: object) {
  const res = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  if (!res.ok) alert(`Failed: ${(await res.json().catch(() => ({}))).error ?? res.status}`);
}

export default function Admin() {
  const [stats, setStats] = useState<Stats | null>(null);
  const [payments, setPayments] = useState<Payment[]>([]);
  const [users, setUsers] = useState<User[]>([]);
  const [total, setTotal] = useState(0);
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState<{ user: User; devices: Device[] } | null>(null);
  const [forbidden, setForbidden] = useState(false);

  const loadOverview = useCallback(async () => {
    const res = await fetch("/api/admin/overview");
    if (res.status === 403) return setForbidden(true);
    const data = await res.json();
    setStats(data.stats);
    setPayments(data.payments);
  }, []);
  const loadUsers = useCallback(async (q: string) => {
    const res = await fetch(`/api/admin/users${q ? `?q=${encodeURIComponent(q)}` : ""}`);
    if (res.status === 403) return setForbidden(true);
    const data = await res.json();
    setUsers(data.users);
    setTotal(data.total);
  }, []);
  useEffect(() => { loadOverview(); loadUsers(""); }, [loadOverview, loadUsers]);

  async function openUser(user: User) {
    const data = await (await fetch(`/api/admin/users?id=${encodeURIComponent(user.id)}`)).json();
    setOpen({ user, devices: data.devices });
  }
  async function refresh() {
    await Promise.all([loadOverview(), loadUsers(query)]);
    if (open) {
      const fresh = (await (await fetch(`/api/admin/users?q=${encodeURIComponent(open.user.email)}`)).json()).users?.find((u: User) => u.id === open.user.id);
      if (fresh) await openUser(fresh);
    }
  }

  if (forbidden) return <p className="pt-10 text-red-400">This page is for Antenna admins. Add your Clerk user id to ADMIN_USER_IDS.</p>;

  return (
    <div className="space-y-6 pt-6">
      <h1 className="text-3xl font-bold">Admin</h1>

      {stats && (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {[
            ["Accounts", stats.users, `${stats.signedUpThisWeek} this week`],
            ["Paying", stats.paying, `${stats.granted} complimentary`],
            ["In trial", stats.inTrial, `${stats.lapsed} lapsed`],
            ["Revenue this month", naira(stats.revenueThisMonth), `${stats.paymentsThisMonth} payments · ${naira(stats.revenueTotal)} total`],
          ].map(([label, value, note]) => (
            <div key={label as string} className="card">
              <p className="text-sm text-muted">{label}</p>
              <p className="text-2xl font-bold">{value}</p>
              <p className="text-xs text-muted">{note}</p>
            </div>
          ))}
        </div>
      )}

      <div className="card space-y-3">
        <div className="flex items-center gap-3">
          <h2 className="flex-1 text-lg font-semibold">Accounts <span className="text-sm text-muted">({total})</span></h2>
          <form onSubmit={e => { e.preventDefault(); loadUsers(query); }} className="flex gap-2">
            <input className="input" placeholder="Search email" value={query} onChange={e => setQuery(e.target.value)} />
            <button className="btn-quiet">Search</button>
          </form>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-left text-muted"><tr><th className="py-2">Email</th><th>Plan</th><th>Until</th><th>Joined</th><th>Last sign-in</th></tr></thead>
            <tbody>
              {users.map(u => (
                <tr key={u.id} className="cursor-pointer border-t border-white/10 hover:bg-white/5" onClick={() => openUser(u)}>
                  <td className="py-2">{u.email}{u.blocked && <span className="ml-2 text-red-400">blocked</span>}</td>
                  <td className={u.active ? "" : "text-muted"}>{u.plan}</td>
                  <td>{when(u.until)}</td>
                  <td>{when(u.createdAt)}</td>
                  <td>{when(u.lastSignIn)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <div className="card space-y-2">
        <h2 className="text-lg font-semibold">Latest payments</h2>
        {payments.length === 0 && <p className="text-muted">No payments yet.</p>}
        {payments.map(p => (
          <div key={p.reference} className="flex justify-between border-t border-white/10 pt-2 text-sm">
            <span>{p.email} · {p.plan}</span><span>{naira(p.ngn)} · {when(p.at)}</span>
          </div>
        ))}
      </div>

      {open && (
        <div className="fixed inset-0 flex items-center justify-center bg-black/60 p-4" onClick={() => setOpen(null)}>
          <div className="card w-full max-w-lg space-y-4" onClick={e => e.stopPropagation()}>
            <div>
              <h2 className="text-xl font-semibold">{open.user.email}</h2>
              <p className="text-sm text-muted">{open.user.id}</p>
              <p>Plan: {open.user.plan} {open.user.until && `· until ${when(open.user.until)}`}</p>
              <p className="text-sm text-muted">Trial {open.user.trialEnds ? `ends ${when(open.user.trialEnds)}` : "not started"}</p>
            </div>
            <div className="flex flex-wrap gap-2">
              {[7, 30, 365].map(d => <button key={d} className="btn-quiet" onClick={async () => { await post("/api/admin/grant", { userId: open.user.id, days: d }); refresh(); }}>+{d} days</button>)}
              <button className="btn-quiet" onClick={async () => { await post("/api/admin/grant", { userId: open.user.id, days: -30 }); refresh(); }}>−30 days</button>
              <button className="btn-quiet text-red-400" onClick={async () => { await post("/api/admin/block", { userId: open.user.id, blocked: !open.user.blocked }); refresh(); }}>
                {open.user.blocked ? "Unblock" : "Block"}
              </button>
            </div>
            <div className="space-y-2">
              <h3 className="font-semibold">Devices</h3>
              {open.devices.length === 0 && <p className="text-sm text-muted">None signed in.</p>}
              {open.devices.map(d => (
                <div key={d.id} className="flex items-center justify-between border-t border-white/10 pt-2 text-sm">
                  <span>{d.name} ({d.platform}) · seen {when(d.lastSeen)}</span>
                  <button className="btn-quiet" onClick={async () => { await post("/api/admin/devices", { userId: open.user.id, deviceId: d.id }); openUser(open.user); }}>Sign out</button>
                </div>
              ))}
            </div>
            <button className="btn w-full" onClick={() => setOpen(null)}>Close</button>
          </div>
        </div>
      )}
    </div>
  );
}
