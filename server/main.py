"""Authoritative asyncio server for the small Unity FPS prototype.

TCP carries JSON lobby/control messages. UDP carries input and snapshots.
Run: python server/main.py [bind-address] [port]
"""
import asyncio
import json
import math
import secrets
import sys
import time
from dataclasses import dataclass
from typing import Dict, Optional, Tuple

HOST = sys.argv[1] if len(sys.argv) > 1 else "0.0.0.0"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 9000
MAX_PLAYERS = 4
TICK_RATE = 30
SNAPSHOT_RATE = 20
MAX_SPEED = 6.0
GRAVITY = 9.81


@dataclass
class Player:
    id: int
    name: str
    writer: asyncio.StreamWriter
    token: str
    address: Optional[Tuple[str, int]] = None
    x: float = 0.0
    y: float = 0.0
    z: float = 0.0
    yaw: float = 0.0
    pitch: float = 0.0
    vy: float = 0.0
    seq: int = 0
    ready: bool = False
    map_data: Optional[dict] = None


@dataclass
class Monster:
    id: int
    x: float
    y: float
    z: float
    hp: int = 10


class GameServer:
    def __init__(self):
        self.players: Dict[int, Player] = {}
        self.next_id = 1
        self.started = False
        self.tick = 0
        self.map_data: Optional[dict] = None
        self.udp_transport = None
        self.monsters: Dict[int, Monster] = {}
        self.next_monster_id = 1

    async def start(self):
        tcp = await asyncio.start_server(self.handle_tcp, HOST, PORT)
        loop = asyncio.get_running_loop()
        self.udp_transport, _ = await loop.create_datagram_endpoint(
            lambda: UdpProtocol(self), local_addr=(HOST, PORT))
        print(f"服务器已启动: {HOST}:{PORT}，最多 {MAX_PLAYERS} 名玩家")
        async with tcp:
            await asyncio.gather(tcp.serve_forever(), self.game_loop())

    async def handle_tcp(self, reader, writer):
        player = None
        try:
            while True:
                line = await reader.readline()
                if not line:
                    break
                try:
                    msg = json.loads(line.decode("utf-8"))
                except (ValueError, UnicodeDecodeError):
                    await self.send(writer, {"type": "info", "text": "消息格式错误"})
                    continue
                command = msg.get("type", "")
                if player is None:
                    if command != "hello":
                        await self.send(writer, {"type": "info", "text": "请先发送 hello"})
                        continue
                    if len(self.players) >= MAX_PLAYERS:
                        await self.send(writer, {"type": "info", "text": "服务器已满"})
                        break
                    player = Player(self.next_id, clean(msg.get("name", "")) or f"玩家{self.next_id}",
                                    writer, secrets.token_hex(12))
                    self.next_id += 1
                    self.players[player.id] = player
                    await self.send(writer, {"type": "welcome", "id": player.id, "token": player.token})
                    await self.broadcast_lobby()
                    print(f"玩家加入: {player.id} {player.name}")
                    continue
                if command == "ready":
                    player.ready = True
                    await self.broadcast_lobby()
                    if all(item.ready for item in self.players.values()):
                        await self.start_game()
                elif command == "start":
                    if player.id == min(self.players):
                        await self.start_game()
                elif command == "map":
                    if isinstance(msg.get("map"), dict):
                        player.map_data = msg["map"]
                        if self.map_data is None:
                            self.map_data = player.map_data
                        await self.send(writer, {"type": "info", "text": "地图数据已收到"})
                        if not self.started:
                            await self.broadcast_map()
                elif command == "shoot":
                    await self.handle_shoot(player, msg)
                elif command == "quit":
                    break
        except (ConnectionError, asyncio.IncompleteReadError):
            pass
        finally:
            if player is not None:
                self.players.pop(player.id, None)
                await self.broadcast_lobby()
                print(f"玩家离开: {player.id} {player.name}")
            writer.close()
            try:
                await writer.wait_closed()
            except ConnectionError:
                pass

    async def start_game(self):
        if self.started or not self.players:
            return
        if self.map_data is None:
            await self.broadcast_event({"type": "info", "text": "等待地图同步完成后再开始"})
            return
        self.started = True
        self.spawn_monsters()
        await self.broadcast_map()
        for player in self.players.values():
            await self.send(player.writer, {"type": "start", "tick": self.tick})
        print("游戏开始")

    def spawn_monsters(self):
        if self.monsters or not self.players:
            return
        origin = next(iter(self.players.values()))
        for index in range(5):
            angle = index * 2.399963
            radius = 18.0 + index * 2.0
            monster = Monster(self.next_monster_id, origin.x + math.cos(angle) * radius,
                              origin.y, origin.z + math.sin(angle) * radius)
            self.next_monster_id += 1
            self.monsters[monster.id] = monster

    async def broadcast_map(self):
        if self.map_data is None:
            return
        for player in self.players.values():
            await self.send(player.writer, {"type": "map", "map": self.map_data})

    async def broadcast_lobby(self):
        if not self.players:
            return
        text = ";".join(
            f"玩家 {p.id}：{p.name}（{'已准备' if p.ready else '等待中'}）"
            for p in sorted(self.players.values(), key=lambda item: item.id))
        for player in list(self.players.values()):
            await self.send(player.writer, {"type": "lobby", "text": text})

    async def broadcast_event(self, message):
        for player in list(self.players.values()):
            await self.send(player.writer, message)

    async def handle_shoot(self, shooter, message):
        ox, oy, oz = float(message.get("x", shooter.x)), float(message.get("y", shooter.y)), float(message.get("z", shooter.z))
        dx, dy, dz = float(message.get("dx", 0.0)), float(message.get("dy", 0.0)), float(message.get("dz", 1.0))
        length = math.sqrt(dx * dx + dy * dy + dz * dz) or 1.0
        dx, dy, dz = dx / length, dy / length, dz / length
        hit = None
        best = 1e9
        for monster in self.monsters.values():
            vx, vy, vz = monster.x - ox, monster.y + 0.7 - oy, monster.z - oz
            projection = vx * dx + vy * dy + vz * dz
            if projection < 0.0 or projection > 100.0:
                continue
            distance_sq = vx * vx + vy * vy + vz * vz - projection * projection
            if distance_sq <= 1.0 and projection < best:
                hit, best = monster, projection
        if hit is not None:
            hit.hp -= 2
            if hit.hp <= 0:
                self.monsters.pop(hit.id, None)
                await self.broadcast_event({"type": "event", "text": f"怪物 {hit.id} 已被消灭"})

    async def send(self, writer, message):
        try:
            writer.write((json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
            await writer.drain()
        except ConnectionError:
            pass

    async def game_loop(self):
        interval = 1.0 / TICK_RATE
        snapshot_clock = 0.0
        while True:
            started = time.monotonic()
            self.tick += 1
            if self.started:
                self.step_monsters()
                snapshot_clock += interval
                if snapshot_clock >= 1.0 / SNAPSHOT_RATE:
                    snapshot_clock = 0.0
                    self.broadcast_snapshot()
            await asyncio.sleep(max(0.0, interval - (time.monotonic() - started)))

    def step_monsters(self):
        if not self.monsters or not self.players:
            return
        dt = 1.0 / TICK_RATE
        for monster in self.monsters.values():
            target = min(self.players.values(), key=lambda p: (p.x - monster.x) ** 2 + (p.z - monster.z) ** 2)
            dx, dz = target.x - monster.x, target.z - monster.z
            distance = math.sqrt(dx * dx + dz * dz)
            if distance > 1.4:
                step = min(2.5 * dt, distance)
                monster.x += dx / distance * step
                monster.z += dz / distance * step
            monster.y = target.y

    def bind_udp(self, player_id, token, address):
        player = self.players.get(player_id)
        if player and secrets.compare_digest(player.token, token):
            player.address = address

    def apply_input(self, player_id, token, msg):
        player = self.players.get(player_id)
        if player is None or player.token != token:
            return
        inputs = msg.get("inputs") or []
        for command in inputs[-3:]:
            seq = int(command.get("seq", 0))
            if seq <= player.seq:
                continue
            x = float(command.get("x", 0.0))
            z = float(command.get("z", 0.0))
            yaw = float(command.get("yaw", player.yaw))
            length = math.sqrt(x * x + z * z)
            if length > 1.0:
                x, z = x / length, z / length
            dt = 1.0 / TICK_RATE
            speed = 5.0 if command.get("run") else 3.0
            rad = math.radians(yaw)
            next_x = player.x + (math.cos(rad) * x + math.sin(rad) * z) * speed * dt
            next_z = player.z + (-math.sin(rad) * x + math.cos(rad) * z) * speed * dt
            if self.can_move(next_x, next_z, player.y):
                player.x, player.z = next_x, next_z
            if command.get("jump") and player.vy == 0.0:
                player.vy = 5.0
            player.vy = max(-30.0, player.vy - GRAVITY * dt)
            player.y = max(0.0, player.y + player.vy * dt)
            if player.y <= 0.0:
                player.y = 0.0
                player.vy = 0.0
            player.yaw = yaw
            player.pitch = float(command.get("pitch", player.pitch))
            player.seq = seq
        if player.address:
            self.udp_transport.sendto(json.dumps({
                "type": "correction", "x": player.x, "y": player.y, "z": player.z,
                "yaw": player.yaw, "ack": player.seq
            }, separators=(",", ":")).encode(), player.address)

    def can_move(self, x, z, current_y):
        """Validate movement against the first client's compact map grid."""
        if not self.map_data:
            return True
        try:
            width = int(self.map_data["width"])
            depth = int(self.map_data["depth"])
            origin_x = float(self.map_data["originX"])
            origin_z = float(self.map_data["originZ"])
            cell = float(self.map_data.get("cell", 1.0))
            ix = math.floor((x - origin_x) / cell + 0.5)
            iz = math.floor((z - origin_z) / cell + 0.5)
            if ix < 0 or iz < 0 or ix >= width or iz >= depth:
                return False
            index = iz * width + ix
            walkable = self.map_data.get("walkable", [])
            heights = self.map_data.get("heights", [])
            return bool(walkable[index]) and abs(float(heights[index]) - current_y) <= 1.0
        except (KeyError, TypeError, ValueError, IndexError):
            return True

    def broadcast_snapshot(self):
        if not self.udp_transport:
            return
        players = [{
            "id": p.id, "name": p.name, "x": p.x, "y": p.y, "z": p.z,
            "yaw": p.yaw, "pitch": p.pitch, "ack": p.seq
        } for p in self.players.values()]
        monsters = [{"id": m.id, "x": m.x, "y": m.y, "z": m.z, "hp": m.hp}
                    for m in self.monsters.values()]
        packet = json.dumps({"type": "snapshot", "tick": self.tick, "players": players, "monsters": monsters},
                            separators=(",", ":")).encode()
        for player in self.players.values():
            if player.address:
                self.udp_transport.sendto(packet, player.address)


class UdpProtocol(asyncio.DatagramProtocol):
    def __init__(self, server):
        self.server = server

    def datagram_received(self, data, address):
        try:
            message = json.loads(data.decode("utf-8"))
            if message.get("type") == "bind":
                self.server.bind_udp(int(message["id"]), message.get("token", ""), address)
            elif message.get("type") == "input":
                self.server.apply_input(int(message["id"]), message.get("token", ""), message)
        except (ValueError, KeyError, TypeError, UnicodeDecodeError):
            pass


def clean(value):
    return str(value).replace("|", " ").replace(";", " ").replace(",", " ").strip()[:16]


if __name__ == "__main__":
    try:
        asyncio.run(GameServer().start())
    except KeyboardInterrupt:
        print("服务器已停止")
