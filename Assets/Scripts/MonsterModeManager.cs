using System.Collections.Generic;
using UnityEngine;

public class MonsterModeManager : MonoBehaviour
{
    [Header("怪物设置")]
    [Range(1, 20)]
    public int monsterCount = 5;
    [Range(1, 100)]
    public int monsterHealth = 10;
    public float monsterSpeed = 2.5f;

    [Header("生成设置")]
    public float spawnRadius = 35f;
    public float spawnHeight = 10f;
    public float spawnSpread = 12f;
    [Min(0f)]
    public float groundClearance = 0.02f;
    public LayerMask groundMask;

    [Tooltip("留空时使用场景中原有的 Enemy 对象作为模板。")]
    public GameObject monsterPrefab;
    public GameObject deathEffect;

    private Transform player;
    private PlayerControl playerControl;
    private WeaponControl weaponControl;
    private GameObject sceneEnemyTemplate;
    private RuntimeAnimatorController templateAnimatorController;
    private readonly List<GameObject> monsters = new List<GameObject>();
    private readonly Collider[] spawnOverlapBuffer = new Collider[32];
    private bool playing;
    private bool finished;
    private bool controlledByGameModeManager;
    private bool networkControlled; // 联机时由服务器管理怪物
    private string legacyCountInput = "5";
    private string legacyHealthInput = "10";
    private string legacySettingsMessage = string.Empty;
    private MonsterWaveSequence waveSequence; // 本局波次调度

    public bool IsPlaying { get { return playing; } }
    public int CurrentWave { get { return waveSequence != null ? waveSequence.CurrentWave : 0; } }
    public int NextWave { get { return waveSequence != null ? waveSequence.NextWave : 0; } }
    public float WaveRemaining { get { return waveSequence != null ? waveSequence.RemainingSeconds : 0f; } }
    public bool WavesComplete { get { return finished; } }

