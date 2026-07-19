#!/usr/bin/env node

import { readFile } from "node:fs/promises";

import {
  evaluateReleaseContractV1,
  renderReleaseContractSummaryV1
} from "./release-contract-v1.mjs";

async function readInput() {
  const path = process.argv[2];
  if (path) {
    return readFile(path, "utf8");
  }

  const chunks = [];
  for await (const chunk of process.stdin) {
    chunks.push(chunk);
  }

  return Buffer.concat(chunks).toString("utf8");
}

try {
  const document = JSON.parse(await readInput());
  const input = document?.input ?? document;
  const context = document?.context ?? {};
  const result = evaluateReleaseContractV1(input);
  process.stdout.write(renderReleaseContractSummaryV1(result, context));
  process.exitCode = result.diagnostics.some(item => item.blocking) ? 1 : 0;
} catch (error) {
  process.stderr.write(`release-contract-diagnose: ${error.message}\n`);
  process.exitCode = 2;
}
