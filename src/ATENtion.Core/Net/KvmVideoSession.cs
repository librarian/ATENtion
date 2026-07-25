using System;
using System.Collections.Generic;
using System.Threading;
using ATENtion.Core.Hid;
using ATENtion.Core.Protocol;
using ATENtion.Core.Video;

namespace ATENtion.Core.Net
{
    /// <summary>
    /// Carries a decoded frame and its changed regions to subscribers of
    /// <see cref="KvmVideoSession.FrameDecoded"/>.
    /// </summary>
    public sealed class FrameDecodedEventArgs : EventArgs
    {
        /// <summary>Creates the event payload for one decoded frame.</summary>
        /// <param name="frame">The framebuffer the decoder wrote into.</param>
        /// <param name="dirty">The regions changed by this frame.</param>
        public FrameDecodedEventArgs(FrameBuffer frame, IReadOnlyList<DirtyRect> dirty)
        {
            Frame = frame;
            Dirty = dirty;
        }

        /// <summary>The framebuffer holding the current decoded image.</summary>
        public FrameBuffer Frame { get; }
        /// <summary>The regions this frame changed, for an incremental on-screen blit.</summary>
        public IReadOnlyList<DirtyRect> Dirty { get; }
    }

    /// <summary>
    /// Drives a complete iKVM video session: it connects, runs the RFB handshake, and then
    /// pumps server messages on a background thread, decoding video and forwarding input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Owns the live connection to a single BMC for the duration of a console
    /// session. It decodes FramebufferUpdate (type 0) messages into an
    /// <see cref="AtenTileDecoder"/> and raises <see cref="FrameDecoded"/> for the UI, and it
    /// accepts keyboard, mouse, and power input and serialises it onto the same connection.
    /// </para>
    /// <para>
    /// OPERATION - Three background threads and three timers cooperate. The receive pump reads
    /// server messages in a loop. A dedicated input-sender thread drains a queue of keystrokes,
    /// clicks, and power commands, coalescing mouse moves to a paced trickle so that dragging
    /// the cursor cannot flood the BMC. A request timer, a keepalive timer, and a stall watchdog
    /// keep the stream flowing and detect a dead link. The request side keeps a bounded
    /// number of FramebufferUpdateRequests in flight; the default is strict request/response
    /// because ASPEED delta frames are baseline-dependent. The keepalive heartbeat is required
    /// for the BMC to keep servicing input. It also holds the socket open.
    /// </para>
    /// <para>
    /// DEPENDENCIES - A <see cref="KvmConnection"/> for transport and handshake, an
    /// <see cref="AtenTileDecoder"/> for video, and the <see cref="KeyboardEncoder"/>,
    /// <see cref="MouseEncoder"/>, and <see cref="PowerControl"/> encoders for input. The owning
    /// UI subscribes to <see cref="FrameDecoded"/>, <see cref="Faulted"/>, and
    /// <see cref="PrivilegeChanged"/>.
    /// </para>
    /// <para>
    /// RESTRICTIONS - All writes to the connection serialise on a single send lock, so no two
    /// threads write the stream at once. The pump must never block on the UI thread: the
    /// FrameDecoded handler is expected to snapshot and return so the pump keeps draining the
    /// socket. A blocked pump stops the BMC servicing input. <see cref="Open"/> must be
    /// called before <see cref="StartPump"/>. <see cref="Dispose"/> closes the transport before
    /// joining the threads.
    /// </para>
    /// <para>
    /// PROVENANCE - Mirrors the native receive pump iKVM64.dll FUN_180012cb0 and its
    /// FramebufferUpdate handler FUN_180013b00, the keepalive task (FUN_180013270), and the
    /// request cadence (runImage FUN_180011950 + updateImage FUN_180013060). VERIFIED LIVE: video, keyboard, mouse, and power all work
    /// against the target BMC.
    /// </para>
    /// </remarks>
    public sealed class KvmVideoSession : IDisposable
    {
        private readonly KvmConnection _connection;
        private readonly IRfbAuthenticator _authenticator;
        private readonly MouseEncoder _mouse = new MouseEncoder();
        private readonly KeyboardEncoder _keyboard = new KeyboardEncoder();
        private readonly object _sendLock = new object();
        private Thread _pumpThread;
        private Timer _requestTimer;
        private Timer _keepAliveTimer;
        private Timer _watchdogTimer;
        private volatile bool _running;
        private int _ticksSinceFull;

