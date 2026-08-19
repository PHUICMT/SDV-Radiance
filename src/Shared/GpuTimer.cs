using System;
using System.Runtime.InteropServices;

namespace SDVRadiance
{
    /// <summary>
    /// What the graphics card spends on each part of this mod, as opposed to what we spend telling
    /// it what to draw.
    ///
    /// <para>
    /// <see cref="FrameCost"/> measures CPU submission, and says so honestly, but a floor is not an
    /// answer. Object shadows read there as a rounding error while actually costing 1.80 ms, because
    /// nine blurred copies of five hundred sprites is almost no submission and a great deal of fill.
    /// Every performance number this project has written down is a lower bound until this class
    /// exists, and two of them were wrong by more than an order of magnitude.
    /// </para>
    ///
    /// <para>
    /// The usual write-ups on GPU timing assume Direct3D. This game is MonoGame DesktopGL, so the
    /// process speaks OpenGL and there is no D3D device to ask - but SDL2 is already loaded and
    /// hands back real function pointers for the game's current context, which
    /// <c>radiance_gldiag</c> proved end to end before this was written.
    /// </para>
    ///
    /// <para>
    /// Three decisions here are not free choices, and reversing any of them breaks the measurement:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Timestamps, not GL_TIME_ELAPSED.</b> Only one elapsed-time query can be
    /// active at a time, and our parts nest: the grid rebuilds run inside the effect chain. A pair
    /// of <c>glQueryCounter</c> marks nests freely.</description></item>
    /// <item><description><b>Read late, never wait.</b> A result is not ready in the frame that asked
    /// for it, and blocking until it is measures the stall we caused rather than the work. Marks go
    /// into a ring and are collected three frames later, and a slot that is still not ready is
    /// dropped rather than waited for.</description></item>
    /// <item><description><b>One thread only.</b> GL calls are legal on the thread holding the
    /// context and nowhere else. The water mask has an async path, so the thread that initialised us
    /// is recorded and marks from any other thread are ignored rather than crashing the game.</description></item>
    /// </list>
    ///
    /// <para>
    /// Off by default. This reaches around MonoGame into the game's own GL context, and no diagnostic
    /// is worth a risk to somebody's game while they are not even asking a question.
    /// <c>radiance_gputime on</c> switches it on, and any GL failure switches it off permanently
    /// rather than repeating itself every frame.
    /// </para>
    /// </summary>
    internal static class GpuTimer
    {
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GL_GetProcAddress")]
        private static extern IntPtr SdlGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string proc);

        private delegate IntPtr GlGetStringDel(uint name);
        private delegate IntPtr GlGetStringiDel(uint name, uint index);
        private delegate void GlGetIntegervDel(uint pname, out int data);
        private delegate void GlGenQueriesDel(int n, [Out] uint[] ids);
        private delegate void GlDeleteQueriesDel(int n, [In] uint[] ids);
        private delegate void GlQueryCounterDel(uint id, uint target);
        private delegate void GlGetQueryObjectivDel(uint id, uint pname, out int p);
        private delegate void GlGetQueryObjectui64vDel(uint id, uint pname, out ulong p);

        private const uint GL_EXTENSIONS = 0x1F03;
        private const uint GL_NUM_EXTENSIONS = 0x821D;
        private const uint GL_TIMESTAMP = 0x8E28;
        private const uint GL_QUERY_RESULT = 0x8866, GL_QUERY_RESULT_AVAILABLE = 0x8867;

        private static GlGenQueriesDel? _genQueries;
        private static GlDeleteQueriesDel? _deleteQueries;
        private static GlQueryCounterDel? _queryCounter;
        private static GlGetQueryObjectivDel? _getQueryObjectiv;
        private static GlGetQueryObjectui64vDel? _getQueryObjectui64v;

        /// <summary>How many frames of marks are in flight. Results are collected from the oldest,
        /// three frames behind the one being written, so a slot is never read while it is being
        /// filled and the collection never has to wait.</summary>
        private const int Ring = 4;
        private const int Latency = 3;
        private const int PartCount = 10;   // must match FrameCost.Part

        /// <summary>Slots beyond the parts, one per pass of the effect chain.
        ///
        /// <para>
        /// The chain reads as ONE part because that is how it is entered, and that is enough to say
        /// it is the most expensive thing the mod does on the GPU - but not enough to do anything
        /// about it. The chain is six or seven full-screen passes in a row, and knowing only their
        /// sum, the obvious fixes (fuse two passes, drop one) are all guesses about which of the
        /// seven the milliseconds are in. Per-pass marks nest inside the chain's own pair, which is
        /// exactly what timestamps allow and elapsed-time queries would not.
        /// </para></summary>
        private const int StageCount = 11;   // must match RenderPipeline._stageNames
        private const int SlotCount = PartCount + StageCount;

