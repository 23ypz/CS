using UnityEngine;

public class WeaponControl : MonoBehaviour
{
    public GameObject FirePoint;
    public GameObject BulletPre;
    public GameObject FirePre;
    public float bulletInterval = 0.3f;
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
    public bool IsResupplyCharging { get { return rHolding && !IsBusy && rHoldTimer > 0f; } }
    public string ActionLabel { get { return IsResupplying ? "补充弹药" : (IsReloading ? "换弹" : string.Empty); } }

    private float timer, actionTimer, rHoldTimer;
    private bool rHolding;
    private bool resupplyHeldLatch;
    private PlayerControl pc;
    private PlayerHealth health;
    private RecoilControl rc;
    private AudioSource AS;

    private void Awake()
    {
        magazineCapacity = Mathf.Max(1, magazineCapacity);
        reserveCapacity = Mathf.Max(0, reserveCapacity);
        CurrentMagazine = magazineCapacity;
        ReserveAmmo = reserveCapacity;
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
        float dt = Time.deltaTime;
        timer += dt;
        if (health != null && health.IsDead) { rHolding = false; rHoldTimer = 0f; resupplyHeldLatch = false; return; }
        HandleReloadKey(dt);
        UpdateTimedAction(dt);
        if (IsBusy || (pc != null && pc.highSpeed) || GameModeManager.IsGameplayPaused || GameModeManager.IsMenuVisible) return;
        if (Input.GetMouseButton(0) && timer >= bulletInterval) TryFire();
    }

    private void HandleReloadKey(float dt)
    {
        if (Input.GetKey(KeyCode.R))
        {
            rHolding = true;
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
                    rHolding = false;
                    rHoldTimer = 0f;
                }
            }
            return;
        }
        if (!rHolding) { resupplyHeldLatch = false; return; }
        if (rHoldTimer < resupplyDuration && !IsBusy) StartReload();
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
            CurrentMagazine = magazineCapacity;
            ReserveAmmo = reserveCapacity;
            IsResupplying = false;
        }
        else { int amount = Mathf.Min(magazineCapacity - CurrentMagazine, ReserveAmmo); CurrentMagazine += amount; ReserveAmmo -= amount; IsReloading = false; }
        actionTimer = 0f; ActionProgress = 0f; ActionRemaining = 0f;
    }

    private void StartReload()
    {
        if (IsBusy || CurrentMagazine >= magazineCapacity || ReserveAmmo <= 0) return;
        IsReloading = true; IsResupplying = false; actionTimer = 0f; ActionProgress = 0f; ActionRemaining = reloadDuration;
    }

    private void StartResupply()
    {
        if (IsBusy || (CurrentMagazine >= magazineCapacity && ReserveAmmo >= reserveCapacity)) return;
        IsResupplying = true; IsReloading = false; actionTimer = 0f; ActionProgress = 0f; ActionRemaining = resupplyDuration;
    }

    private void TryFire()
    {
        if (CurrentMagazine <= 0) { StartReload(); return; }
        if (FirePoint == null || BulletPre == null) return;
        timer = 0f; CurrentMagazine--;
        if (rc != null) rc.Fire();
        Instantiate(BulletPre, FirePoint.transform.position, FirePoint.transform.rotation);
        if (AS != null && fireSound != null) AS.PlayOneShot(fireSound);
        if (FirePre != null) Destroy(Instantiate(FirePre, FirePoint.transform.position, FirePoint.transform.rotation), 0.1f);
        if (CurrentMagazine <= 0 && ReserveAmmo > 0) StartReload();
    }
}
