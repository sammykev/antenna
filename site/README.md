# Antenna accounts site

Sign-up, the 7-day trial, monthly and yearly plans (Paystack), signing apps in with a code, the licences the apps
check, and the admin dashboard. Next.js 15 on Vercel, with Clerk for accounts, Paystack for payments and Upstash Redis
for sign-in codes, devices and the payments log: the same stack as BetIQ.

## How it fits together

1. The app (Windows or Android) asks `/api/device/start` for a code and shows it ("ABCD-2345").
2. The user signs in at `/link` and enters it. The first sign-in starts the 7-day trial (Clerk `publicMetadata.antenna_trial_ends`).
3. The app polls `/api/device/poll` and gets a **licence**: JSON signed with the site's private key (ECDSA P-256).
   The apps hold the public key and check licences offline (`src/Antenna.Core/Licensing`, `android/…/licence/Licence.kt`).
4. Apps refresh at `/api/licence/refresh` when they open and every few hours. A licence is good for at most 14 days
   without a refresh, so payments, admin grants, blocks and signed-out devices reach every app.
5. `/pricing` takes payment with Paystack; `/api/subscribe` and the webhook check it with Paystack and extend
   `antenna_expires` (30 or 365 days, from the current expiry if still running).

Plans, prices, trial length and the device limit are in `lib/pricing.ts`.

## Setting it up

1. **Vercel**: New Project → import `sammykev/antenna` → Root Directory `site`. (Keep the project name in mind: the
   site will be `https://<name>.vercel.app`, or add your own domain.)
2. **Clerk**: create an application (or reuse BetIQ's: Antenna's data lives under `antenna_*` keys). Copy the
   publishable and secret keys.
3. **Paystack**: copy the public and secret keys. Settings → API Keys & Webhooks → Webhook URL:
   `https://<site>/api/paystack/webhook`.
4. **Upstash**: create a Redis database; copy the REST URL and token.
5. **Licence key**: in `site/`, run `npm install` then `npm run keygen`. It prints `LICENCE_PRIVATE_KEY` (for Vercel,
   secret) and `LICENCE_PUBLIC_KEY` (for GitHub, public).
6. **Vercel → Settings → Environment Variables**: everything in `.env.example`. `ADMIN_USER_IDS` is your Clerk user id
   (Clerk dashboard → Users → you → User ID), comma-separated for several admins.
7. **GitHub → antenna → Settings → Secrets and variables → Actions → Variables**: `LICENCE_SERVER` =
   `https://<site>` and `LICENCE_PUBLIC_KEY` from step 5. Releases built after that have the paywall; builds without
   them (Android Studio, CI) don't.
8. Open `https://<site>/admin` signed in as yourself.

## Admin dashboard (`/admin`)

Accounts, trials, paying subscribers, complimentary accounts and lapsed trials; revenue this month and in total; the
latest payments; per account: plan, dates, devices, and buttons to add or take away days, block or unblock, and sign a
device out.

## Developing

`npm run dev` with a `.env.local` (see `.env.example`), `npm test` for the licence, sign-in, entitlement and payment
logic (no services needed), `npm run build` to check the production build.
