using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;

namespace RhinoMCP.Tools.Model
{
    public enum JKind : byte
    {
        Add = 1, Delete = 2, Replace = 3, Modify = 4, Undelete = 5,
        LayerAdded = 10, LayerDeleted = 11, LayerModified = 12, LayerUndeleted = 13
    }

    public struct JEntry
    {
        public long Token; public JKind Kind; public Guid Id; public int Layer; public bool Agent; public long Ms;
    }

    /// <summary>
    /// Document change journal fed by RhinoDoc events.
    ///
    /// Hot reload: the tools DLL is reloaded without unloading, so the events of an older DLL keep firing.
    /// The journal therefore lives in AppDomain data as plain System types (a ring buffer in primitive arrays, so
    /// the newest DLL can adopt the old DLL's data and the token never restarts), together with an "uninstall"
    /// Action. EnsureInstalled() of a newer DLL runs the stored uninstall (unsubscribing the OLD delegates) and
    /// subscribes its own. Each handler also carries a generation number and returns at once when it is no longer
    /// the current one, so even a failed unsubscribe cannot double count.
    ///
    /// Actor: no Core hook exists. Handlers run synchronously on the UI thread, inside ToolRegistry.Execute when an
    /// MCP tool is running, so the actor is "agent" iff a ToolRegistry frame is on the stack (run_command, run_python
    /// and other tools are inside Execute too). Everything else is "user". Request to Core: expose a static
    /// "agent call in progress" flag so this stack walk can go away.
    /// </summary>
    public static class ChangeJournal
    {
        const string DataKey = "RhinoMCP.ChangeJournal.v1";
        const string UninstallKey = "RhinoMCP.ChangeJournal.uninstall";
        public const int Capacity = 131072;

        // meta slots
        const int MTok = 0, MTotal = 1, MEpoch = 2, MGen = 3, MFloor = 4, MErrors = 5;

        static long[] _meta; static long[] _tok; static Guid[] _ids; static int[] _layer; static byte[] _kind; static byte[] _agent; static long[] _ms;
        static object _lock = new object();
        static int _myGen;
        static bool _installed;
        static string _installError;
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly long BootMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;

        // handler delegates kept so the uninstall action can unsubscribe exactly them
        static EventHandler<RhinoObjectEventArgs> _hAdd, _hDel, _hUndel;
        static EventHandler<RhinoReplaceObjectEventArgs> _hRep;
        static EventHandler<RhinoModifyObjectAttributesEventArgs> _hMod;
        static EventHandler<LayerTableEventArgs> _hLay;

        public static bool Installed { get { return _installed; } }
        public static string InstallError { get { return _installError; } }
        public static int Generation { get { return _myGen; } }

        /// <summary>Idempotent. Safe to call from tool constructors and Execute.</summary>
        public static void EnsureInstalled()
        {
            if (_installed && _myGen != 0 && _meta != null && Interlocked_Read(_meta, MGen) == _myGen) return;
            try { Install(); }
            catch (Exception ex) { _installError = ex.GetType().Name + ": " + ex.Message; _installed = false; }
        }

        static long Interlocked_Read(long[] a, int i) { lock (a) return a[i]; }

