using UnityEngine;

public class WeaponControl : MonoBehaviour
{
    public GameObject FirePoint;
    public GameObject BulletPre;
    public GameObject FirePre;
    [Min(0.02f)] public float bulletInterval = 0.1f;
    public AudioClip fireSound;
    [Header("弹药")]
    [Min(1)] public int magazineCapacity = 50;
    [Min(0)] public int reserveCapacity = 200;
    [Min(0.1f)] public float reloadDuration = 1.5f;
    [Min(0.1f)] public float resupplyDuration = 2f;

    public int CurrentMagazine { get; private set; }
    public int ReserveAmmo { get; private set; }
    public bool IsReloading { get; private set; }
    public bool IsResupplying { get; private set; }
    public float ActionProgress { get; private set; }
    public float ActionRemaining { get; private set; }
    public bool IsBusy { get { return IsReloading || IsResupplying; } }
    public bool IsResupplyCharging { get { return rHolding && !IsBusy && rHoldTimer > 0f && !NetworkAuthoritative; } }
    public string ActionLabel { get { return IsResupplying ? "补充弹药" : (IsReloading ? "换弹" : string.Empty); } }

    private bool NetworkAuthoritative
    {
        get
        {
            NetworkClient client = NetworkClient.Active;
            return client != null && client.IsGameStarted;
        }
    }

    private float ShotInterval
    {
        get { return NetworkAuthoritative ? networkShotInterval : Mathf.Max(0.02f, bulletInterval); }
    }

    /* 新生命或新对局恢复初始弹药。 */
    public void ResetAmmo()
    {
        CancelActions();
        CurrentMagazine = magazineCapacity;
        ReserveAmmo = reserveCapacity;
        // 清除上一生命的网络确认与射击冷却。
        networkShotInterval = 0.1f;
        timer = ShotInterval;
        networkAmmoReady = false;
        pendingActionSequence = 0;
        networkLife = 0;
    }

    public void CancelActions()
    {
        // 取消动作、按键状态和进度显示。
        IsReloading = false;
        IsResupplying = false;
        actionTimer = 0f;
        rHoldTimer = 0f;
        rHolding = false;
        resupplyHeldLatch = false;
        ActionProgress = 0f;
        ActionRemaining = 0f;
    }

    /* 联机弹药数量不在客户端预测。 */
    public void ApplyAuthoritativeAmmo(NetEntity state)
    {
        if (!state.weaponState || state.life < networkLife) return;
        // 联机射速由服务器快照决定，不使用本地覆盖值。
        if (state.shotInterval > 0f && !float.IsNaN(state.shotInterval) && !float.IsInfinity(state.shotInterval))
            networkShotInterval = Mathf.Clamp(state.shotInterval, 0.02f, 2f);
        if (state.life != networkLife)
        {
            CancelActions();
            pendingActionSequence = 0;
            timer = ShotInterval;
        }
        networkAmmoReady = true;
        networkLife = state.life;
        // 换弹规则由服务器控制，Inspector 值仅用于单机。
        CurrentMagazine = Mathf.Clamp(state.ammo, 0, 50);
        ReserveAmmo = Mathf.Clamp(state.reserve, 0, 200);
        // 旧快照仍可到达；更新数量但不回退刚发起的动作。
        if (state.weaponAck < pendingActionSequence) return;
        pendingActionSequence = 0;
        bool same = IsReloading == state.reloading && IsResupplying == state.resupplying;
        IsReloading = state.reloading;
        IsResupplying = state.resupplying;
        if (!IsBusy)
        {
            actionTimer = ActionRemaining = ActionProgress = 0f;
            return;
        }
        float duration = IsReloading ? 1.5f : 2f;
        float elapsed = Mathf.Clamp(duration - state.ammoRemaining, 0f, duration);
        actionTimer = same ? Mathf.Max(actionTimer, elapsed) : elapsed;
        UpdateNetworkProgress(0f);
    }

