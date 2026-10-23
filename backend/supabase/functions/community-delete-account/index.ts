import { createHandler } from "./handler.ts";

declare const Deno: {
  env: { get(name: string): string | undefined };
  serve(handler: (request: Request) => Promise<Response>): void;
};

// Supabase supplies these project-scoped values inside Edge. The administrative
// credential never leaves this function and is not installed on the home server.
const handler = createHandler({
  projectUrl: Deno.env.get("SUPABASE_URL") ?? "",
  publishableKey: Deno.env.get("KLIPP_PUBLISHABLE_KEY") ?? Deno.env.get("SUPABASE_ANON_KEY") ?? "",
  serviceRoleKey: Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? "",
});
Deno.serve(handler);
