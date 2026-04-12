# Plan 006: Eliminate EnqueueData Memory Copy — NOT ATTEMPTED

**Addresses:** Triple copy, EnqueueData = 98% of parse time

**Change:** `src/NetConduit/ReadChannel.cs` + `StreamMultiplexer.cs` — Hybrid approach: for payloads > 64KB, use direct memory ownership handoff (skip Pipe copy). For small payloads, keep current Pipe path.

**Theory:** EnqueueData copies entire payload from ReadLoop pipe buffer into per-channel pipe buffer. For 1MB frames this takes 185us. Direct memory handoff eliminates this copy entirely for large frames.

**Result:** Not attempted. Deemed too architecturally complex and analysis showed memcpy was not the primary throughput bottleneck (the pipeline overhead of 3 copies + 2 pipe syncs is the real cost).

**Root cause:** N/A — skipped in favor of other approaches that seemed more promising.
