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
    public int id, ack, hp, ammo;
    public string name;
    public float x, y, z, yaw, pitch, vy;
    public bool ready, reloading;
}

[Serializable]
public class NetMessage
{
    public string type, name, token, text;
    public int version = 1;
    public int id, host, tick, count, health, shot;
    public bool ready, success;
    public double time;
    public float x, y, z, dx, dy, dz;
    public NetInput[] inputs;
    public NetEntity[] players, monsters;
    public NetworkMapData map;
}
