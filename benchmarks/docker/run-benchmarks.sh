#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
RESULTS_DIR="$SCRIPT_DIR/results"

# --- Fairness controls ---
# Pin both Go and .NET to the same CPU cores (0,1) via taskset.
# This ensures identical compute environment for both runtimes.
CPU_MASK="0x3"  # CPUs 0 and 1

# GOMAXPROCS limits Go goroutine parallelism to match CPU pin.
export GOMAXPROCS=2

# .NET equivalent: limit thread pool to match.
export DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1

mkdir -p "$RESULTS_DIR"

echo "============================================"
echo " NetConduit Comparison Benchmark Suite"
echo "============================================"
echo ""
echo "Fairness controls:"
echo "  CPU affinity:  taskset $CPU_MASK (2 cores)"
echo "  GOMAXPROCS:    $GOMAXPROCS"
echo "  Runs per test: 5 (median reported)"
echo "  Strategy:      interleaved by category (Go+.NET run together)"
echo ""

# --- Build Go benchmark ---
echo "[1/6] Building Go benchmark..."
GO_BIN="$HOME/go-install/go/bin"
if [ ! -f "$GO_BIN/go" ]; then
    echo "ERROR: Go not found at $GO_BIN. Install Go 1.23+ first."
    exit 1
fi
export PATH="$GO_BIN:$PATH"
cd "$SCRIPT_DIR/go-comparison"
go build -o "$SCRIPT_DIR/results/go-bench" .
echo "  Built: results/go-bench"

# --- Build .NET benchmark ---
echo ""
echo "[2/6] Building .NET benchmark..."
cd "$REPO_ROOT"
dotnet build benchmarks/docker/netconduit-comparison -c Release --nologo -v q -p:UseLocalNetConduit=true
echo "  Built: Release mode"

# --- Run game-tick: Go then .NET back-to-back ---
echo ""
echo "[3/6] Running Go GAME-TICK benchmarks..."
echo "----------------------------------------------"
taskset "$CPU_MASK" "$RESULTS_DIR/go-bench" game-tick \
    > "$RESULTS_DIR/go-gametick.json"
echo "  Go game-tick complete."

echo ""
echo "[4/6] Running .NET GAME-TICK benchmarks..."
echo "----------------------------------------------"
taskset "$CPU_MASK" dotnet run \
    --project benchmarks/docker/netconduit-comparison \
    -c Release --no-build -- game-tick \
    > "$RESULTS_DIR/dotnet-gametick.json"
echo "  .NET game-tick complete."

# --- Run throughput: Go then .NET back-to-back ---
echo ""
echo "[5/6] Running Go THROUGHPUT benchmarks..."
echo "----------------------------------------------"
taskset "$CPU_MASK" "$RESULTS_DIR/go-bench" throughput \
    > "$RESULTS_DIR/go-throughput.json"
echo "  Go throughput complete."

echo ""
echo "[6/6] Running .NET THROUGHPUT benchmarks..."
echo "----------------------------------------------"
taskset "$CPU_MASK" dotnet run \
    --project benchmarks/docker/netconduit-comparison \
    -c Release --no-build -- throughput \
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
cat "$RESULTS_DIR/comparison-report.md"
