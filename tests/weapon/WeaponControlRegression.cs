using System;
using System.Reflection;
using UnityEngine;

public static class WeaponControlRegression
{
    private static int passed;

    public static int Main()
    {
        try
        {
            Run("initial loadout and reset", ResetClearsEveryAction);
            Run("online waits for first authoritative snapshot", OnlineWaitsForSnapshot);
            Run("online shot has one request and no predicted ammo", OnlineShotUsesOneRequest);
            Run("rejected online shot has no visual/audio recoil", RejectedShotHasNoEffects);
            Run("online tap sends cancel before reload", OnlineTapOrdering);
            Run("online reload waits for snapshot, not local 1.5s timer", OnlineReloadWaitsForSnapshot);
            Run("online 2s hold sends one start and never locally refills", OnlineHoldIsOneOperation);
            Run("old ack does not rewind a pending action", OldAckDoesNotRewind);
            Run("same-action snapshots cannot reverse progress", ProgressDoesNotReverse);
            Run("old life snapshots are ignored", OldLifeIsIgnored);
            Run("menu and death block fire and R", MenuAndDeathBlockInput);
            Run("pause explicitly cancels held online resupply", PauseCancelsResupply);
            Run("offline shot decrements one round", OfflineShotConsumesAmmo);
            Run("offline tap transfers ammo after 1.5s", OfflineManualReload);
            Run("offline last round automatically reloads", OfflineAutomaticReload);
            Run("offline limited reserve and empty reserve", OfflineLimitedReserve);
            Run("offline 2s hold replenishes once without a second timer", OfflineHoldResupplies);
            Run("reset after online mode returns a working offline loadout", ResetToOffline);
            Run("offline held fire repeats every 0.1s and stops on release", OfflineHeldFireCadence);
            Run("online held fire requests every 0.1s and stops on release", OnlineHeldFireCadence);
            Run("online cadence comes from server, not Inspector", OnlineCadenceUsesServerInterval);
            Run("60Hz held fire keeps ten shots inside one second", SixtyHertzHeldFire);
            Run("reload and death interrupt held fire in both modes", ReloadAndDeathInterruptHeldFire);
            Console.WriteLine("PASS: " + passed + " production WeaponControl regression tests.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private sealed class Fixture
    {
        public readonly WeaponControl Weapon = new WeaponControl();
        public readonly PlayerHealth Health = new PlayerHealth();
        public readonly PlayerControl Player = new PlayerControl();
        public readonly RecoilControl Recoil = new RecoilControl();
        public readonly AudioSource Audio = new AudioSource();
        public readonly NetworkClient Client;

        public Fixture(bool online = false, bool snapshot = true, int magazine = 50, int reserve = 200,
            float inspectorShotInterval = 0.1f, float serverShotInterval = 0.1f)
        {
            GameModeManager.IsGameplayPaused = GameModeManager.IsMenuVisible = false;
            Input.R = Input.Fire = false;
            UnityEngine.Object.InstantiateCount = 0;
            NetworkClient.Active = online ? new NetworkClient { IsGameStarted = true } : null;
            Client = NetworkClient.Active;
            Weapon.Attach(Health);
            Weapon.Attach(Player);
            Weapon.Attach(Recoil);
            Weapon.Attach(Audio);
            Weapon.FirePoint = new GameObject();
            Weapon.BulletPre = new GameObject();
            Weapon.FirePre = new GameObject();
            Weapon.fireSound = new AudioClip();
            // 使用 CityNew 的序列化射速，不依赖旧字段默认值。
            Weapon.bulletInterval = inspectorShotInterval;
            Call(Weapon, "Awake");
            Call(Weapon, "Start");
            if (online && snapshot)
                Weapon.ApplyAuthoritativeAmmo(State(magazine, reserve, shotInterval: serverShotInterval));
        }

        public void Step(float duration, bool r = false, bool fire = false)
        {
            Time.deltaTime = Time.unscaledDeltaTime = duration;
            Input.R = r;
            Input.Fire = fire;
            Call(Weapon, "Update");
        }
    }

    private static NetEntity State(int magazine, int reserve, int ack = 0,
        bool reload = false, bool resupply = false, float remaining = 0f, int life = 1,
        float shotInterval = 0.1f)
    {
        return new NetEntity {
            weaponState = true, ammo = magazine, reserve = reserve, weaponAck = ack,
            reloading = reload, resupplying = resupply, ammoRemaining = remaining, life = life,
            shotInterval = shotInterval
        };
    }

    private static void ResetClearsEveryAction()
    {
        Fixture fixture = new Fixture(true, true, 12, 34);
        fixture.Step(0.5f, true);
        fixture.Weapon.ResetAmmo();
        Ammo(fixture, 50, 200);
        Check(!fixture.Weapon.IsBusy && !fixture.Weapon.IsResupplyCharging, "reset action flags");
        Near(0f, fixture.Weapon.ActionProgress, "reset progress");
        Near(0f, fixture.Weapon.ActionRemaining, "reset remaining");
        Near(0f, Private<float>(fixture.Weapon, "actionTimer"), "reset action timer");
        Near(0f, Private<float>(fixture.Weapon, "rHoldTimer"), "reset hold timer");
        Check(!Private<bool>(fixture.Weapon, "rHolding"), "reset held key");
        Check(!Private<bool>(fixture.Weapon, "resupplyHeldLatch"), "reset hold latch");
        Check(!Private<bool>(fixture.Weapon, "networkAmmoReady"), "reset snapshot readiness");
        Equal(0, Private<int>(fixture.Weapon, "pendingActionSequence"), "reset action sequence");
        Equal(0, Private<int>(fixture.Weapon, "networkLife"), "reset life");
        fixture.Step(5f, false, true);
        Equal(0, fixture.Client.ShootCalls, "reset online cannot shoot before fresh snapshot");
    }

    private static void OnlineWaitsForSnapshot()
    {
        Fixture fixture = new Fixture(true, false);
        fixture.Step(3f, true, true);
        Equal(0, fixture.Client.ShootCalls, "uninitialized shots");
        Equal(0, fixture.Client.Actions.Count, "uninitialized R action");
        Ammo(fixture, 50, 200);
    }

    private static void OnlineShotUsesOneRequest()
    {
        Fixture fixture = new Fixture(true, true, 20, 80);
        fixture.Step(0f, false, true);
        Equal(1, fixture.Client.ShootCalls, "one shot request");
        Ammo(fixture, 20, 80);
        fixture.Step(0.05f, false, true);
        Equal(1, fixture.Client.ShootCalls, "cooldown blocks second request");
        fixture.Weapon.ApplyAuthoritativeAmmo(State(19, 80));
        Ammo(fixture, 19, 80);
        fixture.Step(0.05f, false, true);
        Equal(2, fixture.Client.ShootCalls, "next interval has one request");
        Ammo(fixture, 19, 80);
        Equal(2, fixture.Recoil.FireCount, "one recoil per accepted request");
        Equal(2, fixture.Audio.PlayCount, "original AudioSource receives shots");
    }

    private static void RejectedShotHasNoEffects()
    {
        Fixture fixture = new Fixture(true);
        fixture.Client.AcceptShots = false;
        fixture.Step(0f, false, true);
        Equal(1, fixture.Client.ShootCalls, "one rejected request");
        Equal(0, UnityEngine.Object.InstantiateCount, "no rejected-shot projectile");
        Equal(0, fixture.Audio.PlayCount, "no rejected-shot audio");
        Equal(0, fixture.Recoil.FireCount, "no rejected-shot recoil");
        Ammo(fixture, 50, 200);
    }

    private static void OnlineTapOrdering()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Step(0.05f, true);
        fixture.Step(0.05f);
        Equal("resupply_start,resupply_cancel,reload", string.Join(",", fixture.Client.Actions), "tap command ordering");
        Check(fixture.Weapon.IsReloading && !fixture.Weapon.IsResupplying, "tap becomes reload");
        Ammo(fixture, 15, 80);
    }

    private static void OnlineReloadWaitsForSnapshot()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Step(0.05f, true);
        fixture.Step(0.05f);
        fixture.Step(2f);
        Ammo(fixture, 15, 80);
        Check(fixture.Weapon.IsReloading, "local timer cannot finish online reload");
        Near(1f, fixture.Weapon.ActionProgress, "online timer can display completion");
        fixture.Weapon.ApplyAuthoritativeAmmo(State(50, 45, 3));
        Ammo(fixture, 50, 45);
        Check(!fixture.Weapon.IsBusy, "authoritative reload completion");
    }

    private static void OnlineHoldIsOneOperation()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Step(0.5f, true);
        fixture.Step(1.5f, true);
        fixture.Step(4f, true);
        Ammo(fixture, 15, 80);
        Equal("resupply_start", string.Join(",", fixture.Client.Actions), "one start over entire hold");
        Check(fixture.Weapon.IsResupplying, "online resupply stays pending snapshot");
        Near(1f, fixture.Weapon.ActionProgress, "no second hold countdown");
        fixture.Step(0f);
        Equal(1, fixture.Client.Actions.Count, "full hold release does not cancel or reload");
        Ammo(fixture, 15, 80);
        fixture.Weapon.ApplyAuthoritativeAmmo(State(50, 200, 1));
        Ammo(fixture, 50, 200);
        Check(!fixture.Weapon.IsBusy, "server confirms resupply");
    }

