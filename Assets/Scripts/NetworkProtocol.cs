using System;

[Serializable]
public class NetInput
{
    /* 输入序号供服务器确认，x/z 为移动，yaw/pitch 为水平和俯仰角。 */
    public int seq;
    public float x, z, yaw, pitch;
    public bool jump, run;
}

[Serializable]
public class NetEntity
{
    /* ack 确认输入；ammo/reserve 分别是弹夹与备用弹药。 */
    public int id, ack, hp, ammo, reserve;
    public int maxHp;
    public string name;
    public float x, y, z, yaw, pitch, vy;
    public float respawn; /* 复活剩余秒数。 */
    public bool ready, reloading, resupplying, dead;
    public bool weaponState; /* 是否携带有效武器状态。 */
    public int life, weaponAck; /* 生命代次、服务器已确认的武器命令序号。 */
    public float ammoRemaining; /* 换弹或补给的剩余秒数。 */
    public float shotInterval; /* 服务器规定的两发最小间隔（秒）。 */
}

[Serializable]
public class NetScore
{
    /* 排行榜行：服务器按分数排序，id 用于同分时稳定排序。 */
    public int id;
    public string name;
    public int score;
}

[Serializable]
public class NetMessage
{
    /* TCP/UDP 共用消息壳；字段名必须与 server/main.py 的 JSON 保持一致。 */
    public string type, name, token, text, action;
    public int version = 1;
    public int id, host, tick, count, health, shot, score;
    public int weaponSeq, life; /* 武器命令序号与生命代次，拒绝旧生命命令。 */
    public int wave, totalWaves, nextWave;
    public float waveRemaining; /* 下一波生成前的剩余秒数。 */
    public bool wavesComplete;
    public bool ready, success;
    public double time;
    public float x, y, z, dx, dy, dz; /* 位置与射击方向。 */
    public NetInput[] inputs;
    public NetEntity[] players, monsters;
    public NetScore[] scores;
    public NetworkMapData map;
}
