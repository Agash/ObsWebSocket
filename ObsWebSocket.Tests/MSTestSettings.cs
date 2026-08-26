// Method-level parallelism with a fixed worker count. Left to its default, MSTest runs one worker
// per processor, and `dotnet test` runs every target framework at once, so the suite asks for
// several times the machine's parallelism. The tests that suffer are the ones asserting a timeout:
// their continuations wait on a thread pool that is saturated, and they fail on a busy machine
// rather than on a defect.
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]
