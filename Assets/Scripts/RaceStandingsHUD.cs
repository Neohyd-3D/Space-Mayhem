using System.Text;
using UnityEngine;
using TMPro;

namespace SpaceMayhem
{
    /// <summary>
    /// Draws the live race order into a single multi-line text — "1. You / 2. AI Racer …" — with the local
    /// racer highlighted. Pure presentation: it just renders <see cref="RaceDirector.GetStandings"/>, which
    /// is the same leader-first ranking the brain computes from laps + progress (so it stays correct in
    /// multiplayer untouched). One TMP text, no per-row prefabs to wire.
    ///
    /// Put a TMP text in your HUD, drop this on it (or anywhere), and assign the text. The director and the
    /// local racer are auto-found if left empty.
    /// </summary>
    [DisallowMultipleComponent]
    public class RaceStandingsHUD : MonoBehaviour
    {
        [Tooltip("Race brain to read standings from. Auto-found if empty.")]
        public RaceDirector director;

        [Tooltip("The local player's participant (gets highlighted). Auto-found (the ship with SpaceshipInput) " +
                 "if empty.")]
        public RaceParticipant localRacer;

        [Tooltip("The multi-line text the standings are written into.")]
        public TMP_Text standingsText;

        [Header("Style")]
        [Tooltip("Most racers to list.")]
        [Min(1)] public int maxRows = 8;
        [Tooltip("Hex colour (TMP rich-text) for the local player's row.")]
        public string highlightColor = "#FFD23F";

        readonly StringBuilder _sb = new StringBuilder(128);

        void OnEnable()
        {
            if (director == null) director = FindFirstObjectByType<RaceDirector>();
            if (localRacer == null) localRacer = FindLocalRacer();
        }

        void Update()
        {
            if (standingsText == null) return;
            if (director == null) { director = FindFirstObjectByType<RaceDirector>(); if (director == null) return; }
            if (localRacer == null) localRacer = FindLocalRacer();

            var order = director.GetStandings();
            _sb.Clear();
            int rows = Mathf.Min(order.Count, maxRows);
            for (int i = 0; i < rows; i++)
            {
                RaceParticipant r = order[i];
                bool me = r == localRacer;
                if (me) _sb.Append("<b><color=").Append(highlightColor).Append('>');
                _sb.Append(i + 1).Append(". ").Append(r != null ? r.Name : "—");
                if (me) _sb.Append("</color></b>");
                if (i < rows - 1) _sb.Append('\n');
            }
            standingsText.text = _sb.ToString();
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
