#!/bin/sh
# Optional host preparation only; does not run the SDK, Docker, or acquisition.
set -eu
if [ "$#" -ne 2 ]; then
  echo 'Usage from repository root: sh examples/auth-keycloak-security-recipes/fixtures/prepare.sh arm64|amd64 /owned/digest-store' >&2
  exit 2
fi
arch=$1
store=$2
case "$arch" in arm64|amd64) ;; *) echo 'Unsupported fixture platform' >&2; exit 2 ;; esac
fixture=examples/auth-keycloak-security-recipes/fixtures/neutral-$arch
test -f "$fixture/recipe.json" || { echo 'Run from repository root' >&2; exit 2; }
if command -v sha256sum >/dev/null 2>&1; then
  hash() { sha256sum "$1" | cut -d ' ' -f 1; }
elif command -v shasum >/dev/null 2>&1; then
  hash() { shasum -a 256 "$1" | cut -d ' ' -f 1; }
else
  echo 'A SHA256 tool is required: sha256sum or shasum' >&2
  exit 2
fi
verify() {
  test -f "$1" && test "$(wc -c < "$1" | tr -d ' ')" = "$2" && test "$(hash "$1")" = "$3"
}
case "$arch" in
  arm64)
    expected_recipe=fb7e99c8334a93c57d9dd626a92c6851cf8e044af5e332355e28b8f2b2719c4f
    rows=$(cat <<'MATERIALS'
source-oci 8f1b01d53c1d953647866bef0ce6c141601bc9f7879c22cce7340b2de2855de7 266352640 ../../../../.gstack/tmp/issue-887-neutral/arm64/source-oci
source-index 831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669 2364 ../../../../.gstack/tmp/issue-887-neutral/arm64/source-index
runtime-index 7a65df4b22d2de92d4e04056e884f3b9122d70b21e2847fd66084278bd0ce037 5918 ../../../../.gstack/tmp/issue-887-neutral/arm64/runtime-index
runtime-oci 7c80826fcf1e4f532fb8fe741f2fecddd4503315dd6c064322e32fd4c7772655 112936960 ../../../../.gstack/tmp/issue-887-neutral/arm64/runtime-oci
frontend-index 4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e 9078 ../../../../.gstack/tmp/issue-887-neutral/arm64/frontend-index
frontend-oci b8bafb1eb03b42f2cc67ae18510e8a99d9ecce67b2a9a20d985b2290d65ca204 13148160 ../../../../.gstack/tmp/issue-887-neutral/arm64/frontend-oci
maven-8c5623b98f32d5f7 8c5623b98f32d5f7e1287ff61ed50e08ab7b11381e991fff89327faabba42261 594782 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-8c5623b98f32d5f7
maven-1290c2795e93e8a6 1290c2795e93e8a6861a6c4d9ff0d844d32f5ea178362cb2721edf5561e828b1 1709095 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-1290c2795e93e8a6
maven-e36f5237c1267983 e36f5237c1267983e5b88dc2169f6b9d7e50eceec6dc1ca31018e3877e14af66 1547706 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-e36f5237c1267983
maven-0fac87dddd78f122 0fac87dddd78f1223139e8ef88e819c7f483c0a3835cdf5982ad5e4576d1d896 1859003 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-0fac87dddd78f122
maven-45aff76226830db2 45aff76226830db257f4bc39a5bcff83d633e572fee9dc4e45cfa12af9a0a49a 51749 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-45aff76226830db2
maven-1957ef2deaffbc1f 1957ef2deaffbc1fb98cebfd0f5b3109ecf19c994d7e5378598c20747ba8d68b 910984 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-1957ef2deaffbc1f
maven-4c0826d5e7c8522a 4c0826d5e7c8522a8e111470b4e77f77b0a10f520eb5502ddd143fdbcedb2340 83925 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-4c0826d5e7c8522a
maven-5f665369724caa8f 5f665369724caa8ffbb3f83a492b02d1de17a1420758e4a55debcb6ee2bd5e2a 45408 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-5f665369724caa8f
maven-f474b14c7734f15e f474b14c7734f15e0540394cb6f39d67777b7581a42919e4ac89d253d4efd929 365383 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-f474b14c7734f15e
maven-9987b6a660b0a6b1 9987b6a660b0a6b1f0d791485dae33180b3d1c63687c006fe6d3fd025e9e3798 360116 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-9987b6a660b0a6b1
maven-2d9a33ce41bd9f6f 2d9a33ce41bd9f6f607df95039e42518a1fca16f83c9bbfe72beb57df6e2489f 68663 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-2d9a33ce41bd9f6f
maven-5ffa58b94c5ac48f 5ffa58b94c5ac48fb7e41843c18acf7a268e90b5b85423987829c9291ce3a5df 38542 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-5ffa58b94c5ac48f
maven-0535bb5a736472be 0535bb5a736472bef5c948d15eb273c4ab9f796656fc7c5d6b982ad92bddbd49 692573 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-0535bb5a736472be
maven-576ddcfb51b78e86 576ddcfb51b78e86f145c9a52b60e500b1a1d5a257cafec8aa5416ffba044b49 505543 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-576ddcfb51b78e86
maven-4a6190e08988c058 4a6190e08988c058bfeef2b5965e71946439c771abf721c68f4039b4e7b023e8 121963 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-4a6190e08988c058
maven-d31926b01adcc07a d31926b01adcc07af86f5e27b81b6d6c115df17d366e835d1fc3f5a1924e7e52 740314 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-d31926b01adcc07a
maven-d0e4c6ee4779f59f d0e4c6ee4779f59f6ab2fb5d388e4f57147c82270164b37945764bb9bda96a44 588804 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-d0e4c6ee4779f59f
maven-843de10c4cef34cb 843de10c4cef34cbc1a5344090f1b57db532fbebf141559862da675f6b656df4 25841 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-843de10c4cef34cb
maven-b4cf2aeedd9fc7c8 b4cf2aeedd9fc7c8c439bbfe574f63cfe5b83392e88bbc07ca0e8424b7cff955 37907 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-b4cf2aeedd9fc7c8
maven-898d63ca62ed68ff 898d63ca62ed68ff46543c5344f969424b417f1e8d082fe2bb0181ba10144194 188777 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-898d63ca62ed68ff
maven-6251adc2a2921572 6251adc2a2921572382732a2db188d4f4f2251fd6ebb49c5d44bbf33d6bfb1a7 521982 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-6251adc2a2921572
maven-55049554b799dc8e 55049554b799dc8e53bf234fffa36e001f7b5b65c32994b9bd59fc6356f1c52f 147751 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-55049554b799dc8e
maven-8056e7637f9948f9 8056e7637f9948f953314894cf995ab664711b66c73c4cd5b593ae84f0b1048c 44328 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-8056e7637f9948f9
maven-82fb63a2dc90c0bd 82fb63a2dc90c0bddb81e04065a1c06fb4c5be722ad5d64b46491daf8b6f9a01 42889 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-82fb63a2dc90c0bd
maven-185db6053e0e9573 185db6053e0e9573d36fa4eb6567b26139afaa4daa7bf5a068e661568304542b 41506 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-185db6053e0e9573
maven-20af26bf6060bb80 20af26bf6060bb8005cc2389916812c1e0e998dc48d2ced7131b89461b54cff7 10321819 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-20af26bf6060bb80
maven-c9f82b2d4e99c4bb c9f82b2d4e99c4bbdfccf684e52cc06ea06a0b567bfd0d08f9c5a3f417055996 1315409 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-c9f82b2d4e99c4bb
maven-590f55ed5d685292 590f55ed5d68529239898a4a5c4f730b6e37f45d1cfa3fbe51f8485abe32c42d 718812 ../../../../.gstack/tmp/issue-887-neutral/arm64/maven-590f55ed5d685292
signed-release 041541bfe3fd47a76c6ec9f560a0fd3905acbe8a6baa1a021fc3565077c6e9c1 137401 ../../../../.gstack/tmp/issue-887-neutral/arm64/signed-release
signing-keyring 80a36b0a6de2f69f49d2df75ef473ccde121e9e190b9ea01d20a4f63778d5c31 3607 ../../../../.gstack/tmp/issue-887-neutral/arm64/signing-keyring
package-index 4b46267e511cfe3a4d672c2f8840a55872befed0ceb27e964158f109cb640484 908295 ../../../../.gstack/tmp/issue-887-neutral/arm64/package-index
deb-libssl3t64 9fe87b6beec5de636a70e4fb6076517a39f5053c7e23e628c9459b94f85c75ff 2769448 ../../../../.gstack/tmp/issue-887-neutral/arm64/deb-libssl3t64
deb-openssl 623ea7ec0eb80da3f40e89e3318a4e82d4c64146d206c90cabbce6998d822fa8 1222930 ../../../../.gstack/tmp/issue-887-neutral/arm64/deb-openssl
deb-openssl-provider-legacy 757ec89120fe3f418a842342b3a0c596b89b0e3fb0ba770347dda1219f219bbd 38424 ../../../../.gstack/tmp/issue-887-neutral/arm64/deb-openssl-provider-legacy
neutral-theme 241e60d6cfbafb4529dc5c21840a544c674dc92315c4a3ad92fb145c7eb0cbde 19 ../shared/theme.properties
MATERIALS
)
    ;;
  amd64)
    expected_recipe=a3a809007eba3feadf626712c75bfe89543d451d228d6c27225a07516b3fbd83
    rows=$(cat <<'MATERIALS'
source-oci 99d08a39d545a534f07a4c29bbf909c03440f96923227fe9e1dbd64c5cb6a7ea 267909120 ../../../../.gstack/tmp/issue-887-neutral/amd64/source-oci
source-index 831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669 2364 ../../../../.gstack/tmp/issue-887-neutral/amd64/source-index
runtime-index 7a65df4b22d2de92d4e04056e884f3b9122d70b21e2847fd66084278bd0ce037 5918 ../../../../.gstack/tmp/issue-887-neutral/amd64/runtime-index
runtime-oci 81ba11dcc0cab958a9ad56a7c13a009be5e9089f2ba98ccd946afd62f4a49008 114831360 ../../../../.gstack/tmp/issue-887-neutral/amd64/runtime-oci
frontend-index 4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e 9078 ../../../../.gstack/tmp/issue-887-neutral/amd64/frontend-index
frontend-oci b8bafb1eb03b42f2cc67ae18510e8a99d9ecce67b2a9a20d985b2290d65ca204 13148160 ../../../../.gstack/tmp/issue-887-neutral/amd64/frontend-oci
maven-8c5623b98f32d5f7 8c5623b98f32d5f7e1287ff61ed50e08ab7b11381e991fff89327faabba42261 594782 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-8c5623b98f32d5f7
maven-1290c2795e93e8a6 1290c2795e93e8a6861a6c4d9ff0d844d32f5ea178362cb2721edf5561e828b1 1709095 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-1290c2795e93e8a6
maven-e36f5237c1267983 e36f5237c1267983e5b88dc2169f6b9d7e50eceec6dc1ca31018e3877e14af66 1547706 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-e36f5237c1267983
maven-0fac87dddd78f122 0fac87dddd78f1223139e8ef88e819c7f483c0a3835cdf5982ad5e4576d1d896 1859003 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-0fac87dddd78f122
maven-45aff76226830db2 45aff76226830db257f4bc39a5bcff83d633e572fee9dc4e45cfa12af9a0a49a 51749 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-45aff76226830db2
maven-1957ef2deaffbc1f 1957ef2deaffbc1fb98cebfd0f5b3109ecf19c994d7e5378598c20747ba8d68b 910984 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-1957ef2deaffbc1f
maven-4c0826d5e7c8522a 4c0826d5e7c8522a8e111470b4e77f77b0a10f520eb5502ddd143fdbcedb2340 83925 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-4c0826d5e7c8522a
maven-5f665369724caa8f 5f665369724caa8ffbb3f83a492b02d1de17a1420758e4a55debcb6ee2bd5e2a 45408 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-5f665369724caa8f
maven-f474b14c7734f15e f474b14c7734f15e0540394cb6f39d67777b7581a42919e4ac89d253d4efd929 365383 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-f474b14c7734f15e
maven-9987b6a660b0a6b1 9987b6a660b0a6b1f0d791485dae33180b3d1c63687c006fe6d3fd025e9e3798 360116 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-9987b6a660b0a6b1
maven-2d9a33ce41bd9f6f 2d9a33ce41bd9f6f607df95039e42518a1fca16f83c9bbfe72beb57df6e2489f 68663 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-2d9a33ce41bd9f6f
maven-5ffa58b94c5ac48f 5ffa58b94c5ac48fb7e41843c18acf7a268e90b5b85423987829c9291ce3a5df 38542 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-5ffa58b94c5ac48f
maven-0535bb5a736472be 0535bb5a736472bef5c948d15eb273c4ab9f796656fc7c5d6b982ad92bddbd49 692573 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-0535bb5a736472be
maven-576ddcfb51b78e86 576ddcfb51b78e86f145c9a52b60e500b1a1d5a257cafec8aa5416ffba044b49 505543 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-576ddcfb51b78e86
maven-4a6190e08988c058 4a6190e08988c058bfeef2b5965e71946439c771abf721c68f4039b4e7b023e8 121963 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-4a6190e08988c058
maven-d31926b01adcc07a d31926b01adcc07af86f5e27b81b6d6c115df17d366e835d1fc3f5a1924e7e52 740314 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-d31926b01adcc07a
maven-d0e4c6ee4779f59f d0e4c6ee4779f59f6ab2fb5d388e4f57147c82270164b37945764bb9bda96a44 588804 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-d0e4c6ee4779f59f
maven-843de10c4cef34cb 843de10c4cef34cbc1a5344090f1b57db532fbebf141559862da675f6b656df4 25841 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-843de10c4cef34cb
maven-b4cf2aeedd9fc7c8 b4cf2aeedd9fc7c8c439bbfe574f63cfe5b83392e88bbc07ca0e8424b7cff955 37907 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-b4cf2aeedd9fc7c8
maven-898d63ca62ed68ff 898d63ca62ed68ff46543c5344f969424b417f1e8d082fe2bb0181ba10144194 188777 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-898d63ca62ed68ff
maven-6251adc2a2921572 6251adc2a2921572382732a2db188d4f4f2251fd6ebb49c5d44bbf33d6bfb1a7 521982 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-6251adc2a2921572
maven-55049554b799dc8e 55049554b799dc8e53bf234fffa36e001f7b5b65c32994b9bd59fc6356f1c52f 147751 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-55049554b799dc8e
maven-8056e7637f9948f9 8056e7637f9948f953314894cf995ab664711b66c73c4cd5b593ae84f0b1048c 44328 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-8056e7637f9948f9
maven-82fb63a2dc90c0bd 82fb63a2dc90c0bddb81e04065a1c06fb4c5be722ad5d64b46491daf8b6f9a01 42889 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-82fb63a2dc90c0bd
maven-185db6053e0e9573 185db6053e0e9573d36fa4eb6567b26139afaa4daa7bf5a068e661568304542b 41506 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-185db6053e0e9573
maven-20af26bf6060bb80 20af26bf6060bb8005cc2389916812c1e0e998dc48d2ced7131b89461b54cff7 10321819 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-20af26bf6060bb80
maven-c9f82b2d4e99c4bb c9f82b2d4e99c4bbdfccf684e52cc06ea06a0b567bfd0d08f9c5a3f417055996 1315409 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-c9f82b2d4e99c4bb
maven-590f55ed5d685292 590f55ed5d68529239898a4a5c4f730b6e37f45d1cfa3fbe51f8485abe32c42d 718812 ../../../../.gstack/tmp/issue-887-neutral/amd64/maven-590f55ed5d685292
signed-release 041541bfe3fd47a76c6ec9f560a0fd3905acbe8a6baa1a021fc3565077c6e9c1 137401 ../../../../.gstack/tmp/issue-887-neutral/amd64/signed-release
signing-keyring 80a36b0a6de2f69f49d2df75ef473ccde121e9e190b9ea01d20a4f63778d5c31 3607 ../../../../.gstack/tmp/issue-887-neutral/amd64/signing-keyring
package-index 9f665693e975f070fb8a144eecef9cec76b9df772f9d4bd81eec49008569e2d0 883995 ../../../../.gstack/tmp/issue-887-neutral/amd64/package-index
deb-libssl3t64 3ff73e121f6cd4f19e80ab4329dfd914108b3936c9db3708200203a9b245b34d 2368026 ../../../../.gstack/tmp/issue-887-neutral/amd64/deb-libssl3t64
deb-openssl 6965aafe69e0a49ef34d2e26f8e83d09d1c6037b7e9ee37f19b292a3c6851621 1242886 ../../../../.gstack/tmp/issue-887-neutral/amd64/deb-openssl
deb-openssl-provider-legacy 51cf418571059831096abd40e0bd5debaa1ae48a6f8d87d9e4e5f7f12aa4db52 39682 ../../../../.gstack/tmp/issue-887-neutral/amd64/deb-openssl-provider-legacy
neutral-theme 241e60d6cfbafb4529dc5c21840a544c674dc92315c4a3ad92fb145c7eb0cbde 19 ../shared/theme.properties
MATERIALS
)
    ;;
