using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>Unity client for the asyncio server in server/main.py.</summary>
public class NetworkClient : MonoBehaviour
{
    public static NetworkClient Active { get; private set; }
    public event Action<string> StatusChanged;
    public event Action<string> LobbyChanged;
    public event Action LobbyEntered;
    public event Action GameStarted;

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
    private bool connected;
    private bool gameStarted;
    private bool mapReady;
    private Transform localPlayer;
    private GameObject localPlayerObject;
    private NetworkMapData map;
    private Vector3 predictedPosition;
    private float predictedVelocity;
    private Vector3 renderCorrection;
    private float shootTimer;

    public bool IsGameStarted { get { return gameStarted && mapReady; } }
    public int LocalId { get { return localId; } }

    private void Awake()
    {
        if (Active != null && Active != this) { Destroy(gameObject); return; }
        Active = this;
    }

    public void Connect(string serverAddress, int serverPort, string nickname)
    {
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
            SendTcp(JsonUtility.ToJson(new NetMessage { type = "hello", name = Clean(playerName), version = 1 }));
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
        while (incoming.TryDequeue(out string message))
            HandleMessage(message);
        if (localPlayer == null)
        {
            localPlayerObject = GameObject.FindGameObjectWithTag("Player");
            if (localPlayerObject != null) localPlayer = localPlayerObject.transform;
        }
        renderCorrection = Vector3.Lerp(renderCorrection, Vector3.zero, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
        if (IsGameStarted && localPlayer != null)
        {
            shootTimer += Time.unscaledDeltaTime;
            WeaponControl weapon = localPlayer.GetComponent<WeaponControl>();
            if (weapon != null && Input.GetMouseButton(0) && shootTimer >= weapon.bulletInterval)
            {
                shootTimer = 0f;
                if (weapon.FirePoint != null)
                    SendShoot(weapon.FirePoint.transform.position, weapon.FirePoint.transform.forward);
            }
        }
    }

    public bool DrivePlayer(Rigidbody body, Vector2 move, float yaw, float pitch, bool jump, bool run)
    {
        if (!IsGameStarted || body == null) return false;
        if (predictedPosition == Vector3.zero) predictedPosition = body.position;
        NetInput input = new NetInput { seq = ++inputSequence, x = move.x, z = move.y, yaw = yaw, pitch = pitch, jump = jump, run = run };
        Vector3 before = predictedPosition;
        map.Step(ref predictedPosition, ref predictedVelocity, input);
        body.MovePosition(predictedPosition + renderCorrection);
        body.MoveRotation(Quaternion.Euler(0f, yaw, 0f));
        SendUdp(JsonUtility.ToJson(new NetMessage { type = "input", id = localId, token = sessionToken, tick = input.seq, inputs = new[] { input } }));
        return true;
    }

    public void SendReady() { SendTcp(JsonUtility.ToJson(new NetMessage { type = "ready" })); }
    public void SendStart() { SendTcp(JsonUtility.ToJson(new NetMessage { type = "start" })); }
    public void SendShoot(Vector3 origin, Vector3 direction)
    {
        SendTcp(JsonUtility.ToJson(new NetMessage { type = "shoot", x = origin.x, y = origin.y, z = origin.z, dx = direction.x, dy = direction.y, dz = direction.z }));
    }

    public void Disconnect()
    {
        connected = false;
        gameStarted = false;
        mapReady = false;
        try { SendTcp(JsonUtility.ToJson(new NetMessage { type = "quit" })); } catch { }
        try { tcpStream?.Close(); } catch { }
        try { tcp?.Close(); } catch { }
        try { udp?.Close(); } catch { }
        tcp = null; tcpStream = null; udp = null;
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
        if (connected) incoming.Enqueue(JsonUtility.ToJson(new NetMessage { type = "info", text = "TCP 连接已断开" }));
    }

    private void ReadUdp()
    {
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
        NetMessage message;
        try { message = JsonUtility.FromJson<NetMessage>(raw); } catch { return; }
        if (message == null || string.IsNullOrEmpty(message.type)) return;
        switch (message.type)
        {
            case "welcome":
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
                mapReady = map != null && map.width > 0 && map.heights != null;
                StatusChanged?.Invoke(mapReady ? "地图同步完成，等待开始…" : "地图同步失败");
                break;
            case "start":
                gameStarted = true;
                GameStarted?.Invoke();
                break;
            case "snapshot":
                HandleSnapshot(message);
                break;
            case "info":
                StatusChanged?.Invoke(message.text);
                break;
            case "correction":
                Reconcile(message);
                break;
            case "event":
                StatusChanged?.Invoke(message.text);
                break;
        }
    }

    private void HandleSnapshot(NetMessage message)
    {
        if (message.players == null) return;
        for (int i = 0; i < message.players.Length; i++)
        {
            NetEntity entity = message.players[i];
            if (entity.id == localId)
            {
                Vector3 serverPosition = new Vector3(entity.x, entity.y, entity.z);
                float error = Vector3.Distance(predictedPosition, serverPosition);
                if (error > 4f) { predictedPosition = serverPosition; renderCorrection = Vector3.zero; }
                else renderCorrection += serverPosition - predictedPosition;
                continue;
            }
            NetworkPlayerView view;
            if (!remotePlayers.TryGetValue(entity.id, out view) || view == null)
            {
                view = CreateRemotePlayer(entity.id, new Vector3(entity.x, entity.y, entity.z), entity.yaw);
                if (view == null) continue;
                remotePlayers[entity.id] = view;
            }
            view.SetTarget(new Vector3(entity.x, entity.y, entity.z), Quaternion.Euler(0f, entity.yaw, 0f));
        }
        HandleMonsterSnapshot(message.monsters);
    }

    private void HandleMonsterSnapshot(NetEntity[] entities)
    {
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
                MonsterAI ai = monster.GetComponent<MonsterAI>(); if (ai != null) ai.enabled = false;
                EnemyControl health = monster.GetComponentInChildren<EnemyControl>(true); if (health != null) health.enabled = false;
                view = monster.AddComponent<NetworkPlayerView>();
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
            if (view != null) Destroy(view.gameObject);
            remoteMonsters.Remove(removed[i]);
        }
    }

