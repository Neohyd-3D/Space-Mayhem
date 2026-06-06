using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
        [Tooltip("\"P1/4\" field-position readout (hidden in a one-racer race).")]
        public TMP_Text positionText;
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

        [Header("End of race")]
        [Tooltip("Button shown once the local racer finishes. If left empty, the HUD creates one at runtime.")]
        public Button restartButton;
        [Tooltip("Text label for the restart button. Auto-filled when the HUD creates the button.")]
        public TMP_Text restartButtonLabel;
        [Tooltip("Button copy shown after finishing.")]
        public string restartButtonText = "RESTART RACE";

        float _statusClearAt = -1f;   // unscaled time at which to wipe a transient status (GO!)
        bool  _finishShown;
        RaceParticipant _resolvedLocal;   // cached local (player) racer when localRacer is left unassigned

        // The HUD should follow the PLAYER, not "whoever registered first" (that's usually an AI in a full
        // grid). Use the explicit localRacer if set; otherwise find the racer whose ship reads real input
        // (has a SpaceshipInput — AI ships don't). Falls back to FirstRacer only if there's no human ship yet.
        RaceParticipant ResolveLocalRacer()
        {
            if (localRacer != null) return localRacer;
            if (_resolvedLocal != null) return _resolvedLocal;
            foreach (var p in FindObjectsByType<RaceParticipant>(FindObjectsSortMode.None))
                if (p.GetComponent<SpaceshipInput>() != null) { _resolvedLocal = p; return _resolvedLocal; }
            return director != null ? director.FirstRacer : null;
        }

        void OnEnable()
        {
            if (director == null) director = FindFirstObjectByType<RaceDirector>();
            EnsureRestartButton();
            SetRestartButtonVisible(false);

            if (director != null)
            {
                director.CountdownTick += OnCountdownTick;
                director.PhaseChanged  += OnPhaseChanged;
            }
        }

        void OnDisable()
        {
            if (restartButton != null)
                restartButton.onClick.RemoveListener(RestartRace);

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
            else if (phase == RaceDirector.Phase.Countdown || phase == RaceDirector.Phase.PreRace)
            {
                _finishShown = false;
                SetRestartButtonVisible(false);
            }
        }

        void Update()
        {
            if (director == null) return;

            var racer = ResolveLocalRacer();
            if (racer == null || !director.TryGetProgress(racer, out var p)) return;

            if (positionText != null)
                positionText.text = director.RacerCount > 1
                    ? $"{director.GetPosition(racer)}/{director.RacerCount}"
                    : "";
            if (lapText != null)
                lapText.text = $"LAP {Mathf.Clamp(p.lap, 1, director.TotalLaps)}/{director.TotalLaps}";
            if (currentLapText != null)
                currentLapText.text = FormatTime(p.CurrentLapTime(director.RaceTime));
            if (lastLapText != null)
                lastLapText.text = FormatTime(p.lastLapTime);
            if (bestLapText != null)
                bestLapText.text = FormatTime(p.bestLapTime);

            // Finish line: show it once, with the racer's total time.
            if (p.finished && !_finishShown)
            {
                _finishShown = true;
                if (statusText != null)
                {
                    statusText.text = $"FINISH  {FormatTime(p.finishTime)}";
                    _statusClearAt  = -1f;   // keep it up
                }
                SetRestartButtonVisible(true);
            }

            // Clear a transient status (the GO! flash) when its time is up.
            if (_statusClearAt > 0f && Time.unscaledTime >= _statusClearAt && statusText != null)
            {
                statusText.text = "";
                _statusClearAt  = -1f;
            }
        }

        void EnsureRestartButton()
        {
            if (restartButton != null)
            {
                if (restartButtonLabel != null)
                    restartButtonLabel.text = restartButtonText;
                restartButton.onClick.RemoveListener(RestartRace);
                restartButton.onClick.AddListener(RestartRace);
                return;
            }

            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            var buttonObject = new GameObject("RestartRaceButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(canvas.transform, false);

            var rect = (RectTransform)buttonObject.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -135f);
            rect.sizeDelta = new Vector2(320f, 72f);

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.04f, 0.09f, 0.13f, 0.88f);

            restartButton = buttonObject.GetComponent<Button>();
            restartButton.transition = Selectable.Transition.ColorTint;
            restartButton.targetGraphic = image;
            var colors = restartButton.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(0.08f, 0.21f, 0.28f, 0.95f);
            colors.pressedColor = new Color(0.02f, 0.34f, 0.44f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.03f, 0.03f, 0.03f, 0.35f);
            restartButton.colors = colors;

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(buttonObject.transform, false);

            var labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(16f, 8f);
            labelRect.offsetMax = new Vector2(-16f, -8f);

            restartButtonLabel = labelObject.GetComponent<TextMeshProUGUI>();
            restartButtonLabel.text = restartButtonText;
            restartButtonLabel.alignment = TextAlignmentOptions.Center;
            restartButtonLabel.fontSize = 28f;
            restartButtonLabel.fontStyle = FontStyles.Bold;
            restartButtonLabel.color = new Color(0.8f, 0.96f, 1f, 1f);
            restartButtonLabel.raycastTarget = false;

            restartButton.onClick.AddListener(RestartRace);
        }

        void SetRestartButtonVisible(bool visible)
        {
            if (restartButton != null)
                restartButton.gameObject.SetActive(visible);
        }

        public void RestartRace()
        {
            Time.timeScale = 1f;
            var scene = SceneManager.GetActiveScene();
            if (scene.buildIndex >= 0)
                SceneManager.LoadScene(scene.buildIndex);
            else
                SceneManager.LoadScene(scene.name);
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
