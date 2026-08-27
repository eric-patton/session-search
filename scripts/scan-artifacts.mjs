import fs from "node:fs";
import path from "node:path";

const repositoryRoot = path.resolve(import.meta.dirname, "..");
const requestedRoots = process.argv.length > 2
  ? process.argv.slice(2).map((value) => path.resolve(value))
  : [path.join(repositoryRoot, "artifacts")];
const forbiddenNames = [
  /\.jsonl$/iu,
  /\.sqlite3?$/iu,
  /(?:^|-)wal$/iu,
  /(?:^|-)shm$/iu,
  /(?:^|-)journal$/iu,
];
const textExtensions = new Set([
  ".csv",
  ".html",
  ".json",
  ".log",
  ".md",
  ".txt",
  ".xml",
]);
const benchmarkForbiddenKeys = /(?:command|description|directory|path|queryText|rawRoot|sessionId|snippet|title|transcript)/iu;
const pathLikeValue = /(?:[A-Za-z]:\\|\\\\[^\\\s]+\\|\/Users\/|\/home\/)/u;
const realRootCandidates = buildRealRootCandidates();
const configuredCanaries = (process.env.SESSIONSEARCH_ARTIFACT_CANARIES ?? "")
  .split(";")
  .map((value) => value.trim())
  .filter(Boolean);
const canaries = ["REAL-TRANSCRIPT-CANARY", ...configuredCanaries];
const errors = [];

for (const root of requestedRoots) {
  if (!fs.existsSync(root)) {
    continue;
  }

  for (const file of enumerateFiles(root)) {
    inspectFile(file);
  }
}

if (errors.length > 0) {
  for (const error of errors) {
    process.stderr.write(`artifact-scan: ${error}\n`);
  }

  process.exitCode = 1;
} else {
  process.stdout.write("artifact-scan: ok\n");
}

function inspectFile(file) {
  const relative = path.relative(repositoryRoot, file);
  const name = path.basename(file);
  if (forbiddenNames.some((pattern) => pattern.test(name))) {
    errors.push(`${relative}: forbidden index or transcript artifact name`);
    return;
  }

  const bytes = fs.readFileSync(file);
  if (bytes.subarray(0, 16).equals(Buffer.from("SQLite format 3\0", "ascii"))) {
    errors.push(`${relative}: embedded SQLite database header`);
  }

  if (bytes.length >= 4) {
    const magic = bytes.readUInt32BE(0);
    if (magic === 0x377f0682 || magic === 0x377f0683) {
      errors.push(`${relative}: embedded SQLite WAL header`);
    }
  }

  for (const candidate of realRootCandidates) {
    if (includesCaseInsensitive(bytes, candidate)) {
      errors.push(`${relative}: contains a configured provider root`);
      break;
    }
  }

  for (const canary of canaries) {
    if (includesCaseInsensitive(bytes, canary)) {
      errors.push(`${relative}: contains a protected transcript canary`);
      break;
    }
  }

  if (textExtensions.has(path.extname(file).toLowerCase())) {
    inspectTextFile(file, relative, bytes.toString("utf8"));
  }
}

function inspectTextFile(file, relative, text) {
  if (!path.basename(file).startsWith("benchmark-") || path.extname(file) !== ".json") {
    return;
  }

  let value;
  try {
    value = JSON.parse(text);
  } catch {
    errors.push(`${relative}: benchmark report is not valid JSON`);
    return;
  }

  inspectBenchmarkValue(value, relative, "report");
}

function inspectBenchmarkValue(value, relative, key) {
  if (typeof value === "string") {
    if (pathLikeValue.test(value)) {
      errors.push(`${relative}: benchmark report contains a path-like value at ${key}`);
    }

    return;
  }

  if (Array.isArray(value)) {
    value.forEach((item, index) => inspectBenchmarkValue(item, relative, `${key}[${index}]`));
    return;
  }

  if (value === null || typeof value !== "object") {
    return;
  }

  for (const [childKey, childValue] of Object.entries(value)) {
    if (benchmarkForbiddenKeys.test(childKey)) {
      errors.push(`${relative}: benchmark report contains forbidden key ${childKey}`);
    }

    inspectBenchmarkValue(childValue, relative, `${key}.${childKey}`);
  }
}

function* enumerateFiles(root) {
  const pending = [root];
  while (pending.length > 0) {
    const current = pending.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const fullPath = path.join(current, entry.name);
      if (entry.isSymbolicLink()) {
        errors.push(`${path.relative(repositoryRoot, fullPath)}: symbolic links are not allowed`);
      } else if (entry.isDirectory()) {
        pending.push(fullPath);
      } else if (entry.isFile()) {
        yield fullPath;
      }
    }
  }
}

function buildRealRootCandidates() {
  const home = process.env.USERPROFILE;
  const roots = [
    process.env.CLAUDE_CONFIG_DIR,
    process.env.CODEX_HOME,
    home ? path.join(home, ".claude") : null,
    home ? path.join(home, ".codex") : null,
  ];
  return roots
    .filter(Boolean)
    .flatMap((value) => [value, value.replaceAll("\\", "\\\\")]);
}

function includesCaseInsensitive(bytes, value) {
  return bytes.toString("utf8").toLocaleUpperCase("en-US")
    .includes(value.toLocaleUpperCase("en-US"));
}
