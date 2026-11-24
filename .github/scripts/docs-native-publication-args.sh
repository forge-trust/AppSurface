#!/usr/bin/env bash
# Source after the exact Docs artifact is selected and downloaded.
docs_native_args=()
if [[ -n "${DOCS_NATIVE_ARTIFACT_ID:-}" || -n "${DOCS_NATIVE_SHA256:-}" ]]; then
  if [[ ! "${DOCS_NATIVE_ARTIFACT_ID:-}" =~ ^[1-9][0-9]*$
        || ! "${DOCS_NATIVE_SHA256:-}" =~ ^[0-9a-f]{64}$
        || ! -f "$RUNNER_TEMP/docs-native-proof/docs-native-proof.json" ]]; then
    echo "::error::The selected Docs native proof is missing or has invalid artifact identity." >&2
    return 1
  fi
  docs_native_args=(--docs-native-proof "$RUNNER_TEMP/docs-native-proof/docs-native-proof.json"
    --docs-native-artifact-id "$DOCS_NATIVE_ARTIFACT_ID" --docs-native-sha256 "$DOCS_NATIVE_SHA256")
fi
