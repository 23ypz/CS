using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The single entry point for choosing local or online play. The UI is built at
/// runtime so the existing Player prefab, including its AudioSource, remains the
/// source of truth and is never replaced by a menu prefab.
/// </summary>
public class GameModeManager : MonoBehaviour
{
    private static GameModeManager instance;
    private enum ScreenState { ModeSelect, SinglePlayer, Multiplayer, Lobby }

    [Header("Network defaults")]
    public string defaultServerAddress = "127.0.0.1";
    public int defaultServerPort = 9000;

    private ScreenState state = ScreenState.ModeSelect;
    private Canvas canvas;
    private Font uiFont;
    private GameObject content;
    private MonsterModeManager monsterMode;
    private NetworkClient networkClient;
    private Slider monsterCountSlider;
    private Slider monsterHealthSlider;
    private InputField addressField;
    private InputField portField;
    private InputField nameField;
    private Text statusText;
    private Text lobbyPlayersText;
    private Button readyButton;
    private Button hostStartButton;
    private Button singleModeButton;
    private Button multiplayerModeButton;
    private int handledClickFrame = -1;

    public static bool IsMenuVisible
    {
        get { return instance != null && instance.canvas != null && instance.canvas.enabled; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateAtStartup()
    {
        if (instance == null && FindObjectOfType<GameModeManager>() == null)
        {
            GameObject root = new GameObject("GameModeManager");
            DontDestroyOnLoad(root);
            root.AddComponent<GameModeManager>();
        }
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        monsterMode = FindObjectOfType<MonsterModeManager>();
        if (monsterMode != null)
            monsterMode.SuppressLegacyMenu();
        EnsureEventSystem();
        BuildCanvas();
        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        ShowModeSelect();
    }

    private void Update()
    {
        if (networkClient != null)
            networkClient.Tick();

        // PlayerControl historically locks the cursor in Awake. Keep the menu
        // interaction state authoritative while any setup screen is visible.
        if (canvas != null && canvas.enabled)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // Fallback for scenes opened with an incomplete/disabled EventSystem.
        // The normal Button event still handles clicks when available.
        if (canvas != null && canvas.enabled && state == ScreenState.ModeSelect &&
            Input.GetMouseButtonDown(0) && handledClickFrame != Time.frameCount)
        {
            Vector2 pointer = Input.mousePosition;
            if (singleModeButton != null && RectTransformUtility.RectangleContainsScreenPoint(
                singleModeButton.GetComponent<RectTransform>(), pointer, canvas.worldCamera))
            {
                handledClickFrame = Time.frameCount;
                ShowSinglePlayer();
            }
            else if (multiplayerModeButton != null && RectTransformUtility.RectangleContainsScreenPoint(
                multiplayerModeButton.GetComponent<RectTransform>(), pointer, canvas.worldCamera))
            {
                handledClickFrame = Time.frameCount;
                ShowMultiplayer();
            }
        }
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
        if (networkClient != null)
            networkClient.Disconnect();
        if (Time.timeScale == 0f)
            Time.timeScale = 1f;
    }

    private void EnsureEventSystem()
    {
        EventSystem eventSystem = FindObjectOfType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject objectRoot = new GameObject("EventSystem");
            eventSystem = objectRoot.AddComponent<EventSystem>();
        }

        eventSystem.enabled = true;
        BaseInputModule inputModule = eventSystem.GetComponent<BaseInputModule>();
        if (inputModule == null)
            inputModule = eventSystem.gameObject.AddComponent<StandaloneInputModule>();
        inputModule.enabled = true;
    }

    private void BuildCanvas()
    {
        GameObject canvasObject = new GameObject("GameModeCanvas");
        canvasObject.transform.SetParent(transform, false);
        canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        canvasObject.AddComponent<GraphicRaycaster>();
        // Microsoft YaHei is available on Windows and has complete Chinese
        // glyph coverage. The Arial fallback keeps the menu usable on other
        // platforms without changing any Player assets.
        uiFont = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 32);
        if (uiFont == null)
            uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private void ClearContent()
    {
        if (content != null)
            Destroy(content);
        content = new GameObject("PanelContent");
        content.transform.SetParent(canvas.transform, false);
        RectTransform rect = content.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(760f, 560f);

        Image panel = content.AddComponent<Image>();
        panel.color = new Color(0.035f, 0.055f, 0.09f, 0.97f);
        panel.raycastTarget = false;
    }

    private void ShowModeSelect()
    {
        state = ScreenState.ModeSelect;
        canvas.enabled = true;
        ClearContent();
        AddText("城市行动", 38, new Vector2(0f, 190f), new Vector2(680f, 60f), TextAnchor.MiddleCenter, Color.white);
        AddText("选择游戏模式", 24, new Vector2(0f, 125f), new Vector2(680f, 42f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        singleModeButton = AddButton("单人怪物模式", new Vector2(0f, 45f), new Vector2(390f, 64f), ShowSinglePlayer, new Color(0.12f, 0.42f, 0.72f));
        multiplayerModeButton = AddButton("多人联机模式", new Vector2(0f, -45f), new Vector2(390f, 64f), ShowMultiplayer, new Color(0.18f, 0.58f, 0.42f));
        AddText("多人模式最多支持 4 名玩家", 19, new Vector2(0f, -130f), new Vector2(680f, 36f), TextAnchor.MiddleCenter, new Color(0.6f, 0.66f, 0.75f));
    }

    private void ShowSinglePlayer()
    {
        state = ScreenState.SinglePlayer;
        singleModeButton = null;
        multiplayerModeButton = null;
        Debug.Log("GameModeManager: 打开单人怪物模式设置界面", this);
        canvas.enabled = true;
        ClearContent();
        AddText("单人怪物模式", 32, new Vector2(0f, 205f), new Vector2(680f, 52f), TextAnchor.MiddleCenter, Color.white);
        AddText("调整参数后开始游戏", 21, new Vector2(0f, 160f), new Vector2(680f, 34f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        AddText("怪物数量", 22, new Vector2(-220f, 90f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        monsterCountSlider = AddSlider(new Vector2(80f, 100f), 1f, 20f, 5f);
        AddText("怪物血量", 22, new Vector2(-220f, 10f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        monsterHealthSlider = AddSlider(new Vector2(80f, 20f), 1f, 100f, 10f);
        AddButton("开始游戏", new Vector2(0f, -100f), new Vector2(300f, 62f), StartSingle, new Color(0.12f, 0.42f, 0.72f));
        AddButton("返回", new Vector2(0f, -185f), new Vector2(180f, 48f), ShowModeSelect, new Color(0.25f, 0.28f, 0.34f));
    }

    private void ShowMultiplayer()
    {
        state = ScreenState.Multiplayer;
        singleModeButton = null;
        multiplayerModeButton = null;
        Debug.Log("GameModeManager: 打开多人联机设置界面", this);
        canvas.enabled = true;
        ClearContent();
        AddText("多人联机模式", 32, new Vector2(0f, 215f), new Vector2(680f, 52f), TextAnchor.MiddleCenter, Color.white);
        AddText("连接 Python 游戏服务器", 21, new Vector2(0f, 170f), new Vector2(680f, 34f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        AddText("服务器地址", 21, new Vector2(-235f, 105f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        addressField = AddInput(defaultServerAddress, new Vector2(90f, 112f), new Vector2(300f, 48f));
        AddText("服务器端口", 21, new Vector2(-235f, 40f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        portField = AddInput(defaultServerPort.ToString(), new Vector2(90f, 47f), new Vector2(300f, 48f));
        AddText("玩家名称", 21, new Vector2(-235f, -25f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        nameField = AddInput("玩家", new Vector2(90f, -18f), new Vector2(300f, 48f));
        statusText = AddText("请输入服务器信息", 18, new Vector2(0f, -85f), new Vector2(600f, 35f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        AddButton("连接服务器", new Vector2(0f, -145f), new Vector2(300f, 60f), ConnectToServer, new Color(0.18f, 0.58f, 0.42f));
        AddButton("返回", new Vector2(0f, -220f), new Vector2(180f, 48f), ShowModeSelect, new Color(0.25f, 0.28f, 0.34f));
    }

    private void ShowLobby()
    {
        state = ScreenState.Lobby;
        singleModeButton = null;
        multiplayerModeButton = null;
        canvas.enabled = true;
        ClearContent();
        AddText("多人游戏大厅", 32, new Vector2(0f, 205f), new Vector2(680f, 52f), TextAnchor.MiddleCenter, Color.white);
        lobbyPlayersText = AddText("正在获取玩家列表…", 22, new Vector2(0f, 100f), new Vector2(600f, 150f), TextAnchor.UpperCenter, Color.white);
        statusText = AddText("已连接，等待其他玩家", 19, new Vector2(0f, -45f), new Vector2(600f, 36f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        readyButton = AddButton("准备", new Vector2(-145f, -125f), new Vector2(240f, 60f), ToggleReady, new Color(0.18f, 0.58f, 0.42f));
        hostStartButton = AddButton("房主开始", new Vector2(145f, -125f), new Vector2(240f, 60f), StartAsHost, new Color(0.12f, 0.42f, 0.72f));
        AddButton("断开连接", new Vector2(0f, -205f), new Vector2(200f, 46f), DisconnectToMenu, new Color(0.25f, 0.28f, 0.34f));
    }

    private void StartSingle()
    {
        if (monsterMode == null)
            monsterMode = FindObjectOfType<MonsterModeManager>();
        if (monsterMode == null)
        {
            SetStatus("没有找到单人游戏管理器");
            return;
        }
        HideMenu();
        monsterMode.StartSinglePlayer(Mathf.RoundToInt(monsterCountSlider.value), Mathf.RoundToInt(monsterHealthSlider.value));
    }

    private void ConnectToServer()
    {
        int port;
        if (!int.TryParse(portField.text, out port) || port < 1 || port > 65535)
        {
            SetStatus("端口必须是 1 到 65535 之间的数字");
            return;
        }
        if (networkClient != null)
            networkClient.Disconnect();
        networkClient = gameObject.AddComponent<NetworkClient>();
        networkClient.StatusChanged += SetStatus;
        networkClient.LobbyEntered += ShowLobby;
        networkClient.LobbyChanged += UpdateLobby;
        networkClient.GameStarted += StartNetworkGame;
        networkClient.Connect(addressField.text.Trim(), port, nameField.text.Trim());
        SetStatus("正在连接服务器…");
    }

    private void ToggleReady()
    {
        if (networkClient != null)
            networkClient.SendReady();
        if (readyButton != null)
            readyButton.GetComponentInChildren<Text>().text = "已准备";
    }

    private void StartAsHost()
    {
        if (networkClient != null)
            networkClient.SendStart();
    }

    private void UpdateLobby(string players)
    {
        if (lobbyPlayersText != null)
            lobbyPlayersText.text = players;
    }

    private void StartNetworkGame()
    {
        HideMenu();
        if (monsterMode == null)
            monsterMode = FindObjectOfType<MonsterModeManager>();
        if (monsterMode != null)
            monsterMode.StartNetworkMode();
    }

    private void DisconnectToMenu()
    {
        if (networkClient != null)
            networkClient.Disconnect();
        ShowModeSelect();
    }

    private void HideMenu()
    {
        if (canvas != null)
            canvas.enabled = false;
        Time.timeScale = 1f;
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;
    }

    private void OnGUI()
    {
        if (canvas == null || !canvas.enabled || state != ScreenState.ModeSelect)
            return;

        if (Event.current.type != EventType.MouseDown || Event.current.button != 0)
            return;

        float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
        float centerX = Screen.width * 0.5f;
        float centerY = Screen.height * 0.5f;
        Rect singleRect = new Rect(centerX - 195f * scale, centerY - 77f * scale,
            390f * scale, 64f * scale);
        Rect multiplayerRect = new Rect(centerX - 195f * scale, centerY + 13f * scale,
            390f * scale, 64f * scale);

        if (singleRect.Contains(Event.current.mousePosition))
        {
            Event.current.Use();
            ShowSinglePlayer();
        }
        else if (multiplayerRect.Contains(Event.current.mousePosition))
        {
            Event.current.Use();
            ShowMultiplayer();
        }
    }

    private Text AddText(string value, int size, Vector2 position, Vector2 dimensions, TextAnchor alignment, Color color)
    {
        GameObject go = new GameObject("Text");
        go.transform.SetParent(content.transform, false);
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.anchoredPosition = position;
        rect.sizeDelta = dimensions;
        Text text = go.AddComponent<Text>();
        text.text = value;
        text.font = uiFont;
        text.fontSize = size;
        text.alignment = alignment;
        text.color = color;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    private Button AddButton(string label, Vector2 position, Vector2 dimensions, UnityEngine.Events.UnityAction action, Color color)
    {
        GameObject go = new GameObject("Button_" + label);
        go.transform.SetParent(content.transform, false);
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.anchoredPosition = position;
        rect.sizeDelta = dimensions;
        Image image = go.AddComponent<Image>();
        image.color = color;
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() =>
        {
            handledClickFrame = Time.frameCount;
            action();
        });
        GameObject textObject = new GameObject("Label");
        textObject.transform.SetParent(go.transform, false);
        Text text = textObject.AddComponent<Text>();
        text.text = label;
        text.font = uiFont;
        text.fontSize = 24;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        RectTransform textRect = text.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        return button;
    }

    private Slider AddSlider(Vector2 position, float min, float max, float value)
    {
        GameObject go = new GameObject("Slider");
        go.transform.SetParent(content.transform, false);
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(380f, 35f);
        Slider slider = go.AddComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;
        Image background = go.AddComponent<Image>();
        background.color = new Color(0.18f, 0.22f, 0.3f);
        GameObject fill = new GameObject("Fill");
        fill.transform.SetParent(go.transform, false);
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = new Color(0.25f, 0.65f, 0.95f);
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = new Vector2(0f, 0.25f);
        fillRect.anchorMax = new Vector2(1f, 0.75f);
        slider.fillRect = fillRect;
        return slider;
    }

    private InputField AddInput(string value, Vector2 position, Vector2 dimensions)
    {
        GameObject go = new GameObject("InputField");
        go.transform.SetParent(content.transform, false);
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.anchoredPosition = position;
        rect.sizeDelta = dimensions;
        Image image = go.AddComponent<Image>();
        image.color = new Color(0.12f, 0.15f, 0.21f);
        InputField input = go.AddComponent<InputField>();
        input.text = value;
        GameObject textObject = new GameObject("Text");
        textObject.transform.SetParent(go.transform, false);
        Text text = textObject.AddComponent<Text>();
        text.font = uiFont;
        text.fontSize = 21;
        text.color = Color.white;
        text.raycastTarget = false;
        text.alignment = TextAnchor.MiddleLeft;
        RectTransform textRect = text.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(14f, 0f);
        textRect.offsetMax = new Vector2(-14f, 0f);
        input.textComponent = text;
        return input;
    }
}
