using MapEditor;

namespace YameeTests;

static class SingleInstanceTests
{
    [Test("SingleInstance: second start signals first and exits cleanly")]
    static void SecondInstanceSignalsFirst()
    {
        string name = "Local\\YAMEE.Test." + Guid.NewGuid().ToString("N");
        int activations = 0;
        using var signaled = new ManualResetEventSlim(false);

        using (var first = SingleInstance.TryStart(() => { Interlocked.Increment(ref activations); signaled.Set(); }, name))
        {
            Check.True(first != null, "first instance should acquire the lock");

            var second = SingleInstance.TryStart(() => Check.True(false, "second callback must not run"), name);
            Check.True(second == null, "second instance must not acquire the lock");
            Check.True(signaled.Wait(TimeSpan.FromSeconds(2)), "first instance should be signaled");
            Check.Equal(1, Volatile.Read(ref activations), "activation count after second launch");

            signaled.Reset();
            var third = SingleInstance.TryStart(() => { }, name);
            Check.True(third == null, "third instance must not acquire the lock");
            Check.True(signaled.Wait(TimeSpan.FromSeconds(2)), "first instance should be signaled again");
            Check.Equal(2, Volatile.Read(ref activations), "activation count after third launch");
        }

        // After the first instance exits, a new instance can start again.
        using var afterExit = SingleInstance.TryStart(() => { }, name);
        Check.True(afterExit != null, "new instance should acquire the lock after previous exited");
    }
}