    private float timer, actionTimer, rHoldTimer; // 射击冷却、动作计时、R 键按住时长。
    private bool rHolding;
    private bool resupplyHeldLatch; // 一次长按完成后锁定，避免重复补给。
    private bool networkAmmoReady; // 首份权威快照到达后才能联机开火。
    private float networkShotInterval = 0.1f;
    private int pendingActionSequence; // 等待服务器确认的动作序号。
    private int networkLife; // 当前生命代次，过滤死亡前的旧状态。
    private PlayerControl pc;
    private PlayerHealth health;
    private RecoilControl rc;
    private AudioSource AS;

    private void Awake()
    {
        magazineCapacity = Mathf.Max(1, magazineCapacity);
        reserveCapacity = Mathf.Max(0, reserveCapacity);
        ResetAmmo();
    }

    private void Start()
    {
        pc = GetComponent<PlayerControl>();
        health = GetComponent<PlayerHealth>();
        rc = GetComponent<RecoilControl>();
        // 保留并使用 Player 原有 AudioSource。
        AS = GetComponent<AudioSource>();
    }

    private void Update()
    {
        // 死亡和菜单状态不消费输入，也不推进本地动作。
        if (health != null && health.IsDead) return;
        if (GameModeManager.IsGameplayPaused || GameModeManager.IsMenuVisible) return;
        bool online = NetworkAuthoritative;
        float dt = online ? Time.unscaledDeltaTime : Time.deltaTime;
        timer += dt;
        // 联机仅预测进度，单机直接结算弹药。
        if (online)
        {
            if (!networkAmmoReady) return;
            HandleNetworkReloadKey(dt);
            UpdateNetworkProgress(dt);
        }
        else
        {
            HandleReloadKey(dt);
            UpdateTimedAction(dt);
        }
        if (IsBusy || rHolding || (pc != null && pc.highSpeed)) return;
        if (Input.GetMouseButton(0) && timer + 0.000001f >= ShotInterval) TryFire();
    }

    private void HandleNetworkReloadKey(float dt)
    {
        // 按下只发一次开始指令，持续按住累计时长。
        if (Input.GetKey(KeyCode.R))
        {
            if (!rHolding)
            {
                rHolding = true;
                rHoldTimer = 0f;
                resupplyHeldLatch = false;
                // 最新快照可能尚未包含刚发出的子弹，交由服务器裁决。
                RequestNetworkAction("resupply_start", false, true);
            }
            rHoldTimer = Mathf.Min(2f, rHoldTimer + dt);
            if (rHoldTimer >= 2f) resupplyHeldLatch = true;
            return;
        }
        if (!rHolding) return;
        // 先取消补给再换弹，避免服务器仍处于补给状态。
        if (!resupplyHeldLatch)
        {
            RequestNetworkAction("resupply_cancel", false, false);
            RequestNetworkAction("reload", true, false);
        }
        // 2 秒长按本身就是补给，不再启动第二次倒计时。
        rHolding = false;
        rHoldTimer = 0f;
        resupplyHeldLatch = false;
    }

    private void RequestNetworkAction(string action, bool reload, bool resupply)
    {
        NetworkClient client = NetworkClient.Active;
        int seq = client != null ? client.SendAmmoAction(action) : 0;
        if (seq <= 0) return;
        pendingActionSequence = seq;
        // 只更新等待中的动作显示，数量等待权威快照。
        IsReloading = reload;
        IsResupplying = resupply;
        actionTimer = ActionProgress = 0f;
        ActionRemaining = reload ? 1.5f : (resupply ? 2f : 0f);
    }

    private void UpdateNetworkProgress(float dt)
    {
        if (!IsBusy) return;
        float duration = IsReloading ? 1.5f : 2f;
        actionTimer = Mathf.Min(duration, actionTimer + dt);
        ActionProgress = Mathf.Clamp01(actionTimer / duration);
        ActionRemaining = Mathf.Max(0f, duration - actionTimer);
        // 等待快照确认动作完成。
    }

    public void CancelResupplyForPause()
    {
        if (NetworkAuthoritative && (rHolding || IsResupplying))
            RequestNetworkAction("resupply_cancel", false, false);
        rHolding = false;
        rHoldTimer = 0f;
        resupplyHeldLatch = false;
    }

