import { NextResponse } from "next/server";

export const json = (body: unknown, status = 200) => NextResponse.json(body, { status, headers: { "Cache-Control": "no-store" } });

/** Errors the app understands ("expired", "too_many_devices"…) go back as 400s with that code. */
export function result(r: object) {
  return "error" in r ? json(r, (r as { error: string }).error === "signed_out" ? 401 : 400) : json(r);
}

export async function body(req: Request): Promise<Record<string, unknown>> {
  try {
    const b = await req.json();
    return b && typeof b === "object" ? b : {};
  } catch {
    return {};
  }
}
