using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/* 单机和联机共用 HUD。Canvas 动态创建，小地图以本地玩家为中心。 */
public sealed class GameplayHud : MonoBehaviour
{
    private static GameplayHud instance;

    [Header("小地图")]
    [Tooltip("小地图显示的实际场景半径。")]
    public float minimapWorldRadius = 45f;
    public int minimapPixelSize = 224;

    [Header("标记颜色")]
    public Color minimapBackground = new Color(0.025f, 0.045f, 0.07f, 0.88f);
    public Color localPlayerColor = new Color(0.18f, 1f, 0.35f, 1f);
    public Color teammateColor = new Color(0.25f, 0.60f, 1f, 1f);
    public Color monsterColor = new Color(1f, 0.20f, 0.18f, 1f);

    private Canvas canvas;
    private RectTransform mapRoot;
    private RectTransform markerLayer;
    private Image localMarker;
    private RectTransform leaderboardRoot;
    private Text leaderboardTitle;
    private Text leaderboardBody;
    private RectTransform healthRoot;
    private Image healthFill;
    private Text healthLabel;
    private RectTransform respawnRoot;
    private Image respawnFill;
    private Text respawnLabel;
    private RectTransform ammoRoot;
    private Text ammoLabel;
    private RectTransform ammoActionRoot;
    private Image ammoActionFill;
    private Text ammoActionLabel;
    private Text waveStatusLabel;
    private RectTransform waveWarningRoot;
    private Text waveWarningLabel;
    private Font uiFont;
    private Sprite circleSprite;
    private Sprite triangleSprite;

    private Transform localPlayer;
    private MonsterModeManager monsterMode;
    private NetworkClient subscribedClient;
    private NetScore[] latestScores = new NetScore[0];
    private bool roundStarted;

    // 网络视图的 Transform 生命周期稳定，适合保存标记。
    private readonly Dictionary<Transform, Image> teammateMarkers =
        new Dictionary<Transform, Image>();
    private readonly Dictionary<Transform, Image> monsterMarkers =
        new Dictionary<Transform, Image>();
    private readonly HashSet<Transform> seenTeammates = new HashSet<Transform>();
    private readonly HashSet<Transform> seenMonsters = new HashSet<Transform>();

    private float referenceRefreshClock; // 场景引用刷新间隔计时。
    private float singlePlayerScanClock; // 单机怪物标记扫描计时。
    private bool networkHealthKnown;
    private float networkHealth = 100f;
    private float networkMaxHealth = 100f;
    private bool networkDead;
    private float networkRespawnRemaining;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateAtStartup()
    {
        if (instance != null)
            return;

        GameplayHud existing = FindObjectOfType<GameplayHud>();
        if (existing != null)
        {
            instance = existing;
            return;
        }

        GameObject root = new GameObject("GameplayHud");
        DontDestroyOnLoad(root);
        root.AddComponent<GameplayHud>();
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
        BuildCanvas();
        FindGameplayReferences();
    }

    private void OnDestroy()
    {
        if (subscribedClient != null)
        {
            subscribedClient.ScoresChanged -= OnScoresChanged;
            subscribedClient.GameStarted -= OnNetworkGameStarted;
            subscribedClient.PlayerHealthChanged -= OnPlayerHealthChanged;
        }

        if (circleSprite != null)
            Destroy(circleSprite);
        if (triangleSprite != null)
            Destroy(triangleSprite);

        if (instance == this)
            instance = null;
    }

    private void Update()
    {
        /* 场景切换后对象可能晚一帧生成，定时重新查找引用。 */
        // 场景切换时引用可能暂时为空，Player 也可能稍后生成。
        referenceRefreshClock -= Time.unscaledDeltaTime;
        if (referenceRefreshClock <= 0f)
        {
            referenceRefreshClock = 0.25f;
            FindGameplayReferences();
        }

        NetworkClient client = NetworkClient.Active;
        SubscribeToClient(client);

        // 开局消息可能早于地图回传；开局后保持 HUD 可见。
        bool online = client != null && (client.IsGameStarted || roundStarted);
        bool solo = monsterMode != null && monsterMode.IsPlaying && !online;
        bool show = online || solo;

        if (canvas != null)
            canvas.enabled = show;

        if (!show)
        {
            ClearMarkers(teammateMarkers);
            ClearMarkers(monsterMarkers);
            UpdateAmmoHud(false);
            return;
        }

        UpdateMinimap(online, client);
        UpdateLeaderboard(online);
        UpdatePlayerHealth(online);
        UpdateAmmoHud(true);
        UpdateWaveHud(online, client);
    }

