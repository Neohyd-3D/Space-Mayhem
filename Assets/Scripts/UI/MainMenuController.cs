using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SpaceMayhem
{
    /// <summary>
    /// Top-level main-menu navigation. Owns the menu panels and the four primary actions
    /// (Singleplayer → level select, Multiplayer → coming-soon, Settings, Exit). Button click handlers are
    /// wired in CODE, so the scene only needs the object references assigned — no UnityEvent callbacks to
    /// configure in the inspector. Panel switching is a plain show-one / hide-the-rest.
    /// </summary>
    [DisallowMultipleComponent]
    public class MainMenuController : MonoBehaviour
    {
        [Header("Panels (only one shown at a time)")]
        public GameObject mainPanel;
        public GameObject levelSelectPanel;
        public GameObject settingsPanel;
        public GameObject comingSoonPanel;

        [Header("Main buttons")]
        public Button singleplayerButton;
        public Button multiplayerButton;
        public Button settingsButton;
        public Button exitButton;

        [Header("Back buttons (each returns to the main panel)")]
        public Button levelSelectBackButton;
        public Button settingsBackButton;
        public Button comingSoonBackButton;

        void Awake()
        {
            SettingsMenu.ApplySaved();   // honour saved volume/fullscreen/quality at launch

            Wire(singleplayerButton,    ShowLevelSelect);
            Wire(multiplayerButton,     ShowComingSoon);
            Wire(settingsButton,        ShowSettings);
            Wire(exitButton,            QuitGame);
            Wire(levelSelectBackButton, ShowMain);
            Wire(settingsBackButton,    ShowMain);
            Wire(comingSoonBackButton,  ShowMain);
        }

        void Start() => ShowMain();

        static void Wire(Button b, UnityAction action)
        {
            if (b == null) return;
            b.onClick.RemoveListener(action);   // idempotent if Awake runs twice (domain reload)
            b.onClick.AddListener(action);
        }

        public void ShowMain()        => Show(mainPanel);
        public void ShowLevelSelect() => Show(levelSelectPanel);
        public void ShowSettings()    => Show(settingsPanel);
        public void ShowComingSoon()  => Show(comingSoonPanel);

        void Show(GameObject panel)
        {
            if (mainPanel)        mainPanel.SetActive(panel == mainPanel);
            if (levelSelectPanel) levelSelectPanel.SetActive(panel == levelSelectPanel);
            if (settingsPanel)    settingsPanel.SetActive(panel == settingsPanel);
            if (comingSoonPanel)  comingSoonPanel.SetActive(panel == comingSoonPanel);
        }

        public void QuitGame()
        {
            Debug.Log("[MainMenu] Quit requested.");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
