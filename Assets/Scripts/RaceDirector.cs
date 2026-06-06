using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// The race "brain": owns the race phase, ONE authoritative clock, and every racer's
    /// progress (lap, next-expected checkpoint, lap/best times, finish). Checkpoints report
    /// crossings here; this is where they're validated IN ORDER and turned into laps.
    ///
    /// Built deliberately like <see cref="MomentumSystem"/> so it survives the jump to
    /// multiplayer untouched in spirit:
    ///   • NO static singleton — it's a scene object that racers <see cref="Register"/> with.
    ///     When this becomes a server-owned NetworkBehaviour, participants register over an
    ///     RPC instead, but the API (Register / ReportCheckpoint) is identical.
    ///   • ONE clock (<see cref="RaceTime"/>) drives every time value. Nothing reads Time.time
    ///     directly elsewhere, so swapping in the server's authoritative tick is a one-line change.
    ///   • Per-racer state (<see cref="RacerProgress"/>) is plain data with no scene refs in its
    ///     core, so it can become replicated network state.
    ///   • Checkpoints are validated in sequence (you must hit gate i before gate i+1), which is
    ///     both the anti-shortcut rule AND the seed of server-side authority — the server will
    ///     run exactly this validation and reject anything that doesn't fit.
    ///
    /// Track convention: place ordered Checkpoints around the circuit, index 0 = the start/finish
    /// line. Put the start grid just BEHIND checkpoint 0 so every lap (including the first) is a
    /// clean line-to-line segment. Lap 1 begins the instant the racer first crosses gate 0.
    /// </summary>
    [DisallowMultipleComponent]
    public class RaceDirector : MonoBehaviour
    {
        public enum Phase { PreRace, Countdown, Racing, Finished }

        /// <summary>One racer's live standing. Plain data — no scene refs — so it can become
        /// replicated network state later. Keyed by the participant in <see cref="_progress"/>.</summary>
        public class RacerProgress
        {
            public int   lap;             // 0 before the start line; 1 once racing the first lap, etc.
            public int   nextCheckpoint;  // index the racer must cross next (in-order gate)
            public float lapStartTime;    // RaceTime when the current lap began
            public float lastLapTime;     // duration of the most recently completed lap (0 = none yet)
            public float bestLapTime;     // fastest completed lap (0 = none yet)
            public bool  finished;        // crossed the line on the final lap
            public float finishTime;      // RaceTime at finish (total race time)
            public float progress;        // normalised arc-length on the spline (0..1) — for standings/HUD

            public float CurrentLapTime(float now) =>
                lap >= 1 && !finished ? Mathf.Max(0f, now - lapStartTime) : 0f;
        }

        [Header("Race")]
        [Tooltip("Laps required to finish. The race ends for a racer the moment they cross the " +
                 "start/finish line completing this many laps.")]
        [Min(1)] public int totalLaps = 3;

        [Tooltip("Seconds of locked-control countdown before GO. Ships are frozen (RaceParticipant." +
                 "ControlsLocked) until this elapses, then the clock starts.")]
        [Min(0f)] public float countdownDuration = 3f;

        [Tooltip("Start the countdown automatically when the scene plays. Turn off to trigger the " +
                 "race yourself (menu, ready-up, server signal) via BeginCountdown().")]
        public bool autoStart = true;

        [Header("Track progress")]
        [Tooltip("The racing-line spline (RaceSpline wrapping a hand-drawn SplineContainer). When " +
                 "assigned, laps are counted from arc-length progress along it (no trigger boxes): the " +
                 "loop is split into the spline's own Sector Count even sectors, crossed in order, with " +
                 "sector 0 at the start/finish. Sector count and start point are set ON the RaceSpline.")]
        public RaceSpline track;

        [Header("Checkpoints (fallback — only used when no spline is assigned)")]
        [Tooltip("Ordered trigger checkpoints around the track. Used ONLY when 'Track' is empty. " +
                 "Leave empty to auto-collect every Checkpoint in the scene, sorted by index. " +
                 "Index 0 MUST be the start/finish line.")]
        public Checkpoint[] checkpoints;

        // ── Events — HUD, VFX, audio subscribe; the brain stays ignorant of them ──
        public event Action<Phase>          PhaseChanged;     // any phase transition
        public event Action<int>            CountdownTick;    // whole seconds remaining (3,2,1)
        public event Action<RaceParticipant> LapCompleted;    // fired when a racer banks a lap
        public event Action<RaceParticipant> RacerFinished;   // fired when a racer crosses the final line

        public Phase CurrentPhase { get; private set; } = Phase.PreRace;
        public float RaceTime     { get; private set; }   // the one authoritative clock; ticks only while Racing
        public int   TotalLaps    => totalLaps;

        readonly Dictionary<RaceParticipant, RacerProgress> _progress = new();
        float _countdownRemaining;
        int   _lastCountdownWhole = -1;

        // Spline mode when a track is assigned and valid; otherwise the trigger-box fallback.
        bool UsingSpline => track != null && track.IsValid;

        // Number of ordered gates per lap, whichever source is driving — keeps ReportCheckpoint
        // identical between spline sectors and physical checkpoints. Sector count lives on the spline.
        int GateCount => UsingSpline ? track.SectorCount
                                     : Mathf.Max(1, checkpoints != null ? checkpoints.Length : 0);

        // ── Registration ─────────────────────────────────────────────────────────
        // Racers (local player now; AI and remote players later) register through here.
        // The brain never learns whether input is local, AI, or networked — it just tracks progress.
        public void Register(RaceParticipant racer)
        {
            if (racer == null || _progress.ContainsKey(racer)) return;
            _progress[racer] = new RacerProgress { lap = 0, nextCheckpoint = 0 };
            // Lock controls if we're still pre-race / counting down so late joiners don't fly off.
            racer.SetControlsLocked(CurrentPhase == Phase.PreRace || CurrentPhase == Phase.Countdown);
        }

        public void Unregister(RaceParticipant racer)
        {
            if (racer != null) _progress.Remove(racer);
        }

        public bool TryGetProgress(RaceParticipant racer, out RacerProgress progress) =>
            _progress.TryGetValue(racer, out progress);

        /// <summary>First registered racer — convenience for a single-player HUD until racerId
        /// lookup matters. Returns null if nobody has registered yet.</summary>
        public RaceParticipant FirstRacer
        {
            get { foreach (var kv in _progress) return kv.Key; return null; }
        }

        // ── Standings / positioning ───────────────────────────────────────────────
        // Ranking is a pure read of the same progress the brain already tracks: finished racers (ordered by
        // finish time) lead everyone still racing, who are ordered by laps + fraction into the current lap.
        // Multiplayer-safe — the server runs this identical comparison; nothing here is local-only.
        public int RacerCount => _progress.Count;

        /// <summary>1-based field position (1 = leader). 0 if the racer isn't registered. Allocation-free,
        /// so it's safe to poll every frame from the HUD.</summary>
        public int GetPosition(RaceParticipant racer)
        {
            if (racer == null || !_progress.TryGetValue(racer, out var me)) return 0;
            int pos = 1;
            foreach (var kv in _progress)
                if (kv.Key != racer && CompareStanding(kv.Value, me) < 0) pos++;   // someone ahead of me
            return pos;
        }

        /// <summary>Every racer ordered leader-first. Allocates — use for a standings panel, not a hot path.</summary>
        public List<RaceParticipant> GetStandings()
        {
            var racers = new List<RaceParticipant>(_progress.Keys);
            racers.Sort((a, b) => CompareStanding(_progress[a], _progress[b]));
            return racers;
        }

        // Returns < 0 when `a` is ahead of `b`.
        static int CompareStanding(RacerProgress a, RacerProgress b)
        {
            if (a.finished != b.finished) return a.finished ? -1 : 1;        // finished racers lead
            if (a.finished)               return a.finishTime.CompareTo(b.finishTime);   // earlier finish leads
            float pa = a.lap + a.progress, pb = b.lap + b.progress;          // more laps + progress leads
            return pb.CompareTo(pa);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        void Awake()
        {
            if (track == null) track = FindFirstObjectByType<RaceSpline>();

            // Only fall back to trigger checkpoints when there's no spline to drive progress.
            if (!UsingSpline)
            {
                if (checkpoints == null || checkpoints.Length == 0)
                {
                    var found = FindObjectsByType<Checkpoint>(FindObjectsSortMode.None);
                    Array.Sort(found, (a, b) => a.index.CompareTo(b.index));
                    checkpoints = found;
                }

                if (checkpoints == null || checkpoints.Length == 0)
                    Debug.LogWarning("[RaceDirector] No spline and no checkpoints — laps can't be counted. " +
                                     "Assign a RaceSpline, or add Checkpoint trigger volumes (index 0 = start/finish).", this);
            }
        }

        void Start()
        {
            if (autoStart) BeginCountdown();
        }

        /// <summary>Kick off the locked-control countdown. Call this yourself when autoStart is off
        /// (menu start, all-players-ready, server signal).</summary>
        public void BeginCountdown()
        {
            RaceTime            = 0f;
            _countdownRemaining = countdownDuration;
            _lastCountdownWhole = -1;
            foreach (var p in _progress.Values) { p.lap = 0; p.nextCheckpoint = 0; p.finished = false;
                                                  p.lastLapTime = 0f; p.bestLapTime = 0f; }
            SetPhase(countdownDuration > 0f ? Phase.Countdown : Phase.Racing);
            LockAll(CurrentPhase == Phase.Countdown);
        }

        void Update()
        {
            // The clock and countdown are the only per-frame work; everything else is event-driven
            // off checkpoint crossings. dt is read ONCE here and nowhere else, so the time source
            // is a single swappable point for the future server tick.
            float dt = Time.deltaTime;

            if (CurrentPhase == Phase.Countdown)
            {
                _countdownRemaining -= dt;
                int whole = Mathf.CeilToInt(Mathf.Max(0f, _countdownRemaining));
                if (whole != _lastCountdownWhole)
                {
                    _lastCountdownWhole = whole;
                    CountdownTick?.Invoke(whole);
                }
                if (_countdownRemaining <= 0f)
                {
                    SetPhase(Phase.Racing);
                    LockAll(false);
                }
            }
            else if (CurrentPhase == Phase.Racing)
            {
                RaceTime += dt;
                if (UsingSpline) SampleSplineProgress();
            }
        }

        // ── Spline-driven progress → ordered sector crossings ─────────────────────
        // Each frame, project every racer onto the racing line to get arc-length progress, then
        // turn that into the SAME ordered-gate crossings the trigger-box path produces: the lap
        // logic below never learns which source drove it. A crossing fires only when the racer
        // reaches the sector it's expecting NEXT, so reversing or cutting simply doesn't advance.
        void SampleSplineProgress()
        {
            int n = track.SectorCount;
            foreach (var kv in _progress)
            {
                var racer = kv.Key;
                var p     = kv.Value;
                if (racer == null || p.finished) continue;

                p.progress = track.GetProgress(racer.transform.position);
                int sector = Mathf.Clamp((int)(p.progress * n), 0, n - 1) % n;

                // Fire only on reaching the expected next sector — this IS the in-order validation.
                if (sector == p.nextCheckpoint)
                    ReportCheckpoint(racer, sector);
            }
        }

        // ── Checkpoint crossing — the heart of the brain ──────────────────────────
        // Called by Checkpoint.OnTriggerEnter. Validates the gate is the one this racer needs
        // NEXT (out-of-order / wrong-way crossings are simply ignored), advances the gate, and
        // turns a start/finish crossing into a completed lap.
        public void ReportCheckpoint(RaceParticipant racer, int index)
        {
            if (CurrentPhase != Phase.Racing) return;
            if (!_progress.TryGetValue(racer, out var p) || p.finished) return;
            if (index != p.nextCheckpoint) return;           // wrong gate → not a valid crossing

            int n = GateCount;

            if (index == 0)
            {
                if (p.lap == 0)
                {
                    // First time across the line: lap 1 begins now.
                    p.lap          = 1;
                    p.lapStartTime = RaceTime;
                }
                else
                {
                    // Completed a lap.
                    p.lastLapTime = Mathf.Max(0f, RaceTime - p.lapStartTime);
                    if (p.bestLapTime <= 0f || p.lastLapTime < p.bestLapTime)
                        p.bestLapTime = p.lastLapTime;
                    p.lap++;
                    LapCompleted?.Invoke(racer);

                    if (p.lap > totalLaps)
                    {
                        p.finished   = true;
                        p.finishTime = RaceTime;
                        RacerFinished?.Invoke(racer);
                        CheckForRaceEnd();
                        return;
                    }
                    p.lapStartTime = RaceTime;
                }
                p.nextCheckpoint = n > 1 ? 1 : 0;            // head for the first intermediate (or loop if 1-gate)
            }
            else
            {
                // Intermediate gate: advance, wrapping back to the start/finish line after the last.
                p.nextCheckpoint = (index + 1) % n;
            }
        }

        void CheckForRaceEnd()
        {
            foreach (var p in _progress.Values)
                if (!p.finished) return;
            SetPhase(Phase.Finished);
        }

        // ── helpers ───────────────────────────────────────────────────────────────
        void LockAll(bool locked)
        {
            foreach (var racer in _progress.Keys)
                racer.SetControlsLocked(locked);
        }

        void SetPhase(Phase phase)
        {
            if (phase == CurrentPhase) return;
            CurrentPhase = phase;
            PhaseChanged?.Invoke(phase);
        }
    }
}
