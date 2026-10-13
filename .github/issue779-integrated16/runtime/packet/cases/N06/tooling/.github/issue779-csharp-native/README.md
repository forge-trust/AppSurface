# N06 private slot packet — source prepared, review pending

Current source `fc59bb1254294d624f6779f928020edfe988dd5e`, direct parent `1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38`, count 2872, tree `c9ed8adcc08d050dc70af6885d31171038a36f86`. Capture SHA-256 `6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1`, compact source-map SHA-256 `bf7334e8a273dc5693bb9f2226c4b44c90c988fb2f2c8d56ddb0b840adc494c6`, projection SHA-256 `8457d006fa5061f7249afd6038107907557732e6a51de3d8ba151923007e3ea0`.

The legacy checkpoint filename `checkpoint-n01-n02-v5.sh` carries the fixed `N06` slot-capture body. The current captured source map includes `LinuxRunWorkspace.SlotNegative.cs` and the production prelaunch/join capture call sites. This is source-map evidence, not a native execution result.

The available slot-input-correction note is for source 1794/count 2869 and is historical relative to the fc59/count 2872 capture. Its referenced replacement shell bytes were not present in its directory. `build-fdd.sh` therefore fails closed until the main-owned integrated common builder is supplied; no common-build command count is asserted. Donor builder/review/composition records are preserved with `.historical` suffixes.

All review and execution gates remain pending/false. No native execution, native credit, Git operation, root operation, CI run, or publication is claimed. See `source-ready.json` for exact hashes and modes.
