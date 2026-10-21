import type { Config } from "tailwindcss";

export default {
  content: ["./app/**/*.{ts,tsx}", "./components/**/*.{ts,tsx}"],
  theme: { extend: { colors: { accent: "#4F7CFF", surface: "#1B1F28", base: "#12151C", muted: "#9AA3B2" } } },
} satisfies Config;
