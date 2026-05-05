#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
RESULTS_DIR="$SCRIPT_DIR/results"

# --- Fairness controls ---
# Both containers pinned to CPUs 0,1 via --cpuset-cpus (kernel-enforced).
# GOMAXPROCS=2 limits Go goroutine parallelism to match pinned cores.
# DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1 for .NET socket performance.
# --network=none ensures loopback-only (no external traffic interference).
CPUSET="0,1"

mkdir -p "$RESULTS_DIR"

echo "============================================"
echo " NetConduit Docker Benchmark Suite"
echo "============================================"
echo ""
echo "Fairness controls:"
echo "  Docker --cpuset-cpus=$CPUSET (kernel-enforced CPU pinning)"
echo "  GOMAXPROCS=2 (Go goroutine parallelism matches pinned cores)"
echo "  --network=none (loopback only, no external interference)"
echo "  Runs per test: 5 (median reported)"
echo "  Strategy: interleaved by category"
echo ""

# --- Build Docker images ---
echo "[1/6] Building Go benchmark image..."
cd "$REPO_ROOT"
docker build -f benchmarks/docker/Dockerfile.go -t nc-bench-go .
echo "  Built: nc-bench-go"

echo ""
echo "[2/6] Building .NET benchmark image..."
docker build -f benchmarks/docker/Dockerfile.netconduit -t nc-bench-dotnet .
echo "  Built: nc-bench-dotnet"

# --- Run game-tick: Go then .NET back-to-back ---
echo ""
echo "[3/6] Running Go GAME-TICK benchmarks..."
echo "----------------------------------------------"
docker run --rm --cpuset-cpus="$CPUSET" --network=none \
    -e GOMAXPROCS=2 \
    nc-bench-go game-tick \
    > "$RESULTS_DIR/go-gametick.json"
echo "  Go game-tick complete."

echo ""
echo "[4/6] Running .NET GAME-TICK benchmarks..."
echo "----------------------------------------------"
docker run --rm --cpuset-cpus="$CPUSET" --network=none \
    nc-bench-dotnet game-tick \
    > "$RESULTS_DIR/dotnet-gametick.json"
echo "  .NET game-tick complete."

# --- Run throughput: Go then .NET back-to-back ---
echo ""
echo "[5/6] Running Go THROUGHPUT benchmarks..."
echo "----------------------------------------------"
docker run --rm --cpuset-cpus="$CPUSET" --network=none \
    -e GOMAXPROCS=2 \
    nc-bench-go throughput \
    > "$RESULTS_DIR/go-throughput.json"
echo "  Go throughput complete."

echo ""
echo "[6/6] Running .NET THROUGHPUT benchmarks..."
echo "----------------------------------------------"
docker run --rm --cpuset-cpus="$CPUSET" --network=none \
    nc-bench-dotnet throughput \
    > "$RESULTS_DIR/dotnet-throughput.json"
echo "  .NET throughput complete."

# --- Generate report ---
echo ""
echo "============================================"
echo " Generating combined report..."
echo "============================================"

python3 "$SCRIPT_DIR/report.py" \
    "$RESULTS_DIR/go-gametick.json" \
    "$RESULTS_DIR/dotnet-gametick.json" \
    "$RESULTS_DIR/go-throughput.json" \
    "$RESULTS_DIR/dotnet-throughput.json" \
    > "$RESULTS_DIR/comparison-report.md"

echo ""
echo "Results saved to:"
echo "  $RESULTS_DIR/go-gametick.json"
echo "  $RESULTS_DIR/dotnet-gametick.json"
echo "  $RESULTS_DIR/go-throughput.json"
echo "  $RESULTS_DIR/dotnet-throughput.json"
echo "  $RESULTS_DIR/comparison-report.md"
echo ""
echo "Done!"