    private void FindGameplayReferences()
    {
        /* 只保存 Transform，不复制 Player，避免破坏原有组件和声音。 */
        if (localPlayer == null)
        {
            GameObject playerObject = GameObject.FindGameObjectWithTag("Player");
            if (playerObject != null)
                localPlayer = playerObject.transform;
        }

        if (monsterMode == null)
            monsterMode = FindObjectOfType<MonsterModeManager>();

        // 返回菜单后可能生成新的管理器，下次刷新会重新查找。
        if (localPlayer == null)
        {
            GameObject playerObject = GameObject.FindGameObjectWithTag("Player");
            if (playerObject != null)
                localPlayer = playerObject.transform;
        }
    }

    private void SubscribeToClient(NetworkClient client)
    {
        if (subscribedClient == client)
            return;

        if (subscribedClient != null)
        {
            subscribedClient.ScoresChanged -= OnScoresChanged;
            subscribedClient.GameStarted -= OnNetworkGameStarted;
            subscribedClient.PlayerHealthChanged -= OnPlayerHealthChanged;
        }

        subscribedClient = client;
        latestScores = new NetScore[0];
        roundStarted = false;
        networkHealthKnown = false;
        networkHealth = 100f;
        networkMaxHealth = 100f;
        networkDead = false;
        networkRespawnRemaining = 0f;
        if (subscribedClient != null)
        {
            subscribedClient.ScoresChanged += OnScoresChanged;
            subscribedClient.GameStarted += OnNetworkGameStarted;
            subscribedClient.PlayerHealthChanged += OnPlayerHealthChanged;
        }
    }

    private void OnPlayerHealthChanged(int health, int maximum, bool dead, float respawnRemaining)
    {
        networkHealthKnown = true;
        networkHealth = Mathf.Max(0f, health);
        networkMaxHealth = Mathf.Max(1f, maximum);
        networkDead = dead;
        networkRespawnRemaining = Mathf.Max(0f, respawnRemaining);
    }

    private void OnScoresChanged(NetScore[] scores)
    {
        latestScores = scores ?? new NetScore[0];
    }

    private void OnNetworkGameStarted(int count, int health)
    {
        roundStarted = true;
    }

    private void BuildCanvas()
    {
        /* HUD 使用独立 Canvas，便于菜单暂停时整体隐藏。 */
        GameObject canvasObject = new GameObject("GameplayHudCanvas");
        canvasObject.transform.SetParent(transform, false);

        canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 40;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

        // 使用支持中文的字体回退，避免中文显示为空框。
        uiFont = Font.CreateDynamicFontFromOSFont(
            new[] { "Noto Sans SC", "Microsoft YaHei", "SimHei", "Arial" }, 28);
        if (uiFont == null)
            uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

        circleSprite = CreateCircleSprite(Mathf.Max(64, minimapPixelSize));
        triangleSprite = CreateTriangleSprite(64);
        BuildMinimap();
        BuildLeaderboard();
        BuildHealthBar();
        BuildAmmoHud();
        BuildWaveHud();
        canvas.enabled = false;
    }

