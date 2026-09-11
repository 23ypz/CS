using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/* 连接 server/main.py 的 Unity 客户端。 */
public class NetworkClient : MonoBehaviour
{
    public static NetworkClient Active { get; private set; }
    public event Action<string> StatusChanged;
    public event Action<string> LobbyChanged;
    public event Action LobbyEntered;
    public event Action<int, int> GameStarted;
    public event Action<NetScore[]> ScoresChanged;
    /* 快照中的本地玩家权威血量与死亡状态。 */
    public event Action<int, int, bool, float> PlayerHealthChanged;
    /* 从房主大厅收到的权威怪物设置。 */
    public int MonsterCount { get; private set; } = 5;
    public int MonsterHealth { get; private set; } = 10;
    public int CurrentWave { get; private set; }
    public int TotalWaves { get; private set; }
    public int NextWave { get; private set; }
    public bool WavesComplete { get; private set; }
    public float WaveRemaining
    {
        get { return NextWave == 0 ? 0f : Mathf.Max(0f, waveRemainingAtSnapshot - (Time.unscaledTime - waveSnapshotTime)); }
    }
    private float waveRemainingAtSnapshot; /* 收到快照时的波次倒计时。 */
    private float waveSnapshotTime; /* 本地接收时刻，用于平滑显示倒计时。 */
    public IEnumerable<NetworkPlayerView> RemotePlayers { get { return remotePlayers.Values; } }
    public IEnumerable<NetworkPlayerView> RemoteMonsters { get { return remoteMonsters.Values; } }

    /* 网络线程只入队，Unity 主线程统一应用消息。 */
    private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
    private readonly Dictionary<int, NetworkPlayerView> remotePlayers = new Dictionary<int, NetworkPlayerView>();
    private readonly Dictionary<int, NetworkPlayerView> remoteMonsters = new Dictionary<int, NetworkPlayerView>();
    private TcpClient tcp;
    private NetworkStream tcpStream;
    private UdpClient udp;
    private Thread tcpThread;
    private Thread udpThread;
    private string host;
    private int port;
    private string playerName;
    private string sessionToken;
    private int localId = -1;
    private int inputSequence;
    private int lastSnapshotTick = -1; /* 防止乱序快照回退。 */
    private int lastScoreTick = -1; /* TCP 事件与 UDP 分数快照共用顺序。 */
    private bool connected;
    private bool gameStarted;
    private bool mapReady;
    private Transform localPlayer;
    private GameObject localPlayerObject;
    private NetworkMapData map;
    private Vector3 predictedPosition; /* 本地预测的脚底位置。 */
    private float predictedVelocity; /* 预测垂直速度。 */
    private bool predictionInitialized;
    private bool locallyPaused;
    private float predictionClock;
    private int weaponSequence; /* 所有武器命令共用递增序号。 */
    private int weaponLife; /* 最近快照确认的生命代次。 */
    private bool bodyConfigured;
    private bool originalKinematic;
    private bool originalUseGravity;
    private RigidbodyConstraints originalConstraints;
    private const float ServerTickDelta = 1f / 30f;

    public bool IsGameStarted { get { return gameStarted && mapReady; } }
    public int LocalId { get { return localId; } }

    public void SetPaused(bool paused)
    {
        if (paused && !locallyPaused && localPlayer != null)
        {
            WeaponControl weapon = localPlayer.GetComponent<WeaponControl>();
            if (weapon != null) weapon.CancelResupplyForPause();
        }
        locallyPaused = paused;
    }

    private void Awake()
    {
        if (Active != null && Active != this) { Destroy(gameObject); return; }
        Active = this;
    }

    public void Connect(string serverAddress, int serverPort, string nickname)
    {
        /* 连接阶段：建立 TCP/UDP 通道，启动接收线程，再发送 hello。 */
        host = string.IsNullOrEmpty(serverAddress) ? "127.0.0.1" : serverAddress;
        port = serverPort;
        playerName = string.IsNullOrEmpty(nickname) ? "玩家" : nickname;
        try
        {
            tcp = new TcpClient { NoDelay = true };
            tcp.Connect(host, port);
            tcpStream = tcp.GetStream();
            connected = true;
            tcpThread = new Thread(ReadTcp) { IsBackground = true };
            tcpThread.Start();
            udp = new UdpClient(0);
            udpThread = new Thread(ReadUdp) { IsBackground = true };
            udpThread.Start();
            localPlayerObject = GameObject.FindGameObjectWithTag("Player");
            localPlayer = localPlayerObject != null ? localPlayerObject.transform : null;
            Vector3 startPos = localPlayer != null ? localPlayer.position : Vector3.zero;
            SendTcp(JsonUtility.ToJson(new NetMessage {
                type = "hello", name = Clean(playerName), version = 3,
                x = startPos.x, y = startPos.y, z = startPos.z
            }));
            StatusChanged?.Invoke("已连接，等待服务器确认…");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("连接失败：" + ex.Message);
            Disconnect();
        }
    }

