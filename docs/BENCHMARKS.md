# Lightweight export measurements

Actual source-file exports on 27 October 2026, Blender 5.2.1 LTS. MB means 1,000,000 bytes.

| Clips | Full FBX | Lightweight FBX | Saved |
| ---: | ---: | ---: | ---: |
| 1 | 7.14 MB | 3.27 MB | 54.2% |
| 5 | 35.92 MB | 16.58 MB | 53.8% |
| 10 | 77.02 MB | 38.34 MB | 50.2% |
| 25 | 196.93 MB | 100.23 MB | 49.1% |
| 50 | 389.90 MB | 196.50 MB | 49.6% |
| 100 | 789.36 MB | 402.57 MB | 49.0% |
| 200 | 1,575.24 MB | 801.64 MB | 49.1% |

At 200 clips: **773.60 MB saved (49.1%)**. Every Full/Lightweight pair retained identical canonical animation records, bindings, hierarchy and required bind data.

The 200 clips are generated NLA variants of nine authored Valencia actions, with four duration factors and retimed active prop/shape actions. They were exported individually through the add-on in both modes. They are not 200 independently authored animations or multiplied estimates. Repeated character geometry makes much of the saving linear; longer clips retain more animation data. Per-file savings: 30.5% minimum, 52.1% median, 56.7% maximum. ≤2s: 53.6% saved (113 clips); >5s: 38.4% saved (33 clips); 2–5s: 48.8% saved (54 clips).

The nine unchanged authored clips measured **76.82 MB Full → 42.00 MB Lightweight (45.3% saved)**. An earlier nine-clip Combined test measured 31.79 MB with geometry included once. That figure is from the earlier Combined test, not a new matched 200-clip comparison.

The table counts FBX bytes. Actual complete 200-clip support data: 924,169 bytes Full, 925,569 bytes Lightweight. Partial-prefix metadata sizes were not separately exported. Full export took 7.0 minutes and Lightweight 15.2 minutes on this machine; reduction adds export work. These measurements do not establish player-build size, RAM use or import speed.

Separate nine-file reduction/validation run: **2.28–2.90 seconds per FBX**, median 2.43s; excludes baking/copying and varies with machine load. [Per-file timings](benchmarks/lightweight-reduction-times.csv). Esc is serviced between clips, so an individual bake/reduction must finish before cancellation. Reproduce this phase with `tools/measure_lightweight_latency.py`.

[All counts 1–200](benchmarks/lightweight-200.csv) · [Per-file duration/key data](benchmarks/lightweight-files.csv). Reproduce with `tools/benchmark_lightweight.py` in Blender using a local character source; character assets are not included in this repository.
