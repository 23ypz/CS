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
    private enum ScreenState { ModeSelect, SinglePlayer, Multiplayer, Lobby, Gameplay, Pause, PauseSettings }

    [Header("Network defaults")]
    public string defaultServerAddress = "127.0.0.1";
    public int defaultServerPort = 9000;

    private ScreenState state = ScreenState.ModeSelect;
    private Canvas canvas;
    private Font uiFont;
    private GameObject content;
    private MonsterModeManager monsterMode;
    private NetworkClient networkClient;
    // Monster settings are deliberately text fields in both game modes.  The
    // host sends the multiplayer values to the authoritative server when the
    // room starts; single-player passes the same values to MonsterModeManager.
    private InputField monsterCountInput;
    private InputField monsterHealthInput;
    private int selectedMonsterCount = 5;
    private int selectedMonsterHealth = 10;
    private Text settingsStatusText;
    private InputField addressField;
    private InputField portField;
    private InputField nameField;
    private Text statusText;
    private Text lobbyPlayersText;
    private Button readyButton;
    private Button hostStartButton;
    private Button singleModeButton;
    private Button multiplayerModeButton;
    private InputField sensitivityXInput;
    private InputField sensitivityYInput;
    private Text pauseSettingsStatus;
    private int handledClickFrame = -1;

    public static bool IsMenuVisible
    {
        get { return instance != null && instance.canvas != null && instance.canvas.enabled; }
    }

    public static bool IsGameplayPaused
    {
        get { return instance != null && instance.state == ScreenState.Pause; }
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
        Input.ResetInputAxes();
    }

    private void Update()
    {
        if (networkClient != null)
            networkClient.Tick();

        if ((state == ScreenState.Gameplay || state == ScreenState.Pause || state == ScreenState.PauseSettings) &&
            Input.GetKeyDown(KeyCode.Escape))
        {
            if (state == ScreenState.Pause)
                ResumeGame();
            else if (state == ScreenState.PauseSettings)
                ShowPauseMenu();
            else
                ShowPauseMenu();
            return;
        }

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
        // Prefer a CJK-capable font so Chinese labels remain legible on the
        // development image as well as typical Windows installations. Unity
        // selects the first installed font from this fallback list.
        uiFont = Font.CreateDynamicFontFromOSFont(
            new[] { "Noto Sans SC", "Microsoft YaHei", "SimHei", "Arial" }, 32);
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
        AddText("多人模式最多支持 4 名玩家", 20, new Vector2(0f, -130f), new Vector2(680f, 36f), TextAnchor.MiddleCenter, new Color(0.6f, 0.66f, 0.75f));
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
        AddText("怪物数量（1-20）", 22, new Vector2(-220f, 90f), new Vector2(230f, 40f), TextAnchor.MiddleLeft, Color.white);
        monsterCountInput = AddIntegerInput(selectedMonsterCount.ToString(), new Vector2(105f, 90f), new Vector2(220f, 48f));
        AddText("怪物血量（1-100）", 22, new Vector2(-220f, 10f), new Vector2(230f, 40f), TextAnchor.MiddleLeft, Color.white);
        monsterHealthInput = AddIntegerInput(selectedMonsterHealth.ToString(), new Vector2(105f, 10f), new Vector2(220f, 48f));
        settingsStatusText = AddText("请输入怪物数量和血量", 20, new Vector2(0f, -55f), new Vector2(620f, 34f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        AddButton("开始游戏", new Vector2(0f, -125f), new Vector2(300f, 62f), StartSingle, new Color(0.12f, 0.42f, 0.72f));
        AddButton("返回", new Vector2(0f, -205f), new Vector2(180f, 48f), ShowModeSelect, new Color(0.25f, 0.28f, 0.34f));
    }

    private void ShowMultiplayer()
    {
        state = ScreenState.Multiplayer;
        singleModeButton = null;
        multiplayerModeButton = null;
        Debug.Log("GameModeManager: 打开多人联机设置界面", this);
        canvas.enabled = true;
        ClearContent();
        AddText("多人联机模式", 32, new Vector2(0f, 235f), new Vector2(680f, 52f), TextAnchor.MiddleCenter, Color.white);
        AddText("连接服务器前设置怪物参数（房主设置生效）", 21, new Vector2(0f, 195f), new Vector2(680f, 34f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        AddText("服务器地址", 21, new Vector2(-235f, 135f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        addressField = AddInput(defaultServerAddress, new Vector2(90f, 142f), new Vector2(300f, 48f));
        AddText("服务器端口", 21, new Vector2(-235f, 78f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        portField = AddIntegerInput(defaultServerPort.ToString(), new Vector2(90f, 85f), new Vector2(300f, 48f), 5);
        AddText("玩家名称", 21, new Vector2(-235f, 21f), new Vector2(180f, 40f), TextAnchor.MiddleLeft, Color.white);
        nameField = AddInput("玩家", new Vector2(90f, 28f), new Vector2(300f, 48f));
        AddText("怪物数量（1-20）", 21, new Vector2(-235f, -38f), new Vector2(210f, 40f), TextAnchor.MiddleLeft, Color.white);
        monsterCountInput = AddIntegerInput(selectedMonsterCount.ToString(), new Vector2(90f, -31f), new Vector2(300f, 48f));
        AddText("怪物血量（1-100）", 21, new Vector2(-235f, -97f), new Vector2(210f, 40f), TextAnchor.MiddleLeft, Color.white);
        monsterHealthInput = AddIntegerInput(selectedMonsterHealth.ToString(), new Vector2(90f, -90f), new Vector2(300f, 48f));
        settingsStatusText = AddText("请输入服务器和怪物参数", 20, new Vector2(0f, -145f), new Vector2(650f, 32f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        statusText = settingsStatusText;
        AddButton("连接服务器", new Vector2(0f, -195f), new Vector2(300f, 58f), ConnectToServer, new Color(0.18f, 0.58f, 0.42f));
        AddButton("返回", new Vector2(0f, -255f), new Vector2(180f, 46f), ShowModeSelect, new Color(0.25f, 0.28f, 0.34f));
    }

    private void ShowLobby()
    {
        state = ScreenState.Lobby;
        singleModeButton = null;
        multiplayerModeButton = null;
        monsterCountInput = null;
        monsterHealthInput = null;
        canvas.enabled = true;
        ClearContent();
        AddText("多人游戏大厅", 32, new Vector2(0f, 205f), new Vector2(680f, 52f), TextAnchor.MiddleCenter, Color.white);
        lobbyPlayersText = AddText("正在获取玩家列表…", 22, new Vector2(0f, 100f), new Vector2(600f, 150f), TextAnchor.UpperCenter, Color.white);
        AddText("怪物数量：" + selectedMonsterCount + "    怪物血量：" + selectedMonsterHealth,
            21, new Vector2(0f, -25f), new Vector2(650f, 36f), TextAnchor.MiddleCenter, Color.white);
        statusText = AddText("已连接，等待其他玩家", 20, new Vector2(0f, -65f), new Vector2(600f, 36f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
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
        int count;
        int health;
        if (!TryReadMonsterSettings(out count, out health))
            return;
        selectedMonsterCount = count;
        selectedMonsterHealth = health;
        state = ScreenState.Gameplay;
        HideMenu();
        monsterMode.StartSinglePlayer(count, health);
    }

    private void ConnectToServer()
    {
        int count;
        int health;
        if (!TryReadMonsterSettings(out count, out health))
            return;
        selectedMonsterCount = count;
        selectedMonsterHealth = health;

        int port;
        if (!int.TryParse(portField.text, out port) || port < 1 || port > 65535)
        {
            SetSettingsMessage("端口必须是 1 到 65535 之间的整数");
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
            networkClient.SendReady(selectedMonsterCount, selectedMonsterHealth);
        if (readyButton != null)
            readyButton.GetComponentInChildren<Text>().text = "已准备";
    }

    private void StartAsHost()
    {
        if (networkClient != null)
            networkClient.SendStart(selectedMonsterCount, selectedMonsterHealth);
    }

    private void UpdateLobby(string players)
    {
        if (lobbyPlayersText != null)
            lobbyPlayersText.text = players;
    }

    private void StartNetworkGame(int count, int health)
    {
        // The server is authoritative.  Echo its clamped values into the local
        // manager so any HUD/minimap can use the same round settings.
        selectedMonsterCount = Mathf.Clamp(count, 1, 20);
        selectedMonsterHealth = Mathf.Clamp(health, 1, 100);
        state = ScreenState.Gameplay;
        HideMenu();
        if (monsterMode == null)
            monsterMode = FindObjectOfType<MonsterModeManager>();
        if (monsterMode != null)
        {
            monsterMode.monsterCount = selectedMonsterCount;
            monsterMode.monsterHealth = selectedMonsterHealth;
            monsterMode.StartNetworkMode();
        }
    }

    private void DisconnectToMenu()
    {
        if (networkClient != null)
        {
            networkClient.Disconnect();
        
            Destroy(networkClient);
            networkClient = null;
        }
        if (monsterMode != null)
            monsterMode.ReturnToMenu();
        Time.timeScale = 0f;
        ShowModeSelect();
    }

    private void ShowPauseMenu()
    {
        state = ScreenState.Pause;
        // Discard the mouse delta that opened the menu. This prevents one
        // frame of look input from rotating the background camera.
        Input.ResetInputAxes();
        if (networkClient != null)
            networkClient.SetPaused(true);
        if (monsterMode == null)
            monsterMode = FindObjectOfType<MonsterModeManager>();
        if (monsterMode != null)
            monsterMode.SetGameplayEnabled(false);

        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        canvas.enabled = true;
        ClearContent();
        AddText("游戏暂停", 38, new Vector2(0f, 120f), new Vector2(680f, 60f),
            TextAnchor.MiddleCenter, Color.white);
        AddText("当前玩家动作已暂停", 21, new Vector2(0f, 65f), new Vector2(680f, 36f),
            TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        AddButton("设置", new Vector2(0f, -20f), new Vector2(300f, 62f), ShowPauseSettings,
            new Color(0.12f, 0.42f, 0.72f));
        AddButton("继续游戏", new Vector2(0f, -95f), new Vector2(300f, 62f), ResumeGame,
            new Color(0.18f, 0.58f, 0.42f));
        AddButton("退出游戏", new Vector2(0f, -170f), new Vector2(300f, 62f), ExitCurrentRoom,
            new Color(0.65f, 0.20f, 0.18f));
    }

    private void ShowPauseSettings()
    {
        state = ScreenState.PauseSettings;
        canvas.enabled = true;
        ClearContent();
        AddText("游戏设置", 38, new Vector2(0f, 150f), new Vector2(680f, 60f),
            TextAnchor.MiddleCenter, Color.white);
        AddText("鼠标水平灵敏度（0.1-30）", 22, new Vector2(-205f, 65f),
            new Vector2(310f, 42f), TextAnchor.MiddleLeft, Color.white);
        AddText("鼠标垂直灵敏度（0.1-30）", 22, new Vector2(-205f, 0f),
            new Vector2(310f, 42f), TextAnchor.MiddleLeft, Color.white);

        PlayerControl player = FindSettingsPlayer();
        float x = player != null ? player.xScensitivity : PlayerPrefs.GetFloat("PlayerSensitivityX", 7f);
        float y = player != null ? player.yScensitivity : PlayerPrefs.GetFloat("PlayerSensitivityY", 7f);
        sensitivityXInput = AddDecimalInput(x.ToString("0.##"), new Vector2(145f, 65f), new Vector2(220f, 48f));
        sensitivityYInput = AddDecimalInput(y.ToString("0.##"), new Vector2(145f, 0f), new Vector2(220f, 48f));
        pauseSettingsStatus = AddText("调整后点击应用", 20, new Vector2(0f, -58f),
            new Vector2(620f, 34f), TextAnchor.MiddleCenter, new Color(0.65f, 0.75f, 0.9f));
        AddButton("应用", new Vector2(-105f, -125f), new Vector2(220f, 58f), ApplyPauseSettings,
            new Color(0.18f, 0.58f, 0.42f));
        AddButton("返回暂停菜单", new Vector2(130f, -125f), new Vector2(230f, 58f), ShowPauseMenu,
            new Color(0.25f, 0.28f, 0.34f));
    }

    private PlayerControl FindSettingsPlayer()
    {
        PlayerControl[] players = FindObjectsOfType<PlayerControl>();
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null && players[i].enabled && players[i].gameObject.activeInHierarchy)
                return players[i];
        }
        return players.Length > 0 ? players[0] : null;
    }

    private void ApplyPauseSettings()
    {
        float x;
        float y;
        if (!TryReadSensitivity(out x, out y))
            return;

        PlayerPrefs.SetFloat("PlayerSensitivityX", x);
        PlayerPrefs.SetFloat("PlayerSensitivityY", y);
        PlayerPrefs.Save();
        PlayerControl[] players = FindObjectsOfType<PlayerControl>();
        for (int i = 0; i < players.Length; i++)
        {
            // PlayerControl is disabled while the pause overlay is open, but
            // the setting must take effect immediately when gameplay resumes.
            if (players[i] != null)
            {
                players[i].xScensitivity = x;
                players[i].yScensitivity = y;
            }
        }
        if (pauseSettingsStatus != null)
        {
            pauseSettingsStatus.text = "设置已应用";
            pauseSettingsStatus.color = new Color(0.45f, 1f, 0.55f);
        }
    }

    private bool TryReadSensitivity(out float x, out float y)
    {
        x = 7f;
        y = 7f;
        if (sensitivityXInput == null || sensitivityYInput == null)
            return true;
        if (!float.TryParse(sensitivityXInput.text.Trim(), out x) || x < 0.1f || x > 30f ||
            !float.TryParse(sensitivityYInput.text.Trim(), out y) || y < 0.1f || y > 30f)
        {
            if (pauseSettingsStatus != null)
            {
                pauseSettingsStatus.text = "灵敏度必须是 0.1 到 30 之间的数字";
                pauseSettingsStatus.color = new Color(1f, 0.72f, 0.35f);
            }
            return false;
        }
        return true;
    }

    private void ResumeGame()
    {
        state = ScreenState.Gameplay;
        Time.timeScale = 1f;
        if (networkClient != null)
            networkClient.SetPaused(false);
        if (monsterMode != null)
            monsterMode.SetGameplayEnabled(true);
        if (canvas != null)
            canvas.enabled = false;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void ExitCurrentRoom()
    {
        if (networkClient != null)
        {
            networkClient.SetPaused(false);
            networkClient.Disconnect();
            Destroy(networkClient);
            networkClient = null;
        }
        if (monsterMode == null)
            monsterMode = FindObjectOfType<MonsterModeManager>();
        if (monsterMode != null)
            monsterMode.ReturnToMenu();
        state = ScreenState.ModeSelect;
        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
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

    private bool TryReadMonsterSettings(out int count, out int health)
    {
        count = selectedMonsterCount;
        health = selectedMonsterHealth;

        if (monsterCountInput == null || monsterHealthInput == null)
            return true;

        if (!int.TryParse(monsterCountInput.text.Trim(), out count) || count < 1 || count > 20)
        {
            SetSettingsMessage("怪物数量必须是 1 到 20 之间的整数");
            return false;
        }

        if (!int.TryParse(monsterHealthInput.text.Trim(), out health) || health < 1 || health > 100)
        {
            SetSettingsMessage("怪物血量必须是 1 到 100 之间的整数");
            return false;
        }

        return true;
    }

    private void SetSettingsMessage(string message)
    {
        if (settingsStatusText != null)
        {
            settingsStatusText.text = message;
            settingsStatusText.color = new Color(1f, 0.72f, 0.35f);
        }
        else
        {
            SetStatus(message);
        }
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
        input.targetGraphic = image;
        input.caretColor = Color.white;
        input.selectionColor = new Color(0.25f, 0.55f, 0.9f, 0.65f);
        GameObject textObject = new GameObject("Text");
        textObject.transform.SetParent(go.transform, false);
        Text text = textObject.AddComponent<Text>();
        text.font = uiFont;
        text.fontSize = 21;
        text.color = Color.white;
        text.raycastTarget = false;
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;
        RectTransform textRect = text.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(14f, 0f);
        textRect.offsetMax = new Vector2(-14f, 0f);
        input.textComponent = text;
        // Assign after wiring the text component so the initial value is
        // rendered immediately, even before the first focus/update event.
        input.text = value;
        return input;
    }

    private InputField AddIntegerInput(string value, Vector2 position, Vector2 dimensions, int characterLimit = 3)
    {
        InputField input = AddInput(value, position, dimensions);
        input.contentType = InputField.ContentType.IntegerNumber;
        input.characterLimit = characterLimit;
        input.lineType = InputField.LineType.SingleLine;
        return input;
    }

    private InputField AddDecimalInput(string value, Vector2 position, Vector2 dimensions)
    {
        InputField input = AddInput(value, position, dimensions);
        input.contentType = InputField.ContentType.DecimalNumber;
        input.characterLimit = 5;
        input.lineType = InputField.LineType.SingleLine;
        return input;
    }
}
