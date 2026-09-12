#!/usr/bin/env node

import assert from "node:assert/strict";
import { getDocumentedVersionProvenance } from "./site-provenance-version.mjs";

assert.deepEqual(
  getDocumentedVersionProvenance("0.1.12", true),
  { version: "0.1.12", status: "released" },
  "A released documented version remains released on later site commits.",
);
assert.deepEqual(
  getDocumentedVersionProvenance("0.1.12-preview.1", false),
  { version: "0.1.12-preview.1", status: "unreleased" },
);
assert.throws(
  () => getDocumentedVersionProvenance("next", false),
  /Invalid Raven documentation version/,
);

console.log("Site provenance version checks passed.");
