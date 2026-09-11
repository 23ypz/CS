using System;

/* 固定生成节奏和波次数据，不依赖 Unity 场景。 */
public sealed class MonsterWaveSequence
{
    public const int TotalWaves = 3; // 总波数
    public const float IntervalSeconds = 20f; // 出怪间隔
    public const float WarningSeconds = 5f; // 提前预警时间

    public int CurrentWave { get; private set; }
    public int NextWave { get { return CurrentWave < TotalWaves ? CurrentWave + 1 : 0; } }
    public float RemainingSeconds
    {
        get { return NextWave == 0 ? 0f : (float)Math.Max(0d, CurrentWave * IntervalSeconds - elapsed); }
    }

    private double elapsed; // 本局已运行秒数
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

    /* Advance 后可重复调用，避免延迟帧丢失波次。 */
    public bool TryBeginNextWave(out Stats stats)
    {
        stats = default(Stats);
        if (NextWave == 0 || elapsed < CurrentWave * IntervalSeconds)
            return false;

        int increase = CurrentWave++;
        stats = new Stats {
            Wave = CurrentWave,
            Count = baseCount + 2 * increase,
            /* 使用整数计算，避免 10 * 1.2 的浮点误差。 */
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
