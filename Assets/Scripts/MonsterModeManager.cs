using System.Collections.Generic;
using UnityEngine;

public class MonsterModeManager : MonoBehaviour
{
    [Header("Monster mode")]
    [Range(1, 20)]
    public int monsterCount = 5;
    [Range(1, 100)]
    public int monsterHealth = 10;
    public float monsterSpeed = 2.5f;

    [Header("Spawn")]
    public float spawnRadius = 35f;
    public float spawnHeight = 10f;
    public float spawnSpread = 12f;
    [Min(0f)]
    public float groundClearance = 0.02f;
    public LayerMask groundMask;

    [Tooltip("Optional prefab. If empty, the Enemy object already in the scene is used as the template.")]
    public GameObject monsterPrefab;
    public GameObject deathEffect;

    private Transform player;
    private PlayerControl playerControl;
    private WeaponControl weaponControl;
    private GameObject sceneEnemyTemplate;
    private RuntimeAnimatorController templateAnimatorController;
    private readonly List<GameObject> monsters = new List<GameObject>();
    private bool playing;
    private bool finished;
    private bool controlledByGameModeManager;

    public bool IsPlaying { get { return playing; } }

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

        // Keep the original scene enemy as an inactive template. It will not attack
        // or appear before the player confirms the mode settings.
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
        // Do not leave the editor/game paused if this manager is removed or the
        // scene changes while the setup panel is open.
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
        if (!playing || finished)
            return;

        int alive = CountAliveMonsters();
        if (alive == 0)
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
        if (player == null || monsterPrefab == null)
        {
            Debug.LogError("Monster mode requires a Player-tagged object and an enemy template.", this);
            return;
        }

        monsterCount = Mathf.Clamp(monsterCount, 1, 20);
        monsterHealth = Mathf.Clamp(monsterHealth, 1, 100);
        monsters.Clear();

        for (int i = 0; i < monsterCount; i++)
        {
            Vector3 position;
            if (!TryGetSpawnPosition(i, out position))
            {
                Debug.LogWarning("没有找到有效的地面生成点，跳过怪物 " + (i + 1) + ".", this);
                continue;
            }

            // Only turn around the vertical axis. Using the player's full 3D
            // position here gives the monster a pitch, which changes the
            // renderer bounds and makes the later ground correction unstable.
            Vector3 lookDirection = player.position - position;
            lookDirection.y = 0f;
            Quaternion rotation = lookDirection.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(lookDirection, Vector3.up)
                : Quaternion.identity;
            GameObject monster = Instantiate(monsterPrefab, position, rotation);
            monster.name = "Monster_" + (i + 1);
            // Keep spawned instances discoverable by other gameplay systems that
            // use the Enemy tag, even though damage detection uses EnemyControl.
            monster.tag = "Enemy";
            monster.SetActive(true);

            ConfigureMonster(monster);
            AlignMonsterToGround(monster, position.y);

            MonsterAI ai = monster.GetComponent<MonsterAI>();
            if (ai != null)
                ai.SetGroundOffset(monster.transform.position.y - position.y);

            monsters.Add(monster);
        }

        playing = true;
        Time.timeScale = 1f;
        if (playerControl != null)
            playerControl.enabled = true;
        if (weaponControl != null)
            weaponControl.enabled = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void ConfigureMonster(GameObject monster)
    {
        EnemyControl[] healthComponents = monster.GetComponentsInChildren<EnemyControl>(true);
        if (healthComponents.Length == 0)
            healthComponents = new[] { monster.AddComponent<EnemyControl>() };

        for (int i = 0; i < healthComponents.Length; i++)
        {
            healthComponents[i].hp = monsterHealth;
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
            // A collider on a child needs a root rigidbody for reliable bullet
            // collision callbacks and smooth kinematic movement.
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
        ai.Initialize(player, monsterSpeed, groundMask);
    }

    private bool TryGetSpawnPosition(int index, out Vector3 position)
    {
        // Try several nearby points. This avoids spawning outside the terrain or
        // on a position where the raycast has no valid ground hit.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            float angle = (index * 137.5f + attempt * 29f + Random.Range(-20f, 20f)) * Mathf.Deg2Rad;
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            position = player.position + direction *
                (spawnRadius + Random.Range(-spawnSpread, spawnSpread));

            float groundY;
            if (TryGetGroundHeight(position, out groundY))
            {
                position.y = groundY;
                return true;
            }
        }

        position = Vector3.zero;
        return false;
    }

    private bool TryGetGroundHeight(Vector3 position, out float groundY)
    {
        // Use a generous ray range because the city terrain is not guaranteed to
        // be close to the player's current height.
        float rayHeight = Mathf.Max(spawnHeight, 50f);
        Vector3 rayStart = position + Vector3.up * rayHeight;
        RaycastHit[] hits = Physics.RaycastAll(rayStart, Vector3.down,
            rayHeight * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float highestGround = float.NegativeInfinity;
        bool foundGround = false;
        for (int i = 0; i < hits.Length; i++)
        {
            // Ignore walls/ceilings and select the highest upward-facing surface.
            // This lets monsters stand on raised roads and platforms instead of
            // being snapped to the terrain far below them.
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

        // Terrain.SampleHeight is a safe fallback when a point is within the
        // active terrain but its collider was not hit by the raycast.
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
        GUI.Box(panel, "怪物模式");

        GUI.Label(new Rect(panel.x + 30f, panel.y + 55f, 130f, 25f),
            "怪物数量：" + monsterCount);
        monsterCount = Mathf.RoundToInt(GUI.HorizontalSlider(
            new Rect(panel.x + 150f, panel.y + 65f, 160f, 20f), monsterCount, 1f, 20f));

        GUI.Label(new Rect(panel.x + 30f, panel.y + 105f, 130f, 25f),
            "怪物血量：" + monsterHealth);
        monsterHealth = Mathf.RoundToInt(GUI.HorizontalSlider(
            new Rect(panel.x + 150f, panel.y + 115f, 160f, 20f), monsterHealth, 1f, 100f));

        GUI.Label(new Rect(panel.x + 30f, panel.y + 150f, 300f, 25f),
            "怪物会从玩家周围的地图边缘生成");
        if (GUI.Button(new Rect(panel.x + 95f, panel.y + 190f, 170f, 40f), "开始游戏"))
            StartMonsterMode();
    }
}