    public void Tick()
    {
        /* 主线程阶段：消费网络线程入队的消息，并寻找尚未出现的本地 Player。 */
        while (incoming.TryDequeue(out string message))
        {
            if (connected) HandleMessage(message);
        }
        if (localPlayer == null)
        {
            localPlayerObject = GameObject.FindGameObjectWithTag("Player");
            if (localPlayerObject != null) localPlayer = localPlayerObject.transform;
        }
        /* WeaponControl 统一处理输入和射击计时，这里不重复采样鼠标。 */
    }

    public bool DrivePlayer(Rigidbody body, Vector2 move, float yaw, float pitch, bool jump, bool run)
    {
        /* 预测阶段：按服务端 tick 推进本地位置，再把带序号输入发给 UDP。 */
        if (!IsGameStarted || body == null || locallyPaused) return IsGameStarted;
        ConfigureNetworkBody(body);
        if (!predictionInitialized)
        {
            predictedPosition = body.position;
            predictionInitialized = true;
            predictionClock = ServerTickDelta;
        }
        body.MoveRotation(Quaternion.Euler(0f, yaw, 0f));
        predictionClock += Time.fixedDeltaTime;
        if (predictionClock < ServerTickDelta)
        {
            body.MovePosition(predictedPosition);
            return true;
        }

        predictionClock -= ServerTickDelta;
        NetInput input = new NetInput { seq = ++inputSequence, x = move.x, z = move.y, yaw = yaw, pitch = pitch, jump = jump, run = run };
        map.Step(ref predictedPosition, ref predictedVelocity, input);
        body.MovePosition(predictedPosition);
        SendUdp(JsonUtility.ToJson(new NetMessage { type = "input", id = localId, token = sessionToken, tick = input.seq, inputs = new[] { input } }));
        return true;
    }

    public void SendReady()
    {
        /* 大厅协议：准备消息携带当前房主设置。 */
        SendTcp(JsonUtility.ToJson(new NetMessage {
            type = "ready", count = MonsterCount, health = MonsterHealth
        }));
    }

    public void SendReady(int count, int health)
    {
        MonsterCount = Mathf.Clamp(count, 1, 20);
        MonsterHealth = Mathf.Clamp(health, 1, 100);
        SendTcp(JsonUtility.ToJson(new NetMessage {
            type = "ready", count = MonsterCount, health = MonsterHealth
        }));
    }
    public void SendStart(int count, int health)
    {
        /* 大厅协议：房主发送经过本地范围限制的开局设置。 */
        MonsterCount = Mathf.Clamp(count, 1, 20);
        MonsterHealth = Mathf.Clamp(health, 1, 100);
        SendTcp(JsonUtility.ToJson(new NetMessage {
            type = "start",
            count = MonsterCount,
            health = MonsterHealth
        }));
    }

    /* 保留无参数启动调用的兼容性。 */
    public void SendStart() { SendStart(MonsterCount, MonsterHealth); }
    public bool SendShoot(Vector3 origin, Vector3 direction)
    {
        /* 武器协议：所有射击命令共享序号和生命代次。 */
        if (!connected || !IsGameStarted || locallyPaused || weaponLife <= 0) return false;
        SendTcp(JsonUtility.ToJson(new NetMessage {
            type = "shoot", weaponSeq = ++weaponSequence, life = weaponLife,
            x = origin.x, y = origin.y, z = origin.z,
            dx = direction.x, dy = direction.y, dz = direction.z
        }));
        return true;
    }

    public int SendAmmoAction(string action)
    {
        /* 换弹/补给沿用同一套序号，服务器按生命代次去重。 */
        if (!connected || !IsGameStarted || locallyPaused || weaponLife <= 0) return 0;
        int seq = ++weaponSequence;
        SendTcp(JsonUtility.ToJson(new NetMessage {
            type = "ammo_action", action = action, weaponSeq = seq, life = weaponLife
        }));
        return seq;
    }

