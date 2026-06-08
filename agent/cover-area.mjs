/*
 * cover-area.mjs — drive the Claude Agent SDK to cover ONE area's tests completely.
 *
 * Invoked by run-agent.ps1:
 *   node cover-area.mjs --area <Area> --prompt <prompts/cover-area.md> --model <id> --qa-root <qa-ai>
 *
 * Responsibilities:
 *   - Read prompts/cover-area.md, substitute {{AREA}}.
 *   - Run the discovery agent headless via @anthropic-ai/claude-agent-sdk, pinned model.
 *   - Restrict the working dir to qa-ai/ (READ wider, WRITE only here — enforced in prompt;
 *     the SDK's cwd + allowed tools keep the blast radius small).
 *   - Print a final "COST_USD=<n>" line that weekend-runner.ps1 accumulates against the cap.
 *
 * Key handling: ANTHROPIC_API_KEY is read from the environment by the SDK. Never logged,
 * never taken as an argument. If missing, exit non-zero.
 */
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

function arg(name, def = null) {
  const i = process.argv.indexOf(`--${name}`);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : def;
}

const area    = arg("area");
const promptF = arg("prompt");
const model   = arg("model", "claude-opus-4-8");
const qaRoot  = resolve(arg("qa-root", "."));

if (!area || !promptF) { console.error("usage: --area <A> --prompt <file> [--model] [--qa-root]"); process.exit(2); }
if (!process.env.ANTHROPIC_API_KEY) { console.error("ANTHROPIC_API_KEY not set in env."); process.exit(2); }

const prompt = readFileSync(promptF, "utf8").replaceAll("{{AREA}}", area);

// Load the SDK lazily so the script gives a clear install hint if it's missing.
let query;
try {
  ({ query } = await import("@anthropic-ai/claude-agent-sdk"));
} catch {
  console.error("Missing @anthropic-ai/claude-agent-sdk. Run:  npm i --prefix " + qaRoot + " @anthropic-ai/claude-agent-sdk");
  console.error("(Or use the claude -p fallback in run-agent.ps1.)");
  process.exit(3);
}

let totalCostUsd = 0;

try {
  const response = query({
    prompt,
    options: {
      model,
      cwd: qaRoot,                       // work rooted at qa-ai; agent reads ../2.0.x but writes here
      permissionMode: "acceptEdits",      // unattended: auto-accept edits (scope limited by prompt + cwd)
      allowedTools: ["Read", "Grep", "Glob", "Bash", "Write", "Edit"],
      // a hard ceiling per area so one stuck area can't blow the whole budget
      maxTurns: 80,
    },
  });

  for await (const msg of response) {
    if (msg.type === "assistant" && msg.message?.content) {
      for (const block of msg.message.content) {
        if (block.type === "text" && block.text.trim()) console.log(block.text.trim());
      }
    }
    // result message carries the usage/cost for the run
    if (msg.type === "result") {
      if (typeof msg.total_cost_usd === "number") totalCostUsd = msg.total_cost_usd;
      if (msg.subtype && msg.subtype !== "success") console.error("agent ended: " + msg.subtype);
    }
  }
} catch (e) {
  console.error("agent error: " + (e?.message ?? e));
  // still emit a cost line so the runner's accounting stays consistent
  console.log(`COST_USD=${totalCostUsd.toFixed(4)}`);
  process.exit(1);
}

// the line weekend-runner.ps1 parses to enforce the $50 cap
console.log(`COST_USD=${totalCostUsd.toFixed(4)}`);