    private void BuildHealthBar()
    {
        GameObject root = new GameObject("PlayerHealthBar");
        root.transform.SetParent(canvas.transform, false);
        healthRoot = root.AddComponent<RectTransform>();
        healthRoot.anchorMin = new Vector2(0f, 0f); healthRoot.anchorMax = new Vector2(0f, 0f);
        healthRoot.pivot = new Vector2(0f, 0f); healthRoot.anchoredPosition = new Vector2(24f, 24f);
        healthRoot.sizeDelta = new Vector2(330f, 62f);
        Image panel = root.AddComponent<Image>(); panel.color = new Color(0.025f, 0.045f, 0.07f, 0.88f); panel.raycastTarget = false;
        GameObject fillObject = new GameObject("HealthFill"); fillObject.transform.SetParent(root.transform, false);
        healthFill = fillObject.AddComponent<Image>(); healthFill.color = new Color(0.18f, 0.9f, 0.25f, 1f);
        // 左锚点填充条由 UpdatePlayerHealth 按比例缩放宽度。
        healthFill.type = Image.Type.Simple;
        RectTransform fillRect = healthFill.rectTransform;
        fillRect.anchorMin = new Vector2(0f, 0.5f); fillRect.anchorMax = new Vector2(0f, 0.5f);
        fillRect.pivot = new Vector2(0f, 0.5f); fillRect.anchoredPosition = new Vector2(12f, 9f);
        fillRect.sizeDelta = new Vector2(306f, 20f);
        healthLabel = CreateText("生命值 100 / 100", 22, Color.white, TextAnchor.MiddleLeft); healthLabel.transform.SetParent(root.transform, false);
        RectTransform labelRect = healthLabel.rectTransform; labelRect.anchorMin = new Vector2(0f, 0f); labelRect.anchorMax = new Vector2(1f, 0.5f); labelRect.offsetMin = new Vector2(12f, 3f); labelRect.offsetMax = new Vector2(-12f, 0f);

        GameObject respawn = new GameObject("RespawnProgress"); respawn.transform.SetParent(canvas.transform, false);
        respawnRoot = respawn.AddComponent<RectTransform>(); respawnRoot.anchorMin = new Vector2(0.5f, 0.5f); respawnRoot.anchorMax = new Vector2(0.5f, 0.5f); respawnRoot.pivot = new Vector2(0.5f, 0.5f); respawnRoot.anchoredPosition = new Vector2(0f, -170f); respawnRoot.sizeDelta = new Vector2(360f, 76f);
        Image respawnPanel = respawn.AddComponent<Image>(); respawnPanel.color = new Color(0.02f, 0.02f, 0.02f, 0.78f); respawnPanel.raycastTarget = false;
        GameObject respawnFillObject = new GameObject("RespawnFill"); respawnFillObject.transform.SetParent(respawn.transform, false);
        respawnFill = respawnFillObject.AddComponent<Image>(); respawnFill.color = new Color(0.3f, 0.65f, 1f, 1f); respawnFill.type = Image.Type.Filled; respawnFill.fillMethod = Image.FillMethod.Horizontal; respawnFill.fillOrigin = 0;
        RectTransform respawnFillRect = respawnFill.rectTransform; respawnFillRect.anchorMin = new Vector2(0f, 0f); respawnFillRect.anchorMax = new Vector2(1f, 0f); respawnFillRect.offsetMin = new Vector2(14f, 12f); respawnFillRect.offsetMax = new Vector2(-14f, 30f);
        respawnLabel = CreateText("正在复活...", 24, Color.white, TextAnchor.MiddleCenter); respawnLabel.transform.SetParent(respawn.transform, false);
        RectTransform respawnLabelRect = respawnLabel.rectTransform; respawnLabelRect.anchorMin = Vector2.zero; respawnLabelRect.anchorMax = Vector2.one; respawnLabelRect.offsetMin = new Vector2(8f, 30f); respawnLabelRect.offsetMax = new Vector2(-8f, -4f);
        respawnRoot.gameObject.SetActive(false);
    }

    private void BuildWaveHud()
    {
        waveStatusLabel = CreateText("", 24, Color.white, TextAnchor.MiddleCenter);
        waveStatusLabel.name = "WaveStatus";
        waveStatusLabel.transform.SetParent(canvas.transform, false);
        RectTransform statusRect = waveStatusLabel.rectTransform;
        statusRect.anchorMin = statusRect.anchorMax = new Vector2(0.5f, 1f);
        statusRect.pivot = new Vector2(0.5f, 1f);
        statusRect.anchoredPosition = new Vector2(0f, -24f);
        statusRect.sizeDelta = new Vector2(420f, 40f);
        Outline statusOutline = waveStatusLabel.gameObject.AddComponent<Outline>();
        statusOutline.effectColor = Color.black;
        statusOutline.effectDistance = new Vector2(1f, -1f);

        GameObject warning = new GameObject("WaveArrivalCountdown");
        warning.transform.SetParent(canvas.transform, false);
        waveWarningRoot = warning.AddComponent<RectTransform>();
        waveWarningRoot.anchorMin = waveWarningRoot.anchorMax = new Vector2(0.5f, 1f);
        waveWarningRoot.pivot = new Vector2(0.5f, 1f);
        waveWarningRoot.anchoredPosition = new Vector2(0f, -82f);
        waveWarningRoot.sizeDelta = new Vector2(560f, 78f);
        Image background = warning.AddComponent<Image>();
        background.color = new Color(0.08f, 0.04f, 0.01f, 0.85f);
        background.raycastTarget = false;
        waveWarningLabel = CreateText("", 32, new Color(1f, 0.78f, 0.25f), TextAnchor.MiddleCenter);
        waveWarningLabel.transform.SetParent(warning.transform, false);
        RectTransform textRect = waveWarningLabel.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(12f, 6f);
        textRect.offsetMax = new Vector2(-12f, -6f);
        warning.SetActive(false);
    }