        // Stall-watchdog state. A drop is normally detected when the socket read throws (a clean
        // close or RST). But a SILENT stall, where the TCP connection stays open but the BMC stops
        // sending, leaves the pump thread blocked in ConsumeOne forever with no exception, so nothing
        // ever fires Faulted and the UI never reconnects. The watchdog covers that. It tracks the last
        // inbound message time and, after a soft window, sends a forced-full FBUR probe. A live BMC
        // always answers that probe, even on a static screen, which avoids false positives. If even
        // the probe goes unanswered, it raises Faulted so the UI can reconnect.
        private int _faultRaised;     // 0/1, Interlocked - Faulted fires at most once per session
        private int _probedThisStall; // 0/1 - send only one forced-full probe per soft-stall window

        /// <summary>Seconds of inbound silence before the watchdog sends one forced-full FBUR probe
        /// (a liveness check a healthy BMC always answers). 0 disables the probe stage.</summary>
        public int StallProbeSeconds { get; set; } = 4;

        /// <summary>Seconds of inbound silence before the watchdog declares the connection stalled
        /// and raises <see cref="Faulted"/> (drives the UI's auto-reconnect). Must be greater than
        /// <see cref="StallProbeSeconds"/> so the probe has time to be answered. 0 disables.</summary>
        public int StallTimeoutSeconds { get; set; } = 10;

        /// <summary>How long (in 1s timer ticks) without ANY full frame before the timer forces a
        /// non-incremental refresh. This is an adaptive backstop against drift/stale tiles: the
        /// counter resets whenever a full frame is requested for any reason (keyframe, resolution
        /// change, manual refresh), so a quiet, healthy session rarely pays the full-frame cost.
        /// The native viewer also periodically forces a full frame (updateImage's +0x5c flag).
        /// 0 disables.</summary>
        public int FullRefreshIntervalTicks { get; set; } = 5;

        /// <summary>Optional floor (ms) between incremental frame requests (video FPS cap). The earlier
        /// 80ms cap was only needed while the keyframe storm saturated the BMC and
        /// starved input. With the storm gone, incrementals are small and the BMC is request-response
        /// limited (~its natural rate, like the original's tight loop), so it runs uncapped (0). Raise
        /// this only if heavy on-screen activity is seen to lag input again.</summary>
        public int MinFrameIntervalMs { get; set; } = 0;
        private int _lastRequestTick;

        /// <summary>How many FramebufferUpdateRequests to keep in flight. ASPEED delta frames must be
        /// requested and applied in strict request/response order: allowing two outstanding requests
        /// made some firmware encode the next delta against a baseline the client had not displayed,
        /// producing duplicated or displaced text blocks. Keep the default at one. Higher values are
        /// retained only as an explicit compatibility/performance experiment.</summary>
        public int PipelineDepth { get; set; } = 1;

        /// <summary>FBURs sent but not yet answered by a FramebufferUpdate. Held ~= PipelineDepth;
        /// drives the steady-state top-up and the timer's liveness watchdog.</summary>
        private int _outstanding;

        /// <summary>BMC mouse mode sent at startup: 1=Absolute, 2=Relative(NORMAL), 3=Single.</summary>
        public byte MouseMode { get; set; } = 1;

        /// <summary>ATEN image-quality level sent at startup (0=lowest, 11=highest).</summary>
        public byte ImageQuality { get; set; } = ScreenInfoRequest.MaximumQuality;

        /// <summary>ATEN chroma mode sent at startup. 444 enables Enhanced Text Mode; 422 is Normal.</summary>
        public ushort ImageMode { get; set; } = ScreenInfoRequest.EnhancedTextMode;

        /// <summary>Total frames decoded (FramebufferUpdates that produced pixels).</summary>
        public long FramesDecoded { get; private set; }
        /// <summary>Total video payload bytes received (for a bandwidth readout).</summary>
        public long VideoBytes { get; private set; }
        /// <summary>UTC time of the most recently decoded frame (drives the connection-health/stale readout).</summary>
        public DateTime LastFrameUtc { get; private set; }
        /// <summary>UTC time of the most recently received server message of ANY type (frame, status,
        /// privilege, ...). This - not <see cref="LastFrameUtc"/> - is the true liveness signal: a
        /// static screen produces no new frames but a live BMC still answers a forced-full probe.
        /// Drives the stall watchdog.</summary>
        public DateTime LastMessageUtc { get; private set; }

        /// <summary>Input-control state from the server privilege grant (msg 0x39): true = this session
        /// controls input, false = view-only, null = not yet reported. See <see cref="PrivilegeChanged"/>.</summary>
        public bool? Controlling { get; private set; }
        /// <summary>The server's privilege/session string (<c>&lt;sid&gt; ROLE &lt;clientip&gt;</c>).</summary>
        public string PrivilegeInfo { get; private set; }

