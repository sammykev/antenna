import type { Metadata } from "next";
import Link from "next/link";
import { ClerkProvider, Show, SignInButton, UserButton } from "@clerk/nextjs";
import "./globals.css";

export const metadata: Metadata = {
  title: "Antenna",
  description: "IPTV player for Windows and Android: your playlists, guide, matches and goal alerts.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <ClerkProvider appearance={{ variables: { colorPrimary: "#4F7CFF", colorPrimaryForeground: "#FFFFFF", colorBackground: "#1B1F28",
      colorForeground: "#FFFFFF", colorMutedForeground: "#9AA3B2", colorInput: "#12151C", colorInputForeground: "#FFFFFF",
      colorNeutral: "#FFFFFF", colorBorder: "#2A303C", borderRadius: "0.75rem" } }}>
      <html lang="en">
        <body className="min-h-screen">
          <header className="mx-auto flex max-w-5xl items-center gap-6 px-4 py-4">
            <Link href="/" className="text-lg font-bold">Antenna</Link>
            <nav className="flex flex-1 gap-4 text-sm text-muted">
              <Link href="/pricing">Pricing</Link>
              <Show when="signed-in"><Link href="/account">Account</Link></Show>
            </nav>
            <Show when="signed-out"><SignInButton mode="modal"><button className="btn-quiet">Sign in</button></SignInButton></Show>
            <Show when="signed-in"><UserButton /></Show>
          </header>
          <main className="mx-auto max-w-5xl px-4 pb-16">{children}</main>
        </body>
      </html>
    </ClerkProvider>
  );
}