    private void UpdateWaveHud(bool networkPlaying, NetworkClient client)
    {
        int wave = networkPlaying ? client.CurrentWave : (monsterMode != null ? monsterMode.CurrentWave : 0);
        int total = networkPlaying ? client.TotalWaves : MonsterWaveSequence.TotalWaves;
        int next = networkPlaying ? client.NextWave : (monsterMode != null ? monsterMode.NextWave : 0);
        float remaining = networkPlaying ? client.WaveRemaining : (monsterMode != null ? monsterMode.WaveRemaining : 0f);
        bool complete = networkPlaying ? client.WavesComplete : (monsterMode != null && monsterMode.WavesComplete);
        waveStatusLabel.gameObject.SetActive(wave > 0);
        waveStatusLabel.text = complete ? "三波怪物已全部消灭！" : "第 " + wave + " / " + total + " 波";

        bool warn = wave > 0 && next > 0 && remaining <= MonsterWaveSequence.WarningSeconds && !complete;
        waveWarningRoot.gameObject.SetActive(warn);
        if (warn)
        {
            // 等待服务器快照时保留最后一秒，客户端不自行生成波次。
            int seconds = Mathf.Max(1, Mathf.CeilToInt(remaining));
            waveWarningLabel.text = "第 " + next + " 波怪物来袭 · " + seconds + " 秒";
        }
    }

    private void BuildAmmoHud()
    {
        GameObject root = new GameObject("AmmoHud");
        root.transform.SetParent(canvas.transform, false);
        ammoRoot = root.AddComponent<RectTransform>();
        ammoRoot.anchorMin = new Vector2(0f, 0f); ammoRoot.anchorMax = new Vector2(0f, 0f);
        ammoRoot.pivot = new Vector2(0f, 0f); ammoRoot.anchoredPosition = new Vector2(24f, 92f);
        ammoRoot.sizeDelta = new Vector2(330f, 42f);
        Image panel = root.AddComponent<Image>(); panel.color = new Color(0.025f, 0.045f, 0.07f, 0.88f); panel.raycastTarget = false;
        ammoLabel = CreateText("\u5f39\u836f 50 / 200", 22, Color.white, TextAnchor.MiddleLeft);
        ammoLabel.transform.SetParent(root.transform, false);
        RectTransform label = ammoLabel.rectTransform; label.anchorMin = Vector2.zero; label.anchorMax = Vector2.one; label.offsetMin = new Vector2(12f, 0f); label.offsetMax = new Vector2(-12f, 0f);

        GameObject action = new GameObject("AmmoActionProgress"); action.transform.SetParent(canvas.transform, false);
        ammoActionRoot = action.AddComponent<RectTransform>();
        ammoActionRoot.anchorMin = new Vector2(0.5f, 0f); ammoActionRoot.anchorMax = new Vector2(0.5f, 0f);
        ammoActionRoot.pivot = new Vector2(0.5f, 0f); ammoActionRoot.anchoredPosition = new Vector2(0f, 36f); ammoActionRoot.sizeDelta = new Vector2(420f, 52f);
        Image actionPanel = action.AddComponent<Image>(); actionPanel.color = new Color(0.02f, 0.02f, 0.02f, 0.82f); actionPanel.raycastTarget = false;
        GameObject fill = new GameObject("AmmoActionFill"); fill.transform.SetParent(action.transform, false);
        ammoActionFill = fill.AddComponent<Image>(); ammoActionFill.color = new Color(0.25f, 0.68f, 1f, 1f); ammoActionFill.type = Image.Type.Simple;
        // 使用左锚点 Simple Image 缩放宽度，确保进度可见。
        RectTransform fillRect = ammoActionFill.rectTransform; fillRect.anchorMin = new Vector2(0f, 0f); fillRect.anchorMax = new Vector2(0f, 0f); fillRect.pivot = new Vector2(0f, 0f); fillRect.anchoredPosition = new Vector2(12f, 8f); fillRect.sizeDelta = new Vector2(396f, 16f);
        ammoActionLabel = CreateText("", 22, Color.white, TextAnchor.MiddleCenter); ammoActionLabel.transform.SetParent(action.transform, false);
        RectTransform actionLabel = ammoActionLabel.rectTransform; actionLabel.anchorMin = Vector2.zero; actionLabel.anchorMax = Vector2.one; actionLabel.offsetMin = new Vector2(8f, 20f); actionLabel.offsetMax = new Vector2(-8f, -2f);
        ammoActionRoot.gameObject.SetActive(false);
    }

