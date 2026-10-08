// Native UI initialization and real decoder/pipe fixtures share process resources.
// Serialize independent suites; concurrency scenarios remain parallel inside their tests.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

internal static class NativeFixtureThreadPool
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        // Pipe/process fixtures can occupy workers while async readiness and debounce
        // continuations need another worker. Avoid pool starvation on two-core runners.
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), completionPorts);
    }
}
