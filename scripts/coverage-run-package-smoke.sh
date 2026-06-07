#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Release}"
PACKAGE_VERSION_SUFFIX="${PACKAGE_VERSION_SUFFIX:-coverage-run-smoke}"
PACKAGE_VERSION="0.1.0-${PACKAGE_VERSION_SUFFIX}"
WORK_DIR="${WORK_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/appsurface-coverage-run-smoke.XXXXXX")}"
PACKAGE_SOURCE="$WORK_DIR/packages"
SMOKE_REPO="$WORK_DIR/repo"

mkdir -p "$PACKAGE_SOURCE" "$SMOKE_REPO"
echo "Smoke workspace: $WORK_DIR"

echo "Restoring ForgeTrust.AppSurface.Cli..."
dotnet restore "$CLI_PROJECT"

echo "Packing ForgeTrust.AppSurface.Cli $PACKAGE_VERSION..."
dotnet pack "$CLI_PROJECT" \
  --configuration "$BUILD_CONFIGURATION" \
  --no-restore \
  --output "$PACKAGE_SOURCE" \
  /p:VersionSuffix="$PACKAGE_VERSION_SUFFIX"

cd "$SMOKE_REPO"
dotnet new sln -n Smoke >/dev/null
if [[ -f Smoke.slnx ]]; then
  SMOKE_SOLUTION="Smoke.slnx"
else
  SMOKE_SOLUTION="Smoke.sln"
fi
dotnet new classlib -n Smoke >/dev/null
dotnet new xunit -n Smoke.Tests >/dev/null
dotnet sln "$SMOKE_SOLUTION" add Smoke/Smoke.csproj Smoke.Tests/Smoke.Tests.csproj >/dev/null
dotnet add Smoke.Tests/Smoke.Tests.csproj reference Smoke/Smoke.csproj >/dev/null
dotnet add Smoke.Tests/Smoke.Tests.csproj package coverlet.msbuild --version 10.0.1 >/dev/null

cat > Smoke/Calculator.cs <<'CS'
namespace Smoke;

public static class Calculator
{
    public static int Add(int left, int right) => left + right;

    public static string Sign(int value) => value >= 0 ? "non-negative" : "negative";
}
CS

cat > Smoke.Tests/UnitTest1.cs <<'CS'
using Smoke;

namespace Smoke.Tests;

public sealed class UnitTest1
{
    [Fact]
    public void Add_ReturnsSum()
    {
        Assert.Equal(3, Calculator.Add(1, 2));
    }

    [Theory]
    [InlineData(1, "non-negative")]
    [InlineData(-1, "negative")]
    public void Sign_ClassifiesValue(int value, string expected)
    {
        Assert.Equal(expected, Calculator.Sign(value));
    }
}
CS

dotnet new tool-manifest >/dev/null
dotnet tool install ForgeTrust.AppSurface.Cli \
  --add-source "$PACKAGE_SOURCE" \
  --version "$PACKAGE_VERSION" >/dev/null

dotnet tool run appsurface coverage run \
  --solution "$SMOKE_SOLUTION" \
  --include "[Smoke]*" \
  --output ./TestResults/coverage-merged

test -s ./TestResults/coverage-merged/coverage.cobertura.xml
test -s ./TestResults/coverage-merged/summary.txt
test -s ./TestResults/coverage-merged/timings.json
test -s ./TestResults/coverage-merged/projects/Smoke.Tests-*/dotnet-test.log

dotnet tool run appsurface coverage gate \
  --coverage ./TestResults/coverage-merged/coverage.cobertura.xml \
  --min-line 1 \
  --min-branch 0

test -s ./TestResults/coverage-merged/coverage-gate.json
test -s ./TestResults/coverage-merged/coverage-gate.md

echo "Packed coverage run smoke passed. Workspace: $WORK_DIR"