    private void UpdateAmmoHud(bool gameplayVisible)
    {
        WeaponControl weapon = localPlayer != null ? localPlayer.GetComponent<WeaponControl>() : null;
        if (!gameplayVisible || weapon == null)
        {
            if (ammoRoot != null) ammoRoot.gameObject.SetActive(false);
            if (ammoActionRoot != null) ammoActionRoot.gameObject.SetActive(false);
            return;
        }
        if (ammoRoot != null) ammoRoot.gameObject.SetActive(true);
        if (ammoLabel != null)
            ammoLabel.text = "\u5f39\u836f " + weapon.CurrentMagazine + " / " + weapon.ReserveAmmo;
        bool busy = weapon.IsBusy || weapon.IsResupplyCharging;
        if (ammoActionRoot != null) ammoActionRoot.gameObject.SetActive(busy);
        if (busy)
        {
            if (ammoActionFill != null) ammoActionFill.rectTransform.sizeDelta = new Vector2(396f * weapon.ActionProgress, 16f);
            if (ammoActionLabel != null)
                ammoActionLabel.text = (weapon.IsResupplyCharging ? "\u8865\u5145\u5f39\u836f" : weapon.ActionLabel) + "  " + Mathf.CeilToInt(weapon.ActionRemaining) + " \u79d2";
        }
    }

    private void UpdatePlayerHealthLegacy()
    {
        PlayerHealth health = localPlayer != null ? localPlayer.GetComponent<PlayerHealth>() : null;
        if (health == null) { if (healthRoot != null) healthRoot.gameObject.SetActive(false); if (respawnRoot != null) respawnRoot.gameObject.SetActive(false); return; }
        if (healthRoot != null) healthRoot.gameObject.SetActive(true);
        float ratio = Mathf.Clamp01(health.CurrentHealth / Mathf.Max(1f, health.maxHealth));
        if (healthFill != null) { healthFill.fillAmount = ratio; healthFill.color = Color.Lerp(new Color(0.9f, 0.12f, 0.1f), new Color(0.18f, 0.9f, 0.25f), ratio); }
        if (healthLabel != null) healthLabel.text = "生命值 " + Mathf.CeilToInt(health.CurrentHealth) + " / " + Mathf.CeilToInt(health.maxHealth);
        if (respawnRoot != null) respawnRoot.gameObject.SetActive(health.IsDead);
        if (health.IsDead) { if (respawnFill != null) respawnFill.fillAmount = health.RespawnProgress; if (respawnLabel != null) respawnLabel.text = "正在复活... " + Mathf.CeilToInt((1f - health.RespawnProgress) * health.respawnDuration) + " 秒"; }
    }

    private void UpdatePlayerHealth(bool networkPlaying)
    {
        PlayerHealth health = localPlayer != null ? localPlayer.GetComponent<PlayerHealth>() : null;
        bool useNetwork = networkPlaying && networkHealthKnown;
        if (!useNetwork && health == null)
        {
            if (healthRoot != null) healthRoot.gameObject.SetActive(false);
            if (respawnRoot != null) respawnRoot.gameObject.SetActive(false);
            return;
        }

        float current = useNetwork ? networkHealth : health.CurrentHealth;
        float maximum = useNetwork ? networkMaxHealth : health.maxHealth;
        bool dead = useNetwork ? networkDead : health.IsDead;
        float remaining = useNetwork ? networkRespawnRemaining :
            Mathf.Max(0f, (1f - health.RespawnProgress) * health.respawnDuration);
        float ratio = Mathf.Clamp01(current / Mathf.Max(1f, maximum));

        if (healthRoot != null) healthRoot.gameObject.SetActive(true);
        if (healthFill != null)
        {
            RectTransform fillRect = healthFill.rectTransform;
            fillRect.sizeDelta = new Vector2(306f * ratio, 20f);
            healthFill.color = Color.Lerp(new Color(0.9f, 0.12f, 0.1f),
                new Color(0.18f, 0.9f, 0.25f), ratio);
        }
        if (healthLabel != null)
            healthLabel.text = "生命值 " + Mathf.CeilToInt(current) + " / " + Mathf.CeilToInt(maximum);

        // 使用转义 Unicode，兼容不同编辑器编码。
        if (healthLabel != null)
            healthLabel.text = "\u751f\u547d\u503c " + Mathf.CeilToInt(current) + " / " + Mathf.CeilToInt(maximum);
        if (respawnRoot != null) respawnRoot.gameObject.SetActive(dead);
        if (dead)
        {
            float duration = useNetwork ? 2f : Mathf.Max(0.1f, health.respawnDuration);
            float progress = Mathf.Clamp01(1f - remaining / duration);
            if (respawnFill != null) respawnFill.fillAmount = progress;
            if (respawnLabel != null)
                respawnLabel.text = "\u6b63\u5728\u590d\u6d3b... " + Mathf.CeilToInt(remaining) + " \u79d2";
            if (respawnLabel != null)
            respawnLabel.text = "正在复活... " + Mathf.CeilToInt(remaining) + " 秒";
        }
        if (dead && respawnLabel != null)
            respawnLabel.text = "\u6b63\u5728\u590d\u6d3b... " + Mathf.CeilToInt(remaining) + " \u79d2";
    }

