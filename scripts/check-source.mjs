#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const skippedDirectories = new Set([".git", ".vs", "bin", "obj", "artifacts"]);
const textExtensions = new Set([
  ".cs",
  ".csproj",
  ".json",
  ".md",
  ".mjs",
  ".props",
  ".ps1",
  ".targets",
  ".xml",
]);
const findings = [];

if (process.argv.includes("--self-test")) {
  runSelfTest();
} else {
  for (const file of enumerateFiles(root)) {
    const extension = path.extname(file).toLowerCase();
    if (!textExtensions.has(extension)) {
      continue;
    }

    const text = fs.readFileSync(file, "utf8");
    const relative = path.relative(root, file);
    checkUnicodeDashes(relative, text);
    if (extension === ".cs") {
      checkCSharpStrings(relative, text);
    }
  }

  reportFindings();
}

function runSelfTest() {
  const failures = [];
  const cases = [
    {
      name: "rejects multiline verbatim C#",
      evaluate: () => checkCSharpStrings("invalid.cs", 'string value = @"first\nsecond";'),
      expected: "multiline C# string must use a raw string literal",
    },
    {
      name: "accepts multiline raw C#",
      evaluate: () => checkCSharpStrings("valid.cs", 'string value = """\nfirst\nsecond\n""";'),
      expected: null,
    },
    {
      name: "rejects Unicode sentence dash",
      evaluate: () => checkUnicodeDashes("invalid.md", `left${String.fromCharCode(0x2014)}right`),
      expected: "literal Unicode dash is forbidden",
    },
  ];

  for (const testCase of cases) {
    findings.length = 0;
    testCase.evaluate();
    const matched = testCase.expected === null
      ? findings.length === 0
      : findings.some((finding) => finding.includes(testCase.expected));
    if (!matched) {
      failures.push(testCase.name);
    }
  }

  if (failures.length > 0) {
    failures.forEach((failure) => process.stderr.write(`check-source self-test: ${failure}\n`));
    process.exitCode = 1;
    return;
  }

  process.stdout.write("check-source self-test: ok\n");
}

function reportFindings() {
  if (findings.length > 0) {
    for (const finding of findings) {
      process.stderr.write(`${finding}\n`);
    }
    process.stderr.write(`check-source: ${findings.length} error(s)\n`);
    process.exitCode = 1;
  } else {
    process.stdout.write("check-source: ok\n");
  }
}

function* enumerateFiles(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && skippedDirectories.has(entry.name)) {
      continue;
    }

    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      yield* enumerateFiles(fullPath);
    } else if (entry.isFile()) {
      yield fullPath;
    }
  }
}

function checkUnicodeDashes(file, text) {
  for (let index = 0; index < text.length; index += 1) {
    const code = text.charCodeAt(index);
    if (code === 0x2013 || code === 0x2014) {
      findings.push(`${file}:${lineAt(text, index)}: literal Unicode dash is forbidden`);
    }
  }
}

function checkCSharpStrings(file, text) {
  let index = 0;
  while (index < text.length) {
    if (text.startsWith("//", index)) {
      index = skipLineComment(text, index + 2);
      continue;
    }
    if (text.startsWith("/*", index)) {
      index = skipBlockComment(text, index + 2);
      continue;
    }

    const verbatimQuote = verbatimQuoteAt(text, index);
    if (verbatimQuote >= 0) {
      index = scanVerbatimString(file, text, verbatimQuote);
      continue;
    }

    if (text[index] === '"') {
      const quoteCount = countRun(text, index, '"');
      if (quoteCount >= 3) {
        index = scanRawString(file, text, index, quoteCount);
      } else {
        index = scanRegularString(file, text, index);
      }
      continue;
    }

    if (text[index] === "'") {
      index = scanCharacter(text, index);
      continue;
    }

    index += 1;
  }
}

function verbatimQuoteAt(text, index) {
  if (text.startsWith('@"', index)) {
    return index + 1;
  }
  if (text.startsWith('$@"', index) || text.startsWith('@$"', index)) {
    return index + 2;
  }
  return -1;
}

function scanVerbatimString(file, text, quoteIndex) {
  let index = quoteIndex + 1;
  let crossedLine = false;
  while (index < text.length) {
    if (text[index] === "\r" || text[index] === "\n") {
      crossedLine = true;
    }
    if (text[index] === '"') {
      if (text[index + 1] === '"') {
        index += 2;
        continue;
      }
      if (crossedLine) {
        findings.push(
          `${file}:${lineAt(text, quoteIndex)}: multiline C# string must use a raw string literal`,
        );
      }
      return index + 1;
    }
    index += 1;
  }
  findings.push(`${file}:${lineAt(text, quoteIndex)}: unterminated verbatim string`);
  return text.length;
}

function scanRawString(file, text, quoteIndex, quoteCount) {
  let index = quoteIndex + quoteCount;
  while (index < text.length) {
    if (text[index] === '"' && countRun(text, index, '"') >= quoteCount) {
      return index + quoteCount;
    }
    index += 1;
  }
  findings.push(`${file}:${lineAt(text, quoteIndex)}: unterminated raw string`);
  return text.length;
}

function scanRegularString(file, text, quoteIndex) {
  let index = quoteIndex + 1;
  while (index < text.length) {
    if (text[index] === "\\") {
      index += 2;
      continue;
    }
    if (text[index] === '"') {
      return index + 1;
    }
    if (text[index] === "\r" || text[index] === "\n") {
      findings.push(`${file}:${lineAt(text, quoteIndex)}: newline in regular C# string`);
      return index + 1;
    }
    index += 1;
  }
  findings.push(`${file}:${lineAt(text, quoteIndex)}: unterminated regular string`);
  return text.length;
}

function scanCharacter(text, quoteIndex) {
  let index = quoteIndex + 1;
  while (index < text.length) {
    if (text[index] === "\\") {
      index += 2;
      continue;
    }
    if (text[index] === "'") {
      return index + 1;
    }
    if (text[index] === "\r" || text[index] === "\n") {
      return index + 1;
    }
    index += 1;
  }
  return text.length;
}

function skipLineComment(text, index) {
  while (index < text.length && text[index] !== "\n") {
    index += 1;
  }
  return index;
}

function skipBlockComment(text, index) {
  const end = text.indexOf("*/", index);
  return end < 0 ? text.length : end + 2;
}

function countRun(text, index, character) {
  let count = 0;
  while (text[index + count] === character) {
    count += 1;
  }
  return count;
}

function lineAt(text, index) {
  let line = 1;
  for (let cursor = 0; cursor < index; cursor += 1) {
    if (text[cursor] === "\n") {
      line += 1;
    }
  }
  return line;
}