    private void Reconcile(NetMessage message)
    {
        Vector3 server = new Vector3(message.x, message.y, message.z);
        if (Vector3.Distance(predictedPosition, server) > 4f) predictedPosition = server;
        else renderCorrection += server - predictedPosition;
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
        // Preserve the component and all its settings; remote playback is simply disabled.
        AudioSource source = clone.GetComponent<AudioSource>(); if (source != null) source.enabled = false;
        Rigidbody body = clone.GetComponent<Rigidbody>(); if (body != null) { body.isKinematic = true; body.useGravity = false; }
        return clone.AddComponent<NetworkPlayerView>();
    }

    private void SendTcp(string message)
    {
        if (tcpStream == null || !tcpStream.CanWrite) return;
        byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
        try { tcpStream.Write(bytes, 0, bytes.Length); tcpStream.Flush(); } catch { }
    }

    private void SendUdp(string message)
    {
        if (udp == null) return;
        try { byte[] bytes = Encoding.UTF8.GetBytes(message); udp.Send(bytes, bytes.Length, host, port); } catch { }
    }

    private System.Collections.IEnumerator CaptureAndUploadMap()
    {
        while (localPlayer == null)
        {
            localPlayerObject = GameObject.FindGameObjectWithTag("Player");
            if (localPlayerObject != null) localPlayer = localPlayerObject.transform;
            yield return null;
        }
        NetworkMapData captured = null;
        yield return NetworkMap.Capture(localPlayer, value => captured = value,
            value => StatusChanged?.Invoke("正在准备联机地图… " + Mathf.RoundToInt(value * 100f) + "%"));
        if (captured != null)
            SendTcp(JsonUtility.ToJson(new NetMessage { type = "map", map = captured }));
    }

    private static string Clean(string value) { return (value ?? "玩家").Replace("|", " ").Replace(";", " ").Replace(",", " ").Trim(); }
}
