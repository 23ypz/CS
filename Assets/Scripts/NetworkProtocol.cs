using System;

[Serializable]
public class NetInput
{
    public int seq;
    public float x, z, yaw, pitch;
    public bool jump, run;
}

[Serializable]
public class NetEntity
{
    public int id, ack, hp, ammo, reserve;
    public int maxHp;
    public string name;
    public float x, y, z, yaw, pitch, vy;
    public float respawn;
    public bool ready, reloading, resupplying, dead;
    public bool weaponState;
    public int life, weaponAck;
    public float ammoRemaining;
    public float shotInterval;
}

[Serializable]
public class NetScore
{
    public int id;
    public string name;
    public int score;
}

[Serializable]
public class NetMessage
{
    public string type, name, token, text, action;
    public int version = 1;
    public int id, host, tick, count, health, shot, score;
    public int weaponSeq, life;
    public int wave, totalWaves, nextWave;
    public float waveRemaining;
    public bool wavesComplete;
    public bool ready, success;
    public double time;
    public float x, y, z, dx, dy, dz;
    public NetInput[] inputs;
    public NetEntity[] players, monsters;
    public NetScore[] scores;
    public NetworkMapData map;
}
