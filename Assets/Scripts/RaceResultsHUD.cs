using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

namespace SpaceMayhem
{
    /// <summary>
    /// End-of-race leaderboard. Pops a results panel the moment the LOCAL player crosses the finish line
    /// (or when the whole race ends) and lists the final order as ONE ROW PER DRIVER — position, name, total
    /// time, best lap — in aligned columns, with the player's row highlighted. Rows are created on the fly to
    /// match the field size (and pooled), and the card resizes to fit. Keeps the order live as the remaining
    /// AIs come in, then freezes once the race is fully Finished.
    ///
    /// Pure presentation, like <see cref="RaceStandingsHUD"/>: it only READS
    /// <see cref="RaceDirector.GetStandings"/> + <see cref="RaceDirector.TryGetProgress"/>, so it stays correct
    /// in multiplayer untouched.
    ///
    /// SETUP: put this on a PERSISTENT object (the HUD Canvas). Assign <see cref="panel"/> (the show/hide root),
    /// <see cref="card"/> (the centered card it resizes), <see cref="titleText"/>, <see cref="rowContainer"/>
    /// (an empty stretched RectTransform the rows are built into) and the optional <see cref="continueButton"/>.
    /// The script toggles the panel, so the panel must not be this object itself.
    /// </summary>
    [DisallowMultipleComponent]
    public class RaceResultsHUD : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("Race brain. Auto-found if empty.")]
        public RaceDirector director;
        [Tooltip("Local player's participant (its row is highlighted). Auto-found (ship with SpaceshipInput).")]
        public RaceParticipant localRacer;
        [Tooltip("The results root to show/hide — a CHILD of this object, not this object itself.")]
        public GameObject panel;
        [Tooltip("The centered card. Resized each render to fit the number of drivers.")]
        public RectTransform card;
        [Tooltip("Headline text (shows 'FINISHED — Pn/N').")]
        public TMP_Text titleText;
        [Tooltip("Empty, stretched RectTransform inside the card — driver rows are built into this.")]
        public RectTransform rowContainer;
        [Tooltip("Optional 'continue' button — wired in code to load 'menuSceneName'.")]
        public Button continueButton;

        [Header("Behaviour")]
        [Tooltip("Show the instant the PLAYER finishes (others still racing). Off = wait for the whole race.")]
        public bool showOnLocalFinish = true;
        [Min(1)] [Tooltip("Most rows to show.")]
        public int maxRows = 12;
        [Tooltip("Scene the continue button loads. Empty = reload the current scene.")]
        public string menuSceneName = "MainMenu";

        [Header("Layout (px)")]
        public float cardWidth    = 680f;
        public float rowHeight    = 54f;
        public float rowSpacing   = 8f;
        public float headerHeight = 140f;   // room for the title above the rows
        public float footerHeight = 96f;    // room for the continue button below
        public float sidePadding  = 28f;    // inset of the rows from the card edge

        [Header("Style")]
        public Color rowColor        = new Color(1f, 1f, 1f, 0.05f);
        public Color rowAltColor     = new Color(1f, 1f, 1f, 0.10f);
        public Color playerRowColor  = new Color(1f, 0.82f, 0.25f, 0.22f);
        public Color textColor       = new Color(0.92f, 0.94f, 1f, 1f);
        public Color dimColor        = new Color(0.92f, 0.94f, 1f, 0.55f);
        public Color playerTextColor = new Color(1f, 0.86f, 0.35f, 1f);
        public float fontSize        = 26f;

        class RowUI
        {
            public GameObject  root;
            public RectTransform rect;
            public Image       bg;
            public TMP_Text    pos, name, time, best;
        }

        readonly List<RowUI> _rows = new();
        bool _visible, _finalized;

        void OnEnable()
        {
            if (director == null)   director   = FindFirstObjectByType<RaceDirector>();
            if (localRacer == null) localRacer = FindLocalRacer();
            if (panel != null) panel.SetActive(false);
            _visible = _finalized = false;
            if (continueButton != null)
            {
                continueButton.onClick.RemoveListener(OnContinue);
                continueButton.onClick.AddListener(OnContinue);
            }
        }

        void OnDisable()
        {
            if (continueButton != null) continueButton.onClick.RemoveListener(OnContinue);
        }

        void Update()
        {
            if (director == null) { director = FindFirstObjectByType<RaceDirector>(); if (director == null) return; }
            if (localRacer == null) localRacer = FindLocalRacer();

            if (!_visible)
            {
                bool localDone = localRacer != null
                                 && director.TryGetProgress(localRacer, out var lp) && lp.finished;
                bool raceDone  = director.CurrentPhase == RaceDirector.Phase.Finished;
                if ((showOnLocalFinish && localDone) || raceDone) Show();
                else return;
            }

            if (_finalized) return;
            Render();
            if (director.CurrentPhase == RaceDirector.Phase.Finished) _finalized = true;   // settle: one last render
        }

        void Show()
        {
            _visible = true;
            if (panel != null) panel.SetActive(true);
            Render();
        }

        /// <summary>Hide the results panel (e.g. for a rematch on the same scene).</summary>
        public void Hide()
        {
            _visible = _finalized = false;
            if (panel != null) panel.SetActive(false);
        }

        void Render()
        {
            if (rowContainer == null) return;
            var order = director.GetStandings();
            int n = Mathf.Min(order.Count, maxRows);

            EnsureRows(n);

            // Card grows/shrinks to fit the field, so spacing stays even for any driver count.
            if (card != null)
                card.sizeDelta = new Vector2(
                    cardWidth,
                    headerHeight + n * rowHeight + Mathf.Max(0, n - 1) * rowSpacing + footerHeight);

            if (titleText != null)
            {
                int pos = localRacer != null ? director.GetPosition(localRacer) : 0;
                titleText.text = pos > 0 ? $"FINISHED — P{pos}/{director.RacerCount}" : "RESULTS";
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                RowUI r = _rows[i];
                if (i >= n) { r.root.SetActive(false); continue; }
                r.root.SetActive(true);

                r.rect.anchoredPosition = new Vector2(0f, -(headerHeight + i * (rowHeight + rowSpacing)));
                r.rect.sizeDelta        = new Vector2(-2f * sidePadding, rowHeight);

                RaceParticipant racer = order[i];
                bool me = racer == localRacer;

                r.bg.color   = me ? playerRowColor : (i % 2 == 0 ? rowColor : rowAltColor);
                Color tc     = me ? playerTextColor : textColor;
                r.pos.color  = tc;
                r.name.color = tc;
                r.time.color = tc;
                r.best.color = me ? playerTextColor : dimColor;

                r.pos.text  = (i + 1).ToString();
                r.name.text = racer != null ? racer.Name : "—";

                if (racer != null && director.TryGetProgress(racer, out var pr))
                {
                    r.time.text = pr.finished
                        ? FormatTime(pr.finishTime)
                        : $"Lap {Mathf.Max(1, pr.lap)}/{director.TotalLaps}";
                    r.best.text = pr.bestLapTime > 0f ? FormatTime(pr.bestLapTime) : "--:--.---";
                }
                else { r.time.text = ""; r.best.text = ""; }
            }
        }

        // Lazily create (and pool) row objects until there are at least n.
        void EnsureRows(int n)
        {
            while (_rows.Count < n)
            {
                var row = new GameObject($"Row{_rows.Count}", typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)row.transform;
                rect.SetParent(rowContainer, false);
                rect.anchorMin = new Vector2(0f, 1f);     // top-stretch; we place by y from the top
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot     = new Vector2(0.5f, 1f);

                var bg = row.GetComponent<Image>();
                bg.raycastTarget = false;

                var ui = new RowUI
                {
                    root = row, rect = rect, bg = bg,
                    pos  = MakeCell(rect, 0.00f, 0.12f, TextAlignmentOptions.Center),
                    name = MakeCell(rect, 0.14f, 0.56f, TextAlignmentOptions.Left),
                    time = MakeCell(rect, 0.56f, 0.80f, TextAlignmentOptions.Right),
                    best = MakeCell(rect, 0.80f, 1.00f, TextAlignmentOptions.Right),
                };
                _rows.Add(ui);
            }
        }

        TMP_Text MakeCell(RectTransform parent, float anchorMinX, float anchorMaxX, TextAlignmentOptions align)
        {
            var go = new GameObject("Cell", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(anchorMinX, 0f);
            rt.anchorMax = new Vector2(anchorMaxX, 1f);
            rt.offsetMin = new Vector2(10f, 0f);
            rt.offsetMax = new Vector2(-10f, 0f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize      = fontSize;
            tmp.alignment     = align;
            tmp.enableWordWrapping = false;
            tmp.overflowMode  = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            return tmp;
        }

        void OnContinue()
        {
            string scene = string.IsNullOrEmpty(menuSceneName)
                ? SceneManager.GetActiveScene().name : menuSceneName;
            SceneManager.LoadScene(scene);
        }

        // mm:ss.mmm — from the director's single authoritative clock (RaceTime), so it's net-correct.
        static string FormatTime(float t)
        {
            if (t <= 0f) return "--:--.---";
            int m = (int)(t / 60f);
            float s = t - m * 60f;
            return $"{m:00}:{s:00.000}";
        }

        // The human is the racer whose ship reads real input — i.e. has a SpaceshipInput (AI ships don't).
        RaceParticipant FindLocalRacer()
        {
            if (director == null) return null;
            foreach (var p in FindObjectsByType<RaceParticipant>(FindObjectsSortMode.None))
                if (p.GetComponent<SpaceshipInput>() != null)
                    return p;
            return director.FirstRacer;
        }
    }
}
