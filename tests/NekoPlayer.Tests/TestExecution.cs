// These suites share native UI initialization and launch real decoder/IPC fixtures.
// Bound unrelated suite concurrency on two-core hosted Windows runners; individual
// tests still exercise their own concurrent calls and cancellation races.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 2)]
