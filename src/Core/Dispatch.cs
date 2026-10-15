using System;
using System.Threading;
using System.Threading.Tasks;
using Rhino;

namespace RhinoMCP
{
    /// <summary>Marshals work from HTTP threads onto the Rhino UI thread.</summary>
    public static class UiDispatcher
    {
        public const int DefaultTimeoutMs = 60000;

        public static T Invoke<T>(Func<T> work, int timeoutMs = DefaultTimeoutMs)
        {
            var tcs = new TaskCompletionSource<T>();
            int state = 0; // 0 queued, 1 running, 2 abandoned
            Action act = delegate
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return; // timed out before start
                try { tcs.TrySetResult(work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            RhinoApp.InvokeOnUiThread(act);
            if (!tcs.Task.Wait(timeoutMs))
            {
                Interlocked.CompareExchange(ref state, 2, 0);
                throw new TimeoutException("UI thread did not finish within " + (timeoutMs / 1000) + " s");
            }
            try { return tcs.Task.Result; }
            catch (AggregateException ae) { throw ae.InnerException ?? ae; }
        }
    }

    /// <summary>Wraps a mutation in one undo record named "MCP: &lt;tool&gt;".</summary>
    public sealed class UndoScope : IDisposable
    {
        readonly RhinoDoc _doc;
        readonly uint _serial;
        public UndoScope(RhinoDoc doc, string name)
        {
            _doc = doc;
            _serial = doc.BeginUndoRecord("MCP: " + name);
        }
        public void Dispose()
        {
            if (_serial != 0) _doc.EndUndoRecord(_serial);
        }
    }

    /// <summary>Monotonic counter bumped after every mutating tool call; lets clients detect model changes.</summary>
    public static class StateToken
    {
        static long _value;
        public static long Current { get { return Interlocked.Read(ref _value); } }
        public static long Bump() { return Interlocked.Increment(ref _value); }
    }
}
