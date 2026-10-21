"use client";
// Where the user types the code an app shows, to sign that app in.
import { Suspense, useState } from "react";
import { useSearchParams } from "next/navigation";
import Link from "next/link";

function LinkForm() {
  const params = useSearchParams();
  const [code, setCode] = useState(params.get("code") ?? "");
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    const res = await fetch("/api/device/approve", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code }) });
    const data = await res.json().catch(() => ({}));
    setBusy(false);
    setMessage(res.ok ? { ok: true, text: `${data.name} is signed in. You can go back to the app.` }
      : data.error === "too_many_devices" ? { ok: false, text: "You're signed in on the most devices a plan allows. Sign one out on your Account page first." }
      : { ok: false, text: "That code didn't work. Check it, or get a new one in the app (codes last 10 minutes)." });
  }

  return (
    <form onSubmit={submit} className="card mx-auto mt-10 max-w-md space-y-4">
      <h1 className="text-2xl font-bold">Sign in an app</h1>
      <p className="text-muted">Enter the code Antenna shows on your PC or phone.</p>
      <input className="input text-center text-2xl tracking-widest" value={code} onChange={e => setCode(e.target.value)} placeholder="ABCD-1234" autoFocus />
      <button className="btn w-full" disabled={busy || code.replace(/[^A-Za-z0-9]/g, "").length < 8}>Sign in</button>
      {message && <p className={message.ok ? "text-green-400" : "text-red-400"}>{message.text}</p>}
      {message?.ok && <p className="text-sm text-muted">Manage your devices on your <Link className="underline" href="/account">Account</Link> page.</p>}
    </form>
  );
}

export default function LinkPage() {
  return <Suspense><LinkForm /></Suspense>;
}