    private void Start()
    {
        controlledByGameModeManager = FindObjectOfType<GameModeManager>() != null;
        player = FindPlayer();
        if (player != null)
        {
            playerControl = player.GetComponent<PlayerControl>();
            weaponControl = player.GetComponent<WeaponControl>();
        }
        sceneEnemyTemplate = FindEnemyTemplate();

        if (sceneEnemyTemplate != null)
        {
            Animator templateAnimator = sceneEnemyTemplate.GetComponentInChildren<Animator>(true);
            if (templateAnimator != null)
                templateAnimatorController = templateAnimator.runtimeAnimatorController;
        }

        if (groundMask.value == 0)
            groundMask = LayerMask.GetMask("Ground");

        if (monsterPrefab == null)
            monsterPrefab = sceneEnemyTemplate;

        legacyCountInput = monsterCount.ToString();
        legacyHealthInput = monsterHealth.ToString();

        /* 保留原场景怪物作为隐藏模板，确认模式后才启用。 */
        if (sceneEnemyTemplate != null)
            sceneEnemyTemplate.SetActive(false);

        if (!controlledByGameModeManager)
        {
            Time.timeScale = 0f;
            SetGameplayEnabled(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private void OnDestroy()
    {
        /* 管理器销毁或切场景时恢复暂停状态。 */
        if (Time.timeScale == 0f)
            Time.timeScale = 1f;
        if (playerControl != null)
            playerControl.enabled = true;
        if (weaponControl != null)
            weaponControl.enabled = true;
    }

    public void SetGameplayEnabled(bool enabled)
    {
        if (playerControl != null)
            playerControl.enabled = enabled;
        if (weaponControl != null)
            weaponControl.enabled = enabled;
    }

    public void StartSinglePlayer(int count, int health)
    {
        networkControlled = false;
        monsterCount = Mathf.Clamp(count, 1, 20);
        monsterHealth = Mathf.Clamp(health, 1, 100);
        StartMonsterMode();
    }

    public void SuppressLegacyMenu()
    {
        controlledByGameModeManager = true;
    }

    public void StartNetworkMode()
    {
        networkControlled = true;
        ResetPlayerForNewMatch();
        waveSequence = null;
        playing = true;
        finished = false;
        Time.timeScale = 1f;
        SetGameplayEnabled(true);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private Transform FindPlayer()
    {
        GameObject playerObject = GameObject.FindGameObjectWithTag("Player");
        return playerObject != null ? playerObject.transform : null;
    }

    private GameObject FindEnemyTemplate()
    {
        GameObject enemyObject = GameObject.FindGameObjectWithTag("Enemy");
        return enemyObject != null ? enemyObject.transform.root.gameObject : null;
    }

    private void Update()
    {
        if (networkControlled || !playing || finished)
            return;

        /* 按固定节奏生成，暂停时使用缩放时间停止调度。 */
        waveSequence.Advance(Time.deltaTime);
        MonsterWaveSequence.Stats stats;
        while (waveSequence.TryBeginNextWave(out stats))
            SpawnWave(stats);
        monsters.RemoveAll(monster => monster == null);

        int alive = CountAliveMonsters();
        if (CurrentWave == MonsterWaveSequence.TotalWaves && alive == 0)
        {
            finished = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private int CountAliveMonsters()
    {
        int alive = 0;
        for (int i = 0; i < monsters.Count; i++)
        {
            if (monsters[i] != null)
                alive++;
        }

        return alive;
    }

    private void StartMonsterMode()
    {
        networkControlled = false;
        if (player == null || monsterPrefab == null)
        {
            Debug.LogError("Monster mode requires a Player-tagged object and an enemy template.", this);
            return;
        }

        monsterCount = Mathf.Clamp(monsterCount, 1, 20);
        monsterHealth = Mathf.Clamp(monsterHealth, 1, 100);
        ClearMonsters();
        ResetPlayerForNewMatch();
        finished = false;
        waveSequence = new MonsterWaveSequence(monsterCount, monsterHealth, monsterSpeed);
        MonsterWaveSequence.Stats firstWave;
        if (waveSequence.TryBeginNextWave(out firstWave))
            SpawnWave(firstWave);

        playing = true;
        Time.timeScale = 1f;
        if (playerControl != null)
            playerControl.enabled = true;
        if (weaponControl != null)
            weaponControl.enabled = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void SpawnWave(MonsterWaveSequence.Stats stats)
    {
        for (int i = 0; i < stats.Count; i++)
        {
            Vector3 position;
            if (!TryGetSpawnPosition(i, out position))
            {
                Debug.LogWarning("没有找到有效的地面生成点，跳过怪物 " + (i + 1) + ".", this);
                continue;
            }

            /* 只绕垂直轴旋转，避免俯仰导致模型边界和地面校正抖动。 */
            Vector3 lookDirection = player.position - position;
            lookDirection.y = 0f;
            Quaternion rotation = lookDirection.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(lookDirection, Vector3.up)
                : Quaternion.identity;
            GameObject monster = Instantiate(monsterPrefab, position, rotation);
            monster.name = "Monster_Wave" + stats.Wave + "_" + (i + 1);
            /* 保留 Enemy 标签，兼容其他玩法系统的查找。 */
            monster.tag = "Enemy";
            monster.SetActive(true);

            ConfigureMonster(monster, stats);
            AlignMonsterToGround(monster, position.y);

            MonsterAI ai = monster.GetComponent<MonsterAI>();
            if (ai != null)
                ai.SetGroundOffset(monster.transform.position.y - position.y);

            monsters.Add(monster);
        }
    }

    private void ClearMonsters()
    {
        for (int i = 0; i < monsters.Count; i++)
        {
            if (monsters[i] != null)
                Destroy(monsters[i]);
        }
        monsters.Clear();
    }

    public void ReturnToMenu()
    {
        ClearMonsters();
        waveSequence = null;
        playing = false;
        finished = false;
        networkControlled = false;
        ResetPlayerForNewMatch();
        SetGameplayEnabled(false);
    }

    private void ResetPlayerForNewMatch()
    {
        if (player == null)
            player = FindPlayer();
        if (player == null)
            return;
        playerControl = player.GetComponent<PlayerControl>();
        weaponControl = player.GetComponent<WeaponControl>();
        PlayerHealth health = player.GetComponent<PlayerHealth>();
        if (health != null)
            health.ResetForNewMatch();
        else if (weaponControl != null)
            weaponControl.ResetAmmo();
    }

    private void ConfigureMonster(GameObject monster, MonsterWaveSequence.Stats stats)
    {
        EnemyControl[] healthComponents = monster.GetComponentsInChildren<EnemyControl>(true);
        if (healthComponents.Length == 0)
            healthComponents = new[] { monster.AddComponent<EnemyControl>() };

        for (int i = 0; i < healthComponents.Length; i++)
        {
            healthComponents[i].hp = stats.Health;
            if (deathEffect != null)
                healthComponents[i].bombEffect = deathEffect;
        }

        Collider collider = monster.GetComponentInChildren<Collider>(true);
        if (collider == null)
        {
            BoxCollider box = monster.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 1f, 1f);
            box.center = new Vector3(0f, 0.55f, 0f);
        }
        else if (collider.attachedRigidbody == null && monster.GetComponent<Rigidbody>() == null)
        {
            /* 子物体碰撞体需要根刚体，确保子弹回调和运动稳定。 */
            monster.AddComponent<Rigidbody>();
        }

        Rigidbody body = monster.GetComponent<Rigidbody>();
        if (body == null)
            body = monster.AddComponent<Rigidbody>();

        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;

        Animator animator = monster.GetComponentInChildren<Animator>(true);
        if (animator != null && animator.runtimeAnimatorController == null)
            animator.runtimeAnimatorController = templateAnimatorController;

        MonsterAI ai = monster.GetComponent<MonsterAI>();
        if (ai == null)
            ai = monster.AddComponent<MonsterAI>();
        ai.Initialize(player, stats.Speed, groundMask);
        ai.attackDamage = stats.Damage;
    }

    private bool TryGetSpawnPosition(int index, out Vector3 position)
    {
        /* 尝试多个附近点，检查地面和完整碰撞体，避免生成在墙内或地形外。 */
        for (int attempt = 0; attempt < 48; attempt++)
        {
            float angle = (index * 137.5f + attempt * 29f + Random.Range(-20f, 20f)) * Mathf.Deg2Rad;
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            position = player.position + direction *
                (spawnRadius + Random.Range(-spawnSpread, spawnSpread));

            float groundY;
            if (TryGetGroundHeight(position, out groundY))
            {
                position.y = groundY;
                if (IsSpawnPositionClear(position))
                    return true;
            }
        }

        position = Vector3.zero;
        return false;
    }

    private bool IsSpawnPositionClear(Vector3 position)
    {
        if (player != null && Vector2.Distance(
                new Vector2(position.x, position.z),
                new Vector2(player.position.x, player.position.z)) < 5f)
            return false;

        for (int i = 0; i < monsters.Count; i++)
        {
            GameObject other = monsters[i];
            if (other == null) continue;
            Vector3 delta = other.transform.position - position;
            delta.y = 0f;
            if (delta.sqrMagnitude < 2.2f * 2.2f)
                return false;
        }

        /* 运行时碰撞体创建前先用保守胶囊检测，忽略地面和隐藏模板。 */
        int count = Physics.OverlapCapsuleNonAlloc(
            position + Vector3.up * 0.12f,
            position + Vector3.up * 1.7f,
            0.68f, spawnOverlapBuffer, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        if (count >= spawnOverlapBuffer.Length)
            return false;
        int groundLayer = LayerMask.NameToLayer("Ground");
        for (int i = 0; i < count; i++)
        {
            Collider hit = spawnOverlapBuffer[i];
            if (hit == null || hit.transform == player || hit.transform.IsChildOf(player))
                continue;
            if (hit is TerrainCollider || hit.gameObject.layer == groundLayer)
                continue;
            /* 城市地面网格可能未设 Ground 层，薄碰撞体不应被当作墙。 */
            if (hit.bounds.max.y <= position.y + 0.2f)
                continue;
            if (sceneEnemyTemplate != null &&
                (hit.transform == sceneEnemyTemplate.transform ||
                 hit.transform.IsChildOf(sceneEnemyTemplate.transform)))
                continue;
            return false;
        }
        return true;
    }

    private bool TryGetGroundHeight(Vector3 position, out float groundY)
    {
        /* 城市地形高度不固定，使用较长射线。 */
        float rayHeight = Mathf.Max(spawnHeight, 50f);
        Vector3 rayStart = position + Vector3.up * rayHeight;
        RaycastHit[] hits = Physics.RaycastAll(rayStart, Vector3.down,
            rayHeight * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float highestGround = float.NegativeInfinity;
        bool foundGround = false;
        for (int i = 0; i < hits.Length; i++)
        {
            /* 忽略墙面和天花板，选择最高的向上表面。 */
            if (hits[i].normal.y < 0.35f)
                continue;

            if (!foundGround || hits[i].point.y > highestGround)
            {
                highestGround = hits[i].point.y;
                foundGround = true;
            }
        }

        if (foundGround)
        {
            groundY = highestGround;
            return true;
        }

        /* 射线未命中活动地形时使用 Terrain.SampleHeight。 */
        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null && terrain.terrainData != null)
        {
            Vector3 local = position - terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            if (local.x >= 0f && local.x <= size.x &&
                local.z >= 0f && local.z <= size.z)
            {
                groundY = terrain.SampleHeight(position) + terrain.transform.position.y;
                return true;
            }
        }

        groundY = 0f;
        return false;
    }

    private void AlignMonsterToGround(GameObject monster, float groundY)
    {
        Bounds bounds = new Bounds(monster.transform.position, Vector3.zero);
        bool hasBounds = false;

        Renderer[] renderers = monster.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (!renderers[i].enabled)
                continue;

            if (!hasBounds)
            {
                bounds = renderers[i].bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
        }

        if (!hasBounds)
        {
            Collider[] colliders = monster.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!hasBounds)
                {
                    bounds = colliders[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(colliders[i].bounds);
                }
            }
        }

        if (hasBounds)
        {
            float correction = groundY - bounds.min.y + groundClearance;
            monster.transform.position += Vector3.up * correction;
        }
    }

    private void OnGUI()
    {
        if (controlledByGameModeManager)
            return;

        if (playing)
        {
            GUI.Label(new Rect(20f, 20f, 260f, 30f), "剩余怪物：" + CountAliveMonsters());
            if (finished)
                GUI.Label(new Rect(Screen.width * 0.5f - 110f, Screen.height * 0.5f - 20f,
                    240f, 40f), "怪物已全部消灭！");
            return;
        }

        const float width = 360f;
        const float height = 260f;
        Rect panel = new Rect(Screen.width * 0.5f - width * 0.5f,
            Screen.height * 0.5f - height * 0.5f, width, height);

        int previousTextFieldFontSize = GUI.skin != null && GUI.skin.textField != null
            ? GUI.skin.textField.fontSize : 0;
        int previousLabelFontSize = GUI.skin != null && GUI.skin.label != null
            ? GUI.skin.label.fontSize : 0;
        int previousButtonFontSize = GUI.skin != null && GUI.skin.button != null
            ? GUI.skin.button.fontSize : 0;
        int previousBoxFontSize = GUI.skin != null && GUI.skin.box != null
            ? GUI.skin.box.fontSize : 0;
        if (GUI.skin != null)
        {
            if (GUI.skin.textField != null) GUI.skin.textField.fontSize = 20;
            if (GUI.skin.label != null) GUI.skin.label.fontSize = 20;
            if (GUI.skin.button != null) GUI.skin.button.fontSize = 20;
            if (GUI.skin.box != null) GUI.skin.box.fontSize = 22;
        }

        GUI.Box(panel, "怪物模式");

        GUI.Label(new Rect(panel.x + 30f, panel.y + 50f, 130f, 30f), "怪物数量（1-20）");
        legacyCountInput = GUI.TextField(new Rect(panel.x + 165f, panel.y + 48f, 145f, 34f), legacyCountInput);
        GUI.Label(new Rect(panel.x + 30f, panel.y + 98f, 130f, 30f), "怪物血量（1-100）");
        legacyHealthInput = GUI.TextField(new Rect(panel.x + 165f, panel.y + 96f, 145f, 34f), legacyHealthInput);

        GUI.Label(new Rect(panel.x + 30f, panel.y + 143f, 300f, 25f),
            "怪物会从玩家周围的地图边缘生成");
        if (!string.IsNullOrEmpty(legacySettingsMessage))
            GUI.Label(new Rect(panel.x + 30f, panel.y + 166f, 300f, 25f), legacySettingsMessage);
        if (GUI.Button(new Rect(panel.x + 95f, panel.y + 200f, 170f, 40f), "开始游戏"))
        {
            int count;
            int health;
            if (!int.TryParse(legacyCountInput, out count) || count < 1 || count > 20)
                legacySettingsMessage = "数量必须是 1-20 的整数";
            else if (!int.TryParse(legacyHealthInput, out health) || health < 1 || health > 100)
                legacySettingsMessage = "血量必须是 1-100 的整数";
            else
            {
                monsterCount = count;
                monsterHealth = health;
                legacySettingsMessage = string.Empty;
                StartMonsterMode();
            }
        }

        if (GUI.skin != null)
        {
            if (GUI.skin.textField != null) GUI.skin.textField.fontSize = previousTextFieldFontSize;
            if (GUI.skin.label != null) GUI.skin.label.fontSize = previousLabelFontSize;
            if (GUI.skin.button != null) GUI.skin.button.fontSize = previousButtonFontSize;
            if (GUI.skin.box != null) GUI.skin.box.fontSize = previousBoxFontSize;
        }
    }
}

