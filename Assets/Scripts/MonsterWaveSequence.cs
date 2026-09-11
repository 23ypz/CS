using System;

/// <summary>Fixed spawn cadence and per-wave stats, independent of Unity's scene.</summary>
public sealed class MonsterWaveSequence
{
    public const int TotalWaves = 3;
    public const float IntervalSeconds = 20f;
    public const float WarningSeconds = 5f;

    public int CurrentWave { get; private set; }
    public int NextWave { get { return CurrentWave < TotalWaves ? CurrentWave + 1 : 0; } }
    public float RemainingSeconds
    {
        get { return NextWave == 0 ? 0f : (float)Math.Max(0d, CurrentWave * IntervalSeconds - elapsed); }
    }

    private double elapsed;
    private int baseCount;
    private int baseHealth;
    private float baseSpeed;

    public MonsterWaveSequence(int count, int health, float speed)
    {
        baseCount = Math.Max(1, Math.Min(20, count));
        baseHealth = Math.Max(1, Math.Min(100, health));
        baseSpeed = Math.Max(0.1f, speed);
    }

    public void Advance(float deltaTime)
    {
        if (!float.IsNaN(deltaTime) && !float.IsInfinity(deltaTime))
            elapsed += Math.Max(0f, deltaTime);
    }

    /// <summary>Call repeatedly after Advance to handle a delayed frame without losing a wave.</summary>
    public bool TryBeginNextWave(out Stats stats)
    {
        stats = default(Stats);
        if (NextWave == 0 || elapsed < CurrentWave * IntervalSeconds)
            return false;

        int increase = CurrentWave++;
        stats = new Stats {
            Wave = CurrentWave,
            Count = baseCount + 2 * increase,
            // Integer arithmetic avoids rounding 10 * 1.2 to 13 by accident.
            Health = (baseHealth * (10 + 2 * increase) + 9) / 10,
            Damage = 10 + 2 * increase,
            Speed = baseSpeed * (1f + 0.1f * increase)
        };
        return true;
    }

    public struct Stats
    {
        public int Wave, Count, Health, Damage;
        public float Speed;
    }
}
