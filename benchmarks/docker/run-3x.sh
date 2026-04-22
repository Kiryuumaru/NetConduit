#!/usr/bin/env bash
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS_DIR="$SCRIPT_DIR/results"

for RUN in 1 2 3; do
    echo ""
    echo "========================================================"
    echo " RUN $RUN / 3"
    echo "========================================================"
    bash "$SCRIPT_DIR/run-benchmarks.sh" || echo "  WARNING: Run $RUN had errors (continuing)"

    # Save per-run copies (skip missing/empty files)
    for f in go-gametick.json dotnet-gametick.json go-throughput.json dotnet-throughput.json; do
        if [ -s "$RESULTS_DIR/$f" ]; then
            cp "$RESULTS_DIR/$f" "$RESULTS_DIR/run${RUN}-${f}"
        fi
    done
    if [ -s "$RESULTS_DIR/comparison-report.md" ]; then
        cp "$RESULTS_DIR/comparison-report.md" "$RESULTS_DIR/run${RUN}-report.md"
    fi

    echo ""
    echo "  Run $RUN results saved."
done

echo ""
echo "========================================================"
echo " All 3 runs complete."
echo "========================================================"