    private static void OldAckDoesNotRewind()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Step(0.5f, true);
        float progress = fixture.Weapon.ActionProgress;
        fixture.Weapon.ApplyAuthoritativeAmmo(State(14, 79, 0));
        Ammo(fixture, 14, 79);
        Check(fixture.Weapon.IsResupplying, "old ack cannot remove pending resupply");
        Near(progress, fixture.Weapon.ActionProgress, "old ack cannot rewind progress");
    }

    private static void ProgressDoesNotReverse()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Step(0.5f, true);
        fixture.Weapon.ApplyAuthoritativeAmmo(State(15, 80, 1, false, true, 1.9f));
        Near(0.25f, fixture.Weapon.ActionProgress, "same action elapsed does not reverse");
        fixture.Weapon.ApplyAuthoritativeAmmo(State(15, 80, 1, false, true, 1f));
        Near(0.5f, fixture.Weapon.ActionProgress, "server can advance progress");
    }

    private static void OldLifeIsIgnored()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Weapon.ApplyAuthoritativeAmmo(State(25, 100, 0, false, false, 0f, 2));
        fixture.Weapon.ApplyAuthoritativeAmmo(State(2, 3, 99, true, false, 1f, 1));
        Ammo(fixture, 25, 100);
        Check(!fixture.Weapon.IsBusy, "old life action ignored");
    }

    private static void MenuAndDeathBlockInput()
    {
        foreach (bool online in new[] { false, true })
        {
            Fixture fixture = new Fixture(online);
            GameModeManager.IsMenuVisible = true;
            fixture.Step(3f, true, true);
            GameModeManager.IsMenuVisible = false;
            GameModeManager.IsGameplayPaused = true;
            fixture.Step(3f, true, true);
            GameModeManager.IsGameplayPaused = false;
            fixture.Health.IsDead = true;
            fixture.Step(3f, true, true);
            Ammo(fixture, 50, 200);
            Equal(0, fixture.Recoil.FireCount, "blocked input recoil");
            Check(!fixture.Weapon.IsBusy && !fixture.Weapon.IsResupplyCharging, "blocked input R action");
            if (online)
            {
                Equal(0, fixture.Client.Actions.Count, "blocked input network actions");
                Equal(0, fixture.Client.ShootCalls, "blocked input network shots");
            }
        }
    }

    private static void PauseCancelsResupply()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Step(0.5f, true);
        fixture.Weapon.CancelResupplyForPause();
        Equal("resupply_start,resupply_cancel", string.Join(",", fixture.Client.Actions), "pause cancel ordering");
        Check(!fixture.Weapon.IsBusy, "pause clears predicted resupply");
        fixture.Step(0f);
        Equal(2, fixture.Client.Actions.Count, "release after pause does not reload");
    }

    private static void OfflineShotConsumesAmmo()
    {
        Fixture fixture = new Fixture();
        fixture.Step(0f, false, true);
        Ammo(fixture, 49, 200);
        Equal(1, fixture.Audio.PlayCount, "offline original AudioSource");
    }

    private static void OfflineManualReload()
    {
        Fixture fixture = new Fixture();
        fixture.Step(0f, false, true);
        fixture.Step(0.05f, true);
        fixture.Step(0f);
        Check(fixture.Weapon.IsReloading, "offline manual reload started");
        fixture.Step(1.49f);
        Ammo(fixture, 49, 200);
        fixture.Step(0.02f);
        Ammo(fixture, 50, 199);
        Check(!fixture.Weapon.IsBusy, "offline manual reload complete");
    }

    private static void OfflineAutomaticReload()
    {
        Fixture fixture = new Fixture();
        for (int i = 0; i < 50; i++) fixture.Step(0.3f, false, true);
        Ammo(fixture, 0, 200);
        Check(fixture.Weapon.IsReloading, "empty magazine auto reload");
        fixture.Step(1.49f);
        Ammo(fixture, 0, 200);
        fixture.Step(0.02f);
        Ammo(fixture, 50, 150);
    }

    private static void OfflineLimitedReserve()
    {
        Fixture fixture = new Fixture();
        fixture.Weapon.reserveCapacity = 3;
        fixture.Weapon.ResetAmmo();
        for (int i = 0; i < 50; i++) fixture.Step(0.3f, false, true);
        fixture.Step(1.5f);
        Ammo(fixture, 3, 0);
        for (int i = 0; i < 3; i++) fixture.Step(0.3f, false, true);
        Ammo(fixture, 0, 0);
        Check(!fixture.Weapon.IsBusy, "zero reserve never starts a reload");
        fixture.Step(3f, false, true);
        Ammo(fixture, 0, 0);
    }

    private static void OfflineHoldResupplies()
    {
        Fixture fixture = new Fixture();
        fixture.Step(0f, false, true);
        fixture.Step(0.05f, true);
        fixture.Step(0f);
        fixture.Step(1.5f);
        fixture.Step(0.3f, false, true);
        Ammo(fixture, 49, 199);
        fixture.Step(1.99f, true);
        Ammo(fixture, 49, 199);
        Check(fixture.Weapon.IsResupplyCharging, "hold progress visible");
        fixture.Step(0.02f, true);
        Ammo(fixture, 50, 200);
        Check(!fixture.Weapon.IsBusy, "hold completion has no second countdown");
        fixture.Step(4f, true);
        fixture.Step(0f);
        Check(!fixture.Weapon.IsBusy, "releasing completed hold does not reload");
        fixture.Step(0f, false, true);
        Ammo(fixture, 49, 200);
    }

    private static void ResetToOffline()
    {
        Fixture fixture = new Fixture(true, true, 15, 80);
        fixture.Step(0.5f, true);
        NetworkClient.Active = null;
        fixture.Weapon.ResetAmmo();
        fixture.Step(0f, false, true);
        Ammo(fixture, 49, 200);
        Equal(0, fixture.Client.ShootCalls, "offline reset no network shoot");
        fixture.Step(0.05f, true);
        fixture.Step(0f);
        fixture.Step(1.5f);
        Ammo(fixture, 50, 199);
    }

    private static void OfflineHeldFireCadence()
    {
        Fixture fixture = new Fixture();
        fixture.Step(0f, false, true);
        for (int i = 1; i < 10; i++)
        {
            fixture.Step(0.1f, false, true);
            Equal(i + 1, fixture.Recoil.FireCount, "offline one shot per held 0.1s interval");
        }
        Ammo(fixture, 40, 200);
        for (int i = 0; i < 5; i++) fixture.Step(0.1f);
        Equal(10, fixture.Recoil.FireCount, "offline release immediately stops continuous fire");
        Ammo(fixture, 40, 200);
        fixture.Step(0f, false, true);
        Equal(11, fixture.Recoil.FireCount, "offline repress resumes firing");
        Ammo(fixture, 39, 200);
    }

    private static void OnlineHeldFireCadence()
    {
        Fixture fixture = new Fixture(true);
        fixture.Step(0f, false, true);
        for (int i = 1; i < 10; i++)
        {
            fixture.Step(0.1f, false, true);
            Equal(i + 1, fixture.Client.ShootCalls, "online one request per held 0.1s interval");
        }
        Equal(10, fixture.Recoil.FireCount, "online held fire produces one effect per request");
        Ammo(fixture, 50, 200);
        for (int i = 0; i < 5; i++) fixture.Step(0.1f);
        Equal(10, fixture.Client.ShootCalls, "online release immediately stops continuous fire");
        fixture.Step(0f, false, true);
        Equal(11, fixture.Client.ShootCalls, "online repress resumes firing");
        Ammo(fixture, 50, 200);
    }

    private static void OnlineCadenceUsesServerInterval()
    {
        // 本地覆盖值不能加速或减慢服务器射速。
        foreach (float inspectorInterval in new[] { 0.5f, 0.01f })
        {
            Fixture fixture = new Fixture(true, inspectorShotInterval: inspectorInterval);
            fixture.Step(0.5f, false, true);
            Equal(1, fixture.Client.ShootCalls, "online first shot");
            fixture.Step(0.05f, false, true);
            Equal(1, fixture.Client.ShootCalls, "Inspector cannot shorten server cooldown");
            fixture.Step(0.05f, false, true);
            Equal(2, fixture.Client.ShootCalls, "Inspector cannot lengthen server cooldown");

            // 非默认间隔验证快照字段生效，而非硬编码 0.1 秒。
            fixture.Weapon.ApplyAuthoritativeAmmo(State(48, 200, shotInterval: 0.2f));
            fixture.Step(0.1f, false, true);
            Equal(2, fixture.Client.ShootCalls, "updated server cooldown is respected");
            fixture.Step(0.1f, false, true);
            Equal(3, fixture.Client.ShootCalls, "updated server cadence permits next shot");
            Ammo(fixture, 48, 200);
        }
    }

    private static void ReloadAndDeathInterruptHeldFire()
    {
        foreach (bool online in new[] { false, true })
        {
            Fixture fixture = new Fixture(online);
            fixture.Step(0f, false, true);
            if (online) fixture.Weapon.ApplyAuthoritativeAmmo(State(49, 200));
            fixture.Step(0.05f, true, true);
            fixture.Step(0f, false, true);
            Check(fixture.Weapon.IsReloading, "reload begins while fire is held");
            for (int i = 0; i < 10; i++) fixture.Step(0.1f, false, true);
            Equal(1, fixture.Recoil.FireCount, "reload blocks held-fire effects");
            Ammo(fixture, 49, 200);
            fixture.Health.IsDead = true;
            for (int i = 0; i < 20; i++) fixture.Step(0.1f, false, true);
            Equal(1, fixture.Recoil.FireCount, "death blocks held-fire effects");
            if (online)
                Equal(1, fixture.Client.ShootCalls, "reload and death block further network shots");
        }
    }

    private static void SixtyHertzHeldFire()
    {
        foreach (bool online in new[] { false, true })
        {
            Fixture fixture = new Fixture(online);
            fixture.Step(0f, false, true); // t=0 时开火
            // 59 帧仍未到 1 秒，浮点容差不能多产生第 11 发。
            for (int frame = 0; frame < 59; frame++)
                fixture.Step(1f / 60f, false, true);
            Equal(10, fixture.Recoil.FireCount, "60Hz local cadence");
            if (online)
                Equal(10, fixture.Client.ShootCalls, "60Hz network cadence");
        }
    }

    private static void Ammo(Fixture fixture, int magazine, int reserve)
    {
        Equal(magazine, fixture.Weapon.CurrentMagazine, "magazine");
        Equal(reserve, fixture.Weapon.ReserveAmmo, "reserve");
    }
    private static void Call(object target, string name)
    {
        try { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static T Private<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Equal(int expected, int actual, string label)
    { Check(expected == actual, label + ": expected " + expected + ", got " + actual); }
    private static void Equal(string expected, string actual, string label)
    { Check(expected == actual, label + ": expected " + expected + ", got " + actual); }
    private static void Near(float expected, float actual, string label)
    { Check(Math.Abs(expected - actual) < 0.0001f, label + ": expected " + expected + ", got " + actual); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
