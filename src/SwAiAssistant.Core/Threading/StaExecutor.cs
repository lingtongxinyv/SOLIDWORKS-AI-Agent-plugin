using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SwAiAssistant.Core.Logging;

namespace SwAiAssistant.Core.Threading
{
    /// <summary>
    /// 专用单实例 STA 线程执行器。
    /// SolidWorks COM 对象必须在 STA 线程上访问；Cad 层所有 COM 调用都经由此执行器
    /// 封送到同一条长生命周期 STA 线程，杜绝跨线程 COM 调用。
    /// </summary>
    public sealed class StaExecutor : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private bool _disposed;

        public StaExecutor(string name = "SwCadSTA")
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = name
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>该执行器后台线程的托管线程 ID。</summary>
        public int ThreadId => _thread.ManagedThreadId;

        private void RunLoop()
        {
            Log.Info("Sta", $"STA 线程已启动（T{_thread.ManagedThreadId}）");
            try
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Sta", "STA 任务抛出未处理异常", ex);
                    }
                }
            }
            finally
            {
                Log.Info("Sta", "STA 线程已退出");
            }
        }

        /// <summary>在 STA 线程上同步执行有返回值的工作。</summary>
        public T Run<T>(Func<T> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            // 已在执行器线程上（Cad 层内部嵌套封送）时直接执行，避免队列自死锁
            if (Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId)
            {
                return work();
            }
            var tcs = new TaskCompletionSource<T>();
            _queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task.GetAwaiter().GetResult();
        }

        /// <summary>在 STA 线程上同步执行无返回值的工作。</summary>
        public void Run(Action work)
        {
            Run<object>(() => { work(); return null; });
        }

        /// <summary>异步排队（调用方不阻塞），结果通过 Task 观察。</summary>
        public Task<T> RunAsync<T>(Func<T> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId)
            {
                try { return Task.FromResult(work()); }
                catch (Exception ex) { return Task.FromException<T>(ex); }
            }
            var tcs = new TaskCompletionSource<T>();
            _queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            try { _thread.Join(3000); } catch { /* 忽略 */ }
            _queue.Dispose();
        }
    }
}
