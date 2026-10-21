import Link from "next/link";
import { TRIAL_DAYS } from "@/lib/pricing";

const DOWNLOADS = "https://github.com/sammykev/antenna-releases/releases/latest";

export default function Home() {
  return (
    <div className="space-y-10 pt-8">
      <section className="space-y-4">
        <h1 className="text-4xl font-bold">Your TV, on your PC and phone.</h1>
        <p className="max-w-2xl text-lg text-muted">
          Antenna plays the playlists from your TV provider (M3U or Xtream Codes) with a full guide, movies and series,
          today&apos;s matches on your channels, goal alerts, picture-in-picture and Chromecast. Antenna doesn&apos;t
          include any channels: you bring your own.
        </p>
        <div className="flex gap-3">
          <a className="btn" href={DOWNLOADS}>Download for Windows or Android</a>
          <Link className="btn-quiet" href="/pricing">Pricing</Link>
        </div>
        <p className="text-sm text-muted">{TRIAL_DAYS} days free, then a monthly or yearly plan.</p>
      </section>
      <section className="grid gap-4 sm:grid-cols-3">
        {[
          ["Live TV and guide", "Categories, search, now and next, and a guide you can search across every channel."],
          ["Matches and alerts", "Today's fixtures matched to your channels, with kick-off, goal and full-time alerts."],
          ["Everywhere", "Windows and Android, synced through your own Google Drive or OneDrive."],
        ].map(([t, d]) => (
          <div key={t} className="card"><h2 className="mb-1 font-semibold">{t}</h2><p className="text-sm text-muted">{d}</p></div>
        ))}
      </section>
    </div>
  );
}