        static void Install()
        {
            // Adopt (or create) the shared storage.
            object[] shared = AppDomain.CurrentDomain.GetData(DataKey) as object[];
            if (shared == null || shared.Length != 7)
            {
                shared = new object[] { new long[8], new long[Capacity], new Guid[Capacity], new int[Capacity], new byte[Capacity], new byte[Capacity], new long[Capacity] };
                AppDomain.CurrentDomain.SetData(DataKey, shared);
            }
            _meta = (long[])shared[0]; _tok = (long[])shared[1]; _ids = (Guid[])shared[2]; _layer = (int[])shared[3];
            _kind = (byte[])shared[4]; _agent = (byte[])shared[5]; _ms = (long[])shared[6];
            _lock = _meta;

            // Run the previous installation's uninstall (it unsubscribes the old DLL's delegates).
            var prev = AppDomain.CurrentDomain.GetData(UninstallKey) as Action;
            if (prev != null) { try { prev(); } catch (Exception) { } }
            AppDomain.CurrentDomain.SetData(UninstallKey, null);

            int gen;
            lock (_lock) { _meta[MGen]++; gen = (int)_meta[MGen]; }
            _myGen = gen;

            _hAdd = delegate (object s, RhinoObjectEventArgs e) { OnObj(gen, JKind.Add, e); };
            _hDel = delegate (object s, RhinoObjectEventArgs e) { OnObj(gen, JKind.Delete, e); };
            _hUndel = delegate (object s, RhinoObjectEventArgs e) { OnObj(gen, JKind.Undelete, e); };
            _hRep = delegate (object s, RhinoReplaceObjectEventArgs e) { OnReplace(gen, e); };
            _hMod = delegate (object s, RhinoModifyObjectAttributesEventArgs e) { OnModify(gen, e); };
            _hLay = delegate (object s, LayerTableEventArgs e) { OnLayer(gen, e); };
            RhinoDoc.AddRhinoObject += _hAdd;
            RhinoDoc.DeleteRhinoObject += _hDel;
            RhinoDoc.UndeleteRhinoObject += _hUndel;
            RhinoDoc.ReplaceRhinoObject += _hRep;
            RhinoDoc.ModifyObjectAttributes += _hMod;
            RhinoDoc.LayerTableEvent += _hLay;

            // Capture the delegates in locals so the Action does not depend on later statics.
            var a1 = _hAdd; var a2 = _hDel; var a3 = _hUndel; var a4 = _hRep; var a5 = _hMod; var a6 = _hLay;
            Action uninstall = delegate
            {
                RhinoDoc.AddRhinoObject -= a1;
                RhinoDoc.DeleteRhinoObject -= a2;
                RhinoDoc.UndeleteRhinoObject -= a3;
                RhinoDoc.ReplaceRhinoObject -= a4;
                RhinoDoc.ModifyObjectAttributes -= a5;
                RhinoDoc.LayerTableEvent -= a6;
            };
            AppDomain.CurrentDomain.SetData(UninstallKey, uninstall);
            _installed = true; _installError = null;
        }

        // ---------------------------------------------------------------- handlers (must never throw)

        static bool Current(int gen) { return _meta != null && _meta[MGen] == gen; }

        static bool OurDoc(RhinoObject o)
        {
            try
            {
                var active = RhinoDoc.ActiveDoc;
                return o != null && (active == null || o.Document == active);
            }
            catch (Exception) { return true; }
        }

        static void OnObj(int gen, JKind kind, RhinoObjectEventArgs e)
        {
            try
            {
                if (!Current(gen)) return;
                var o = e.TheObject;
                if (!OurDoc(o)) return;
                Record(kind, e.ObjectId, o.Attributes.LayerIndex, true);
            }
            catch (Exception) { Fault(); }
        }

        static void OnReplace(int gen, RhinoReplaceObjectEventArgs e)
        {
            try
            {
                if (!Current(gen)) return;
                var n = e.NewRhinoObject;
                if (!OurDoc(n)) return;
                Record(JKind.Replace, n.Id, n.Attributes.LayerIndex, true);
            }
            catch (Exception) { Fault(); }
        }

        static void OnModify(int gen, RhinoModifyObjectAttributesEventArgs e)
        {
            try
            {
                if (!Current(gen)) return;
                var o = e.RhinoObject;
                if (!OurDoc(o)) return;
                Record(JKind.Modify, o.Id, e.NewAttributes != null ? e.NewAttributes.LayerIndex : o.Attributes.LayerIndex, false);
            }
            catch (Exception) { Fault(); }
        }

        static void OnLayer(int gen, LayerTableEventArgs e)
        {
            try
            {
                if (!Current(gen)) return;
                JKind k;
                switch (e.EventType.ToString())
                {
                    case "Added": k = JKind.LayerAdded; break;
                    case "Deleted": k = JKind.LayerDeleted; break;
                    case "Undeleted": k = JKind.LayerUndeleted; break;
                    case "Modified": k = JKind.LayerModified; break;
                    default: return; // Sorted / Current are noise
                }
                Record(k, Guid.Empty, e.LayerIndex, false);
            }
            catch (Exception) { Fault(); }
        }

        static void Fault() { try { lock (_lock) _meta[MErrors]++; } catch (Exception) { } }

