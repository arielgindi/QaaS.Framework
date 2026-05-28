namespace QaaS.Framework.Policies.Tests;

[TestFixture]
public class PolicyConcurrencyEndToEndTests
{
    [Test]
    public void CountPolicyChainedWithTimeoutPolicy_FromManyThreads_StopsExactlyAtCountLimit()
    {
        // End-to-end chain with two real, stateful policies. The CountPolicy increments
        // _counter on every RunThis; TimeoutPolicy reads the shared Stopwatch. Without
        // the per-instance lock on RunChain, parallel callers would race the counter
        // and the final true-count would drift away from (limit - 1).
        const int limit = 75;
        const int workers = 256;
        // Timeout long enough that the wall-clock policy never trips during the test —
        // any premature termination has to be the CountPolicy doing its job.
        var chain = new CountPolicy(limit).Add(new TimeoutPolicy(60_000));
        chain.SetupChain();

        var trueCount = 0;
        var falseCount = 0;
        Parallel.For(0, workers, _ =>
        {
            if (chain.RunChain()) Interlocked.Increment(ref trueCount);
            else Interlocked.Increment(ref falseCount);
        });

        Assert.Multiple(() =>
        {
            Assert.That(trueCount, Is.EqualTo(limit - 1),
                "RunChain must serialise CountPolicy._counter so exactly (limit - 1) calls precede the stop.");
            Assert.That(trueCount + falseCount, Is.EqualTo(workers),
                "Every caller must observe a definitive bool result.");
        });
    }
}