    private void BuildMinimap()
    {
        GameObject root = new GameObject("Minimap");
        root.transform.SetParent(canvas.transform, false);
        mapRoot = root.AddComponent<RectTransform>();
        mapRoot.anchorMin = new Vector2(0f, 1f);
        mapRoot.anchorMax = new Vector2(0f, 1f);
        mapRoot.pivot = new Vector2(0f, 1f);
        mapRoot.anchoredPosition = new Vector2(24f, -24f);
        float mapSize = Mathf.Clamp(minimapPixelSize, 160, 320);
        mapRoot.sizeDelta = new Vector2(mapSize, mapSize);

        Image background = root.AddComponent<Image>();
        background.sprite = circleSprite;
        background.type = Image.Type.Simple;
        background.color = minimapBackground;
        background.raycastTarget = false;

        // 裁剪标记到圆形小地图内，避免溢出矩形角落。
        Mask circularMask = root.AddComponent<Mask>();
        circularMask.showMaskGraphic = true;

        Outline outline = root.AddComponent<Outline>();
        outline.effectColor = new Color(0.35f, 0.75f, 0.95f, 0.75f);
        outline.effectDistance = new Vector2(2f, -2f);

        GameObject markerObject = new GameObject("Markers");
        markerObject.transform.SetParent(root.transform, false);
        markerLayer = markerObject.AddComponent<RectTransform>();
        markerLayer.anchorMin = Vector2.zero;
        markerLayer.anchorMax = Vector2.one;
        markerLayer.offsetMin = Vector2.zero;
        markerLayer.offsetMax = Vector2.zero;

        localMarker = CreateMarker("LocalPlayer", triangleSprite, localPlayerColor, 25f);
        localMarker.transform.SetParent(markerLayer, false);
        RectTransform localRect = localMarker.rectTransform;
        localRect.anchorMin = new Vector2(0.5f, 0.5f);
        localRect.anchorMax = new Vector2(0.5f, 0.5f);
        localRect.anchoredPosition = Vector2.zero;
    }

    private void BuildLeaderboard()
    {
        GameObject root = new GameObject("MultiplayerKillLeaderboard");
        root.transform.SetParent(canvas.transform, false);
        leaderboardRoot = root.AddComponent<RectTransform>();
        leaderboardRoot.anchorMin = new Vector2(0f, 0.5f);
        leaderboardRoot.anchorMax = new Vector2(0f, 0.5f);
        leaderboardRoot.pivot = new Vector2(0f, 0.5f);
        // 排行榜放在小地图下方左侧，避免遮挡准星。
        leaderboardRoot.anchoredPosition = new Vector2(24f, -120f);
        leaderboardRoot.sizeDelta = new Vector2(270f, 206f);

        Image panel = root.AddComponent<Image>();
        panel.color = new Color(0.025f, 0.045f, 0.07f, 0.86f);
        panel.raycastTarget = false;
        Outline outline = root.AddComponent<Outline>();
        outline.effectColor = new Color(0.25f, 0.60f, 1f, 0.5f);
        outline.effectDistance = new Vector2(1f, -1f);

        leaderboardTitle = CreateText("击杀排行榜", 23, Color.white, TextAnchor.MiddleCenter);
        leaderboardTitle.transform.SetParent(root.transform, false);
        RectTransform titleRect = leaderboardTitle.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.anchoredPosition = new Vector2(0f, -10f);
        titleRect.sizeDelta = new Vector2(-20f, 38f);

        leaderboardBody = CreateText("等待击杀数据…", 21, new Color(0.86f, 0.92f, 1f), TextAnchor.UpperLeft);
        leaderboardBody.transform.SetParent(root.transform, false);
        RectTransform bodyRect = leaderboardBody.rectTransform;
        bodyRect.anchorMin = new Vector2(0f, 0f);
        bodyRect.anchorMax = new Vector2(1f, 1f);
        bodyRect.pivot = new Vector2(0.5f, 0.5f);
        bodyRect.offsetMin = new Vector2(16f, 12f);
        bodyRect.offsetMax = new Vector2(-16f, -52f);
        leaderboardRoot.gameObject.SetActive(false);
    }