        public KvmVideoSession(KvmConnectionOptions options, IRfbAuthenticator authenticator = null)
        {
            _connection = new KvmConnection(options);
            _authenticator = authenticator; // null => RfbHandshake default (TokenChallengeAuthenticator)
        }

        public AtenTileDecoder Decoder { get; private set; }
        public RfbSession Session { get; private set; }

        public event EventHandler<FrameDecodedEventArgs> FrameDecoded;
        public event EventHandler<Exception> Faulted;
        /// <summary>Raised when the server reports the input-control state (view-only vs controlling).</summary>
        public event EventHandler PrivilegeChanged;

        /// <summary>Connect + handshake (synchronous). Sizes the decoder from ServerInit.</summary>
        public void Open()
        {
            _connection.Connect();
            Session = _connection.Handshake(_authenticator);

            int w = Math.Max(1, Session.ServerInit.Width);
            int h = Math.Max(1, Session.ServerInit.Height);
            Decoder = new AtenTileDecoder(w, h);
        }

        /// <summary>Start the background receive loop.</summary>
        public void StartPump()
        {
            if (Decoder == null) throw new InvalidOperationException("Call Open() first.");
            _running = true;
            LastMessageUtc = DateTime.UtcNow; // arm the stall watchdog from connect, not from epoch
            _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "iKVM-receive" };
            _pumpThread.Start();
            StartInputSender();

            // Keep asking for updates (catches on-change frames) and keep the connection alive
            // so the BMC does not drop the session for inactivity. Mirrors the viewer's runImage cadence.
            // Mostly incremental (only changed 16x16 tiles), but every Nth tick force a full,
            // non-incremental refresh so stale tiles can't accumulate (see FullRefreshIntervalTicks).
            _requestTimer = new Timer(_ =>
            {
                try
                {
                    if (!_running) return;
                    int t = System.Threading.Interlocked.Increment(ref _ticksSinceFull);
                    if (FullRefreshIntervalTicks > 0 && t >= FullRefreshIntervalTicks)
                        SendUpdate(incremental: false);                       // periodic forced full repairs any delta drift
                    else if (System.Threading.Volatile.Read(ref _outstanding) <= 0)
                        SendUpdate(incremental: true);                        // liveness watchdog ONLY: the pipeline
                                                                              // normally keeps PipelineDepth requests
                                                                              // in flight, so there is no per-second
                                                                              // extra request to inflate depth.
                }
                catch (Exception ex) { OnSendFault(ex); } // a failed FBUR write = dead socket -> reconnect
            }, null, 1000, 1000);

            // Periodic client heartbeat. The native viewer's keepAliveTask sends this every 3s;
            // without it the BMC keeps streaming video but stops servicing the client's
            // keyboard/mouse/power - the cause of "input doesn't work at all".
            _keepAliveTimer = new Timer(_ =>
            {
                try { if (_running) SendKeepAlive(); }
                catch (Exception ex) { OnSendFault(ex); } // a failed keepalive write = dead socket -> reconnect
            }, null, 3000, 3000);

            // Stall watchdog: catches a SILENT drop (TCP open, no inbound bytes) that the blocking
            // read can never surface as an exception. Two stages off LastMessageUtc (see fields).
            _watchdogTimer = new Timer(_ => { try { WatchdogTick(); } catch (Exception ex) { Diagnostics.KvmLog.Error("watchdog", ex); } }, null, 2000, 2000);
        }

        /// <summary>Watchdog body (off the timer thread): probe after a soft stall, fault after a hard one.</summary>
        private void WatchdogTick()
        {
            if (!_running) return;
            double idle = (DateTime.UtcNow - LastMessageUtc).TotalSeconds;

            // Hard stall: even a forced-full probe went unanswered -> declare the link dead.
            if (StallTimeoutSeconds > 0 && idle >= StallTimeoutSeconds)
            {
                RaiseFault(new System.IO.IOException(
                    $"No video/response from BMC for {(int)idle}s - connection stalled."));
                return;
            }

            // Soft stall: nudge the BMC with one forced-full FBUR. A live BMC always answers (even
            // on a static screen), which refreshes LastMessageUtc and re-primes a stuck pipeline; a
            // dead one stays silent until the hard-stall fault above. One probe per stall window.
            if (StallProbeSeconds > 0 && idle >= StallProbeSeconds)
            {
                if (System.Threading.Interlocked.Exchange(ref _probedThisStall, 1) == 0)
                {
                    Diagnostics.KvmLog.Write($"watchdog: {(int)idle}s since last message - sending forced-full probe.");
                    try { SendUpdate(incremental: false); } catch (Exception ex) { OnSendFault(ex); }
                }
            }
            else
            {
                System.Threading.Interlocked.Exchange(ref _probedThisStall, 0); // healthy -> re-arm the probe
            }
        }

