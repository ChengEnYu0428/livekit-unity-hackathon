using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Adds one persistent overlay button that switches between the original
/// meeting UI and the second-batch advanced API UI.
/// </summary>
public sealed class LiveKitUiSceneSwitcher : MonoBehaviour
{
    private const string MeetingScene = "StreamingSDKSimpleSample";
    private const string AdvancedScene = "StreamingSDKAdvancedSample";

    private Button switchButton;
    private TMP_Text buttonLabel;
    private CanvasGroup loadingOverlay;
    private TMP_Text loadingText;
    private bool loadingScene;
    private bool built;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindFirstObjectByType<LiveKitUiSceneSwitcher>() != null)
        {
            return;
        }

        var root = new GameObject("LiveKit UI Scene Switcher");
        DontDestroyOnLoad(root);
        root.AddComponent<LiveKitUiSceneSwitcher>();
    }

    private void OnEnable()
    {
        if (built) return;

        built = true;
        RemoveStaleSwitcherChildren();
        Build();
    }

    private void RemoveStaleSwitcherChildren()
    {
        for (int index = transform.childCount - 1; index >= 0; index--)
        {
            Destroy(transform.GetChild(index).gameObject);
        }
    }

    private void Build()
    {
        var canvas = GetComponent<Canvas>();
        if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        var scaler = GetComponent<CanvasScaler>();
        if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        if (GetComponent<GraphicRaycaster>() == null)
        {
            gameObject.AddComponent<GraphicRaycaster>();
        }

        var buttonObject = new GameObject(
            "Switch UI Button",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button));
        buttonObject.transform.SetParent(transform, false);
        buttonObject.SetActive(false);

        var buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.anchorMin = new Vector2(1f, 1f);
        buttonRect.anchorMax = new Vector2(1f, 1f);
        buttonRect.pivot = new Vector2(1f, 1f);
        buttonRect.anchoredPosition = new Vector2(-24f, -24f);
        buttonRect.sizeDelta = new Vector2(230f, 58f);

        var image = buttonObject.GetComponent<Image>();
        image.color = new Color32(20, 174, 205, 245);

        switchButton = buttonObject.GetComponent<Button>();
        switchButton.targetGraphic = image;
        switchButton.navigation = new Navigation { mode = Navigation.Mode.None };
        ColorBlock colors = switchButton.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color32(220, 250, 255, 255);
        colors.pressedColor = new Color32(165, 225, 238, 255);
        colors.selectedColor = Color.white;
        switchButton.colors = colors;
        switchButton.onClick.AddListener(SwitchScene);

        var labelObject = new GameObject(
            "Label",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(buttonObject.transform, false);
        var labelRect = (RectTransform)labelObject.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        buttonLabel = labelObject.GetComponent<TextMeshProUGUI>();
        buttonLabel.alignment = TextAlignmentOptions.Center;
        buttonLabel.color = Color.white;
        buttonLabel.fontSize = 18f;
        buttonLabel.fontStyle = FontStyles.Bold;
        buttonLabel.raycastTarget = false;
        buttonLabel.textWrappingMode = TextWrappingModes.NoWrap;

        BuildLoadingOverlay();

        SceneManager.sceneLoaded += OnSceneLoaded;
        Scene activeScene = SceneManager.GetActiveScene();
        PrepareLoadedScene(activeScene);
        Refresh(activeScene);
    }

    private void BuildLoadingOverlay()
    {
        var overlayObject = new GameObject(
            "Scene Loading Overlay",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(CanvasGroup));
        overlayObject.transform.SetParent(transform, false);

        var overlayRect = (RectTransform)overlayObject.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        var overlayImage = overlayObject.GetComponent<Image>();
        overlayImage.color = new Color32(6, 13, 24, 255);

        loadingOverlay = overlayObject.GetComponent<CanvasGroup>();
        loadingOverlay.alpha = 0f;
        loadingOverlay.interactable = false;
        loadingOverlay.blocksRaycasts = false;

        var textObject = new GameObject(
            "Loading Label",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        textObject.transform.SetParent(overlayObject.transform, false);

        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = new Vector2(0.5f, 0.5f);
        textRect.anchorMax = new Vector2(0.5f, 0.5f);
        textRect.pivot = new Vector2(0.5f, 0.5f);
        textRect.anchoredPosition = Vector2.zero;
        textRect.sizeDelta = new Vector2(620f, 90f);

        loadingText = textObject.GetComponent<TextMeshProUGUI>();
        loadingText.alignment = TextAlignmentOptions.Center;
        loadingText.color = new Color32(47, 205, 232, 255);
        loadingText.fontSize = 24f;
        loadingText.fontStyle = FontStyles.Bold;
        loadingText.raycastTarget = false;
        loadingText.textWrappingMode = TextWrappingModes.NoWrap;
        loadingText.text = "SWITCHING VIEW...";

        overlayObject.SetActive(false);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        PrepareLoadedScene(scene);
        Refresh(scene);
        if (!loadingScene && switchButton != null)
        {
            switchButton.interactable = true;
        }
    }

    private void Refresh(Scene scene)
    {
        bool supported = scene.name == MeetingScene || scene.name == AdvancedScene;
        gameObject.SetActive(supported);
        if (!supported || buttonLabel == null)
        {
            return;
        }

        buttonLabel.text = scene.name == AdvancedScene
            ? "UI 1 / MEETING"
            : "UI 2 / ADVANCED";
    }

    private void SwitchScene()
    {
        if (loadingScene)
        {
            return;
        }

        string current = SceneManager.GetActiveScene().name;
        string target = current == AdvancedScene ? MeetingScene : AdvancedScene;
        if (!Application.CanStreamedLevelBeLoaded(target))
        {
            Debug.LogError($"UI scene '{target}' is not included in Build Settings.");
            return;
        }

        loadingScene = true;
        switchButton.interactable = false;
        StartCoroutine(LoadSceneWithOverlay(target));
    }

    private IEnumerator LoadSceneWithOverlay(string target)
    {
        ShowLoading(true);

        // Give Unity one rendered frame to display the persistent cover before
        // unloading the current scene.
        yield return null;

        AsyncOperation operation = SceneManager.LoadSceneAsync(
            target,
            LoadSceneMode.Single);
        if (operation == null)
        {
            Debug.LogError($"Unable to load UI scene '{target}'.");
            loadingScene = false;
            ShowLoading(false);
            if (switchButton != null) switchButton.interactable = true;
            yield break;
        }

        while (!operation.isDone)
        {
            yield return null;
        }

        PrepareLoadedScene(SceneManager.GetActiveScene());

        // Runtime-created TextMeshPro elements finish their first layout pass
        // after the sceneLoaded event. Keep the cover until that pass is done.
        yield return null;
        Canvas.ForceUpdateCanvases();
        yield return null;

        loadingScene = false;
        ShowLoading(false);
        if (switchButton != null) switchButton.interactable = true;
        Refresh(SceneManager.GetActiveScene());
    }

    private static void PrepareLoadedScene(Scene scene)
    {
        if (!scene.IsValid()) return;
        RemoveLeakedMeetingDecorations(scene);
    }

    private static void RemoveLeakedMeetingDecorations(Scene activeScene)
    {
        string[] customNames =
        {
            "Custom Meeting Backdrop",
            "Custom Connection Panel",
            "LiveKit Meeting View"
        };

        Transform[] allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (Transform candidateTransform in allTransforms)
        {
            if (candidateTransform == null ||
                !candidateTransform.gameObject.scene.IsValid() ||
                candidateTransform.gameObject.scene == activeScene)
            {
                continue;
            }

            foreach (string objectName in customNames)
            {
                if (candidateTransform.name != objectName) continue;
                Destroy(candidateTransform.gameObject);
                break;
            }
        }
    }

    private void ShowLoading(bool visible)
    {
        if (loadingOverlay == null) return;

        GameObject overlayObject = loadingOverlay.gameObject;
        if (visible)
        {
            overlayObject.SetActive(true);
            overlayObject.transform.SetAsLastSibling();
            loadingOverlay.alpha = 1f;
            loadingOverlay.interactable = true;
            loadingOverlay.blocksRaycasts = true;
        }
        else
        {
            loadingOverlay.alpha = 0f;
            loadingOverlay.interactable = false;
            loadingOverlay.blocksRaycasts = false;
            overlayObject.SetActive(false);
        }
    }
}
