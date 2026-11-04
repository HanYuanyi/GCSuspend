using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GCSuspend
{
    /// <summary>
    /// Suspends the managed (Boehm) garbage collector to eliminate the periodic
    /// stop-the-world pauses that Unity 5.6 cannot avoid.
    ///
    /// How it works
    /// ------------
    /// Koikatsu runs on Unity 5.6 + Mono (Boehm GC), a non-generational,
    /// fully blocking collector. This plugin keeps the GC suspend counter
    /// inside mono.dll above zero, which makes the collector skip its automatic
    /// runs. The counter is a plain int; incrementing it disables collection,
    /// decrementing it re-enables collection once it reaches zero.
    ///
    /// The counter address is located at runtime by scanning the .text section
    /// for a byte signature, so no hardcoded offsets are required. If the
    /// signature cannot be found, the plugin falls back to monoPatcher.dll.
    ///
    /// Safety valve
    /// ------------
    /// While suspended, the managed heap is sampled every N seconds. If it
    /// exceeds the configured limit, the plugin re-enables the collector,
    /// performs a single collection, then suspends it again. An explicit
    /// Collect() has no effect while the collector is suspended, so the
    /// counter has to be cleared first.
    ///
    /// Scope
    /// -----
    /// This plugin only affects the managed heap. Native Unity asset lifetime
    /// is out of scope and is left to whatever handles it in your setup.
    /// </summary>
    [BepInPlugin(Guid, "GC Suspend", Version)]
    [BepInIncompatibility("bucky.kk.gcsuspend.cn")]
    [BepInProcess("Koikatu.exe")]
    [BepInProcess("CharaStudio.exe")]
    [BepInProcess("KoikatuVR.exe")]
    [BepInProcess("Koikatsu Party.exe")]
    [BepInProcess("Koikatsu Party VR.exe")]
    public class GCSuspendPlugin : BaseUnityPlugin
    {
        public const string Guid = "bucky.kk.gcsuspend";
        public const string Version = "1.0.0";

        private const string HSceneName = "HProc";

        // ---------------- Native interop ----------------

        private delegate bool GcInitDelegate();

        private delegate bool GcSetStatusDelegate(bool enable);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX2
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
            public ulong PrivateUsage;
            public ulong PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        private static readonly uint MemCountersSize =
            (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX2));

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess,
            ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        private static GcSetStatusDelegate _gcSetStatus;

        /// <summary>
        /// Address of the GC suspend counter inside mono.dll (&gt;0 = collection suspended).
        /// Resolved from the actual instructions by LocateGcCounter(), never hardcoded.
        /// </summary>
        private static IntPtr _gcCounter;

        // ---------------- Configuration ----------------

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _onlyInH;
        private ConfigEntry<int> _heapLimitMb;
        private ConfigEntry<float> _checkInterval;
        private ConfigEntry<bool> _trimWorkingSet;
        private ConfigEntry<KeyCode> _forceCollectKey;
        private ConfigEntry<bool> _showPanel;

        // ---------------- State ----------------

        private bool _isStudio;
        private bool _suspended;
        private bool _patchFailed;
        private Coroutine _valve;

        /// <summary>Cached hotkey. Re-reading the ConfigEntry every frame is unnecessary.</summary>
        private KeyCode _keyCode = KeyCode.None;

        // ---------------- Monitor panel ----------------

        private bool _panelOn;                 // cached _showPanel value, avoids reading the ConfigEntry every frame
        private GUIStyle _style;
        private float _nextCache;
        private bool _cSuspended;
        private bool _cHProc;
        private string _cHeap = "?";
        private string _cProcMem = "?";
        private string _cProc = "?";
        private string _cCounter = "?";

        // ---------------- Diagnostic fields (read by GCSuspendDiag via reflection, do not rename) ----------------

        public static int DiagValveCount;
        public static long DiagProcBefore;
        public static long DiagProcAfter;
        public static long DiagHeapBefore;
        public static long DiagHeapAfter;
        public static float DiagUnloadSeconds;
        public static bool DiagUnloadCompleted;
        public static int DiagUnloadState;
        public static string DiagUnloadError = "";
        public static int DiagRounds;
        public static int DiagAttempt;
        public static int DiagF10Count;
        public static long DiagF10Before;
        public static long DiagF10After;
        public static int DiagF10Rounds;

        // ---------------- Lifecycle ----------------

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "Master switch\n"
                + "Turning this off immediately re-enables GC (no restart needed)");

            _onlyInH = Config.Bind("General", "Only in H scene", true,
                "Only suspend GC in H scenes (auto on enter, auto off on exit)\n"
                + "Turn this off = GC suspension applies globally\n"
                + "CharaStudio is unaffected and always suspends");

            _heapLimitMb = Config.Bind("General", "Heap limit (MB)", 16384,
                new ConfigDescription(
                    "Collect once the managed heap exceeds this size. Higher = fewer stutters",
                    new AcceptableValueRange<int>(256, 32768)));

            _checkInterval = Config.Bind("General", "Check interval (s)", 3f,
                new ConfigDescription(
                    "Seconds between heap size checks. Exceeding the limit triggers one collection",
                    new AcceptableValueRange<float>(1f, 60f)));

            _trimWorkingSet = Config.Bind("Advanced", "Also release native memory", false,
                "Also release native memory while cleaning up:\n"
                + "On = GC collect + trim working set (task manager memory)\n"
                + "Trimming the working set causes a long freeze (not recommended)");

            _forceCollectKey = Config.Bind("Advanced", "Force collect key", KeyCode.None,
                "Hotkey to force one manual collection\n"
                + "May need to be pressed repeatedly");

            _showPanel = Config.Bind("Display", "Show panel (F8)", false,
                "Press F8 to toggle\n"
                + "Shows the memory monitor in the top-left corner");

            _keyCode = _forceCollectKey.Value;
            _panelOn = _showPanel.Value;

            _enabled.SettingChanged += OnSettingChanged;
            _onlyInH.SettingChanged += OnSettingChanged;
            _heapLimitMb.SettingChanged += OnSettingChanged;
            _checkInterval.SettingChanged += OnSettingChanged;
            _forceCollectKey.SettingChanged += OnKeySettingChanged;
            _showPanel.SettingChanged += OnPanelSettingChanged;

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void Start()
        {
            if (!InitPatcher())
            {
                _patchFailed = true;
                Warn("Could not locate the GC counter and monoPatcher.dll is unavailable - "
                     + "this plugin will not work. (monoPatcher.dll is bundled with CharaAnime)");
                return;
            }

            _isStudio = IsStudioProcess();
            Reconcile();
        }

        private void Update()
        {
            // F8 toggles the monitor panel. Writing the config entry keeps F1 in sync and persists to cfg.
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _panelOn = !_panelOn;
                _showPanel.Value = _panelOn;
            }

            // Only sample when the panel is visible, refreshed every 0.5 s (zero cost while hidden).
            if (_panelOn && Time.unscaledTime >= _nextCache)
            {
                _nextCache = Time.unscaledTime + 0.5f;
                _cSuspended = _suspended;
                _cHProc = IsHSceneLoaded();
                _cHeap = HeapMb();
                _cProcMem = ProcMemMb();
                _cProc = SafeProcName();
                _cCounter = GcCounterText();
            }

            // With no hotkey bound this is a single field comparison per frame.
            if (_patchFailed || _keyCode == KeyCode.None) return;
            if (Input.GetKeyDown(_keyCode)) ForceCollectOnce();
        }

        private void OnGUI()
        {
            if (!_panelOn) return;

            try
            {
                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label);
                    _style.fontSize = 15;
                    _style.normal.textColor = Color.white;
                }

                GUI.Box(new Rect(10f, 10f, 400f, 132f), "");
                GUI.Label(new Rect(20f, 14f, 380f, 22f), "GCSuspend monitor    [F8 to hide]", _style);

                // green = GC collection disabled (smooth); red = GC collection enabled (stutters)
                _style.normal.textColor = _cSuspended
                    ? new Color(0.5f, 1f, 0.5f)
                    : new Color(1f, 0.45f, 0.45f);
                GUI.Label(new Rect(20f, 38f, 380f, 22f),
                    _cSuspended ? "On: yes (GC collection disabled)" : "Off: yes (GC collection enabled)", _style);

                _style.normal.textColor = Color.white;
                GUI.Label(new Rect(20f, 62f, 380f, 22f),
                    "Managed heap: " + _cHeap + " MB    GC counter: " + _cCounter, _style);
                GUI.Label(new Rect(20f, 86f, 380f, 22f),
                    "Process memory: " + _cProcMem + " MB (same as task manager)", _style);
                GUI.Label(new Rect(20f, 110f, 380f, 22f),
                    "H scene: " + (_cHProc ? "active" : "inactive") + "    Process: " + _cProc, _style);
            }
            catch { }
        }

        private void OnDestroy()
        {
            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                _enabled.SettingChanged -= OnSettingChanged;
                _onlyInH.SettingChanged -= OnSettingChanged;
                _heapLimitMb.SettingChanged -= OnSettingChanged;
                _checkInterval.SettingChanged -= OnSettingChanged;
                _forceCollectKey.SettingChanged -= OnKeySettingChanged;
                _showPanel.SettingChanged -= OnPanelSettingChanged;
            }
            catch { }

            StopValve();

            // Always restore GC on unload / quit. Not configurable on purpose:
            // otherwise a hot reload could leave GC suspended forever.
            if (_suspended) Resume();
        }

        // ---------------- Events ----------------

        private void OnSettingChanged(object sender, EventArgs e)
        {
            if (!_patchFailed) Reconcile();
        }

        private void OnKeySettingChanged(object sender, EventArgs e)
        {
            _keyCode = _forceCollectKey.Value;
        }

        private void OnPanelSettingChanged(object sender, EventArgs e)
        {
            _panelOn = _showPanel.Value;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_patchFailed || !_enabled.Value) return;
            if (scene.name == HSceneName) Reconcile();
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (_patchFailed || !_enabled.Value) return;
            if (scene.name == HSceneName) Reconcile();
        }

        /// <summary>Aligns the desired suspend state with the current one. Idempotent.</summary>
        private void Reconcile()
        {
            if (!_enabled.Value)
            {
                Resume();
                return;
            }

            bool shouldSuspend = _isStudio || !_onlyInH.Value || IsHSceneLoaded();

            if (shouldSuspend && !_suspended) Suspend();
            else if (!shouldSuspend && _suspended) Resume();
        }

        // ---------------- Core ----------------

        /// <summary>
        /// Initializes either path; both are optional and either one is enough:
        ///   1. monoPatcher.dll exports (legacy path - its internal state flag can lie)
        ///   2. The GC suspend counter inside mono.dll (preferred - reflects real state)
        /// Path 2 is used when available; path 1 is only a fallback.
        /// </summary>
        private bool InitPatcher()
        {
            bool apiOk = TryLoadPatcher();
            LocateGcCounter();

            // Read-only diagnostic: reports which path was taken, useful for bug reports.
            if (_gcCounter != IntPtr.Zero)
            {
                try
                {
                    IntPtr mono = GetModuleHandle("mono.dll");
                    Logger.LogInfo("[GCSuspend] GC counter located at mono+0x"
                                   + (_gcCounter.ToInt64() - mono.ToInt64()).ToString("X"));
                }
                catch { }
            }
            else
            {
                Warn("Could not locate the GC counter (signature not found), falling back to monoPatcher.");
            }

            return apiOk || _gcCounter != IntPtr.Zero;
        }

        private bool TryLoadPatcher()
        {
            try
            {
                string path = Path.Combine(Paths.PluginPath, "monoPatcher.dll");
                if (!File.Exists(path)) return false;

                IntPtr h = LoadLibrary(path);
                if (h == IntPtr.Zero) return false;

                IntPtr pInit = GetProcAddress(h, "monoPatchInit");
                IntPtr pSet = GetProcAddress(h, "monoSetGCStatus");
                if (pInit == IntPtr.Zero || pSet == IntPtr.Zero) return false;

                var gcInit = (GcInitDelegate)Marshal.GetDelegateForFunctionPointer(
                    pInit, typeof(GcInitDelegate));
                _gcSetStatus = (GcSetStatusDelegate)Marshal.GetDelegateForFunctionPointer(
                    pSet, typeof(GcSetStatusDelegate));

                return gcInit();
            }
            catch
            {
                return false;
            }
        }

        // ---------------- GC suspend counter (core) ----------------

        /// <summary>
        /// Locates the GC suspend counter inside mono.dll.
        ///
        /// Why not just use monoPatcher
        /// ----------------------------
        /// monoSetGCStatus() keeps an internal "already applied" flag, and
        /// monoPatchInit() sets that flag to 1 unconditionally. Once the flag
        /// is out of sync with the real state, later calls go wrong:
        ///   - enabling gets skipped because the flag claims it is already on
        ///   - disabling still calls GC_disable, so the counter gains an extra +1
        /// The counter in mono.dll is inc/dec based, not a boolean, so an extra
        /// +1 can never be removed and collection stays suspended until the
        /// process restarts. Writing the counter directly avoids the flag.
        ///
        /// How the counter is found
        /// ------------------------
        ///   1. GetModuleHandle("mono.dll"), read the PE section table to get
        ///      the .text RVA and size
        ///   2. Copy only .text into a managed array and search for
        ///      `48 83 EC 28 48 8D 0D`
        ///      (= sub rsp,0x28 ; lea rcx,[rip+disp32], the standard prologue
        ///      of GC_enable / GC_disable)
        ///   3. Within 40 bytes of that prologue, find `FF 0D` (dec) or
        ///      `FF 05` (inc)
        ///   4. Compute the counter address from the instruction displacement
        ///   5. Both functions must resolve to the same address, otherwise the
        ///      result is discarded
        ///
        /// Verified layouts (same signature, different builds):
        ///   Koikatsu / CharaStudio / Koikatsu Party -> function 0x157308, counter 0x266BF4
        ///   KoikatuVR / Koikatsu Party VR           -> function 0x15730C, counter 0x266BF4
        ///   Community debug mono (2,855,936 bytes)  -> function 0x1A4414, counter 0x2AD57C
        /// Three different layouts, the signature hits exactly once in each.
        /// </summary>
        private static void LocateGcCounter()
        {
            try
            {
                IntPtr mono = GetModuleHandle("mono.dll");
                if (mono == IntPtr.Zero) return;

                // Scan .text only: it is always committed and readable, unlike
                // the unmapped gaps between sections inside SizeOfImage.
                int textRva, textSize;
                if (!TextSection(mono, out textRva, out textSize)) return;
                if (textSize < 0x10000 || textSize > 0x8000000) return;

                byte[] text = new byte[textSize];
                Marshal.Copy(Ptr(mono, textRva), text, 0, textSize);

                IntPtr fromDec = FindCounter(mono, text, textRva, SigDec);
                IntPtr fromInc = FindCounter(mono, text, textRva, SigInc);

                // Both directions must resolve to the same address; otherwise we
                // matched an unrelated function and discard the result.
                if (fromDec != IntPtr.Zero && fromDec == fromInc)
                    _gcCounter = fromDec;
            }
            catch
            {
                _gcCounter = IntPtr.Zero;
            }
        }

        // Shared prologue of GC_enable / GC_disable: sub rsp,0x28 ; lea rcx,[rip+disp32]
        private static readonly byte[] SigFuncHead = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8D, 0x0D };
        // Counter decrement (GC_enable) / increment (GC_disable)
        private static readonly byte[] SigDec = { 0xFF, 0x0D };
        private static readonly byte[] SigInc = { 0xFF, 0x05 };

        /// <summary>
        /// Reads the PE section table and returns the RVA and size of .text.
        /// The first 4 bytes of the name equal ".tex" (little-endian 0x7865742E).
        /// </summary>
        private static bool TextSection(IntPtr module, out int rva, out int size)
        {
            rva = 0; size = 0;
            try
            {
                int pe = Marshal.ReadInt32(Ptr(module, 0x3C));
                if (pe <= 0 || pe > 0x1000) return false;
                if (Marshal.ReadInt32(Ptr(module, pe)) != 0x00004550) return false;   // "PE\0\0"

                int coff = pe + 4;
                int nsec = Marshal.ReadInt16(Ptr(module, coff + 2)) & 0xFFFF;
                int optSize = Marshal.ReadInt16(Ptr(module, coff + 16)) & 0xFFFF;
                int table = pe + 24 + optSize;

                for (int i = 0; i < nsec; i++)
                {
                    int s = table + i * 40;
                    if (Marshal.ReadInt32(Ptr(module, s)) != 0x7865742E) continue;    // ".tex"
                    int vsize = Marshal.ReadInt32(Ptr(module, s + 8));
                    int vaddr = Marshal.ReadInt32(Ptr(module, s + 12));
                    if (vsize <= 0) continue;
                    rva = vaddr;
                    size = vsize;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Finds the single spot where the function prologue is followed by the
        /// given inc/dec instruction, and returns the counter address.
        /// text holds the bytes of .text, textRva is its starting RVA in the module.
        /// Returns IntPtr.Zero when nothing matches.
        /// </summary>
        private static IntPtr FindCounter(IntPtr baseAddr, byte[] text, int textRva, byte[] op)
        {
            int i = 0;
            while (i < text.Length)
            {
                int func = IndexOf(text, SigFuncHead, i, text.Length);
                if (func < 0) return IntPtr.Zero;

                int ins = IndexOf(text, op, func, func + 40);
                if (ins >= 0)
                {
                    int disp = BitConverter.ToInt32(text, ins + 2);
                    int insRva = textRva + ins;                 // RVA of the instruction inside the module
                    // counter = address of the next instruction + disp32
                    return Ptr(Ptr(baseAddr, insRva + 6), disp);
                }

                i = func + 1;
            }
            return IntPtr.Zero;
        }

        /// <summary>Searches hay[start..end) for needle, returns the index or -1.</summary>
        private static int IndexOf(byte[] hay, byte[] needle, int start, int end)
        {
            int limit = Math.Min(end, hay.Length) - needle.Length;
            byte first = needle[0];
            for (int i = Math.Max(0, start); i <= limit; i++)
            {
                if (hay[i] != first) continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>net35 has no IntPtr arithmetic.</summary>
        private static IntPtr Ptr(IntPtr p, int offset)
        {
            return new IntPtr(p.ToInt64() + offset);
        }

        /// <summary>
        /// Suspends or resumes GC. Writes the counter directly when available,
        /// otherwise falls back to monoPatcher.
        /// </summary>
        private static void SetGcPaused(bool paused)
        {
            if (_gcCounter != IntPtr.Zero)
            {
                try
                {
                    Marshal.WriteInt32(_gcCounter, paused ? 1 : 0);
                    return;
                }
                catch { }
            }

            if (_gcSetStatus != null)
            {
                try { _gcSetStatus(!paused); }
                catch { }
            }
        }

        /// <summary>
        /// Reads the real state: counter &gt; 0 means collection is suspended.
        /// Assumes suspended when the counter could not be located.
        /// </summary>
        private static bool IsGcPaused()
        {
            if (_gcCounter == IntPtr.Zero) return true;   // without the counter we have to trust our own state
            try { return Marshal.ReadInt32(_gcCounter) > 0; }
            catch { return true; }
        }

        private void Suspend()
        {
            if (_suspended) return;

            SetGcPaused(true);
            if (!IsGcPaused()) return;   // do not start the watchdog unless suspension actually took effect

            _suspended = true;

            if (_valve != null) return;
            try { _valve = StartCoroutine(ValveLoop()); }
            catch { _valve = null; }
        }

        private void Resume()
        {
            StopValve();

            if (!_suspended) return;

            SetGcPaused(false);
            _suspended = false;
        }

        private void StopValve()
        {
            if (_valve == null) return;
            try { StopCoroutine(_valve); }
            catch { }
            _valve = null;
        }

        /// <summary>
        /// Performs one manual collection: re-enable GC, collect once, then
        /// restore the previous state.
        /// </summary>
        private void ForceCollectOnce()
        {
            bool wasSuspended = _suspended;
            long before = ProcessMemoryBytes();

            try
            {
                if (wasSuspended) SetGcPaused(false);
                ForceCollect();
            }
            catch { }
            finally
            {
                if (wasSuspended) SetGcPaused(true);
            }

            DiagF10Before = before;
            DiagF10After = ProcessMemoryBytes();
            DiagF10Rounds = 1;
            DiagF10Count++;
        }

        /// <summary>
        /// Performs a single collection.
        /// Uses the parameterized overload: some plugins patch the parameterless
        /// GC.Collect() and swallow the call, while the overload still runs.
        /// </summary>
        private static void ForceCollect()
        {
            try { GC.Collect(GC.MaxGeneration); }
            catch { }
        }

        /// <summary>
        /// Safety valve: while suspended, samples the managed heap periodically.
        /// When it exceeds the limit, one collection is performed and collection
        /// is suspended again. The coroutine only exists while suspended.
        /// </summary>
        private IEnumerator ValveLoop()
        {
            WaitForSeconds wait = null;
            float lastInterval = -1f;

            while (_suspended)
            {
                // Reuse WaitForSeconds so each cycle does not allocate a new one.
                float interval = Mathf.Max(1f, _checkInterval.Value);
                if (wait == null || !Mathf.Approximately(interval, lastInterval))
                {
                    wait = new WaitForSeconds(interval);
                    lastInterval = interval;
                }
                yield return wait;

                if (!_suspended) break;

                // Re-assert suspension: one idempotent memory write. If something
                // else changed the counter in the meantime, this pulls it back to 1.
                SetGcPaused(true);

                long limit = (long)_heapLimitMb.Value * 1024L * 1024L;
                long heap;
                try { heap = GC.GetTotalMemory(false); }
                catch { continue; }

                if (heap <= limit) continue;

                DiagProcBefore = ProcessMemoryBytes();
                try { DiagHeapBefore = GC.GetTotalMemory(false); }
                catch { DiagHeapBefore = -1L; }

                try
                {
                    SetGcPaused(false);
                    ForceCollect();
                }
                catch { }
                DiagRounds = 1;

                // Only runs when "Also release native memory" is enabled; off by default.
                if (_trimWorkingSet.Value)
                {
                    AsyncOperation op = null;
                    DiagUnloadState = 0;
                    DiagUnloadError = "";
                    try
                    {
                        op = Resources.UnloadUnusedAssets();
                        if (op == null) DiagUnloadError = "returned null (no exception)";
                    }
                    catch (Exception ex)
                    {
                        DiagUnloadError = ex.GetType().Name + ": " + ex.Message;
                    }

                    float t0 = Time.realtimeSinceStartup;
                    float deadline = t0 + 10f;
                    while (op != null && !op.isDone && Time.realtimeSinceStartup < deadline)
                        yield return null;

                    DiagUnloadSeconds = Time.realtimeSinceStartup - t0;
                    DiagUnloadCompleted = op == null || op.isDone;
                    DiagUnloadState = op == null ? 0 : (op.isDone ? 1 : 2);
                }
                else
                {
                    DiagUnloadState = 3;   // 3 = not enabled
                }

                if (_suspended) SetGcPaused(true);

                if (_trimWorkingSet.Value) TrimWorkingSet();

                yield return new WaitForSeconds(0.5f);

                DiagProcAfter = ProcessMemoryBytes();
                try { DiagHeapAfter = GC.GetTotalMemory(false); }
                catch { DiagHeapAfter = -1L; }

                DiagValveCount++;
            }
        }

        // ---------------- Utilities ----------------

        private static void TrimWorkingSet()
        {
            try { EmptyWorkingSet(GetCurrentProcess()); }
            catch { }
        }

        private static long ProcessMemoryBytes()
        {
            try
            {
                var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = MemCountersSize };
                if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb))
                    return -1L;
                return (long)counters.PrivateWorkingSetSize;
            }
            catch
            {
                return -1L;
            }
        }

        private static bool IsStudioProcess()
        {
            try
            {
                return string.Equals(Process.GetCurrentProcess().ProcessName, "CharaStudio",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------------- Panel sampling (only called while the panel is visible) ----------------

        private static string HeapMb()
        {
            try { return (GC.GetTotalMemory(false) / 1048576L).ToString(); }
            catch { return "?"; }
        }

        /// <summary>Real GC counter (&gt;0 = suspended). Only read when the panel refreshes, never per frame.</summary>
        private static string GcCounterText()
        {
            if (_gcCounter == IntPtr.Zero) return "?";
            try { return Marshal.ReadInt32(_gcCounter).ToString(); }
            catch { return "?"; }
        }

        private static string ProcMemMb()
        {
            long bytes = ProcessMemoryBytes();
            return bytes < 0 ? "?" : (bytes / 1048576L).ToString();
        }

        private static string SafeProcName()
        {
            try { return Process.GetCurrentProcess().ProcessName; }
            catch { return "?"; }
        }

        private static bool IsHSceneLoaded()
        {
            try { return SceneManager.GetSceneByName(HSceneName).isLoaded; }
            catch { return false; }
        }

        private void Warn(string msg)
        {
            try { Logger.LogWarning("[GCSuspend] " + msg); }
            catch { }
        }
    }
}