    private void HandleReloadKey(float dt)
    {
        // 单机长按直接结算补给，短按在松开时进入换弹。
        if (Input.GetKey(KeyCode.R))
        {
            if (!rHolding)
            {
                // 2 秒补给可替代自动换弹，不再等待额外的 1.5 秒动作。
                CancelActions();
                rHolding = true;
            }
            if (resupplyHeldLatch) return;
            if (!IsBusy)
            {
                rHoldTimer = Mathf.Min(resupplyDuration, rHoldTimer + dt);
                ActionProgress = Mathf.Clamp01(rHoldTimer / Mathf.Max(0.1f, resupplyDuration));
                ActionRemaining = Mathf.Max(0f, resupplyDuration - rHoldTimer);
                if (rHoldTimer >= resupplyDuration)
                {
                    // 长按已完成补给，不再启动第二次计时。
                    CurrentMagazine = magazineCapacity;
                    ReserveAmmo = reserveCapacity;
                    ActionProgress = 1f;
                    ActionRemaining = 0f;
                    resupplyHeldLatch = true;
                }
            }
            return;
        }
        if (!rHolding) { resupplyHeldLatch = false; return; }
        if (!resupplyHeldLatch && !IsBusy) StartReload();
        rHolding = false; rHoldTimer = 0f;
        resupplyHeldLatch = false;
        if (!IsBusy) { ActionProgress = 0f; ActionRemaining = 0f; }
    }

    private void UpdateTimedAction(float dt)
    {
        // 先推进进度，计时结束后再一次性转移弹药。
        if (!IsBusy) return;
        actionTimer += dt;
        float duration = IsResupplying ? resupplyDuration : reloadDuration;
        ActionProgress = Mathf.Clamp01(actionTimer / Mathf.Max(0.1f, duration));
        ActionRemaining = Mathf.Max(0f, duration - actionTimer);
        if (actionTimer < duration) return;
        if (IsResupplying)
        {
            // 补给完成后恢复整套初始弹药。
            if (!NetworkAuthoritative)
            {
                CurrentMagazine = magazineCapacity;
                ReserveAmmo = reserveCapacity;
            }
            IsResupplying = false;
        }
        else
        {
            if (!NetworkAuthoritative)
            {
                // 仅从备用弹药扣除弹夹实际缺少的数量。
                int amount = Mathf.Min(magazineCapacity - CurrentMagazine, ReserveAmmo);
                CurrentMagazine += amount;
                ReserveAmmo -= amount;
            }
            IsReloading = false;
        }
        actionTimer = 0f; ActionProgress = 0f; ActionRemaining = 0f;
    }

    private void StartReload()
    {
        // 联机发送动作请求，单机检查弹夹和备用量后计时。
        if (NetworkAuthoritative)
        {
            if (!IsBusy && CurrentMagazine < 50 && ReserveAmmo > 0)
                RequestNetworkAction("reload", true, false);
            return;
        }
        if (IsBusy || CurrentMagazine >= magazineCapacity || ReserveAmmo <= 0) return;
        IsReloading = true; IsResupplying = false; actionTimer = 0f; ActionProgress = 0f; ActionRemaining = reloadDuration;
    }

    private void TryFire()
    {
        // 空弹夹转入换弹；联机发送请求，单机扣弹。
        if (CurrentMagazine <= 0) { StartReload(); return; }
        if (FirePoint == null || BulletPre == null) return;
        if (NetworkAuthoritative)
        {
            if (!NetworkClient.Active.SendShoot(FirePoint.transform.position, FirePoint.transform.forward)) return;
        }
        else
            CurrentMagazine--;
        // 确认发射后统一播放后坐力、子弹、音效和枪口效果。
        timer = 0f;
        if (rc != null) rc.Fire();
        Instantiate(BulletPre, FirePoint.transform.position, FirePoint.transform.rotation);
        if (AS != null && fireSound != null) AS.PlayOneShot(fireSound);
        if (FirePre != null) Destroy(Instantiate(FirePre, FirePoint.transform.position, FirePoint.transform.rotation), 0.1f);
        if (CurrentMagazine <= 0 && ReserveAmmo > 0) StartReload();
    }
}
