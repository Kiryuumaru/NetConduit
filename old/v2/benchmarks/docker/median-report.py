#!/usr/bin/env python3
"""Generate benchmarks.md from median of 3 benchmark runs."""

import json
import sys
import statistics
from collections import defaultdict
from pathlib import Path


def load_json(path):
    try:
        with open(path) as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError) as e:
        print(f"WARNING: {path}: {e}", file=sys.stderr)
        return []


def main():
    results_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("benchmarks/docker/results")

    # Collect all data across 3 runs
    # Key: (scenario, implementation, channels, dataSizeBytes)
    # Value: list of metric values (one per run)
    gametick_data = defaultdict(list)
    throughput_data = defaultdict(list)

    for run in range(1, 4):
        # Game-tick
        for prefix in ["go", "dotnet"]:
            data = load_json(results_dir / f"run{run}-{prefix}-gametick.json")
            for r in data:
                key = (r["implementation"], r["channels"], r["dataSizeBytes"])
                val = r.get("messagesPerSec", 0)
                if val and val > 0:
                    gametick_data[key].append(val)

        # Throughput
        for prefix in ["go", "dotnet"]:
            data = load_json(results_dir / f"run{run}-{prefix}-throughput.json")
            for r in data:
                key = (r["implementation"], r["channels"], r["dataSizeBytes"])
                val = r.get("throughputMBps", 0)
                if val and val > 0:
                    throughput_data[key].append(val)

    # Compute medians
    gt_median = {k: statistics.median(v) for k, v in gametick_data.items() if len(v) >= 2}
    tp_median = {k: statistics.median(v) for k, v in throughput_data.items() if len(v) >= 2}

    # Implementation names
    GO_RAW = "Raw TCP (Go)"
    GO_FRP = "FRP/Yamux (Go)"
    GO_SMUX = "Smux (Go)"
    NET_RAW = "Raw TCP (.NET)"
    NET_MUX = "NetConduit Mux TCP"

    # Helper functions
    def gt(impl, ch, size):
        return gt_median.get((impl, ch, size))

    def tp(impl, ch, size):
        return tp_median.get((impl, ch, size))

    def fmt_int(v):
        if v is None:
            return "—"
        return f"{v:,.0f}"

    def fmt_float(v):
        if v is None:
            return "—"
        return f"{v:,.1f}"

    def ratio(a, b):
        if a is None or b is None or b == 0:
            return "—"
        r = a / b
        if r >= 1.0:
            return f"**{r:.2f}x**"
        return f"{r:.2f}x"

    def ratio_plain(a, b):
        if a is None or b is None or b == 0:
            return "—"
        r = a / b
        return f"{r:.1f}x"

    # Count wins for throughput
    tp_wins = 0
    tp_total = 0
    for ch in [1, 10, 100]:
        for size in [1024, 102400, 1048576]:
            nc = tp(NET_MUX, ch, size)
            frp = tp(GO_FRP, ch, size)
            smux = tp(GO_SMUX, ch, size)
            if nc and frp:
                tp_total += 1
                if nc > frp:
                    tp_wins += 1
            if nc and smux:
                tp_total += 1
                if nc > smux:
                    tp_wins += 1

    # Count wins for game-tick
    gt_wins = 0
    gt_total = 0
    for ch in [1, 10, 50, 1000]:
        for size in [64, 256]:
            nc = gt(NET_MUX, ch, size)
            frp = gt(GO_FRP, ch, size)
            smux = gt(GO_SMUX, ch, size)
            if nc and frp:
                gt_total += 1
                if nc > frp:
                    gt_wins += 1
            if nc and smux:
                gt_total += 1
                if nc > smux:
                    gt_wins += 1

    # Generate output
    print("# NetConduit Comparison Benchmark Results")
    print()
    print("All benchmarks run on loopback (127.0.0.1), identical workloads,")
    print("3 runs with median reported. CPU-pinned via `taskset 0x3` (2 cores).")
    print()
    print("**Fairness controls:** Both Go and .NET benchmarks pinned to same 2 CPU cores,")
    print("GOMAXPROCS=2, `--network none` when Docker, `taskset` when native.")
    print()
    print("| Implementation | Language | Description |")
    print("|---------------|----------|-------------|")
    print("| **NetConduit** | C# | 1 TCP connection, N multiplexed channels — credit-based flow control, priority queuing, adaptive windowing |")
    print("| **FRP/Yamux** | Go | HashiCorp Yamux — stream multiplexer used by FRP, Consul, Nomad |")
    print("| **Smux** | Go | Popular Go stream multiplexer (xtaci/smux) |")
    print("| Raw TCP | C# / Go | Baseline — N separate TCP connections (not a mux, shown for context) |")
    print()
    print("---")
    print()
    print("## Multiplexer Head-to-Head")
    print()
    print("The comparison that matters: **NetConduit vs FRP/Yamux vs Smux**.")
    print("All three multiplex N channels over a single TCP connection.")
    print()

    # Bulk Throughput head-to-head
    print("### Bulk Throughput (MB/s)")
    print()
    print("Each channel sends one data payload. Higher = better.")
    print()
    print("| Channels | Data Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |")
    print("|----------|-----------|----------:|----------:|-----:|----------:|----------:|")
    for ch in [1, 10, 100]:
        for size, label in [(1024, "1KB"), (102400, "100KB"), (1048576, "1MB")]:
            nc = tp(NET_MUX, ch, size)
            frp = tp(GO_FRP, ch, size)
            smux = tp(GO_SMUX, ch, size)
            print(f"| {ch} | {label} | {fmt_float(nc)} | {fmt_float(frp)} | {fmt_float(smux)} | {ratio(nc, frp)} | {ratio(nc, smux)} |")
    print()

    # Game-Tick head-to-head
    print("### Game-Tick Message Rate (msg/s)")
    print()
    print("Each channel sends many small messages (simulates game state updates). Higher = better.")
    print()
    print("| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |")
    print("|----------|----------|----------:|----------:|-----:|----------:|----------:|")
    for ch in [1, 10, 50, 1000]:
        for size, label in [(64, "64B"), (256, "256B")]:
            nc = gt(NET_MUX, ch, size)
            frp = gt(GO_FRP, ch, size)
            smux = gt(GO_SMUX, ch, size)
            print(f"| {ch} | {label} | {fmt_int(nc)} | {fmt_int(frp)} | {fmt_int(smux)} | {ratio(nc, frp)} | {ratio(nc, smux)} |")
    print()

    # Key Takeaways
    print("---")
    print()
    print("## Key Takeaways")
    print()
    print(f"**Bulk throughput:** NetConduit wins {tp_wins}/{tp_total} comparisons against Go multiplexers.")
    print("Credit-based flow control adds per-transfer overhead most visible in large bulk scenarios.")

    # Find specific highlights
    nc_10_1kb = tp(NET_MUX, 10, 1024)
    frp_10_1kb = tp(GO_FRP, 10, 1024)
    smux_10_1kb = tp(GO_SMUX, 10, 1024)
    if nc_10_1kb and frp_10_1kb and nc_10_1kb > frp_10_1kb:
        print(f"At 10 channels with 1KB payloads, NetConduit delivers {fmt_float(nc_10_1kb)} MB/s")
        print(f"({nc_10_1kb/frp_10_1kb:.1f}x FRP/Yamux, {nc_10_1kb/smux_10_1kb:.1f}x Smux).")
    print()

    print(f"**Game-tick messaging:** NetConduit wins {gt_wins}/{gt_total} comparisons against Go multiplexers.")
    nc_1_64 = gt(NET_MUX, 1, 64)
    frp_1_64 = gt(GO_FRP, 1, 64)
    smux_1_64 = gt(GO_SMUX, 1, 64)
    if nc_1_64 and frp_1_64:
        min_ratio = min(nc_1_64/frp_1_64 if frp_1_64 else 0, nc_1_64/smux_1_64 if smux_1_64 else 0)
        max_ratio = max(nc_1_64/frp_1_64 if frp_1_64 else 0, nc_1_64/smux_1_64 if smux_1_64 else 0)
    # Find overall min/max ratios across all game-tick scenarios
    all_frp_ratios = []
    all_smux_ratios = []
    for ch in [1, 10, 50, 1000]:
        for size in [64, 256]:
            nc = gt(NET_MUX, ch, size)
            frp = gt(GO_FRP, ch, size)
            smux = gt(GO_SMUX, ch, size)
            if nc and frp and frp > 0:
                all_frp_ratios.append(nc / frp)
            if nc and smux and smux > 0:
                all_smux_ratios.append(nc / smux)
    if all_frp_ratios:
        print(f"NetConduit reaches **{min(all_frp_ratios):.1f}–{max(all_frp_ratios):.1f}x faster than FRP/Yamux**")
        print(f"and **{min(all_smux_ratios):.1f}–{max(all_smux_ratios):.1f}x faster than Smux**.")
        if nc_1_64:
            print(f"At 1 channel with 64B messages, NetConduit delivers {fmt_int(nc_1_64)} msg/s.")
    print()

    print("**What NetConduit pays for:** Credit-based backpressure prevents OOM under load,")
    print("priority queuing ensures critical channels aren't starved, and adaptive windowing")
    print("automatically reclaims memory from idle channels. These features add measurable")
    print("overhead in bulk transfer but provide production safety guarantees that simpler")
    print("muxes don't offer.")
    print()
    print("---")
    print()

    # Raw TCP baselines
    print("## Raw TCP Baselines")
    print()
    print("Raw TCP uses N separate connections (one per channel) — no multiplexing overhead.")
    print("This is the theoretical ceiling, not a practical alternative (connection limits,")
    print("no flow control, no channel management).")
    print()

    # Throughput all implementations
    print("### Throughput: All Implementations (MB/s)")
    print()
    print("| Channels | Data Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |")
    print("|----------|-----------|----------:|----------:|----------:|----------:|----------:|")
    for ch in [1, 10, 100]:
        for size, label in [(1024, "1KB"), (102400, "100KB"), (1048576, "1MB")]:
            print(f"| {ch} | {label} | {fmt_float(tp(GO_RAW, ch, size))} | {fmt_float(tp(GO_FRP, ch, size))} | {fmt_float(tp(GO_SMUX, ch, size))} | {fmt_float(tp(NET_RAW, ch, size))} | {fmt_float(tp(NET_MUX, ch, size))} |")
    print()

    # Game-tick all implementations
    print("### Game-Tick: All Implementations (msg/s)")
    print()
    print("| Channels | Msg Size | Raw TCP (Go) | FRP/Yamux (Go) | Smux (Go) | Raw TCP (.NET) | NetConduit Mux TCP |")
    print("|----------|----------|----------:|----------:|----------:|----------:|----------:|")
    for ch in [1, 10, 50, 1000]:
        for size, label in [(64, "64B"), (256, "256B")]:
            print(f"| {ch} | {label} | {fmt_int(gt(GO_RAW, ch, size))} | {fmt_int(gt(GO_FRP, ch, size))} | {fmt_int(gt(GO_SMUX, ch, size))} | {fmt_int(gt(NET_RAW, ch, size))} | {fmt_int(gt(NET_MUX, ch, size))} |")
    print()

    # Overhead section
    print("---")
    print()
    print("## Multiplexer Overhead vs Raw TCP")
    print()
    print("How much does multiplexing cost compared to raw connections?")
    print("All multiplexers share this overhead — it's inherent to running N streams over 1 connection.")
    print()

    # Throughput overhead
    print("### Throughput Overhead")
    print()
    print("Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).")
    print()
    print("| Channels | Data Size | NetConduit | FRP/Yamux | Smux |")
    print("|----------|-----------|----------:|----------:|----------:|")
    for ch in [1, 10, 100]:
        for size, label in [(1024, "1KB"), (102400, "100KB"), (1048576, "1MB")]:
            nc_raw = tp(NET_RAW, ch, size)
            nc_mux = tp(NET_MUX, ch, size)
            go_raw = tp(GO_RAW, ch, size)
            frp = tp(GO_FRP, ch, size)
            smux = tp(GO_SMUX, ch, size)
            nc_oh = ratio_plain(nc_raw, nc_mux) if nc_raw and nc_mux else "—"
            frp_oh = ratio_plain(go_raw, frp) if go_raw and frp else "—"
            smux_oh = ratio_plain(go_raw, smux) if go_raw and smux else "—"
            print(f"| {ch} | {label} | {nc_oh} | {frp_oh} | {smux_oh} |")
    print()

    # Game-tick overhead
    print("### Game-Tick Overhead")
    print()
    print("Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).")
    print()
    print("| Channels | Msg Size | NetConduit | FRP/Yamux | Smux |")
    print("|----------|----------|----------:|----------:|----------:|")
    for ch in [1, 10, 50, 1000]:
        for size, label in [(64, "64B"), (256, "256B")]:
            nc_raw = gt(NET_RAW, ch, size)
            nc_mux = gt(NET_MUX, ch, size)
            go_raw = gt(GO_RAW, ch, size)
            frp = gt(GO_FRP, ch, size)
            smux = gt(GO_SMUX, ch, size)
            nc_oh = ratio_plain(nc_raw, nc_mux) if nc_raw and nc_mux else "—"
            frp_oh = ratio_plain(go_raw, frp) if go_raw and frp else "—"
            smux_oh = ratio_plain(go_raw, smux) if go_raw and smux else "—"
            print(f"| {ch} | {label} | {nc_oh} | {frp_oh} | {smux_oh} |")
    print()
    print("---")
    print()
    print("*Generated by NetConduit comparison benchmark suite — median of 3 runs*")


if __name__ == "__main__":
    main()