        /// <summary>Query ids, laid out [slot][part * 2 + (0 = begin, 1 = end)].</summary>
        private static uint[][]? _ids;
        /// <summary>Which parts actually marked BOTH ends in a given slot. A part that did not run
        /// this frame, or returned early between its two marks, must not be read: its query objects
        /// still hold whatever the same slot recorded four frames ago.</summary>
        private static bool[][]? _begun;
        private static bool[][]? _ended;

        private static int _writeSlot;
        private static long _framesMarked;
        private static int _glThreadId;

        private static readonly double[] _lastFrameMs = new double[SlotCount];
        private static readonly bool[] _lastFrameValid = new bool[SlotCount];

        /// <summary>Requested by the user. Distinct from <see cref="Ready"/>: asked-for but broken
        /// is a state the report has to be able to describe.</summary>
        private static bool _wanted;
        private static bool _ready;
        private static string _status = "off";

        /// <summary>Set once when something goes wrong. Nothing re-arms it: a driver that failed a
        /// query call once will fail every frame, and a log line per frame is its own problem.</summary>
        private static bool _brokenForGood;

        internal static bool Ready => _ready;
        internal static string Status => _status;


        internal static void SetWanted(bool on)
        {
            _wanted = on;
            if (!on)
            {
                _ready = false;
                _status = "off";
                Array.Clear(_lastFrameValid, 0, SlotCount);
                // Clearing our own side is not enough: FrameCost keeps a 300-frame window, so a
                // report run straight after switching off would still print a full GPU column of
                // numbers from before, labelled as current. Caught by the third leg of the in-game
                // test, which exists to check that off returns the report to exactly what it was.
                FrameCost.ForgetGpu();
                // The per-pass window inside the chain keeps a GPU column of its own, and it is
                // the same mistake if it survives the switch: numbers from before, labelled now.
                RenderPipeline.Current?.ForgetStageGpu();
                return;
            }
            if (_brokenForGood)
            {
                _status = "unavailable on this machine: " + _status;
                return;
            }
            _status = "on, waiting for the first frame";
        }