        static void Record(JKind kind, Guid id, int layer, bool bumpEpoch)
        {
            bool agent = IsAgentNow();
            long ms = BootMs + Clock.ElapsedMilliseconds;
            lock (_lock)
            {
                long tok = ++_meta[MTok];
                int i = (int)(_meta[MTotal] % Capacity);
                _meta[MTotal]++;
                _tok[i] = tok; _ids[i] = id; _layer[i] = layer; _kind[i] = (byte)kind; _agent[i] = agent ? (byte)1 : (byte)0; _ms[i] = ms;
                if (bumpEpoch && kind != JKind.Modify) _meta[MEpoch]++;
            }
        }

        // Core >= 0.2.0 exposes ToolRegistry.InToolCall (set around tool.Execute). Resolved once by reflection so that this
        // tools dll still works on an older Core, where we fall back to scanning the stack for a ToolRegistry frame.
        static System.Reflection.PropertyInfo _inToolCall;
        static bool _inToolCallResolved;

        static bool IsAgentNow()
        {
            try
            {
                if (!_inToolCallResolved)
                {
                    _inToolCallResolved = true;
                    var rt = typeof(ITool).Assembly.GetType("RhinoMCP.ToolRegistry");
                    var pi = rt == null ? null : rt.GetProperty("InToolCall", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (pi != null && pi.PropertyType == typeof(bool)) _inToolCall = pi;
                }
                if (_inToolCall != null) return (bool)_inToolCall.GetValue(null, null);
            }
            catch (Exception) { }
            try
            {
                var st = new StackTrace(2, false);
                int n = st.FrameCount;
                for (int i = 0; i < n; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    var t = m == null ? null : m.DeclaringType;
                    if (t != null && t.FullName == "RhinoMCP.ToolRegistry") return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // ---------------------------------------------------------------- read API (UI thread)

        public static long Token { get { EnsureInstalled(); return _meta == null ? 0 : Interlocked_Read(_meta, MTok); } }
        public static long Epoch { get { return _meta == null ? -1 : Interlocked_Read(_meta, MEpoch); } }
        public static long Errors { get { return _meta == null ? 0 : Interlocked_Read(_meta, MErrors); } }
        public static long Floor { get { return _meta == null ? 0 : Interlocked_Read(_meta, MFloor); } }

        /// <summary>Oldest token still retained (events with token &gt;= this are complete).</summary>
        public static long OldestToken()
        {
            lock (_lock)
            {
                long total = _meta[MTotal];
                if (total == 0) return _meta[MTok] + 1;
                long first = total > Capacity ? total - Capacity : 0;
                return Math.Max(_tok[(int)(first % Capacity)], _meta[MFloor] + 1);
            }
        }

        /// <summary>Entries with token &gt; since, oldest first. <paramref name="truncated"/> is true when older events were overwritten.</summary>
        public static List<JEntry> Since(long since, out bool truncated)
        {
            EnsureInstalled();
            var res = new List<JEntry>();
            truncated = false;
            lock (_lock)
            {
                long total = _meta[MTotal];
                long first = total > Capacity ? total - Capacity : 0;
                long floor = Math.Max(since, _meta[MFloor]);
                if (first > 0 && since < _tok[(int)(first % Capacity)] - 1) truncated = true;
                for (long n = total - 1; n >= first; n--)
                {
                    int i = (int)(n % Capacity);
                    if (_tok[i] <= floor) break;
                    res.Add(new JEntry { Token = _tok[i], Kind = (JKind)_kind[i], Id = _ids[i], Layer = _layer[i], Agent = _agent[i] != 0, Ms = _ms[i] });
                }
            }
            res.Reverse();
            return res;
        }

        /// <summary>Forget everything up to now (the token itself keeps counting).</summary>
        public static long Reset()
        {
            EnsureInstalled();
            lock (_lock) { _meta[MFloor] = _meta[MTok]; _meta[MEpoch]++; return _meta[MTok]; }
        }

        public static string KindName(JKind k)
        {
            switch (k)
            {
                case JKind.Add: return "add";
                case JKind.Delete: return "delete";
                case JKind.Replace: return "replace";
                case JKind.Modify: return "modify";
                case JKind.Undelete: return "undelete";
                case JKind.LayerAdded: return "layer_added";
                case JKind.LayerDeleted: return "layer_deleted";
                case JKind.LayerModified: return "layer_modified";
                case JKind.LayerUndeleted: return "layer_undeleted";
            }
            return k.ToString();
        }
    }
}