    private Text CreateText(string value, int fontSize, Color color, TextAnchor alignment)
    {
        GameObject textObject = new GameObject("Text");
        Text text = textObject.AddComponent<Text>();
        text.text = value;
        text.font = uiFont;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private Image CreateMarker(string name, Sprite sprite, Color color, float size)
    {
        GameObject markerObject = new GameObject(name);
        markerObject.transform.SetParent(markerLayer, false);
        Image image = markerObject.AddComponent<Image>();
        image.sprite = sprite;
        image.type = Image.Type.Simple;
        image.color = color;
        image.raycastTarget = false;
        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(size, size);
        return image;
    }

    private void UpdateMinimap(bool networkPlaying, NetworkClient client)
    {
        /* 先维护标记集合，再把世界坐标映射到圆形区域。 */
        if (localPlayer == null || mapRoot == null)
        {
            if (localMarker != null)
                localMarker.enabled = false;
            ClearMarkers(teammateMarkers);
            ClearMarkers(monsterMarkers);
            return;
        }

        float radius = Mathf.Max(1f, minimapWorldRadius);
        float uiRadius = mapRoot.rect.width * 0.5f - 14f;
        if (uiRadius < 1f)
            uiRadius = mapRoot.sizeDelta.x * 0.5f - 14f;

        localMarker.enabled = true;
        localMarker.rectTransform.anchoredPosition = Vector2.zero;
        // 三角形默认朝 +Z，UI 旋转方向相反，所以使用负 yaw。
        localMarker.rectTransform.localEulerAngles = new Vector3(0f, 0f, -localPlayer.eulerAngles.y);

        if (networkPlaying && client != null)
        {
            seenTeammates.Clear();
            IEnumerable<NetworkPlayerView> remotePlayers = client.RemotePlayers;
            if (remotePlayers != null)
            {
                foreach (NetworkPlayerView view in remotePlayers)
                {
                    if (view == null || view.transform == null)
                        continue;
                    Transform target = view.transform;
                    seenTeammates.Add(target);
                    Image marker;
                    if (!teammateMarkers.TryGetValue(target, out marker) || marker == null)
                    {
                        marker = CreateMarker("Teammate", triangleSprite, teammateColor, 21f);
                        teammateMarkers[target] = marker;
                    }
                    SetMarkerPosition(marker, target, radius, uiRadius);
                    marker.rectTransform.localEulerAngles = new Vector3(0f, 0f, -target.eulerAngles.y);
                    marker.enabled = true;
                }
            }
            RemoveUnseenMarkers(teammateMarkers, seenTeammates);

            seenMonsters.Clear();
            IEnumerable<NetworkPlayerView> remoteMonsters = client.RemoteMonsters;
            if (remoteMonsters != null)
            {
                foreach (NetworkPlayerView view in remoteMonsters)
                {
                    if (view == null || view.transform == null)
                        continue;
                    Transform target = view.transform;
                    seenMonsters.Add(target);
                    Image marker;
                    if (!monsterMarkers.TryGetValue(target, out marker) || marker == null)
                    {
                        marker = CreateMarker("Monster", circleSprite, monsterColor, 11f);
                        monsterMarkers[target] = marker;
                    }
                    SetMarkerPosition(marker, target, radius, uiRadius);
                    marker.rectTransform.localEulerAngles = Vector3.zero;
                    marker.enabled = true;
                }
            }
            RemoveUnseenMarkers(monsterMarkers, seenMonsters);
        }
        else
        {
            // 单机使用 Enemy 标签查找怪物；模板未激活时会自动排除。
            singlePlayerScanClock -= Time.unscaledDeltaTime;
            if (singlePlayerScanClock > 0f)
            {
                UpdateExistingMarkerPositions(monsterMarkers, radius, uiRadius);
                ClearMarkers(teammateMarkers);
                localMarker.transform.SetAsLastSibling();
                return;
            }
            singlePlayerScanClock = 0.2f;
            seenMonsters.Clear();
            GameObject[] enemies = GameObject.FindGameObjectsWithTag("Enemy");
            for (int i = 0; i < enemies.Length; i++)
            {
                GameObject enemy = enemies[i];
                if (enemy == null || !enemy.activeInHierarchy)
                    continue;
                Transform target = enemy.transform;
                seenMonsters.Add(target);
                Image marker;
                if (!monsterMarkers.TryGetValue(target, out marker) || marker == null)
                {
                    marker = CreateMarker("Monster", circleSprite, monsterColor, 11f);
                    monsterMarkers[target] = marker;
                }
                SetMarkerPosition(marker, target, radius, uiRadius);
                marker.enabled = true;
            }
            RemoveUnseenMarkers(monsterMarkers, seenMonsters);
            ClearMarkers(teammateMarkers);
        }

        // 本地箭头置于最上层，避免与其他标记重叠隐藏。
        localMarker.transform.SetAsLastSibling();
    }

    private void UpdateExistingMarkerPositions(Dictionary<Transform, Image> markers,
        float worldRadius, float uiRadius)
    {
        foreach (KeyValuePair<Transform, Image> pair in markers)
        {
            if (pair.Key != null && pair.Value != null)
                SetMarkerPosition(pair.Value, pair.Key, worldRadius, uiRadius);
        }
    }

    private void SetMarkerPosition(Image marker, Transform target, float worldRadius, float uiRadius)
    {
        Vector3 delta = target.position - localPlayer.position;
        Vector2 mapPosition = new Vector2(delta.x, delta.z);
        if (mapPosition.sqrMagnitude > worldRadius * worldRadius)
            mapPosition = mapPosition.normalized * worldRadius;
        marker.rectTransform.anchoredPosition = mapPosition / worldRadius * uiRadius;
    }

    private void UpdateLeaderboard(bool networkPlaying)
    {
        /* 排行榜只接受服务器分数，客户端不自行计算击杀。 */
        if (leaderboardRoot == null)
            return;
        leaderboardRoot.gameObject.SetActive(networkPlaying);
        if (!networkPlaying || leaderboardBody == null)
            return;

        List<NetScore> scores = new List<NetScore>();
        if (latestScores != null)
        {
            for (int i = 0; i < latestScores.Length; i++)
            {
                if (latestScores[i] != null)
                    scores.Add(latestScores[i]);
            }
        }
        scores.Sort(CompareScores);

        if (scores.Count == 0)
        {
            leaderboardBody.text = "等待击杀数据…";
            return;
        }

        StringBuilder text = new StringBuilder();
        for (int i = 0; i < scores.Count; i++)
        {
            NetScore score = scores[i];
            string name = string.IsNullOrEmpty(score.name) ? ("玩家" + score.id) : score.name;
            // 面板宽度有限，截短过长昵称避免遮挡画面。
            if (name.Length > 8)
                name = name.Substring(0, 8) + "…";
            text.Append(i + 1).Append("  ").Append(name).Append("  ")
                .Append(score.score).Append("分");
            if (i + 1 < scores.Count)
                text.Append('\n');
        }
        leaderboardBody.text = text.ToString();
    }

    private static int CompareScores(NetScore first, NetScore second)
    {
        int scoreComparison = second.score.CompareTo(first.score);
        if (scoreComparison != 0)
            return scoreComparison;
        int nameComparison = string.Compare(first.name, second.name, StringComparison.Ordinal);
        if (nameComparison != 0)
            return nameComparison;
        return first.id.CompareTo(second.id);
    }

    private void RemoveUnseenMarkers(Dictionary<Transform, Image> markers, HashSet<Transform> seen)
    {
        List<Transform> stale = null;
        foreach (KeyValuePair<Transform, Image> pair in markers)
        {
            if (pair.Key == null || !seen.Contains(pair.Key))
            {
                if (pair.Value != null)
                    Destroy(pair.Value.gameObject);
                if (stale == null)
                    stale = new List<Transform>();
                stale.Add(pair.Key);
            }
        }
        if (stale == null)
            return;
        for (int i = 0; i < stale.Count; i++)
            markers.Remove(stale[i]);
    }

    private void ClearMarkers(Dictionary<Transform, Image> markers)
    {
        if (markers.Count == 0)
            return;
        foreach (Image marker in markers.Values)
        {
            if (marker != null)
                Destroy(marker.gameObject);
        }
        markers.Clear();
    }

    private static Sprite CreateCircleSprite(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
        texture.name = "GameplayHudCircleTexture";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        Color32[] pixels = new Color32[size * size];
        float centre = (size - 1) * 0.5f;
        float outer = centre - 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - centre;
                float dy = y - centre;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                byte alpha = (byte)Mathf.Clamp(Mathf.RoundToInt((outer + 1f - distance) * 255f), 0, 255);
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static Sprite CreateTriangleSprite(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
        texture.name = "GameplayHudTriangleTexture";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        Color32[] pixels = new Color32[size * size];
        Vector2 a = new Vector2(0f, 0.93f);
        Vector2 b = new Vector2(-0.82f, -0.78f);
        Vector2 c = new Vector2(0.82f, -0.78f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) / size * 2f - 1f,
                    (y + 0.5f) / size * 2f - 1f);
                bool inside = SameSide(p, a, b, c) && SameSide(p, b, c, a) && SameSide(p, c, a, b);
                pixels[y * size + x] = inside
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(255, 255, 255, 0);
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static bool SameSide(Vector2 point, Vector2 edgeStart, Vector2 edgeEnd, Vector2 opposite)
    {
        float reference = Cross(edgeEnd - edgeStart, opposite - edgeStart);
        float value = Cross(edgeEnd - edgeStart, point - edgeStart);
        return reference * value >= 0f;
    }

    private static float Cross(Vector2 first, Vector2 second)
    {
        return first.x * second.y - first.y * second.x;
    }
}