esac
test "$(hash "$fixture/recipe.json")" = "$expected_recipe" || { echo 'Canonical recipe changed; stop for review' >&2; exit 1; }
printf '%s\n' "$rows" | while read -r id digest bytes relative; do
  destination=$fixture/$relative
  case "$id" in
    source-index|runtime-index|frontend-index) source=$fixture/$id.json ;;
    neutral-theme) source=examples/auth-keycloak-security-recipes/fixtures/shared/theme.properties ;;
    *) source=$store/$digest ;;
  esac
  verify "$source" "$bytes" "$digest" || { echo "Missing or changed retained material: $id ($digest)" >&2; exit 1; }
  if test "$source" != "$destination"; then
    mkdir -p "$(dirname "$destination")"
    if test -e "$destination"; then
      verify "$destination" "$bytes" "$digest" || { echo "Destination differs: $id; preserve it and choose fresh owned paths" >&2; exit 1; }
    else
      (set -C; cat "$source" > "$destination")
      verify "$destination" "$bytes" "$digest" || { echo "Copied bytes differ: $id; stop before capture" >&2; exit 1; }
    fi
  fi
  echo "verified $id $digest"
done
test "$(hash "$fixture/recipe.json")" = "$expected_recipe" || { echo 'Recipe changed during preparation' >&2; exit 1; }
echo 'All 41 portable bindings verified; recipe unchanged. Next: run the documented SDK sample.'
