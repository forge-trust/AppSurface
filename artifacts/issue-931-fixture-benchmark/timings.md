# Timing appendix

## Samples

- [Initial measured run](#initial-measured-run)
- [Independent verification after reporting change](#independent-verification-after-reporting-change)
- [Final verification after cleanup and acceptance repairs](#final-verification-after-cleanup-and-acceptance-repairs)

Each source-pinned sample contains 24 process receipts, including four warmup processes and five measured pairs. Process rows follow recorded start time, preserving the executed arm order. Wall seconds are shown to the receipt’s nanosecond precision. Phase totals include all 24 processes. Boundary load values are the recorded one-minute load average, with UTC start and end timestamps.

## Initial measured run

Source commit `b8d5dd5ebf2e6570d2b9643f22b4a08d16277f74`. Release test assembly SHA-256 `ff71b34754db6fc25f09640da93957e71bf012edae7a4d19d4d6a28cc4aecd1b`. Recorded result `NO_GO`.

### Per-process wall durations and fixture phases

| Run order | Window | Arm | Cohort | Wall (s) | Template prepare (count / ms) | Database setup (count / ms) | Database teardown (count / ms) | Template teardown (count / ms) |
|---:|---|---|---|---:|---:|---:|---:|---:|
| 1 | Warm-up | Fresh | Work | 4.939818166 | 0 / 0.000 | 4 / 590.984 | 4 / 82.053 | 0 / 0.000 |
| 2 | Warm-up | Fresh | Flow | 8.637723542 | 0 / 0.000 | 30 / 3225.131 | 30 / 579.938 | 0 / 0.000 |
| 3 | Warm-up | Template | Work | 4.896904042 | 1 / 218.795 | 4 / 267.171 | 4 / 110.924 | 1 / 22.342 |
| 4 | Warm-up | Template | Flow | 6.813437042 | 1 / 206.871 | 30 / 1888.888 | 30 / 566.404 | 1 / 26.249 |
| 5 | Pair 1 | Fresh | Work | 4.116967500 | 0 / 0.000 | 4 / 539.245 | 4 / 77.824 | 0 / 0.000 |
| 6 | Pair 1 | Fresh | Flow | 7.721410334 | 0 / 0.000 | 30 / 3104.649 | 30 / 581.445 | 0 / 0.000 |
| 7 | Pair 1 | Template | Work | 4.036456250 | 1 / 202.617 | 4 / 271.725 | 4 / 110.071 | 1 / 22.503 |
| 8 | Pair 1 | Template | Flow | 6.835910917 | 1 / 207.993 | 30 / 1905.935 | 30 / 611.138 | 1 / 23.618 |
| 9 | Pair 2 | Template | Work | 4.199331875 | 1 / 224.341 | 4 / 278.699 | 4 / 131.724 | 1 / 22.619 |
| 10 | Pair 2 | Template | Flow | 7.214249083 | 1 / 210.157 | 30 / 1843.166 | 30 / 583.189 | 1 / 23.970 |
| 11 | Pair 2 | Fresh | Work | 4.020902042 | 0 / 0.000 | 4 / 522.791 | 4 / 81.714 | 0 / 0.000 |
| 12 | Pair 2 | Fresh | Flow | 7.716684459 | 0 / 0.000 | 30 / 3081.914 | 30 / 566.868 | 0 / 0.000 |
| 13 | Pair 3 | Fresh | Work | 3.759242250 | 0 / 0.000 | 4 / 484.183 | 4 / 75.620 | 0 / 0.000 |
| 14 | Pair 3 | Fresh | Flow | 7.600533125 | 0 / 0.000 | 30 / 3079.208 | 30 / 576.060 | 0 / 0.000 |
| 15 | Pair 3 | Template | Work | 3.903335375 | 1 / 209.226 | 4 / 267.098 | 4 / 111.228 | 1 / 21.079 |
| 16 | Pair 3 | Template | Flow | 6.291464458 | 1 / 197.840 | 30 / 1760.178 | 30 / 533.799 | 1 / 21.602 |
| 17 | Pair 4 | Template | Work | 3.970668167 | 1 / 207.008 | 4 / 265.757 | 4 / 109.624 | 1 / 22.775 |
| 18 | Pair 4 | Template | Flow | 6.725700333 | 1 / 208.519 | 30 / 1836.079 | 30 / 562.539 | 1 / 21.447 |
| 19 | Pair 4 | Fresh | Work | 3.978297833 | 0 / 0.000 | 4 / 544.323 | 4 / 80.595 | 0 / 0.000 |
| 20 | Pair 4 | Fresh | Flow | 8.289547958 | 0 / 0.000 | 30 / 3398.434 | 30 / 664.294 | 0 / 0.000 |
| 21 | Pair 5 | Fresh | Work | 4.076816750 | 0 / 0.000 | 4 / 518.163 | 4 / 87.818 | 0 / 0.000 |
| 22 | Pair 5 | Fresh | Flow | 8.030918792 | 0 / 0.000 | 30 / 3411.471 | 30 / 622.286 | 0 / 0.000 |
| 23 | Pair 5 | Template | Work | 4.084490041 | 1 / 218.925 | 4 / 272.054 | 4 / 134.964 | 1 / 23.479 |
| 24 | Pair 5 | Template | Flow | 7.579192500 | 1 / 218.601 | 30 / 1989.503 | 30 / 582.938 | 1 / 21.777 |
### Fixture phase totals

| Phase | Count | Elapsed (ms) |
|---|---:|---:|
| Template prepare | 12 | 2530.893 |
| Database setup | 408 | 35346.749 |
| Database teardown | 408 | 8225.057 |
| Template teardown | 12 | 273.460 |

### Arm boundary load

| Window | Arm | Start UTC / 1-minute load | End UTC / 1-minute load |
|---|---|---|---|
| Warm-up | Fresh | 2026-11-21 09:28:35Z / 15.41796875 | 2026-11-21 09:28:49Z / 13.32470703125 |
| Warm-up | Template | 2026-11-21 09:28:49Z / 13.32470703125 | 2026-11-21 09:29:01Z / 15.68505859375 |
| Pair 1 | Fresh | 2026-11-21 09:29:01Z / 15.68505859375 | 2026-11-21 09:29:13Z / 14.33642578125 |
| Pair 1 | Template | 2026-11-21 09:29:13Z / 14.33642578125 | 2026-11-21 09:29:24Z / 13.60205078125 |
| Pair 2 | Template | 2026-11-21 09:29:24Z / 13.60205078125 | 2026-11-21 09:29:35Z / 15.92431640625 |
| Pair 2 | Fresh | 2026-11-21 09:29:35Z / 15.92431640625 | 2026-11-21 09:29:47Z / 13.4970703125 |
| Pair 3 | Fresh | 2026-11-21 09:29:47Z / 13.4970703125 | 2026-11-21 09:29:59Z / 12.35693359375 |
| Pair 3 | Template | 2026-11-21 09:29:59Z / 12.35693359375 | 2026-11-21 09:30:09Z / 11.298828125 |
| Pair 4 | Template | 2026-11-21 09:30:09Z / 11.298828125 | 2026-11-21 09:30:20Z / 10.84521484375 |
| Pair 4 | Fresh | 2026-11-21 09:30:20Z / 10.84521484375 | 2026-11-21 09:30:32Z / 10.9296875 |
| Pair 5 | Fresh | 2026-11-21 09:30:32Z / 10.9296875 | 2026-11-21 09:30:44Z / 10.71923828125 |
| Pair 5 | Template | 2026-11-21 09:30:44Z / 10.71923828125 | 2026-11-21 09:30:56Z / 10.615234375 |

## Independent verification after reporting change

Source commit `ce2cc1f16c74e9342871bdf17600fa06077cddfc`. Release test assembly SHA-256 `24154ed4a689f21c74fdfbd8e7f928c6a9f54af245e398466bf3d51cb24fd6f8`. Recorded result `NO_GO`.

### Per-process wall durations and fixture phases

| Run order | Window | Arm | Cohort | Wall (s) | Template prepare (count / ms) | Database setup (count / ms) | Database teardown (count / ms) | Template teardown (count / ms) |
|---:|---|---|---|---:|---:|---:|---:|---:|
| 1 | Warm-up | Fresh | Work | 3.690795042 | 0 / 0.000 | 4 / 480.172 | 4 / 74.104 | 0 / 0.000 |
| 2 | Warm-up | Fresh | Flow | 7.269361041 | 0 / 0.000 | 30 / 2993.024 | 30 / 551.123 | 0 / 0.000 |
| 3 | Warm-up | Template | Work | 3.752371875 | 1 / 198.617 | 4 / 248.860 | 4 / 99.967 | 1 / 21.541 |
| 4 | Warm-up | Template | Flow | 6.238906083 | 1 / 200.476 | 30 / 1757.239 | 30 / 539.631 | 1 / 21.280 |
| 5 | Pair 1 | Fresh | Work | 3.879224750 | 0 / 0.000 | 4 / 476.732 | 4 / 75.122 | 0 / 0.000 |
| 6 | Pair 1 | Fresh | Flow | 7.983216959 | 0 / 0.000 | 30 / 3200.072 | 30 / 582.634 | 0 / 0.000 |
| 7 | Pair 1 | Template | Work | 4.144414917 | 1 / 211.528 | 4 / 283.109 | 4 / 114.789 | 1 / 23.193 |
| 8 | Pair 1 | Template | Flow | 7.687362833 | 1 / 211.760 | 30 / 1916.528 | 30 / 575.814 | 1 / 22.642 |
| 9 | Pair 2 | Template | Work | 3.756383417 | 1 / 202.122 | 4 / 248.987 | 4 / 101.055 | 1 / 21.295 |
| 10 | Pair 2 | Template | Flow | 6.408543500 | 1 / 206.548 | 30 / 1818.969 | 30 / 546.522 | 1 / 22.163 |
| 11 | Pair 2 | Fresh | Work | 3.977300292 | 0 / 0.000 | 4 / 498.291 | 4 / 76.774 | 0 / 0.000 |
| 12 | Pair 2 | Fresh | Flow | 7.322366000 | 0 / 0.000 | 30 / 2980.825 | 30 / 554.019 | 0 / 0.000 |
| 13 | Pair 3 | Fresh | Work | 3.801975000 | 0 / 0.000 | 4 / 487.919 | 4 / 74.113 | 0 / 0.000 |
| 14 | Pair 3 | Fresh | Flow | 7.288040583 | 0 / 0.000 | 30 / 2925.337 | 30 / 540.828 | 0 / 0.000 |
| 15 | Pair 3 | Template | Work | 3.805863417 | 1 / 200.244 | 4 / 245.539 | 4 / 105.006 | 1 / 21.274 |
| 16 | Pair 3 | Template | Flow | 6.986853708 | 1 / 210.728 | 30 / 2035.506 | 30 / 626.763 | 1 / 23.148 |
| 17 | Pair 4 | Template | Work | 4.359022709 | 1 / 213.548 | 4 / 270.890 | 4 / 112.812 | 1 / 22.912 |
| 18 | Pair 4 | Template | Flow | 6.874390833 | 1 / 202.734 | 30 / 1940.807 | 30 / 597.816 | 1 / 25.232 |
| 19 | Pair 4 | Fresh | Work | 4.540344875 | 0 / 0.000 | 4 / 547.803 | 4 / 96.105 | 0 / 0.000 |
| 20 | Pair 4 | Fresh | Flow | 8.039945208 | 0 / 0.000 | 30 / 3170.791 | 30 / 575.617 | 0 / 0.000 |
| 21 | Pair 5 | Fresh | Work | 4.030470750 | 0 / 0.000 | 4 / 507.928 | 4 / 78.763 | 0 / 0.000 |
| 22 | Pair 5 | Fresh | Flow | 7.766818500 | 0 / 0.000 | 30 / 3167.393 | 30 / 576.834 | 0 / 0.000 |
| 23 | Pair 5 | Template | Work | 4.096713583 | 1 / 215.146 | 4 / 267.793 | 4 / 119.966 | 1 / 22.217 |
| 24 | Pair 5 | Template | Flow | 7.122852833 | 1 / 227.271 | 30 / 2013.255 | 30 / 592.717 | 1 / 25.101 |
### Fixture phase totals

| Phase | Count | Elapsed (ms) |
|---|---:|---:|
| Template prepare | 12 | 2500.722 |
| Database setup | 408 | 34483.769 |
| Database teardown | 408 | 7988.894 |
| Template teardown | 12 | 271.998 |

### Arm boundary load

| Window | Arm | Start UTC / 1-minute load | End UTC / 1-minute load |
|---|---|---|---|
| Warm-up | Fresh | 2026-11-21 09:35:41Z / 9.00048828125 | 2026-11-21 09:35:52Z / 8.39111328125 |
| Warm-up | Template | 2026-11-21 09:35:52Z / 8.39111328125 | 2026-11-21 09:36:02Z / 7.71533203125 |
| Pair 1 | Fresh | 2026-11-21 09:36:02Z / 7.71533203125 | 2026-11-21 09:36:14Z / 8.3447265625 |
| Pair 1 | Template | 2026-11-21 09:36:14Z / 8.3447265625 | 2026-11-21 09:36:26Z / 7.80615234375 |
| Pair 2 | Template | 2026-11-21 09:36:26Z / 7.80615234375 | 2026-11-21 09:36:36Z / 7.22021484375 |
| Pair 2 | Fresh | 2026-11-21 09:36:36Z / 7.22021484375 | 2026-11-21 09:36:48Z / 6.87841796875 |
| Pair 3 | Fresh | 2026-11-21 09:36:48Z / 6.87841796875 | 2026-11-21 09:36:59Z / 9.138671875 |
| Pair 3 | Template | 2026-11-21 09:36:59Z / 9.138671875 | 2026-11-21 09:37:10Z / 8.82861328125 |
| Pair 4 | Template | 2026-11-21 09:37:10Z / 8.82861328125 | 2026-11-21 09:37:21Z / 8.11572265625 |
| Pair 4 | Fresh | 2026-11-21 09:37:21Z / 8.11572265625 | 2026-11-21 09:37:34Z / 8.82470703125 |
| Pair 5 | Fresh | 2026-11-21 09:37:34Z / 8.82470703125 | 2026-11-21 09:37:46Z / 8.556640625 |
| Pair 5 | Template | 2026-11-21 09:37:46Z / 8.556640625 | 2026-11-21 09:37:57Z / 8.7333984375 |

## Final verification after cleanup and acceptance repairs

Source commit `756b2425ddab69827c6ba592ce7806a6dd02f191`. Release test assembly SHA-256 `ec325ffc3e350897a11fae5da4bbfdb227cb3f2e46482b929c6b73b531afda7f`. Recorded result `GO`.

### Per-process wall durations and fixture phases

| Run order | Window | Arm | Cohort | Wall (s) | Template prepare (count / ms) | Database setup (count / ms) | Database teardown (count / ms) | Template teardown (count / ms) |
|---:|---|---|---|---:|---:|---:|---:|---:|
| 1 | Warm-up | Fresh | Work | 3.757831292 | 0 / 0.000 | 4 / 516.871 | 4 / 82.295 | 0 / 0.000 |
| 2 | Warm-up | Fresh | Flow | 7.563879250 | 0 / 0.000 | 30 / 3082.416 | 30 / 657.384 | 0 / 0.000 |
| 3 | Warm-up | Template | Work | 3.811791791 | 1 / 205.027 | 4 / 261.880 | 4 / 113.674 | 1 / 20.106 |
| 4 | Warm-up | Template | Flow | 7.228162250 | 1 / 207.809 | 30 / 1857.108 | 30 / 630.845 | 1 / 21.528 |
| 5 | Pair 1 | Fresh | Work | 4.557501041 | 0 / 0.000 | 4 / 499.983 | 4 / 101.164 | 0 / 0.000 |
| 6 | Pair 1 | Fresh | Flow | 7.611990875 | 0 / 0.000 | 30 / 3197.136 | 30 / 622.355 | 0 / 0.000 |
| 7 | Pair 1 | Template | Work | 3.774983250 | 1 / 204.751 | 4 / 262.311 | 4 / 111.548 | 1 / 21.990 |
| 8 | Pair 1 | Template | Flow | 6.744706292 | 1 / 206.998 | 30 / 1891.344 | 30 / 600.961 | 1 / 22.875 |
| 9 | Pair 2 | Template | Work | 3.940670833 | 1 / 205.820 | 4 / 261.596 | 4 / 117.114 | 1 / 22.449 |
| 10 | Pair 2 | Template | Flow | 7.400510917 | 1 / 241.829 | 30 / 2356.725 | 30 / 860.693 | 1 / 24.130 |
| 11 | Pair 2 | Fresh | Work | 3.876613625 | 0 / 0.000 | 4 / 497.511 | 4 / 81.755 | 0 / 0.000 |
| 12 | Pair 2 | Fresh | Flow | 7.633819458 | 0 / 0.000 | 30 / 3097.302 | 30 / 613.437 | 0 / 0.000 |
| 13 | Pair 3 | Fresh | Work | 3.887548042 | 0 / 0.000 | 4 / 521.258 | 4 / 81.955 | 0 / 0.000 |
| 14 | Pair 3 | Fresh | Flow | 8.363142750 | 0 / 0.000 | 30 / 3212.341 | 30 / 631.367 | 0 / 0.000 |
| 15 | Pair 3 | Template | Work | 3.843882000 | 1 / 207.163 | 4 / 268.497 | 4 / 115.518 | 1 / 45.797 |
| 16 | Pair 3 | Template | Flow | 7.081392792 | 1 / 210.654 | 30 / 1947.989 | 30 / 574.948 | 1 / 22.874 |
| 17 | Pair 4 | Template | Work | 3.931017583 | 1 / 211.265 | 4 / 275.302 | 4 / 117.552 | 1 / 22.668 |
| 18 | Pair 4 | Template | Flow | 6.646415500 | 1 / 202.740 | 30 / 1978.912 | 30 / 560.346 | 1 / 22.437 |
| 19 | Pair 4 | Fresh | Work | 3.923149500 | 0 / 0.000 | 4 / 504.165 | 4 / 78.966 | 0 / 0.000 |
| 20 | Pair 4 | Fresh | Flow | 7.603706916 | 0 / 0.000 | 30 / 3148.991 | 30 / 580.236 | 0 / 0.000 |
| 21 | Pair 5 | Fresh | Work | 3.931875292 | 0 / 0.000 | 4 / 506.237 | 4 / 82.688 | 0 / 0.000 |
| 22 | Pair 5 | Fresh | Flow | 7.576556125 | 0 / 0.000 | 30 / 3133.033 | 30 / 583.676 | 0 / 0.000 |
| 23 | Pair 5 | Template | Work | 3.878158792 | 1 / 203.061 | 4 / 256.595 | 4 / 107.370 | 1 / 22.490 |
| 24 | Pair 5 | Template | Flow | 6.660634584 | 1 / 210.987 | 30 / 1898.909 | 30 / 597.875 | 1 / 22.508 |
### Fixture phase totals

| Phase | Count | Elapsed (ms) |
|---|---:|---:|
| Template prepare | 12 | 2518.104 |
| Database setup | 408 | 35434.412 |
| Database teardown | 408 | 8705.722 |
| Template teardown | 12 | 291.852 |

### Arm boundary load

| Window | Arm | Start UTC / 1-minute load | End UTC / 1-minute load |
|---|---|---|---|
| Warm-up | Fresh | 2026-11-21 10:25:59Z / 3.1201171875 | 2026-11-21 10:26:11Z / 3.2548828125 |
| Warm-up | Template | 2026-11-21 10:26:11Z / 3.2548828125 | 2026-11-21 10:26:22Z / 3.671875 |
| Pair 1 | Fresh | 2026-11-21 10:26:22Z / 3.671875 | 2026-11-21 10:26:34Z / 3.8818359375 |
| Pair 1 | Template | 2026-11-21 10:26:34Z / 3.8818359375 | 2026-11-21 10:26:45Z / 3.97314453125 |
| Pair 2 | Template | 2026-11-21 10:26:45Z / 3.97314453125 | 2026-11-21 10:26:56Z / 3.83056640625 |
| Pair 2 | Fresh | 2026-11-21 10:26:56Z / 3.83056640625 | 2026-11-21 10:27:08Z / 4.009765625 |
| Pair 3 | Fresh | 2026-11-21 10:27:08Z / 4.009765625 | 2026-11-21 10:27:20Z / 4.9111328125 |
| Pair 3 | Template | 2026-11-21 10:27:20Z / 4.9111328125 | 2026-11-21 10:27:31Z / 4.79443359375 |
| Pair 4 | Template | 2026-11-21 10:27:31Z / 4.79443359375 | 2026-11-21 10:27:42Z / 5.29296875 |
| Pair 4 | Fresh | 2026-11-21 10:27:42Z / 5.29296875 | 2026-11-21 10:27:54Z / 5.25390625 |
| Pair 5 | Fresh | 2026-11-21 10:27:54Z / 5.25390625 | 2026-11-21 10:28:05Z / 5.29443359375 |
| Pair 5 | Template | 2026-11-21 10:28:05Z / 5.29443359375 | 2026-11-21 10:28:16Z / 5.53662109375 |
