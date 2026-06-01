using TMPro;
using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Reads the <see cref="RaceDirector"/> and draws the local racer's standing: lap count, the
    /// running current-lap clock, last lap, best lap, and the big centre status line (countdown →
    /// GO! → FINISH). Pure presentation — it never decides anything, it only renders what the brain
    /// already computed, so multiplayer changes nothing here except WHICH participant is "local".
    ///
    /// Wire any TMP text you want shown; leave the rest null and they're simply skipped. Assign
    /// <see cref="localRacer"/> to the player's ship (recommended), or leave it null for a single-
    /// player race and the HUD will track the first racer that registers.
    /// </summary>
    [DisallowMultipleComponent]
    public class RaceHUD : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Race brain to read from. Auto-found in the scene if left null.")]
        public RaceDirector director;

        [Tooltip("The local player's participant. Leave null in single-player to track the first " +
                 "registered racer; assign it explicitly once there are several ships.")]
        public RaceParticipant localRacer;

        [Header("Text fields (any may be left empty)")]
        [Tooltip("\"LAP 1/3\" style readout.")]
        public TMP_Text lapText;
        [Tooltip("Running time of the current lap.")]
        public TMP_Text currentLapText;
        [Tooltip("Duration of the last completed lap.")]
        public TMP_Text lastLapText;
        [Tooltip("Fastest completed lap.")]
        public TMP_Text bestLapText;
        [Tooltip("Big centre line: 3 / 2 / 1 / GO! during the countdown, FINISH on completion.")]
        public TMP_Text statusText;

        [Header("Display")]
        [Tooltip("Seconds the \"GO!\" flash stays up after the countdown before clearing.")]
        public float goFlashDuration = 1f;

        float _statusClearAt = -1f;   // unscaled time at which to wipe a transient status (GO!)
        bool  _finishShown;

        void OnEnable()
        {
            if (director == null) director = FindFirstObjectByType<RaceDirector>();
            if (director != null)
            {
                director.CountdownTick += OnCountdownTick;
                director.PhaseChanged  += OnPhaseChanged;
            }
        }

        void OnDisable()
        {
            if (director != null)
            {
                director.CountdownTick -= OnCountdownTick;
                director.PhaseChanged  -= OnPhaseChanged;
            }
        }

        void OnCountdownTick(int secondsRemaining)
        {
            if (statusText == null) return;
            statusText.text = secondsRemaining > 0 ? secondsRemaining.ToString() : "GO!";
            if (secondsRemaining == 0) _statusClearAt = Time.unscaledTime + goFlashDuration;
        }

        void OnPhaseChanged(RaceDirector.Phase phase)
        {
            if (phase == RaceDirector.Phase.Racing && statusText != null && string.IsNullOrEmpty(statusText.text))
            {
                statusText.text = "GO!";
                _statusClearAt  = Time.unscaledTime + goFlashDuration;
            }
        }

        void Update()
        {
            if (director == null) return;

            var racer = localRacer != null ? localRacer : director.FirstRacer;
            if (racer == null || !director.TryGetProgress(racer, out var p)) return;

            if (lapText != null)
                lapText.text = $"LAP {Mathf.Clamp(p.lap, 1, director.TotalLaps)}/{director.TotalLaps}";
            if (currentLapText != null)
                currentLapText.text = FormatTime(p.CurrentLapTime(director.RaceTime));
            if (lastLapText != null)
                lastLapText.text = FormatTime(p.lastLapTime);
            if (bestLapText != null)
                bestLapText.text = FormatTime(p.bestLapTime);

            // Finish line: show it once, with the racer's total time.
            if (p.finished && !_finishShown && statusText != null)
            {
                _finishShown    = true;
                statusText.text = $"FINISH  {FormatTime(p.finishTime)}";
                _statusClearAt  = -1f;   // keep it up
            }

            // Clear a transient status (the GO! flash) when its time is up.
            if (_statusClearAt > 0f && Time.unscaledTime >= _statusClearAt && statusText != null)
            {
                statusText.text = "";
                _statusClearAt  = -1f;
            }
        }

        // mm:ss.mmm, or a dashed placeholder when there's no time yet.
        static string FormatTime(float t)
        {
            if (t <= 0f) return "--:--.---";
            int minutes   = (int)(t / 60f);
            float seconds = t - minutes * 60f;
            return $"{minutes:00}:{seconds:00.000}";
        }
    }
}