    public void Disconnect()
    {
        bool wasConnected = connected || gameStarted;
        CurrentWave = TotalWaves = NextWave = 0;
        WavesComplete = false;
        waveRemainingAtSnapshot = 0f;
        connected = false;
        gameStarted = false;
        mapReady = false;
        predictionInitialized = false;
        locallyPaused = false;
        predictionClock = 0f;
        predictedVelocity = 0f;
        lastSnapshotTick = -1;
        lastScoreTick = -1;
        weaponSequence = weaponLife = 0;
        RestoreNetworkBody();
        if (wasConnected && localPlayer != null)
        {
            PlayerHealth health = localPlayer.GetComponent<PlayerHealth>();
            if (health != null) health.ResetForNewMatch();
            else
            {
                WeaponControl weapon = localPlayer.GetComponent<WeaponControl>();
                if (weapon != null) weapon.ResetAmmo();
            }
        }
        try { SendTcp(JsonUtility.ToJson(new NetMessage { type = "quit" })); } catch { }
        try { tcpStream?.Close(); } catch { }
        try { tcp?.Close(); } catch { }
        try { udp?.Close(); } catch { }
        tcp = null; tcpStream = null; udp = null;
        while (incoming.TryDequeue(out string ignored)) { }
        foreach (NetworkPlayerView view in remotePlayers.Values)
            if (view != null) Destroy(view.gameObject);
        remotePlayers.Clear();
        foreach (NetworkPlayerView view in remoteMonsters.Values)
            if (view != null) Destroy(view.gameObject);
        remoteMonsters.Clear();
        if (Active == this) Active = null;
    }

    private void OnDestroy() { Disconnect(); }