        /// <summary>A background send (keepalive/FBUR/probe) threw - almost always a broken socket.
        /// Surface it as a fault so the UI reconnects, instead of silently swallowing it.</summary>
        private void OnSendFault(Exception ex)
        {
            if (!_running) return; // an expected error during teardown - ignore
            Diagnostics.KvmLog.Error("background send", ex);
            RaiseFault(ex);
        }

        /// <summary>Raise <see cref="Faulted"/> at most once per session (the pump catch, the
        /// watchdog and a failed background send can all race to report the same dead connection).</summary>
        private void RaiseFault(Exception ex)
        {
            if (System.Threading.Interlocked.Exchange(ref _faultRaised, 1) != 0) return;
            Faulted?.Invoke(this, ex);
        }

        /// <summary>Client keepalive heartbeat (native sendKeepAliveAck -> RFBScreen vtable[4]
        /// FUN_180013270): <c>[0x15][1:u32 BE][0:u32 BE]</c>, sent every ~3s.</summary>
        public void SendKeepAlive()
        {
            lock (_sendLock)
            {
                _connection.Stream.WriteBytes(KeepAliveFrame);
                _connection.Stream.Flush();
            }
            if (LogInput) Diagnostics.KvmLog.Write("TX keepalive : 15 00 00 00 01 00 00 00 00");
        }

        // Constant control frames - cached so the periodic timers don't allocate a fresh array on
        // every tick. (They are only ever written under _sendLock, never mutated.)
        private static readonly byte[] KeepAliveFrame = { 0x15, 0, 0, 0, 1, 0, 0, 0, 0 };
        private static readonly byte[] RunImageFrame = { 7, 0x07, 0x80 };

        /// <summary>Force a fresh full (non-incremental) frame now - repaints the whole
        /// surface, clearing any stale tiles. Wired to the UI's manual refresh.</summary>
        public void RequestFullRefresh()
        {
            try { SendUpdate(incremental: false); } catch (Exception ex) { Diagnostics.KvmLog.Error("manual refresh", ex); }
        }

        /// <summary>Issues one framebuffer-update request and accounts for it in the request
        /// pipeline. The per-cycle message is the FramebufferUpdateRequest alone: runImage
        /// (<c>[7,0x0780]</c>) is sent once at session start by <see cref="SendRunImage"/>, not per
        /// cycle. Sending runImage on every cycle was the cause of the full-frame storm
        /// (FUN_180011950 + FUN_180013060).</summary>
        private void SendUpdate(bool incremental)
        {
            // Per-cycle message is JUST the FramebufferUpdateRequest. The original viewer sends
            // runImage ([7,0x0780]) only ONCE at startup, not every cycle (pcap) -
            // sending it per frame was ~hundreds of extra messages and likely extra BMC frames.
            if (!incremental) System.Threading.Interlocked.Exchange(ref _ticksSinceFull, 0);
            RequestFrame(incremental);
            System.Threading.Interlocked.Increment(ref _outstanding); // one more request in flight
            _lastRequestTick = Environment.TickCount;
        }

        /// <summary>runImage ([7,0x0780]) - sent ONCE at session start (FUN_180011950). The original
        /// does not repeat it per frame.</summary>
        private void SendRunImage()
        {
            lock (_sendLock)
            {
                _connection.Stream.WriteBytes(RunImageFrame);
                _connection.Stream.Flush();
            }
        }

        /// <summary>Ask the BMC for a framebuffer update (RFB type 3). Without this it sends
        /// no video and drops the idle connection (FUN_180013060).</summary>
        public void RequestFrame(bool incremental)
        {
            byte[] frame = FramebufferUpdateRequest.Build(incremental, 0, 0, Decoder.Width, Decoder.Height);
            lock (_sendLock)
            {
                _connection.Stream.WriteBytes(frame);
                _connection.Stream.Flush();
            }
        }

