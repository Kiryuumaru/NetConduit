using Xunit;

// Ipc integration tests open real loopback/UDS sockets and the leak probe measures
// process-wide HandleCount deltas. Both are sensitive to concurrent socket activity
// from sibling classes, so disable cross-class parallelism within this assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
