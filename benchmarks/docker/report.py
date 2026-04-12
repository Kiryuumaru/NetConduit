#!/usr/bin/env python3
"""Merge Go and .NET benchmark results into a Markdown comparison report."""

import json
import sys
from collections import defaultdict


def load_results(*files):
    all_results = []
    for f in files:
        with open(f) as fp:
            all_results.extend(json.load(fp))
    return all_results


def format_size(b):
    if b >= 1_048_576:
        return f"{b / 1_048_576:.0f}MB"
    if b >= 1024:
        return f"{b / 1024:.0f}KB"
    return f"{b}B"


def fmt_ratio(val):
    """Format a ratio as Nx with bold when >= 1."""
    if val < 0.005:
        return "<0.01x"
    if val >= 1.0:
        return f"**{val:.2f}x**"
    return f"{val:.2f}x"


def main():
    if len(sys.argv) < 3:
        print("Usage: report.py <go-results.json> <dotnet-results.json>", file=sys.stderr)
        sys.exit(1)

    results = load_results(*sys.argv[1:])

    # Split by scenario
    throughput_results = [r for r in results if r.get("scenario") == "throughput"]
    game_results = [r for r in results if r.get("scenario") == "game-tick"]

    # Group by (channels, dataSize)
    tp_grouped = defaultdict(dict)
    for r in throughput_results:
        key = (r["channels"], r["dataSizeBytes"])
        tp_grouped[key][r["implementation"]] = r

    game_grouped = defaultdict(dict)
    for r in game_results:
        key = (r["channels"], r["dataSizeBytes"])
        game_grouped[key][r["implementation"]] = r

    # --- Multiplexer-only names ---
    mux_impls = ["NetConduit Mux TCP", "FRP/Yamux (Go)", "Smux (Go)"]
    all_impls = ["Raw TCP (Go)", "FRP/Yamux (Go)", "Smux (Go)", "Raw TCP (.NET)", "NetConduit Mux TCP"]

    # ================================================================
    # HEADER
    # ================================================================
    print("# NetConduit Comparison Benchmark Results")
    print()
    print("All benchmarks run on loopback (127.0.0.1), identical workloads,")
    print("5 runs with median reported. CPU-pinned via `taskset 0x3` (2 cores).")
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

    # ================================================================
    # 1. MULTIPLEXER HEAD-TO-HEAD
    # ================================================================
    print("---")
    print()
    print("## Multiplexer Head-to-Head")
    print()
    print("The comparison that matters: **NetConduit vs FRP/Yamux vs Smux**.")
    print("All three multiplex N channels over a single TCP connection.")
    print()

    # --- Throughput mux-only ---
    if tp_grouped:
        print("### Bulk Throughput (MB/s)")
        print()
        print("Each channel sends one data payload. Higher = better.")
        print()
        print("| Channels | Data Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |")
        print("|----------|-----------|----------:|----------:|-----:|----------:|----------:|")

        for key in sorted(tp_grouped.keys()):
            ch, ds = key
            nc = tp_grouped[key].get("NetConduit Mux TCP", {})
            frp = tp_grouped[key].get("FRP/Yamux (Go)", {})
            smux = tp_grouped[key].get("Smux (Go)", {})

            nc_tp = nc.get("throughputMBps", 0)
            frp_tp = frp.get("throughputMBps", 0)
            smux_tp = smux.get("throughputMBps", 0)

            nc_vs_frp = fmt_ratio(nc_tp / frp_tp) if frp_tp > 0 else "—"
            nc_vs_smux = fmt_ratio(nc_tp / smux_tp) if smux_tp > 0 else "—"

            print(f"| {ch} | {format_size(ds)} | {nc_tp:,.1f} | {frp_tp:,.1f} | {smux_tp:,.1f} | {nc_vs_frp} | {nc_vs_smux} |")

        print()

    # --- Game-tick mux-only ---
    if game_grouped:
        print("### Game-Tick Message Rate (msg/s)")
        print()
        print("Each channel sends many small messages (simulates game state updates). Higher = better.")
        print()
        print("| Channels | Msg Size | NetConduit | FRP/Yamux | Smux | NC vs FRP | NC vs Smux |")
        print("|----------|----------|----------:|----------:|-----:|----------:|----------:|")

        for key in sorted(game_grouped.keys()):
            ch, ms = key
            nc = game_grouped[key].get("NetConduit Mux TCP", {})
            frp = game_grouped[key].get("FRP/Yamux (Go)", {})
            smux = game_grouped[key].get("Smux (Go)", {})

            nc_mps = nc.get("messagesPerSec", 0)
            frp_mps = frp.get("messagesPerSec", 0)
            smux_mps = smux.get("messagesPerSec", 0)

            nc_vs_frp = fmt_ratio(nc_mps / frp_mps) if frp_mps > 0 else "—"
            nc_vs_smux = fmt_ratio(nc_mps / smux_mps) if smux_mps > 0 else "—"

            print(f"| {ch} | {format_size(ms)} | {nc_mps:,.0f} | {frp_mps:,.0f} | {smux_mps:,.0f} | {nc_vs_frp} | {nc_vs_smux} |")

        print()

    # ================================================================
    # 2. KEY TAKEAWAYS
    # ================================================================
    print("---")
    print()
    print("## Key Takeaways")
    print()

    # Generate takeaways from actual data
    # Count wins/losses in throughput
    tp_wins = 0
    tp_total = 0
    for key in tp_grouped:
        nc = tp_grouped[key].get("NetConduit Mux TCP", {})
        frp = tp_grouped[key].get("FRP/Yamux (Go)", {})
        smux = tp_grouped[key].get("Smux (Go)", {})
        nc_tp = nc.get("throughputMBps", 0)
        frp_tp = frp.get("throughputMBps", 0)
        smux_tp = smux.get("throughputMBps", 0)
        if nc_tp > 0 and frp_tp > 0:
            tp_total += 1
            if nc_tp > frp_tp:
                tp_wins += 1
        if nc_tp > 0 and smux_tp > 0:
            tp_total += 1
            if nc_tp > smux_tp:
                tp_wins += 1

    gt_wins = 0
    gt_total = 0
    for key in game_grouped:
        nc = game_grouped[key].get("NetConduit Mux TCP", {})
        frp = game_grouped[key].get("FRP/Yamux (Go)", {})
        smux = game_grouped[key].get("Smux (Go)", {})
        nc_mps = nc.get("messagesPerSec", 0)
        frp_mps = frp.get("messagesPerSec", 0)
        smux_mps = smux.get("messagesPerSec", 0)
        if nc_mps > 0 and frp_mps > 0:
            gt_total += 1
            if nc_mps > frp_mps:
                gt_wins += 1
        if nc_mps > 0 and smux_mps > 0:
            gt_total += 1
            if nc_mps > smux_mps:
                gt_wins += 1

    print(f"**Bulk throughput:** NetConduit wins {tp_wins}/{tp_total} comparisons against Go multiplexers.")
    print("Credit-based flow control adds per-transfer overhead most visible in large bulk scenarios.")
    print("NetConduit is competitive or faster for small-to-medium payloads (1KB–100KB).")
    print()
    print(f"**Game-tick messaging:** NetConduit wins {gt_wins}/{gt_total} comparisons against Go multiplexers.")
    print("When per-message overhead dominates (not raw throughput), the credit system's cost")
    print("is proportionally smaller.")
    print()
    print("**What NetConduit pays for:** Credit-based backpressure prevents OOM under load,")
    print("priority queuing ensures critical channels aren't starved, and adaptive windowing")
    print("automatically reclaims memory from idle channels. These features add measurable")
    print("overhead but provide production safety guarantees that simpler muxes don't offer.")
    print()

    # ================================================================
    # 3. RAW TCP BASELINES (context only)
    # ================================================================
    print("---")
    print()
    print("## Raw TCP Baselines")
    print()
    print("Raw TCP uses N separate connections (one per channel) — no multiplexing overhead.")
    print("This is the theoretical ceiling, not a practical alternative (connection limits,")
    print("no flow control, no channel management).")
    print()

    if tp_grouped:
        print("### Throughput: All Implementations (MB/s)")
        print()

        header = "| Channels | Data Size | " + " | ".join(all_impls) + " |"
        separator = "|----------|-----------|" + "|".join(["----------:" for _ in all_impls]) + "|"
        print(header)
        print(separator)

        for key in sorted(tp_grouped.keys()):
            ch, ds = key
            row = f"| {ch} | {format_size(ds)} |"
            for impl in all_impls:
                if impl in tp_grouped[key]:
                    tp = tp_grouped[key][impl]["throughputMBps"]
                    row += f" {tp:,.1f} |"
                else:
                    row += " — |"
            print(row)

        print()

    if game_grouped:
        print("### Game-Tick: All Implementations (msg/s)")
        print()

        header = "| Channels | Msg Size | " + " | ".join(all_impls) + " |"
        separator = "|----------|----------|" + "|".join(["----------:" for _ in all_impls]) + "|"
        print(header)
        print(separator)

        for key in sorted(game_grouped.keys()):
            ch, ms = key
            row = f"| {ch} | {format_size(ms)} |"
            for impl in all_impls:
                if impl in game_grouped[key]:
                    mps = game_grouped[key][impl].get("messagesPerSec", 0)
                    row += f" {mps:,.0f} |"
                else:
                    row += " — |"
            print(row)

        print()

    # ================================================================
    # 4. MULTIPLEXER OVERHEAD COMPARISON
    # ================================================================
    print("---")
    print()
    print("## Multiplexer Overhead vs Raw TCP")
    print()
    print("How much does multiplexing cost compared to raw connections?")
    print("All multiplexers share this overhead — it's inherent to running N streams over 1 connection.")
    print()

    if tp_grouped:
        print("### Throughput Overhead")
        print()
        print("Ratio = Raw TCP throughput / Mux throughput (how many times slower the mux is).")
        print()

        mux_pairs = [
            ("NetConduit Mux TCP", "Raw TCP (.NET)", "Raw TCP (Go)"),
            ("FRP/Yamux (Go)", "Raw TCP (Go)", None),
            ("Smux (Go)", "Raw TCP (Go)", None),
        ]
        mux_labels = ["NetConduit", "FRP/Yamux", "Smux"]

        header = "| Channels | Data Size | " + " | ".join(mux_labels) + " |"
        separator = "|----------|-----------|" + "|".join(["----------:" for _ in mux_labels]) + "|"
        print(header)
        print(separator)

        for key in sorted(tp_grouped.keys()):
            ch, ds = key
            row = f"| {ch} | {format_size(ds)} |"
            for mux_impl, raw_impl, raw_fallback in mux_pairs:
                mux_r = tp_grouped[key].get(mux_impl)
                raw_r = tp_grouped[key].get(raw_impl)
                if not raw_r and raw_fallback:
                    raw_r = tp_grouped[key].get(raw_fallback)
                if mux_r and raw_r:
                    mux_tp = mux_r["throughputMBps"]
                    raw_tp = raw_r["throughputMBps"]
                    if mux_tp > 0:
                        ratio = raw_tp / mux_tp
                        row += f" {ratio:.1f}x |"
                    else:
                        row += " — |"
                else:
                    row += " — |"
            print(row)

        print()

    if game_grouped:
        print("### Game-Tick Overhead")
        print()
        print("Ratio = Raw TCP msg/s / Mux msg/s (how many times slower the mux is).")
        print()

        header = "| Channels | Msg Size | " + " | ".join(mux_labels) + " |"
        separator = "|----------|----------|" + "|".join(["----------:" for _ in mux_labels]) + "|"
        print(header)
        print(separator)

        for key in sorted(game_grouped.keys()):
            ch, ms = key
            row = f"| {ch} | {format_size(ms)} |"
            for mux_impl, raw_impl, raw_fallback in mux_pairs:
                mux_r = game_grouped[key].get(mux_impl)
                raw_r = game_grouped[key].get(raw_impl)
                if not raw_r and raw_fallback:
                    raw_r = game_grouped[key].get(raw_fallback)
                if mux_r and raw_r:
                    mux_mps = mux_r.get("messagesPerSec", 0)
                    raw_mps = raw_r.get("messagesPerSec", 0)
                    if mux_mps > 0:
                        ratio = raw_mps / mux_mps
                        row += f" {ratio:.1f}x |"
                    else:
                        row += " — |"
                else:
                    row += " — |"
            print(row)

        print()

    print("---")
    print()
    print("*Generated by NetConduit comparison benchmark suite*")


if __name__ == "__main__":
    main()
