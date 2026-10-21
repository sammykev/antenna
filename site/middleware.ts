import { clerkMiddleware, createRouteMatcher } from "@clerk/nextjs/server";

// Pages that need an account; the API routes check for themselves (apps call some without one).
const signedIn = createRouteMatcher(["/link(.*)", "/account(.*)", "/admin(.*)"]);

export default clerkMiddleware(async (auth, req) => {
  if (signedIn(req)) await auth.protect();
});

export const config = { matcher: ["/((?!_next|.*\\..*).*)", "/(api|trpc)(.*)"] };
