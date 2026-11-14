// UI guardrail, run before every build (`npm run build`, and so the web
// Docker image). Fails when a native form control that can't follow the dark
// theme is used instead of MAZDA's component.
//
//   <select> / <option>  → use <Select> from src/shared/components/Select.tsx
//                          (the native option list is drawn by the OS: white
//                          popup on our dark UI, and it ignores CSS).
//
// To add a rule: push { pattern, allowIn, message } onto RULES.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../src", import.meta.url));

const RULES = [
  {
    pattern: /<(select|option)[\s>/]/,
    allowIn: ["shared/components/Select.tsx"],
    message: "Native <select>/<option> renders an OS-styled (white) popup. Use <Select> from @/shared/components/Select.",
  },
];

function* files(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) yield* files(p);
    else if (/\.(tsx|jsx)$/.test(name)) yield p;
  }
}

const problems = [];
for (const file of files(root)) {
  const rel = relative(root, file).split(sep).join("/");
  const lines = readFileSync(file, "utf8").split("\n");
  for (const rule of RULES) {
    if (rule.allowIn.includes(rel)) continue;
    lines.forEach((line, i) => {
      const code = line.replace(/\/\/.*$/, "");               // ignore line comments
      if (/^\s*(\*|\/\*)/.test(line)) return;                   // ignore block-comment lines
      if (rule.pattern.test(code)) problems.push(`src/${rel}:${i + 1}  ${rule.message}`);
    });
  }
}

if (problems.length) {
  console.error("UI check failed:\n" + problems.map((p) => "  " + p).join("\n"));
  process.exit(1);
}
console.log("UI check passed.");
