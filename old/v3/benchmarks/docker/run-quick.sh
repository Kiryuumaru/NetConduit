#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS_DIR="$SCRIPT_DIR/results"
CPUSET="0,1"

mkdir -p "$RESULTS_DIR"

echo "=== Go GAME-TICK ==="
docker run --rm --cpuset-cpus="$CPUSET" --network=none -e GOMAXPROCS=2 nc-bench-go game-tick > "$RESULTS_DIR/go-gametick.json"
echo "Done."

echo "=== .NET GAME-TICK ==="
docker run --rm --cpuset-cpus="$CPUSET" --network=none nc-bench-dotnet game-tick > "$RESULTS_DIR/dotnet-gametick.json"
echo "Done."

echo "=== Go THROUGHPUT ==="
docker run --rm --cpuset-cpus="$CPUSET" --network=none -e GOMAXPROCS=2 nc-bench-go throughput > "$RESULTS_DIR/go-throughput.json"
echo "Done."

echo "=== .NET THROUGHPUT ==="
docker run --rm --cpuset-cpus="$CPUSET" --network=none nc-bench-dotnet throughput > "$RESULTS_DIR/dotnet-throughput.json"
echo "Done."

echo "=== Generating Report ==="
python3 "$SCRIPT_DIR/report.py" \
    "$RESULTS_DIR/go-gametick.json" \
    "$RESULTS_DIR/dotnet-gametick.json" \
    "$RESULTS_DIR/go-throughput.json" \
    "$RESULTS_DIR/dotnet-throughput.json" \
    > "$RESULTS_DIR/comparison-report.md"
echo "Report saved to $RESULTS_DIR/comparison-report.md"

echo ""
echo "=== ALL DONE ==="
