using System.Threading;
using SwAiAssistant.Core.Threading;
using Xunit;

namespace SwAiAssistant.Core.Tests
{
    public class StaExecutorTests
    {
        [Fact]
        public void Run_ExecutesOnDedicatedStaThread()
        {
            using (var sta = new StaExecutor("SwTestSTA"))
            {
                int observedThreadId = -1;
                ApartmentState state = ApartmentState.Unknown;

                sta.Run(() =>
                {
                    observedThreadId = Thread.CurrentThread.ManagedThreadId;
                    state = Thread.CurrentThread.GetApartmentState();
                });

                Assert.Equal(ApartmentState.STA, state);
                Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, observedThreadId);
                Assert.Equal(sta.ThreadId, observedThreadId);
            }
        }

        [Fact]
        public void Run_PropagatesExceptionsToCaller()
        {
            using (var sta = new StaExecutor("SwTestSTA"))
            {
                Assert.Throws<System.InvalidOperationException>(
                    () => sta.Run(() => throw new System.InvalidOperationException("boom")));
            }
        }
    }
}
