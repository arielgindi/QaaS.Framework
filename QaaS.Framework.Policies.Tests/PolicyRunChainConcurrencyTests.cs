namespace QaaS.Framework.Policies.Tests;

[TestFixture]
public class PolicyRunChainConcurrencyTests
{
    [Test]
    public void CountPolicy_RunChain_FromParallelWorkers_AllowsExactNumberBeforeStop()
    {
        const int countPolicyLimit = 257;
        const int workerCount = 64;
        const int callsPerWorker = 32;
        var policy = new CountPolicy(countPolicyLimit);

        policy.SetupChain();

        var results = RunConcurrently(workerCount, callsPerWorker, policy.RunChain);
        var totalCalls = workerCount * callsPerWorker;

        Assert.Multiple(() =>
        {
            Assert.That(results.SuccessCount, Is.EqualTo(countPolicyLimit - 1));
            Assert.That(results.StopCount, Is.EqualTo(totalCalls - countPolicyLimit + 1));
        });
    }

    [Test]
    public void CountPolicyThenStubPolicy_RunChain_FromParallelWorkers_RunsStubSerially()
    {
        const int countPolicyLimit = 129;
        const int workerCount = 64;
        const int callsPerWorker = 16;
        var stubPolicy = new TrackingPolicy();
        var policy = new CountPolicy(countPolicyLimit).Add(stubPolicy);

        policy.SetupChain();

        var results = RunConcurrently(workerCount, callsPerWorker, policy.RunChain);

        Assert.Multiple(() =>
        {
            Assert.That(results.SuccessCount, Is.EqualTo(countPolicyLimit - 1));
            Assert.That(stubPolicy.RunCount, Is.EqualTo(countPolicyLimit - 1));
            Assert.That(stubPolicy.MaxConcurrentRuns, Is.EqualTo(1));
        });
    }

    private static (int SuccessCount, int StopCount) RunConcurrently(
        int workerCount,
        int callsPerWorker,
        Func<bool> action)
    {
        using var ready = new CountdownEvent(workerCount);
        using var start = new ManualResetEventSlim(false);
        var successCount = 0;
        var stopCount = 0;
        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ =>
                Task.Factory.StartNew(
                    () =>
                    {
                        ready.Signal();
                        start.Wait();

                        for (var call = 0; call < callsPerWorker; call++)
                        {
                            if (action())
                                Interlocked.Increment(ref successCount);
                            else
                                Interlocked.Increment(ref stopCount);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
            .ToArray();

        Assert.That(ready.Wait(TimeSpan.FromSeconds(10)), Is.True);
        start.Set();
        Task.WaitAll(tasks);

        return (successCount, stopCount);
    }

    private static void RecordMax(ref int maxValue, int value)
    {
        while (true)
        {
            var currentMax = Volatile.Read(ref maxValue);
            if (value <= currentMax)
                return;

            if (Interlocked.CompareExchange(ref maxValue, value, currentMax) == currentMax)
                return;
        }
    }

    private sealed class TrackingPolicy : Policy
    {
        private int _activeRuns;
        private int _runCount;
        private int _maxConcurrentRuns;

        public int RunCount => Volatile.Read(ref _runCount);

        public int MaxConcurrentRuns => Volatile.Read(ref _maxConcurrentRuns);

        protected override uint Index { get; set; } = 1;

        protected override void SetupThis()
        {
        }

        protected override void RunThis()
        {
            var activeRuns = Interlocked.Increment(ref _activeRuns);
            RecordMax(ref _maxConcurrentRuns, activeRuns);

            try
            {
                Thread.SpinWait(50_000);
                Interlocked.Increment(ref _runCount);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRuns);
            }
        }
    }
}
