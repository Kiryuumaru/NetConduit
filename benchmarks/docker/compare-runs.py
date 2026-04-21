#!/usr/bin/env python3
"""Compare 3 benchmark runs and calculate consistency metrics."""

import json
import sys
import statistics
from collections import defaultdict


def load_results(*files):
    all_results = []
    for f in files:
        try:
            with open(f) as fp:
                data = json.load(fp)
                all_results.extend(data)
        except (json.JSONDecodeError, FileNotFoundError):
            print(f"WARNING: Skipping {f} (empty or invalid)", file=sys.stderr)
    return all_results


def format_size(b):
    if b >= 1_048_576:
        return f"{b / 1_048_576:.0f}MB"
    if b >= 1024:
        return f"{b / 1024:.0f}KB"
    return f"{b}B"


def cv_pct(values):
    """Coefficient of variation as percentage."""
    if len(values) < 2:
        return 0.0
    m = statistics.mean(values)
    if m == 0:
        return 0.0
    return (statistics.stdev(values) / m) * 100


def main():
    results_dir = sys.argv[1] if len(sys.argv) > 1 else "benchmarks/docker/results"

    # Load all 3 runs
    runs_data = []
    for run in range(1, 4):
        gt = load_results(f"{results_dir}/run{run}-go-gametick.json",
                          f"{results_dir}/run{run}-dotnet-gametick.json")
        tp = load_results(f"{results_dir}/run{run}-go-throughput.json",
                          f"{results_dir}/run{run}-dotnet-throughput.json")
        runs_data.append(gt + tp)

    # Group by (scenario, implementation, channels, dataSize)
    grouped = defaultdict(list)
    for run_idx, run in enumerate(runs_data):
        for r in run:
            key = (r["scenario"], r["implementation"],
                   r["channels"], r["dataSizeBytes"])
            metric = r.get("messagesPerSec", 0) or r.get("throughputMBps", 0)
            grouped[key].append((run_idx, metric))

    # Build per-key stats
    print("# 3-Run Benchmark Consistency Report")
    print()
    print("CV% = Coefficient of Variation (lower = more consistent). CV% < 10% is good.")
    print()

    # Split by scenario
    for scenario in ["game-tick", "throughput"]:
        metric_label = "msg/s" if scenario == "game-tick" else "MB/s"
        print(f"## {scenario.title()}")
        print()
        print(f"| Implementation | Channels | Size | Run1 | Run2 | Run3 | Mean | CV% |")
        print(f"|----------------|----------|------|-----:|-----:|-----:|-----:|----:|")

        keys_for_scenario = sorted(
            [k for k in grouped if k[0] == scenario],
            key=lambda k: (k[1], k[2], k[3])
        )

        high_cv = []
        all_cvs = []

        for key in keys_for_scenario:
            _, impl, ch, ds = key
            values_by_run = {run_idx: val for run_idx, val in grouped[key]}

            if len(values_by_run) < 3:
                continue

            vals = [values_by_run[i] for i in range(3)]
            mean_val = statistics.mean(vals)
            cv = cv_pct(vals)
            all_cvs.append(cv)

            if cv > 15:
                high_cv.append((impl, ch, ds, cv))

            size_str = format_size(ds)
            if scenario == "game-tick":
                v1, v2, v3 = [f"{v:,.0f}" for v in vals]
                mean_str = f"{mean_val:,.0f}"
            else:
                v1, v2, v3 = [f"{v:,.1f}" for v in vals]
                mean_str = f"{mean_val:,.1f}"

            cv_flag = " **" if cv > 15 else ""
            print(f"| {impl} | {ch} | {size_str} | {v1} | {v2} | {v3} | {mean_str} | {cv:.1f}%{cv_flag} |")

        print()

        if all_cvs:
            median_cv = statistics.median(all_cvs)
            max_cv = max(all_cvs)
            low_cv_count = sum(1 for c in all_cvs if c < 10)
            print(f"**Median CV%: {median_cv:.1f}%** | Max CV%: {max_cv:.1f}% | Tests with CV% < 10%: {low_cv_count}/{len(all_cvs)}")
            print()

            if high_cv:
                print(f"High variance tests (CV% > 15%):")
                for impl, ch, ds, cv in high_cv:
                    print(f"  - {impl} ch={ch} {format_size(ds)}: CV={cv:.1f}%")
                print()

    # Overall summary
    print("---")
    print()
    print("## Conclusion")
    print()

    # Collect all CVs
    all_cvs_total = []
    for key in grouped:
        values_by_run = {run_idx: val for run_idx, val in grouped[key]}
        if len(values_by_run) == 3:
            vals = [values_by_run[i] for i in range(3)]
            all_cvs_total.append(cv_pct(vals))

    if all_cvs_total:
        median_cv = statistics.median(all_cvs_total)
        mean_cv = statistics.mean(all_cvs_total)
        max_cv = max(all_cvs_total)
        low_count = sum(1 for c in all_cvs_total if c < 10)
        med_count = sum(1 for c in all_cvs_total if 10 <= c < 20)
        high_count = sum(1 for c in all_cvs_total if c >= 20)
        total = len(all_cvs_total)

        print(f"- Total tests compared: {total}")
        print(f"- Median CV%: {median_cv:.1f}%")
        print(f"- Mean CV%: {mean_cv:.1f}%")
        print(f"- Max CV%: {max_cv:.1f}%")
        print(f"- CV% < 10% (consistent): {low_count}/{total} ({low_count/total*100:.0f}%)")
        print(f"- CV% 10-20% (moderate): {med_count}/{total}")
        print(f"- CV% > 20% (high variance): {high_count}/{total}")
        print()

        if median_cv < 10:
            print("**Results are consistent across all 3 runs.** Median variance is low.")
        elif median_cv < 15:
            print("**Results are mostly consistent.** Some variance in specific tests.")
        else:
            print("**Results show notable variance.** Consider more runs or isolating noisy tests.")


if __name__ == "__main__":
    main()
