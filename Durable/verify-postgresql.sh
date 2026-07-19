#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj"
mode="${1:---quick}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-durable-postgresql.XXXXXX")"
list_log="$work_dir/list-tests.log"

cleanup() {
  if [[ -d "$work_dir" && "$work_dir" == "${TMPDIR:-/tmp}"/appsurface-durable-postgresql.* ]]; then
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

fail() {
  echo "Durable PostgreSQL verification failed: $1" >&2
  echo "Test project: $project" >&2
  echo "Use APPSURFACE_POSTGRES_TEST_CONNECTION for an external PostgreSQL 17.5 database," >&2
  echo "or start Docker so the pinned Testcontainers path can run." >&2
  exit 1
}

[[ -f "$project" ]] || fail "the PostgreSQL test project is missing"

if [[ "${CI:-}" == "true" && "${APPSURFACE_POSTGRES_TEST_ALLOW_SKIP:-}" == "true" ]]; then
  fail "APPSURFACE_POSTGRES_TEST_ALLOW_SKIP is a local-only escape hatch and cannot be enabled in CI"
fi

case "$mode" in
  --quick)
    dotnet test "$project" --list-tests \
      --filter 'FullyQualifiedName~DurableSlice3ReferenceWorkloadTests' >"$list_log" \
      || fail "test discovery failed"
    grep -Fq 'DurableSlice3ReferenceWorkloadTests' "$list_log" \
      || fail "the named reference workload selected zero tests"
    dotnet test "$project" \
      --filter 'FullyQualifiedName~DurableSlice3ReferenceWorkloadTests' \
      --logger 'console;verbosity=normal'
    ;;
  --ci)
    dotnet test "$project" --logger 'console;verbosity=normal'
    ;;
  *)
    echo "Usage: $0 --quick|--ci" >&2
    exit 2
    ;;
esac

echo "Durable PostgreSQL $mode verification passed."