        /// <summary>Resolve the entry points and build the query pool. Must run on the thread that
        /// holds the GL context, so it is called from the first mark of a frame rather than from
        /// mod startup, where there is no context yet.</summary>
        private static void Initialise()
        {
            // Switching off does not throw the pool away, so switching back on must not build a
            // second one: every on/off cycle would otherwise strand 80 GL query objects with no
            // owner. Re-arm and return.
            if (_ids != null)
            {
                _glThreadId = Environment.CurrentManagedThreadId;
                _writeSlot = 0;
                _framesMarked = 0;
                for (int s = 0; s < Ring; s++)
                {
                    Array.Clear(_begun![s], 0, SlotCount);
                    Array.Clear(_ended![s], 0, SlotCount);
                }
                _ready = true;
                _status = "on";
                return;
            }
            try
            {
                _genQueries = Load<GlGenQueriesDel>("glGenQueries");
                _deleteQueries = Load<GlDeleteQueriesDel>("glDeleteQueries");
                _queryCounter = Load<GlQueryCounterDel>("glQueryCounter");
                _getQueryObjectiv = Load<GlGetQueryObjectivDel>("glGetQueryObjectiv");
                _getQueryObjectui64v = Load<GlGetQueryObjectui64vDel>("glGetQueryObjectui64v");
                if (_genQueries == null || _queryCounter == null || _getQueryObjectiv == null
                    || _getQueryObjectui64v == null)
                {
                    Fail("the driver did not provide glQueryCounter and friends");
                    return;
                }
                if (!HasTimerExtension())
                {
                    Fail("this GL context does not advertise GL_ARB_timer_query");
                    return;
                }

                _ids = new uint[Ring][];
                _begun = new bool[Ring][];
                _ended = new bool[Ring][];
                for (int s = 0; s < Ring; s++)
                {
                    var ids = new uint[SlotCount * 2];
                    _genQueries(ids.Length, ids);
                    foreach (uint id in ids)
                    {
                        if (id != 0)
                            continue;
                        Fail("glGenQueries produced no ids");
                        return;
                    }
                    _ids[s] = ids;
                    _begun[s] = new bool[SlotCount];
                    _ended[s] = new bool[SlotCount];
                }

                _glThreadId = Environment.CurrentManagedThreadId;
                _writeSlot = 0;
                _framesMarked = 0;
                _ready = true;
                _status = "on";
            }
            catch (DllNotFoundException)
            {
                Fail("SDL2 is not loaded, so this is not a DesktopGL build");
            }
            catch (Exception ex)
            {
                Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool HasTimerExtension()
        {
            var getString = Load<GlGetStringDel>("glGetString");
            if (getString == null)
                return false;
            // A compatibility context answers GL_EXTENSIONS as one space-separated string; a core
            // context returns null there and requires the indexed form. Ask both ways rather than
            // assuming which kind MonoGame requested.
            IntPtr flatPtr = getString(GL_EXTENSIONS);
            string flat = flatPtr == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(flatPtr) ?? "");
            if (flat.Length > 0)
                return flat.Contains("GL_ARB_timer_query");

            var getStringi = Load<GlGetStringiDel>("glGetStringi");
            var getIntegerv = Load<GlGetIntegervDel>("glGetIntegerv");
            if (getStringi == null || getIntegerv == null)
                return false;
            getIntegerv(GL_NUM_EXTENSIONS, out int count);
            for (uint i = 0; i < (uint)count; i++)
            {
                IntPtr p = getStringi(GL_EXTENSIONS, i);
                if (p != IntPtr.Zero && Marshal.PtrToStringAnsi(p) == "GL_ARB_timer_query")
                    return true;
            }
            return false;
        }

        private static T? Load<T>(string name) where T : Delegate
        {
            IntPtr p = SdlGetProcAddress(name);
            return p == IntPtr.Zero ? null : (T)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        private static void Fail(string why)
        {
            _brokenForGood = true;
            _ready = false;
            _status = why;
        }

        /// <summary>True while it is safe to touch GL from here: switched on, initialised, and on
        /// the thread that owns the context.</summary>
        private static bool CanMark()
        {
            if (!_wanted || _brokenForGood)
                return false;
            if (!_ready)
            {
                Initialise();
                if (!_ready)
                    return false;
            }
            return Environment.CurrentManagedThreadId == _glThreadId;
        }

        internal static void MarkBegin(int part)
        {
            if (!CanMark())
                return;
            try
            {
                _queryCounter!(_ids![_writeSlot][part * 2], GL_TIMESTAMP);
                _begun![_writeSlot][part] = true;
            }
            catch (Exception ex) { Fail($"{ex.GetType().Name} marking a start: {ex.Message}"); }
        }

        internal static void MarkEnd(int part)
        {
            if (!CanMark() || !_begun![_writeSlot][part])
                return;
            try
            {
                _queryCounter!(_ids![_writeSlot][part * 2 + 1], GL_TIMESTAMP);
                _ended![_writeSlot][part] = true;
            }
            catch (Exception ex) { Fail($"{ex.GetType().Name} marking an end: {ex.Message}"); }
        }

        /// <summary>Collect the frame that finished <see cref="Latency"/> frames ago and move the
        /// write cursor on. Whatever it collected is readable through
        /// <see cref="TryTakeLastFrame"/> until the next call.</summary>
        internal static void NextFrame()
        {
            Array.Clear(_lastFrameValid, 0, SlotCount);
            if (!_ready || !_wanted || _brokenForGood)
                return;
            if (Environment.CurrentManagedThreadId != _glThreadId)
                return;

            _framesMarked++;
            if (_framesMarked > Latency)
            {
                int readSlot = (_writeSlot + Ring - Latency) % Ring;
                Collect(readSlot);
            }
            _writeSlot = (_writeSlot + 1) % Ring;
            Array.Clear(_begun![_writeSlot], 0, SlotCount);
            Array.Clear(_ended![_writeSlot], 0, SlotCount);
        }

        private static void Collect(int slot)
        {
            try
            {
                uint[] ids = _ids![slot];
                for (int part = 0; part < SlotCount; part++)
                {
                    if (!_begun![slot][part] || !_ended![slot][part])
                        continue;
                    uint endId = ids[part * 2 + 1];
                    _getQueryObjectiv!(endId, GL_QUERY_RESULT_AVAILABLE, out int available);
                    if (available == 0)
                        continue;   // still in flight: drop it rather than wait for it
                    _getQueryObjectui64v!(ids[part * 2], GL_QUERY_RESULT, out ulong startNs);
                    _getQueryObjectui64v!(endId, GL_QUERY_RESULT, out ulong endNs);
                    if (endNs < startNs)
                        continue;   // the GPU clock moved under us; there is no disjoint query in GL
                    double ms = (endNs - startNs) / 1_000_000.0;
                    if (ms > 250.0)
                        continue;   // a load screen or a driver reset, not a frame
                    _lastFrameMs[part] = ms;
                    _lastFrameValid[part] = true;
                }
            }
            catch (Exception ex) { Fail($"{ex.GetType().Name} collecting: {ex.Message}"); }
        }

        internal static bool TryTakeLastFrame(int part, out double milliseconds)
        {
            milliseconds = _lastFrameMs[part];
            return _lastFrameValid[part];
        }

        /// <summary>Start one pass of the effect chain. <paramref name="stage"/> indexes
        /// RenderPipeline's own stage-name table, not <see cref="FrameCost.Part"/>.</summary>
        internal static void MarkStageBegin(int stage)
        {
            if ((uint)stage < StageCount)
                MarkBegin(PartCount + stage);
        }

        internal static void MarkStageEnd(int stage)
        {
            if ((uint)stage < StageCount)
                MarkEnd(PartCount + stage);
        }

        internal static bool TryTakeLastStage(int stage, out double milliseconds)
        {
            milliseconds = 0;
            if ((uint)stage >= StageCount)
                return false;
            milliseconds = _lastFrameMs[PartCount + stage];
            return _lastFrameValid[PartCount + stage];
        }
    }
}
