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
echo ""

# --- Build Go benchmark ---
echo "[1/4] Building Go benchmark..."
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
echo "[2/4] Building .NET benchmark..."
cd "$REPO_ROOT"
dotnet build benchmarks/docker/netconduit-comparison -c Release --nologo -v q
echo "  Built: Release mode"

# --- Run Go benchmarks ---
echo ""
echo "[3/4] Running Go benchmarks (Raw TCP, Yamux, Smux)..."
echo "  CPU affinity: cores 0,1"
echo "----------------------------------------------"
taskset "$CPU_MASK" "$RESULTS_DIR/go-bench" \
    > "$RESULTS_DIR/go-results.json"
echo "  Go benchmarks complete."

# --- Run .NET benchmarks ---
echo ""
echo "[4/4] Running .NET benchmarks (Raw TCP, NetConduit Mux)..."
echo "  CPU affinity: cores 0,1"
echo "----------------------------------------------"
taskset "$CPU_MASK" dotnet run \
    --project benchmarks/docker/netconduit-comparison \
    -c Release --no-build \
    > "$RESULTS_DIR/dotnet-results.json"
echo "  .NET benchmarks complete."

# --- Generate report ---
echo ""
echo "============================================"
echo " Generating combined report..."
echo "============================================"

python3 "$SCRIPT_DIR/report.py" \
    "$RESULTS_DIR/go-results.json" \
    "$RESULTS_DIR/dotnet-results.json" \
    > "$RESULTS_DIR/comparison-report.md"

echo ""
echo "Results saved to:"
echo "  $RESULTS_DIR/go-results.json"
echo "  $RESULTS_DIR/dotnet-results.json"
echo "  $RESULTS_DIR/comparison-report.md"
echo ""
cat "$RESULTS_DIR/comparison-report.md"