        private void PumpLoop()
        {
            Diagnostics.KvmLog.Write("Receive pump started.");
            int count = 0;
            try
            {
                // Replicate the vendor viewer's startup wire sequence:
                //   updateInfo -> [0x37], changeScreenInfo -> [0x32,0,quality,mode BE]
                //   then each cycle: runImage [7,0x0780] (FUN_180011950) + updateImage FBUR
                //   (FUN_180013060). First cycle requests a full keyframe.
                Diagnostics.KvmLog.Write($"Video start: query 0x37, set quality {ImageQuality}/mode {ImageMode}, " +
                    $"then [7,0x0780] + full FBUR ({Decoder.Width}x{Decoder.Height}).");
                lock (_sendLock)
                {
                    _connection.Stream.WriteU8(0x37);
                    _connection.Stream.Flush();
                }
                SendScreenInfo(ImageQuality, ImageMode);
                SendRunImage();                 // runImage [7,0x0780] - once, like the original
                SendUpdate(incremental: false); // first keyframe (bare FBUR)
                // Prime the request pipeline: keep PipelineDepth FBURs in flight so the BMC encodes
                // the next frame while the current one is decoded and presented (overlaps the round trip).
                for (int i = 1; i < PipelineDepth; i++) SendUpdate(incremental: true);
                // Tell the BMC the configured mouse mode (native sends setMouseMode on connect). Absolute (1) is needed for the absolute pointer coordinates to track.
                SendMouseMode(MouseMode);
                // NOTE: do NOT auto-send hotPlug (0x3a). It appears to be a toggle/replug pulse with
                // no parameter; the virtual USB keyboard/mouse is normally already attached, so an
                // auto-send can TOGGLE it OFF. It's exposed as a manual menu action instead.

                while (_running)
                {
                    var msg = ServerMessageReader.ConsumeOne(_connection.Stream, Decoder);
                    // Any inbound message proves the link is alive and re-arms the probe (a static
                    // screen has no new frames but still answers the watchdog's forced-full probe).
                    LastMessageUtc = DateTime.UtcNow;
                    System.Threading.Interlocked.Exchange(ref _probedThisStall, 0);
                    if (++count <= 20) // log the first messages to characterise the stream
                        Diagnostics.KvmLog.Write($"msg #{count}: type 0x{msg.Type:x2}{(msg.IsFrame ? " (frame)" : "")}");
                    VideoBytes += msg.PayloadBytes;
                    if (msg.HasPrivilege && (Controlling != msg.Controlling || PrivilegeInfo != msg.PrivilegeInfo))
                    {
                        Controlling = msg.Controlling;
                        PrivilegeInfo = msg.PrivilegeInfo;
                        PrivilegeChanged?.Invoke(this, EventArgs.Empty);
                    }
                    if (msg.IsFrame)
                    {
                        FramesDecoded++;
                        LastFrameUtc = DateTime.UtcNow;
                        System.Threading.Interlocked.Decrement(ref _outstanding); // this frame answered one request
                        FrameDecoded?.Invoke(this, new FrameDecodedEventArgs(Decoder.Frame, msg.Dirty));
                        if (msg.Resized)
                        {
                            // Resolution changed -> one full keyframe to repaint the new-size surface.
                            // This also restores pipeline depth (−1 consumed above, +1 here).
                            SendUpdate(incremental: false);
                        }
                        else
                        {
                            // Top the pipeline back up to PipelineDepth. Steady state is exactly one
                            // request per frame consumed (consume 1 -> send 1), so depth is bounded at
                            // PipelineDepth and cannot storm. The pump never blocks on the UI (the
                            // present is async), so it keeps draining the socket and the BMC keeps
                            // servicing input.
                            while (_running && System.Threading.Volatile.Read(ref _outstanding) < PipelineDepth)
                                SendUpdate(incremental: true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    Diagnostics.KvmLog.Error("receive pump", ex);
                    RaiseFault(ex);
                }
            }
        }

        /// <summary>When true, every input packet sent is logged as hex (diagnostics). Mouse
        /// moves are throttled to avoid flooding; clicks/keys/power are always logged.</summary>
        public bool LogInput { get; set; } = true;
        private int _lastMouseLogTick;
        private int _lastLoggedButtons = -1;

        // --- Off-UI input sender. Keys/clicks/power are enqueued and sent promptly and IN ORDER by a
        // dedicated thread; mouse MOVES are coalesced to the latest position and emitted at most every
        // MouseMoveIntervalMs (~125Hz) so dragging the cursor can't flood the BMC's input channel or
        // block the UI thread on a TLS write. All sends still serialize on _sendLock with the pump. ---
        private Thread _senderThread;
        private readonly object _inputLock = new object();
        private readonly Queue<InputItem> _discrete = new Queue<InputItem>();
        private readonly System.Threading.ManualResetEventSlim _inputSignal = new System.Threading.ManualResetEventSlim(false);
        private byte[] _moveFrame;
        private bool _movePending;
        private volatile bool _senderRunning;

        /// <summary>Keysyms currently held down (added on a key-down, removed on a key-up). Lets
        /// <see cref="ReleaseHeldKeys"/> send an up for everything still held when the viewer loses
        /// focus, so a modifier can't stick on the host. Guarded by <see cref="_inputLock"/>.</summary>
        private readonly HashSet<uint> _heldKeys = new HashSet<uint>();

        /// <summary>One queued input frame plus the metadata the sender needs to optionally shed it.
        /// <see cref="Coalescible"/> marks an OS auto-repeat key-DOWN - the only kind that may be dropped, and
        /// only once it has gone stale (see <see cref="RepeatMaxAgeMs"/>). Everything else (first press,
        /// release, taps, clicks, power) is non-coalescible and always sent, in order.</summary>
        private readonly struct InputItem
        {
            public InputItem(byte[] frame, bool coalescible, int stampMs)
            {
                Frame = frame; Coalescible = coalescible; StampMs = stampMs;
            }
            public byte[] Frame { get; }
            public bool Coalescible { get; }
            public int StampMs { get; } // source event time (Environment.TickCount basis), for staleness
        }

        /// <summary>Minimum interval (ms) between coalesced mouse-move sends. ~8ms ≈ 125Hz - far below
        /// WPF's raw MouseMove rate, so a drag collapses to a steady trickle of latest positions.</summary>
        public int MouseMoveIntervalMs { get; set; } = 8;

        // --- Auto-repeat shedding. Holding a key streams OS auto-repeat DOWNs; the client keeps relaying
        // them (so held-key repeat works), but if the send path stalls (TLS write blocked, BMC backpressure)
        // or the UI thread is saturated, those repeats pile up in _discrete and would otherwise BURST
        // onto the host the instant the stall clears ("key repeated a lot"). The guards below cap that:
        // a repeat that is already stale when the sender reaches it is dropped, and the queue is bounded.
        // Normal repeats (sent within ~1ms) are never stale, so steady-state auto-repeat is unaffected. ---

        /// <summary>An OS auto-repeat key-down older than this (ms) when the sender finally reaches it is
        /// dropped - it represents a repeat the host's own typematic already covers. Normal auto-repeat is
        /// unaffected (it ages ~0ms). This also bounds any post-stall burst to ≈ this window of repeats.
        /// 0 disables staleness shedding.</summary>
        public int RepeatMaxAgeMs { get; set; } = 150;

        /// <summary>Hard cap on queued input frames. Past it, incoming auto-repeats are dropped at the
        /// source so a long stall can't grow the queue without bound; real transitions still enqueue.
        /// 0 disables the cap.</summary>
        public int MaxQueuedInput { get; set; } = 512;

        /// <summary>Count of auto-repeat key-downs shed by the staleness/cap guards (diagnostics).</summary>
        public long DroppedKeyRepeats => System.Threading.Interlocked.Read(ref _droppedRepeats);
        private long _droppedRepeats;
        private int _lastDropLogTick;

        private void StartInputSender()
        {
            _senderRunning = true;
            _senderThread = new Thread(SenderLoop) { IsBackground = true, Name = "iKVM-input" };
            _senderThread.Start();
        }

        private void SenderLoop()
        {
            while (_senderRunning)
            {
                // Wake immediately when a discrete event is signalled; otherwise time out to flush a
                // pending coalesced move at the capped rate. The queue is always drained regardless of
                // which path woke the loop, so a missed or un-reset signal can never lose an event
                // (latency is at most one interval).
                _inputSignal.Wait(MouseMoveIntervalMs);
                _inputSignal.Reset();

                while (true) // discrete events (keys, clicks, power) in FIFO order
                {
                    InputItem item; bool has;
                    lock (_inputLock)
                    {
                        has = _discrete.Count > 0;
                        item = has ? _discrete.Dequeue() : default(InputItem);
                    }
                    if (!has) break;
                    // Shed an auto-repeat that has aged out while the sender was behind: replaying a
                    // backlog of stale repeats is the post-stall burst to avoid. unchecked() so the tick
                    // subtraction is correct across the 32-bit Environment.TickCount wraparound.
                    if (item.Coalescible && RepeatMaxAgeMs > 0 &&
                        unchecked(Environment.TickCount - item.StampMs) > RepeatMaxAgeMs)
                    {
                        NoteDroppedRepeat();
                        continue;
                    }
                    WriteFrame(item.Frame);
                }

                byte[] move = null; // then the single latest coalesced move, if any
                lock (_inputLock) { if (_movePending) { move = _moveFrame; _movePending = false; } }
                if (move != null) WriteFrame(move);
            }
        }

        private void WriteFrame(byte[] frame)
        {
            try
            {
                lock (_sendLock)
                {
                    _connection.Stream.WriteBytes(frame);
                    _connection.Stream.Flush();
                }
            }
            catch (Exception ex) { if (_senderRunning) Diagnostics.KvmLog.Error("input send", ex); }
        }

        /// <summary>Send an absolute mouse move/click (plaintext PointerEvent, type 5). Pure moves
        /// (<paramref name="coalesce"/>=true) are collapsed to the latest position and paced by the
        /// sender thread; button transitions (false) are sent promptly and drop any stale move (it
        /// carried the old button mask), so a press/release is never coalesced away.</summary>
        public void SendMouse(int x, int y, int buttonMask, bool coalesce = false)
        {
            byte[] frame = _mouse.BuildPlaintext(x, y, buttonMask);
            if (coalesce)
            {
                lock (_inputLock) { _moveFrame = frame; _movePending = true; }
            }
            else
            {
                lock (_inputLock) { _discrete.Enqueue(new InputItem(frame, false, 0)); _movePending = false; }
                _inputSignal.Set();
            }
            if (LogInput)
            {
                int now = Environment.TickCount;
                bool buttonsChanged = buttonMask != _lastLoggedButtons;
                if (buttonsChanged || now - _lastMouseLogTick >= 300)
                {
                    _lastMouseLogTick = now;
                    _lastLoggedButtons = buttonMask;
                    Diagnostics.KvmLog.Write($"TX mouse x={x} y={y} buttons=0x{buttonMask:x2} : " +
                                             Diagnostics.KvmLog.Hex(frame));
                }
            }
        }

        /// <summary>Send a key press/release (KeyEvent, type 4) for the given RFB keysym.
        /// <paramref name="autoRepeat"/> marks an OS auto-repeat DOWN (pass <c>KeyEventArgs.IsRepeat</c>);
        /// such events are sheddable if the send path falls behind, with <paramref name="stampMs"/> the
        /// source event time (<c>KeyEventArgs.Timestamp</c>) used to measure staleness. Real presses and
        /// all releases (autoRepeat=false) are never dropped.</summary>
        public void SendKey(uint keysym, bool down, bool autoRepeat = false, int stampMs = 0)
        {
            byte[] frame = _keyboard.BuildKeyEvent(keysym, down);
            bool coalescible = autoRepeat && down; // only repeated DOWNs are sheddable; never a release
            bool dropped = false;
            lock (_inputLock)
            {
                // Track held state for focus-loss cleanup, keyed on the base usage. The 0xFF00 lock-key
                // marker is stripped (the App ORs it onto a lock key whose lock is off, so a Caps down can
                // arrive as 0xFF39 while its up is 0x39) so down/up always match and nothing sticks in the
                // set. The set also dedups repeat-downs; a shed repeat keeps the key held (added by the
                // first down, removed only by the up), so the held set stays accurate regardless of shedding.
                uint held = keysym & 0x00FFu;
                if (down) _heldKeys.Add(held); else _heldKeys.Remove(held);
                if (coalescible && MaxQueuedInput > 0 && _discrete.Count >= MaxQueuedInput)
                    dropped = true; // queue saturated by a long stall - drop the new repeat, keep memory bounded
                else
                    _discrete.Enqueue(new InputItem(frame, coalescible, coalescible ? stampMs : 0));
            }
            if (dropped) { NoteDroppedRepeat(); return; }
            _inputSignal.Set();
            if (LogInput)
                Diagnostics.KvmLog.Write($"TX key keysym=0x{keysym:x4} {(down ? "down" : "up")}{(autoRepeat ? " (repeat)" : "")} : " +
                                         Diagnostics.KvmLog.Hex(frame));
        }

        /// <summary>Release every key currently held down (send an up for each) and clear the held set.
        /// Call when the viewer loses focus so a modifier (or any key) can't stick on the host if the
        /// user Alt-Tabs or clicks away mid-hold. Releases are non-coalescible, so they are never shed.
        /// Mirrors the original viewer's <c>releasePressedKeys()</c>. No-op if nothing held.</summary>
        public void ReleaseHeldKeys()
        {
            int n;
            lock (_inputLock)
            {
                n = _heldKeys.Count;
                if (n == 0) return;
                foreach (uint keysym in _heldKeys)
                    _discrete.Enqueue(new InputItem(_keyboard.BuildKeyEvent(keysym, false), false, 0));
                _heldKeys.Clear();
            }
            _inputSignal.Set();
            if (LogInput) Diagnostics.KvmLog.Write($"input: released {n} held key(s) on focus loss.");
        }

        /// <summary>Tally a shed auto-repeat and emit at most ~1 diagnostic line/sec (logging on).</summary>
        private void NoteDroppedRepeat()
        {
            System.Threading.Interlocked.Increment(ref _droppedRepeats);
            if (!LogInput) return;
            int now = Environment.TickCount;
            if (unchecked(now - _lastDropLogTick) >= 1000)
            {
                _lastDropLogTick = now;
                Diagnostics.KvmLog.Write($"input: shedding stale auto-repeats (total {DroppedKeyRepeats}) - send path behind.");
            }
        }

        /// <summary>Set the BMC mouse mode (native setMouseMode -> RFBMouse FUN_180011b10 =
        /// <c>[0x36][0][mode]</c>). mode: 1=ABSOLUTE, 2=NORMAL/relative, 3=SINGLE. This client sends
        /// absolute coordinates, so the BMC must be in absolute mode (1) or the pointer will not track.</summary>
        public void SendMouseMode(byte mode = 1)
        {
            byte[] frame = { 0x36, 0x00, mode };
            lock (_sendLock)
            {
                _connection.Stream.WriteBytes(frame);
                _connection.Stream.Flush();
            }
            Diagnostics.KvmLog.Write($"TX mousemode {mode} : " + Diagnostics.KvmLog.Hex(frame));
        }

        /// <summary>Set the BMC image quality and chroma mode. Enhanced Text Mode uses mode 444
        /// (<c>0x01bc</c>) and avoids the coloured fringes caused by YUV420 chroma subsampling.</summary>
        public void SendScreenInfo(byte quality, ushort mode)
        {
            byte[] frame = ScreenInfoRequest.Build(quality, mode);
            lock (_sendLock)
            {
                _connection.Stream.WriteBytes(frame);
                _connection.Stream.Flush();
            }
            Diagnostics.KvmLog.Write($"TX screeninfo quality={quality} mode={mode} : " +
                Diagnostics.KvmLog.Hex(frame));
        }

        /// <summary>Re-attach the virtual USB keyboard/mouse to the host ("Keyboard Mouse HotPlug" -
        /// native RFBMouse vtable+0x40 = a single byte <c>0x3a</c>). If the host hasn't enumerated the
        /// BMC's virtual HID, injected keystrokes/mouse go nowhere until this is sent.</summary>
        public void SendHotPlug()
        {
            lock (_sendLock)
            {
                _connection.Stream.WriteU8(0x3a);
                _connection.Stream.Flush();
            }
            Diagnostics.KvmLog.Write("TX hotplug : 3a");
        }

        /// <summary>Send a chassis power command (OEM record <c>[0x1a][code]</c>).</summary>
        public void SetPower(PowerCommand command)
        {
            byte[] frame = PowerControl.Build(command);
            lock (_inputLock) { _discrete.Enqueue(new InputItem(frame, false, 0)); }
            _inputSignal.Set();
            if (LogInput)
                Diagnostics.KvmLog.Write($"TX power {command} : " + Diagnostics.KvmLog.Hex(frame));
        }

        public void Stop()
        {
            _running = false;
            _senderRunning = false;
            try { _inputSignal.Set(); } catch { } // wake the sender so it exits its Wait
            try { _requestTimer?.Dispose(); } catch { }
            _requestTimer = null;
            try { _keepAliveTimer?.Dispose(); } catch { }
            _keepAliveTimer = null;
            try { _watchdogTimer?.Dispose(); } catch { }
            _watchdogTimer = null;
        }

        public void Dispose()
        {
            Stop();
            // Close the transport FIRST so the pump thread's blocking ConsumeOne read unblocks, THEN
            // join the workers so they have truly exited before the shared wait handle is released.
            // The threads are background (so a missed join can't hang app exit), but joining stops
            // reconnect cycles from leaking threads/handles. _running is already false, so the pump's
            // read-throws-on-close lands in its catch as an expected teardown (no spurious Faulted).
            try { _connection?.Dispose(); } catch { }
            try { _pumpThread?.Join(2000); } catch { }
            try { _senderThread?.Join(1000); } catch { }
            try { _inputSignal.Dispose(); } catch { }
        }
    }
}
