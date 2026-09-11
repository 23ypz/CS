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

    /// <summary>Restore the initial loadout for a new life or a new match.</summary>
    public void ResetAmmo()
    {
        CancelActions();
        CurrentMagazine = magazineCapacity;
        ReserveAmmo = reserveCapacity;
        networkShotInterval = 0.1f;
        timer = ShotInterval;
        networkAmmoReady = false;
        pendingActionSequence = 0;
        networkLife = 0;
    }

    public void CancelActions()
    {
        IsReloading = false;
        IsResupplying = false;
        actionTimer = 0f;
        rHoldTimer = 0f;
        rHolding = false;
        resupplyHeldLatch = false;
        ActionProgress = 0f;
        ActionRemaining = 0f;
    }

    /// <summary>Counts are never predicted online, including timed refills.</summary>
    public void ApplyAuthoritativeAmmo(NetEntity state)
    {
        if (!state.weaponState || state.life < networkLife) return;
        // Multiplayer cadence comes from the same server rule that accepts
        // shots, not a second hard-coded delay or a local inspector override.
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
        // The server owns these rules; inspector overrides remain single-player only.
        CurrentMagazine = Mathf.Clamp(state.ammo, 0, 50);
        ReserveAmmo = Mathf.Clamp(state.reserve, 0, 200);
        // A snapshot sent before our R command may contain the old action.
        // Apply its counts, but don't rewind the action we have just requested.
        if (state.weaponAck < pendingActionSequence) return;
        pendingActionSequence = 0;
        bool sameAction = IsReloading == state.reloading && IsResupplying == state.resupplying;
        IsReloading = state.reloading;
        IsResupplying = state.resupplying;
        if (!IsBusy)
        {
            actionTimer = ActionRemaining = ActionProgress = 0f;
            return;
        }
        float duration = IsReloading ? 1.5f : 2f;
        float elapsed = Mathf.Clamp(duration - state.ammoRemaining, 0f, duration);
        actionTimer = sameAction ? Mathf.Max(actionTimer, elapsed) : elapsed;
        UpdateNetworkProgress(0f);
    }

    private float timer, actionTimer, rHoldTimer;
    private bool rHolding;
    private bool resupplyHeldLatch;
    private bool networkAmmoReady;
    private float networkShotInterval = 0.1f;
    private int pendingActionSequence;
    private int networkLife;
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
        // Preserve and use the existing Player AudioSource.
        AS = GetComponent<AudioSource>();
    }

    private void Update()
    {
        if (health != null && health.IsDead) return;
        if (GameModeManager.IsGameplayPaused || GameModeManager.IsMenuVisible) return;
        bool online = NetworkAuthoritative;
        float dt = online ? Time.unscaledDeltaTime : Time.deltaTime;
        timer += dt;
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
        if (Input.GetKey(KeyCode.R))
        {
            if (!rHolding)
            {
                rHolding = true;
                rHoldTimer = 0f;
                resupplyHeldLatch = false;
                // A just-fired shot may not yet be in the latest snapshot.
                // Let the server decide even when the displayed count is full.
                RequestNetworkAction("resupply_start", false, true);
            }
            rHoldTimer = Mathf.Min(2f, rHoldTimer + dt);
            if (rHoldTimer >= 2f) resupplyHeldLatch = true;
            return;
        }
        if (!rHolding) return;
        // Cancel first, then reload. Previously the order was reversed, so
        // the server discarded the reload while it still saw a resupply.
        if (!resupplyHeldLatch)
        {
            RequestNetworkAction("resupply_cancel", false, false);
            RequestNetworkAction("reload", true, false);
        }
        // A full two-second hold is already the operation; don't start a
        // second countdown or locally change the ammo at this boundary.
        rHolding = false;
        rHoldTimer = 0f;
        resupplyHeldLatch = false;
    }

    private void RequestNetworkAction(string action, bool reload, bool resupply)
    {
        NetworkClient client = NetworkClient.Active;
        int sequence = client != null ? client.SendAmmoAction(action) : 0;
        if (sequence <= 0) return;
        pendingActionSequence = sequence;
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
        // Keep the action pending until a snapshot confirms completion.
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
        if (Input.GetKey(KeyCode.R))
        {
            if (!rHolding)
            {
                // A two-second hold may replace an automatic reload in both
                // modes; it must not wait for another 1.5s operation first.
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
                    // The hold itself is the two-second operation. Do not
                    // start a second timed action after the hold completes.
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
        if (!IsBusy) return;
        actionTimer += dt;
        float duration = IsResupplying ? resupplyDuration : reloadDuration;
        ActionProgress = Mathf.Clamp01(actionTimer / Mathf.Max(0.1f, duration));
        ActionRemaining = Mathf.Max(0f, duration - actionTimer);
        if (actionTimer < duration) return;
        if (IsResupplying)
        {
            // A completed two-second resupply restores the complete initial
            // loadout, including the rounds currently in the front magazine.
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
        if (CurrentMagazine <= 0) { StartReload(); return; }
        if (FirePoint == null || BulletPre == null) return;
        if (NetworkAuthoritative)
        {
            if (!NetworkClient.Active.SendShoot(FirePoint.transform.position, FirePoint.transform.forward)) return;
        }
        else
            CurrentMagazine--;
        timer = 0f;
        if (rc != null) rc.Fire();
        Instantiate(BulletPre, FirePoint.transform.position, FirePoint.transform.rotation);
        if (AS != null && fireSound != null) AS.PlayOneShot(fireSound);
        if (FirePre != null) Destroy(Instantiate(FirePre, FirePoint.transform.position, FirePoint.transform.rotation), 0.1f);
        if (CurrentMagazine <= 0 && ReserveAmmo > 0) StartReload();
    }
}
