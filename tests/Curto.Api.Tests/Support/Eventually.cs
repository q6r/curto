namespace Curto.Api.Tests.Support;

/// <summary>Polls a condition that becomes true asynchronously (background writers, disconnect handling).</summary>
public static class Eventually
{
    /// <summary>Waits until <paramref name="condition"/> returns true, failing the test after <paramref name="timeout"/>.</summary>
    /// <param name="condition">Condition to poll.</param>
    /// <param name="timeout">Longest wait; defaults to 10 seconds.</param>
    public static async Task Async(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition did not become true in time.");
            }

            await Task.Delay(10);
        }
    }
}