    private void ReadTcp()
    {
        /* TCP 接收阶段：按换行拆帧，只入队，不在后台线程触碰 Unity 对象。 */
        try
        {
            using (StreamReader reader = new StreamReader(tcpStream, Encoding.UTF8, false, 2048, true))
            {
                while (connected)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    incoming.Enqueue(line);
                }
            }
        }
        catch { }
        if (connected)
            incoming.Enqueue(JsonUtility.ToJson(new NetMessage { type = "disconnect", text = "TCP 连接已断开" }));
    }

    private void ReadUdp()
    {
        /* UDP 接收阶段：读取快照/校正包，交由主线程统一解析。 */
        try
        {
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            while (connected)
            {
                byte[] data = udp.Receive(ref endpoint);
                incoming.Enqueue(Encoding.UTF8.GetString(data));
            }
        }
        catch { }
    }

    private void HandleMessage(string raw)
    {
        /* 协议分发阶段：反序列化后按消息类型进入大厅、快照或校正流程。 */
        NetMessage message;
        try { message = JsonUtility.FromJson<NetMessage>(raw); } catch { return; }
        if (message == null || string.IsNullOrEmpty(message.type)) return;
        switch (message.type)
        {
            case "welcome":
                if (message.version < 3)
                {
                    StatusChanged?.Invoke("服务器版本过旧，请重启更新后的 server/main.py 再连接");
                    Disconnect();
                    break;
                }
                localId = message.id;
                sessionToken = message.token;
                SendUdp(JsonUtility.ToJson(new NetMessage { type = "bind", id = localId, token = sessionToken }));
                LobbyEntered?.Invoke();
                StartCoroutine(CaptureAndUploadMap());
                StatusChanged?.Invoke("已加入服务器，等待大厅开始…");
                break;
            case "lobby":
                LobbyChanged?.Invoke((message.text ?? string.Empty).Replace(";", "\n"));
                break;
            case "map":
                map = message.map;
                mapReady = IsValidMap(map);
                StatusChanged?.Invoke(mapReady ? "地图同步完成，等待开始…" : "地图同步失败");
                break;
            case "start":
                if (gameStarted) break;
                if (localPlayer != null)
                    ConfigureNetworkBody(localPlayer.GetComponent<Rigidbody>());
                gameStarted = true;
                ReadWaveState(message);
                MonsterCount = Mathf.Clamp(message.count > 0 ? message.count : MonsterCount, 1, 20);
                MonsterHealth = Mathf.Clamp(message.health > 0 ? message.health : MonsterHealth, 1, 100);
                GameStarted?.Invoke(MonsterCount, MonsterHealth);
                if (localPlayer != null)
                {
                    WeaponControl weapon = localPlayer.GetComponent<WeaponControl>();
                    if (weapon != null) weapon.ResetAmmo();
                }
                break;
            case "snapshot":
                HandleSnapshot(message);
                break;
            case "info":
                StatusChanged?.Invoke(message.text);
                break;
            case "disconnect":
                StatusChanged?.Invoke(message.text);
                Disconnect();
                break;
            case "correction":
                Reconcile(message);
                break;
            case "event":
                PublishScores(message.scores, message.tick, true);
                StatusChanged?.Invoke(message.text);
                break;
        }
    }

    private void HandleSnapshot(NetMessage message)
    {
        /* 快照阶段：先按 tick 丢弃旧包，再应用设置、分数、波次和角色状态。 */
        if (!connected || !IsGameStarted) return;
        /* UDP 可能乱序；丢弃旧快照，避免怪物复现和排行榜回退。 */
        if (message.tick > 0)
        {
            if (message.tick <= lastSnapshotTick)
                return;
            lastSnapshotTick = message.tick;
        }

        /* 即使快照缺少角色数组，设置和分数仍然有效。 */
        if (message.count > 0)
            MonsterCount = Mathf.Clamp(message.count, 1, 20);
        if (message.health > 0)
            MonsterHealth = Mathf.Clamp(message.health, 1, 100);
        PublishScores(message.scores, message.tick);
        ReadWaveState(message);
        if (message.players == null)
        {
            HandleMonsterSnapshot(message.monsters);
            return;
        }
        HashSet<int> present = new HashSet<int>();
        for (int i = 0; i < message.players.Length; i++)
        {
            NetEntity entity = message.players[i];
            if (entity.id == localId)
            {
                int maxHp = entity.maxHp > 0 ? entity.maxHp : 100;
                PlayerHealth health = localPlayer != null
                    ? localPlayer.GetComponent<PlayerHealth>() : null;
                WeaponControl weapon = localPlayer != null
                    ? localPlayer.GetComponent<WeaponControl>() : null;
                bool wasDead = health != null && health.IsDead;
                PlayerHealthChanged?.Invoke(entity.hp, maxHp, entity.dead,
                    Mathf.Max(0f, entity.respawn));
                if (health != null)
                    health.ApplyAuthoritativeState(entity.hp, maxHp,
                        entity.dead, Mathf.Max(0f, entity.respawn),
                        new Vector3(entity.x, entity.y, entity.z));
                /* 先恢复生命代次，再应用对应武器状态。 */
                if (entity.weaponState && entity.life >= weaponLife)
                {
                    weaponLife = entity.life;
                    if (weapon != null) weapon.ApplyAuthoritativeAmmo(entity);
                }
                Vector3 serverPos = new Vector3(entity.x, entity.y, entity.z);
                /* 复活点由服务器选择，收到快照后覆盖本地预测位置。 */
                if (!entity.dead && localPlayer != null &&
                    (wasDead || (health != null &&
                     health.CurrentHealth >= maxHp &&
                     Vector3.Distance(localPlayer.position, serverPos) > 2f)))
                {
                    localPlayer.position = serverPos;
                    Rigidbody localBody = localPlayer.GetComponent<Rigidbody>();
                    if (localBody != null)
                        localBody.position = serverPos;
                    predictedPosition = serverPos;
                    predictedVelocity = 0f;
                    predictionInitialized = true;
                }
                float error = Vector3.Distance(predictedPosition, serverPos);
                ReconcilePosition(serverPos, error);
                continue;
            }
            present.Add(entity.id);
            NetworkPlayerView view;
            if (!remotePlayers.TryGetValue(entity.id, out view) || view == null)
            {
                view = CreateRemotePlayer(entity.id, new Vector3(entity.x, entity.y, entity.z), entity.yaw);
                if (view == null) continue;
                remotePlayers[entity.id] = view;
            }
            view.SetTarget(new Vector3(entity.x, entity.y, entity.z), Quaternion.Euler(0f, entity.yaw, 0f));
            view.SetHealth(entity.hp, entity.maxHp, entity.dead, entity.respawn);
        }
        List<int> removed = new List<int>();
        foreach (int id in remotePlayers.Keys)
            if (!present.Contains(id)) removed.Add(id);
        for (int i = 0; i < removed.Count; i++)
        {
            NetworkPlayerView view = remotePlayers[removed[i]];
            if (view != null) Destroy(view.gameObject);
            remotePlayers.Remove(removed[i]);
        }
        HandleMonsterSnapshot(message.monsters);
    }

    private void ReadWaveState(NetMessage message)
    {
        /* 波次阶段：兼容旧服务器，并避免 TCP 开局消息覆盖较新的 UDP 波次。 */
        if (message.totalWaves <= 0 || message.wave < CurrentWave)
            return;
        TotalWaves = message.totalWaves;
        CurrentWave = Mathf.Clamp(message.wave, 0, TotalWaves);
        NextWave = Mathf.Clamp(message.nextWave, 0, TotalWaves);
        WavesComplete = message.wavesComplete;
        waveRemainingAtSnapshot = Mathf.Max(0f, message.waveRemaining);
        waveSnapshotTime = Time.unscaledTime;
    }

    private static bool IsValidMap(NetworkMapData value)
    {
        if (value == null || value.width <= 0 || value.depth <= 0 ||
            value.cell <= 0f || float.IsNaN(value.cell) || float.IsInfinity(value.cell) ||
            float.IsNaN(value.originX) || float.IsInfinity(value.originX) ||
            float.IsNaN(value.originZ) || float.IsInfinity(value.originZ) ||
            value.heights == null || value.walkable == null)
            return false;

        long cells = (long)value.width * value.depth;
        /* 先拒绝非法地图，避免索引越界；限制最大地图尺寸。 */
        return cells <= 1024L * 1024L && value.heights.Length >= cells &&
               value.walkable.Length >= cells;
    }

    private void PublishScores(NetScore[] scores, int serverTick, bool authoritativeEvent = false)
    {
        if (scores == null)
            return;
        /* 击杀事件走 TCP、快照走 UDP，使用服务器 tick 防止分数回退。 */
        if (serverTick > 0 && (serverTick < lastScoreTick ||
            (serverTick == lastScoreTick && !authoritativeEvent)))
            return;
        if (serverTick > 0)
            lastScoreTick = serverTick;
        ScoresChanged?.Invoke(scores);
    }

    private void HandleMonsterSnapshot(NetEntity[] entities)
    {
        /* 怪物阶段：按服务器实体 id 创建/更新代理，缺失实体触发死亡特效后销毁。 */
        if (entities == null) return;
        HashSet<int> present = new HashSet<int>();
        GameObject prefab = null;
        MonsterModeManager manager = FindObjectOfType<MonsterModeManager>();
        if (manager != null) prefab = manager.monsterPrefab;
        if (prefab == null) return;
        for (int i = 0; i < entities.Length; i++)
        {
            NetEntity entity = entities[i];
            present.Add(entity.id);
            NetworkPlayerView view;
            if (!remoteMonsters.TryGetValue(entity.id, out view) || view == null)
            {
                GameObject monster = Instantiate(prefab, new Vector3(entity.x, entity.y, entity.z), Quaternion.identity);
                monster.name = "NetworkMonster_" + entity.id;
                MonsterAI[] aiParts = monster.GetComponentsInChildren<MonsterAI>(true);
                for (int c = 0; c < aiParts.Length; c++)
                    aiParts[c].enabled = false;
                EnemyControl[] healthParts = monster.GetComponentsInChildren<EnemyControl>(true);
                for (int c = 0; c < healthParts.Length; c++)
                {
                    /* 网络血量由服务器负责；保留碰撞组件，但禁止本地提前销毁代理。 */
                    healthParts[c].hp = int.MaxValue;
                    healthParts[c].bombEffect = null;
                    healthParts[c].SetNetworkControlled(true);
                }
                Rigidbody body = monster.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.isKinematic = true;
                    body.useGravity = false;
                    body.interpolation = RigidbodyInterpolation.Interpolate;
                }
                view = monster.AddComponent<NetworkPlayerView>();
                monster.SetActive(true);
                remoteMonsters[entity.id] = view;
            }
            view.SetTarget(new Vector3(entity.x, entity.y, entity.z), Quaternion.Euler(0f, entity.yaw, 0f));
        }
        List<int> removed = new List<int>();
        foreach (int id in remoteMonsters.Keys)
            if (!present.Contains(id)) removed.Add(id);
        for (int i = 0; i < removed.Count; i++)
        {
            NetworkPlayerView view = remoteMonsters[removed[i]];
            if (view != null)
            {
                if (manager != null && manager.deathEffect != null)
                    EnemyControl.SpawnDeathEffect(manager.deathEffect, view.transform);
                Destroy(view.gameObject);
            }
            remoteMonsters.Remove(removed[i]);
        }
    }

    private void Reconcile(NetMessage message)
    {
        /* 校正阶段：服务器回包只修正明显漂移，保留正常预测的连续性。 */
        Vector3 serverPos = new Vector3(message.x, message.y, message.z);
        if (!predictionInitialized)
        {
            predictedPosition = serverPos;
            predictionInitialized = true;
            return;
        }
        ReconcilePosition(serverPos, Vector3.Distance(predictedPosition, serverPos));
    }

    private void ReconcilePosition(Vector3 serverPos, float error)
    {
        /* 忽略正常时序误差，只修正明显漂移，避免刚体来回抖动。 */
        if (error > 3f)
            predictedPosition = serverPos;
        else if (error > 0.75f)
            predictedPosition = Vector3.Lerp(predictedPosition, serverPos, 0.12f);
    }

    private void ConfigureNetworkBody(Rigidbody body)
    {
        if (bodyConfigured || body == null)
            return;
        originalKinematic = body.isKinematic;
        originalUseGravity = body.useGravity;
        originalConstraints = body.constraints;
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        bodyConfigured = true;
    }

    private void RestoreNetworkBody()
    {
        if (!bodyConfigured || localPlayer == null)
            return;
        Rigidbody body = localPlayer.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.isKinematic = originalKinematic;
            body.useGravity = originalUseGravity;
            body.constraints = originalConstraints;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        bodyConfigured = false;
    }

    private NetworkPlayerView CreateRemotePlayer(int id, Vector3 position, float yaw)
    {
        if (localPlayerObject == null) localPlayerObject = GameObject.FindGameObjectWithTag("Player");
        if (localPlayerObject == null) return null;
        GameObject clone = Instantiate(localPlayerObject, position, Quaternion.Euler(0f, yaw, 0f));
        clone.name = "RemotePlayer_" + id;
        clone.tag = "Untagged";
        PlayerControl control = clone.GetComponent<PlayerControl>(); if (control != null) control.enabled = false;
        WeaponControl weapon = clone.GetComponent<WeaponControl>(); if (weapon != null) weapon.enabled = false;
        RecoilControl recoil = clone.GetComponent<RecoilControl>(); if (recoil != null) recoil.enabled = false;
        foreach (Camera camera in clone.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        foreach (AudioListener listener in clone.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
        /* 保留组件及设置，仅关闭远端播放。 */
        AudioSource source = clone.GetComponent<AudioSource>(); if (source != null) source.enabled = false;
        Rigidbody body = clone.GetComponent<Rigidbody>(); if (body != null) { body.isKinematic = true; body.useGravity = false; }
        return clone.AddComponent<NetworkPlayerView>();
    }

    private void SendTcp(string message)
    {
        /* TCP 发送阶段：补换行形成一帧 JSON，写入失败时由连接状态处理。 */
        if (tcpStream == null || !tcpStream.CanWrite) return;
        byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
        try { tcpStream.Write(bytes, 0, bytes.Length); tcpStream.Flush(); } catch { }
    }

    private void SendUdp(string message)
    {
        /* UDP 发送阶段：输入和 bind 包不追加换行，直接发送 UTF-8 数据报。 */
        if (udp == null) return;
        try { byte[] bytes = Encoding.UTF8.GetBytes(message); udp.Send(bytes, bytes.Length, host, port); } catch { }
    }

    private System.Collections.IEnumerator CaptureAndUploadMap()
    {
        /* 地图阶段：等待本地 Player，分帧采集碰撞图，再经 TCP 上传。 */
        while (localPlayer == null)
        {
            localPlayerObject = GameObject.FindGameObjectWithTag("Player");
            if (localPlayerObject != null) localPlayer = localPlayerObject.transform;
            yield return null;
        }
        NetworkMapData mapData = null;
        yield return NetworkMap.Capture(localPlayer, value => mapData = value,
            value => StatusChanged?.Invoke("正在准备联机地图… " + Mathf.RoundToInt(value * 100f) + "%"));
        if (mapData != null)
            SendTcp(JsonUtility.ToJson(new NetMessage { type = "map", map = mapData }));
    }

    private static string Clean(string value) { return (value ?? "玩家").Replace("|", " ").Replace(";", " ").Replace(",", " ").Trim(); }
}
