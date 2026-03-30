#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
RESULTS_DIR="$SCRIPT_DIR/results"

mkdir -p "$RESULTS_DIR"

echo "============================================"
echo " NetConduit Comparison Benchmark Suite"
echo "============================================"
echo ""
echo "Contestants:"
echo "  - Raw TCP (.NET)        — baseline, N connections"
echo "  - NetConduit Mux TCP    — 1 connection, N channels"
echo "  - Raw TCP (Go)          — baseline, N connections"
echo "  - Yamux (Go)            — HashiCorp multiplexer (used in Consul/Nomad, similar to FRP)"
echo "  - Smux (Go)             — popular Go stream multiplexer"
echo ""
echo "Matrix: channels={1,10,100} × dataSize={1KB,100KB,1MB}"
echo ""

# Build images
echo "[1/4] Building Go benchmark image..."
docker build -t netconduit-bench-go \
    -f "$SCRIPT_DIR/Dockerfile.go" \
    "$SCRIPT_DIR" 2>&1

echo ""
echo "[2/4] Building .NET benchmark image..."
docker build -t netconduit-bench-dotnet \
    -f "$SCRIPT_DIR/Dockerfile.netconduit" \
    "$REPO_ROOT" 2>&1

# Run Go benchmarks
echo ""
echo "[3/4] Running Go benchmarks (Raw TCP, Yamux, Smux)..."
echo "----------------------------------------------"
docker run --rm --name bench-go \
    --network none \
    netconduit-bench-go \
    > "$RESULTS_DIR/go-results.json"

echo "  Go benchmarks complete."

# Run .NET benchmarks
echo ""
echo "[4/4] Running .NET benchmarks (Raw TCP, NetConduit Mux)..."
echo "----------------------------------------------"
docker run --rm --name bench-dotnet \
    --network none \
    netconduit-bench-dotnet \
    > "$RESULTS_DIR/dotnet-results.json"

echo "  .NET benchmarks complete."

# Merge results
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
