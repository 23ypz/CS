"""Authoritative asyncio server for the small Unity FPS prototype.

TCP carries JSON lobby/control messages. UDP carries input and snapshots.
Run: python server/main.py [bind-address] [port]
"""
import asyncio
import heapq
import json
import math
import random
import secrets
import sys
import time
from dataclasses import dataclass, field
from typing import Dict, Optional, Tuple

HOST = sys.argv[1] if len(sys.argv) > 1 else "0.0.0.0"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 9000
MAX_PLAYERS = 4
TICK_RATE = 30
SNAPSHOT_RATE = 20
MAX_SPEED = 6.0
GRAVITY = 9.81
# Keep monster settings bounded server-side.  The Unity setup UI uses these
# same ranges, but the server remains authoritative for every client.
DEFAULT_MONSTER_COUNT = 5
DEFAULT_MONSTER_HEALTH = 10
MIN_MONSTER_COUNT = 1
MAX_MONSTER_COUNT = 20
MIN_MONSTER_HEALTH = 1
MAX_MONSTER_HEALTH = 100
# Projectile knockback is simulated on the authoritative server so every
# client observes the same monster displacement.
MONSTER_KNOCKBACK_SPEED = 2.8
MONSTER_KNOCKBACK_DAMPING = 10.0
# The Unity client uploads an 81x81 collision map as one newline-delimited JSON
# frame. It is larger than asyncio's default 64 KiB StreamReader limit. Keep a
# finite cap so a malformed client cannot allocate unbounded memory.
MAX_TCP_MESSAGE = 16 * 1024 * 1024
PLAYER_MAX_HEALTH = 100
MONSTER_ATTACK_DAMAGE = 10
MONSTER_ATTACK_RANGE = 2.2
PLAYER_RESPAWN_SECONDS = 2.0
TOTAL_WAVES = 3
WAVE_INTERVAL_TICKS = TICK_RATE * 20


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
    score: int = 0
    max_hp: int = PLAYER_MAX_HEALTH
    hp: int = PLAYER_MAX_HEALTH
    dead: bool = False
    respawn_at: float = 0.0
    spawn_x: float = 0.0
    spawn_y: float = 0.0
    spawn_z: float = 0.0


@dataclass
class Monster:
    id: int
    x: float
    y: float
    z: float
    hp: int = 10
    move_speed: float = 2.5
    attack_damage: int = MONSTER_ATTACK_DAMAGE
    knockback_x: float = 0.0
    knockback_z: float = 0.0
    # A monster can damage each nearby player once per second.  Keeping the
    # cooldown on the authoritative monster prevents client-side rate abuse.
    attack_ticks: Dict[int, int] = field(default_factory=dict)
    # Waypoints are generated from the shared collision grid when a direct
    # pursuit segment is blocked.  Keeping this state per monster avoids
    # repeatedly rebuilding a grid path every simulation tick.
    path: list = field(default_factory=list)
    path_goal: Optional[Tuple[int, int]] = None
    path_tick: int = 0


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
        self.monster_count = DEFAULT_MONSTER_COUNT
        self.monster_health = DEFAULT_MONSTER_HEALTH
        self.pending_start = False
        self.pending_count = DEFAULT_MONSTER_COUNT
        self.pending_health = DEFAULT_MONSTER_HEALTH
        self.current_wave = 0
        self.next_wave_tick = 0
        self.waves_complete = False

    async def start(self):
        tcp = await asyncio.start_server(
            self.handle_tcp, HOST, PORT, limit=MAX_TCP_MESSAGE)
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
                try:
                    line = await reader.readline()
                except (ValueError, asyncio.LimitOverrunError) as exc:
                    # A line longer than the configured limit cannot be
                    # recovered safely because its frame boundary is unknown.
                    # Close only this client and keep the server loop alive.
                    peer = writer.get_extra_info("peername")
                    print(f"TCP 消息过长，已断开客户端 {peer}: {exc}")
                    break
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
                                    writer, secrets.token_hex(12),
                                    x=float(msg.get("x", 0.0)),
                                    y=float(msg.get("y", 0.0)),
                                    z=float(msg.get("z", 0.0)))
                    player.spawn_x, player.spawn_y, player.spawn_z = player.x, player.y, player.z
                    self.next_id += 1
                    self.players[player.id] = player
                    await self.send(writer, {"type": "welcome", "id": player.id, "token": player.token})
                    await self.broadcast_lobby()
                    print(f"玩家加入: {player.id} {player.name}")
                    continue
                if command == "ready":
                    player.ready = True
                    # A host may include the setup-screen values with Ready.
                    # This covers the common flow where all players ready up
                    # before the host presses the explicit Start button.
                    if player.id == min(self.players):
                        self.monster_count = clamp_int(
                            msg.get("count"), self.monster_count,
                            MIN_MONSTER_COUNT, MAX_MONSTER_COUNT)
                        self.monster_health = clamp_int(
                            msg.get("health"), self.monster_health,
                            MIN_MONSTER_HEALTH, MAX_MONSTER_HEALTH)
                    await self.broadcast_lobby()
                    if all(item.ready for item in self.players.values()):
                        await self.start_game()
                elif command == "start":
                    # Only the lobby host may choose match settings.  Clamp
                    # them here so every client receives one authoritative
                    # configuration, even if a malformed client sends JSON.
                    if player.id == min(self.players):
                        count = clamp_int(msg.get("count"), self.monster_count,
                                          MIN_MONSTER_COUNT, MAX_MONSTER_COUNT)
                        health = clamp_int(msg.get("health"), self.monster_health,
                                           MIN_MONSTER_HEALTH, MAX_MONSTER_HEALTH)
                        self.pending_start = True
                        self.pending_count = count
                        self.pending_health = health
                        await self.start_game(count, health)
                elif command == "map":
                    if isinstance(msg.get("map"), dict):
                        player.map_data = msg["map"]
                        if self.map_data is None:
                            self.map_data = player.map_data
                        await self.send(writer, {"type": "info", "text": "地图数据已收到"})
                        if not self.started:
                            await self.broadcast_map()
                            # Retry an auto-start that may have happened
                            # before the first map upload completed.
                            if self.pending_start:
                                await self.start_game(self.pending_count, self.pending_health)
                            elif self.players and all(item.ready for item in self.players.values()):
                                await self.start_game()
                elif command == "shoot":
                    await self.handle_shoot(player, msg)
                elif command == "quit":
                    break
        except (ConnectionError, asyncio.IncompleteReadError, asyncio.CancelledError):
            pass
        finally:
            if player is not None:
                was_host = bool(self.players) and player.id == min(self.players)
                self.players.pop(player.id, None)
                if was_host and not self.started:
                    # Do not let a departed host's queued Start request launch
                    # a later lobby with stale settings.
                    self.pending_start = False
                await self.broadcast_lobby()
                print(f"玩家离开: {player.id} {player.name}")
                if not self.players:
                    self.started = False
                    self.map_data = None
                    self.monsters.clear()
                    self.next_monster_id = 1
                    self.monster_count = DEFAULT_MONSTER_COUNT
                    self.monster_health = DEFAULT_MONSTER_HEALTH
                    self.pending_start = False
                    self.pending_count = DEFAULT_MONSTER_COUNT
                    self.pending_health = DEFAULT_MONSTER_HEALTH
                    self.current_wave = 0
                    self.next_wave_tick = 0
                    self.waves_complete = False
            writer.close()
            try:
                await writer.wait_closed()
            except ConnectionError:
                pass

    async def start_game(self, count=None, health=None):
        if self.started or not self.players:
            return
        if self.map_data is None:
            await self.broadcast_event({"type": "info", "text": "等待地图同步完成后再开始"})
            return
        self.monster_count = clamp_int(count, self.monster_count,
                                      MIN_MONSTER_COUNT, MAX_MONSTER_COUNT)
        self.monster_health = clamp_int(health, self.monster_health,
                                        MIN_MONSTER_HEALTH, MAX_MONSTER_HEALTH)
        for player in self.players.values():
            player.score = 0
            player.max_hp = PLAYER_MAX_HEALTH
            player.hp = player.max_hp
            player.dead = False
            player.respawn_at = 0.0
            player.spawn_x, player.spawn_y, player.spawn_z = player.x, player.y, player.z
        self.started = True
        self.current_wave = 0
        self.next_wave_tick = self.tick
        self.waves_complete = False
        self.monsters.clear()
        self._advance_waves()
        await self.broadcast_map()
        for player in self.players.values():
            await self.send(player.writer, {
                "type": "start", "tick": self.tick,
                "count": self.monster_count, "health": self.monster_health,
                "wave": self.current_wave, "totalWaves": TOTAL_WAVES,
                "nextWave": (self.current_wave + 1 if self.current_wave < TOTAL_WAVES else 0),
                "waveRemaining": (max(0, self.next_wave_tick - self.tick) / TICK_RATE
                                   if self.current_wave < TOTAL_WAVES else 0.0),
                "wavesComplete": False
            })
        self.pending_start = False
        print("游戏开始")

    def _advance_waves(self):
        while self.current_wave < TOTAL_WAVES and self.tick >= self.next_wave_tick:
            self.current_wave += 1
            self.spawn_monsters(self.current_wave)
            self.next_wave_tick = self.tick + WAVE_INTERVAL_TICKS
        if self.current_wave >= TOTAL_WAVES:
            self.next_wave_tick = 0

    def spawn_monsters(self, wave=1):
        if not self.players:
            return
        wave = max(1, min(TOTAL_WAVES, int(wave)))
        origin = next(iter(self.players.values()))
        count = self.monster_count + 2 * (wave - 1)
        health = (self.monster_health * (10 + 2 * (wave - 1)) + 9) // 10
        damage = MONSTER_ATTACK_DAMAGE + 2 * (wave - 1)
        speed = 2.5 * (1.0 + 0.1 * (wave - 1))
        for index in range(count):
            angle = index * 2.399963
            # Keep every configured count inside the client's 81x81 map.  An
            # ever-growing radius would place the upper end of the 1-20 range
            # beyond that authoritative collision grid.
            radius = 18.0 + (index % 5) * 2.0
            monster_x = origin.x + math.cos(angle) * radius
            monster_z = origin.z + math.sin(angle) * radius
            spawn = self.find_nearest_walkable(monster_x, monster_z, origin.x, origin.z)
            if spawn is None:
                continue
            monster_x, monster_z, monster_y = spawn
            monster = Monster(self.next_monster_id, monster_x, monster_y, monster_z,
                              hp=health, move_speed=speed, attack_damage=damage)
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

    def score_snapshot(self):
        """Return deterministic leaderboard rows (highest score first)."""
        return [{"id": p.id, "name": p.name, "score": getattr(p, "score", 0)}
                for p in sorted(self.players.values(),
                                key=lambda item: (-getattr(item, "score", 0), item.id))]

    async def handle_shoot(self, shooter, message):
        if not self.started or shooter.id not in self.players or shooter.dead:
            return
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
            # Apply the same small horizontal impulse used by the local
            # MonsterAI.  Position remains server-authoritative and is sent in
            # the next snapshot, so clients cannot diverge from the hit result.
            hit.knockback_x += dx * MONSTER_KNOCKBACK_SPEED
            hit.knockback_z += dz * MONSTER_KNOCKBACK_SPEED
            speed_sq = hit.knockback_x * hit.knockback_x + hit.knockback_z * hit.knockback_z
            max_speed_sq = (MONSTER_KNOCKBACK_SPEED * 1.8) ** 2
            if speed_sq > max_speed_sq:
                scale = (max_speed_sq / speed_sq) ** 0.5
                hit.knockback_x *= scale
                hit.knockback_z *= scale
            hit.hp -= 2
            if hit.hp <= 0:
                self.monsters.pop(hit.id, None)
                # Every destroyed monster is worth ten points to the player
                # whose authoritative shot dealt the killing blow.  Scores
                # are included in every snapshot for deterministic sorting.
                shooter.score = getattr(shooter, "score", 0) + 10
                await self.broadcast_event({
                    "type": "event",
                    "tick": self.tick,
                    "text": f"怪物 {hit.id} 已被消灭",
                    "id": shooter.id,
                    "score": shooter.score,
                    "scores": self.score_snapshot()
                })

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
        if not self.players:
            return
        dt = 1.0 / TICK_RATE
        self._update_respawns()
        self._advance_waves()
        alive_players = [p for p in self.players.values() if not p.dead]
        if not self.monsters or not alive_players:
            if self.current_wave >= TOTAL_WAVES and not self.monsters:
                self.waves_complete = True
            return
        for monster in self.monsters.values():
            # Integrate projectile impulse before pursuit.  Collision checks
            # keep the knockback from moving a monster through map walls.
            impulse_x = monster.knockback_x * dt
            impulse_z = monster.knockback_z * dt
            if abs(impulse_x) > 1e-6 or abs(impulse_z) > 1e-6:
                next_x = monster.x + impulse_x
                next_z = monster.z + impulse_z
                if self.can_move_segment(monster.x, monster.z, next_x, next_z,
                                         monster.y, radius=0.55):
                    monster.x, monster.z = next_x, next_z
                damping = MONSTER_KNOCKBACK_DAMPING * dt
                speed = math.sqrt(monster.knockback_x ** 2 + monster.knockback_z ** 2)
                if speed <= damping:
                    monster.knockback_x = monster.knockback_z = 0.0
                elif speed > 0.0:
                    factor = (speed - damping) / speed
                    monster.knockback_x *= factor
                    monster.knockback_z *= factor
            target = min(alive_players, key=lambda p: (p.x - monster.x) ** 2 + (p.z - monster.z) ** 2)
            dx, dz = target.x - monster.x, target.z - monster.z
            distance = math.sqrt(dx * dx + dz * dz)
            if distance > 1.4:
                # Reduce pursuit while an impulse is active so the retreat is
                # visible instead of being cancelled by the same tick's chase.
                knockback_speed = math.sqrt(monster.knockback_x ** 2 + monster.knockback_z ** 2)
                pursuit_scale = 0.35 if knockback_speed > 0.25 else 1.0
                step = min(monster.move_speed * dt * pursuit_scale, distance)
                direction_x, direction_z = dx / distance, dz / distance
                perpendicular_x, perpendicular_z = -direction_z, direction_x
                candidates = [
                    (direction_x, direction_z),
                    (perpendicular_x, perpendicular_z),
                    (-perpendicular_x, -perpendicular_z),
                    (direction_x + perpendicular_x, direction_z + perpendicular_z),
                    (direction_x - perpendicular_x, direction_z - perpendicular_z),
                ]
                path_used = False
                # Prefer the global route whenever the complete direct segment
                # is blocked. Local steering alone can otherwise keep choosing
                # the same short side step at a long wall and appear stuck.
                if self.map_data and not self.can_move_segment(
                        monster.x, monster.z, target.x, target.z,
                        monster.y, radius=0.55):
                    waypoint = self._monster_waypoint(monster, target)
                    if waypoint is not None:
                        wx, wz = waypoint
                        wdx, wdz = wx - monster.x, wz - monster.z
                        wlen = math.sqrt(wdx * wdx + wdz * wdz)
                        if wlen > 1e-5:
                            move = min(step, wlen)
                            nx = monster.x + wdx / wlen * move
                            nz = monster.z + wdz / wlen * move
                            if self.can_move_segment(monster.x, monster.z, nx, nz,
                                                     monster.y, radius=0.55):
                                monster.x, monster.z = nx, nz
                                path_used = True
                best = None
                best_distance = float("inf")
                for candidate_x, candidate_z in ([] if path_used else candidates):
                    length = math.sqrt(candidate_x * candidate_x + candidate_z * candidate_z) or 1.0
                    next_x = monster.x + candidate_x / length * step
                    next_z = monster.z + candidate_z / length * step
                    if not self.can_move_segment(monster.x, monster.z, next_x, next_z,
                                                 monster.y, radius=0.55):
                        continue
                    candidate_distance = (target.x - next_x) ** 2 + (target.z - next_z) ** 2
                    if candidate_distance < best_distance:
                        best_distance = candidate_distance
                        best = (next_x, next_z)
                if best is not None:
                    monster.x, monster.z = best
                # The local steering above handles small corners, but a long
                # wall or U-shaped building needs a real route.  If the
                # direct segment is blocked, follow a waypoint from the
                # authoritative collision grid instead of attempting to walk
                # through the obstacle.
                elif self.map_data and not path_used:
                    waypoint = self._monster_waypoint(monster, target)
                    if waypoint is not None:
                        wx, wz = waypoint
                        wdx, wdz = wx - monster.x, wz - monster.z
                        wlen = math.sqrt(wdx * wdx + wdz * wdz)
                        if wlen > 1e-5:
                            move = min(step, wlen)
                            nx = monster.x + wdx / wlen * move
                            nz = monster.z + wdz / wlen * move
                            if self.can_move_segment(monster.x, monster.z, nx, nz,
                                                     monster.y, radius=0.55):
                                monster.x, monster.z = nx, nz
            ground = self.ground_height(monster.x, monster.z, monster.y)
            if ground is not None:
                monster.y = ground
        self._apply_monster_attacks()

    def _monster_waypoint(self, monster, target):
        """Return the next walkable A* waypoint for a blocked pursuit."""
        start = self.map_cell(monster.x, monster.z)
        raw_goal = self.map_cell(target.x, target.z)
        if start is None or raw_goal is None:
            monster.path = []
            monster.path_goal = raw_goal
            return None
        width = int(self.map_data.get("width", 0))
        depth = int(self.map_data.get("depth", 0))
        if width <= 0 or depth <= 0:
            return None
        start_cell = (start % width, start // width)
        # A player can stand next to a wall, where the target grid cell is not
        # large enough for a monster's footprint. Select the nearest cell that
        # has clearance for the complete monster radius instead of generating a
        # path to an unreachable cell.
        goal_cell = self._nearest_clear_cell(target.x, target.z, width, depth,
                                             monster.y, 6)
        start_cell = self._nearest_clear_cell(monster.x, monster.z, width, depth,
                                              monster.y, 2) or start_cell
        if goal_cell is None or start_cell == goal_cell:
            monster.path = []
            monster.path_goal = goal_cell
            return None
        if monster.path_goal != goal_cell or not monster.path or self.tick - monster.path_tick > 15:
            monster.path = self._find_grid_path(start_cell, goal_cell, width)
            monster.path_goal = goal_cell
            monster.path_tick = self.tick
        while monster.path:
            cell = monster.path[0]
            wx, wz = self._cell_world(cell[0], cell[1], width)
            if (wx - monster.x) ** 2 + (wz - monster.z) ** 2 < 0.35 ** 2:
                monster.path.pop(0)
                continue
            return wx, wz
        return None

    def _cell_world(self, ix, iz, width):
        cell = float(self.map_data.get("cell", 1.0))
        return (float(self.map_data.get("originX", 0.0)) + ix * cell,
                float(self.map_data.get("originZ", 0.0)) + iz * cell)

    def _find_grid_path(self, start, goal, width):
        """A bounded 8-neighbour A* over the uploaded walkability grid."""
        depth = int(self.map_data.get("depth", 0))
        walkable = self.map_data.get("walkable", [])
        if depth <= 0 or not walkable:
            return []
        def valid(x, z):
            if x < 0 or z < 0 or x >= width or z >= depth:
                return False
            index = z * width + x
            if index >= len(walkable) or not walkable[index]:
                return False
            world_x, world_z = self._cell_world(x, z, width)
            ground = self.ground_height(world_x, world_z, None)
            # Inflate the grid by the monster footprint. The old planner only
            # checked the cell centre, producing waypoints that the real
            # radius=0.55 movement could never reach at wall corners.
            return ground is not None and self.can_move(world_x, world_z, ground, radius=0.55)
        if not valid(*start) or not valid(*goal):
            return []
        frontier = [(0.0, start)]
        came = {start: None}
        cost = {start: 0.0}
        while frontier and len(came) < 6000:
            _, current = heapq.heappop(frontier)
            if current == goal:
                result = []
                while current != start:
                    result.append(current)
                    current = came[current]
                result.reverse()
                return result
            cx, cz = current
            for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1),
                           (1, 1), (1, -1), (-1, 1), (-1, -1)):
                nx, nz = cx + dx, cz + dz
                if not valid(nx, nz):
                    continue
                # Do not cut diagonally across a blocked corner.
                if dx and dz and (not valid(cx + dx, cz) or not valid(cx, cz + dz)):
                    continue
                new_cost = cost[current] + (1.41421356 if dx and dz else 1.0)
                neighbor = (nx, nz)
                if new_cost >= cost.get(neighbor, float("inf")):
                    continue
                cost[neighbor] = new_cost
                heuristic = abs(goal[0] - nx) + abs(goal[1] - nz)
                came[neighbor] = current
                heapq.heappush(frontier, (new_cost + heuristic, neighbor))
        return []

    def _nearest_clear_cell(self, x, z, width, depth, current_y, max_radius):
        """Find a nearby grid cell with enough clearance for a monster."""
        center = self.map_cell(x, z)
        if center is None:
            return None
        cx, cz = center % width, center // width
        candidates = []
        for radius in range(max(0, int(max_radius)) + 1):
            for dz in range(-radius, radius + 1):
                for dx in range(-radius, radius + 1):
                    if max(abs(dx), abs(dz)) != radius:
                        continue
                    ix, iz = cx + dx, cz + dz
                    if ix < 0 or iz < 0 or ix >= width or iz >= depth:
                        continue
                    wx, wz = self._cell_world(ix, iz, width)
                    ground = self.ground_height(wx, wz, None)
                    if ground is None or abs(ground - current_y) > 1.0:
                        continue
                    if not self.can_move(wx, wz, ground, radius=0.55):
                        continue
                    candidates.append((dx * dx + dz * dz, (ix, iz)))
            if candidates:
                candidates.sort(key=lambda item: item[0])
                return candidates[0][1]
        return None

    def _update_respawns(self):
        """Restore dead players after the authoritative two second timer."""
        now = time.monotonic()
        for player in self.players.values():
            if not player.dead or now < player.respawn_at:
                continue
            respawn = self.find_random_respawn(player)
            if respawn is None:
                respawn = (player.spawn_x, player.spawn_z, player.spawn_y)
            player.x, player.z, player.y = respawn
            player.vy = 0.0
            player.hp = player.max_hp
            player.dead = False
            player.respawn_at = 0.0

    def find_random_respawn(self, player):
        """Choose a different walkable point near the player's start point."""
        origin_x, origin_z = player.spawn_x, player.spawn_z
        # Several independent samples keep the result unpredictable while
        # still keeping the player inside the uploaded collision map.
        for _ in range(32):
            angle = random.random() * math.pi * 2.0
            radius = random.uniform(5.0, 16.0)
            x = origin_x + math.cos(angle) * radius
            z = origin_z + math.sin(angle) * radius
            ground = self.ground_height(x, z, player.spawn_y)
            if ground is None or not self.can_move(x, z, ground, radius=0.3):
                continue
            if (x - player.x) ** 2 + (z - player.z) ** 2 < 4.0:
                continue
            occupied = False
            for other in self.players.values():
                if other is player or other.dead:
                    continue
                if (x - other.x) ** 2 + (z - other.z) ** 2 < 2.25:
                    occupied = True
                    break
            if occupied:
                continue
            for monster in self.monsters.values():
                if (x - monster.x) ** 2 + (z - monster.z) ** 2 < 9.0:
                    occupied = True
                    break
            if not occupied:
                return x, z, ground
        return None

    def _apply_monster_attacks(self):
        """Apply one 10 HP hit per monster/player pair each second nearby."""
        attack_range_sq = MONSTER_ATTACK_RANGE * MONSTER_ATTACK_RANGE
        for monster in self.monsters.values():
            for player in self.players.values():
                if player.dead:
                    continue
                dx = player.x - monster.x
                dz = player.z - monster.z
                if dx * dx + dz * dz > attack_range_sq:
                    continue
                last_tick = monster.attack_ticks.get(player.id, self.tick - TICK_RATE)
                if self.tick - last_tick < TICK_RATE:
                    continue
                monster.attack_ticks[player.id] = self.tick
                player.hp = max(0, player.hp - monster.attack_damage)
                if player.hp <= 0:
                    player.dead = True
                    player.respawn_at = time.monotonic() + PLAYER_RESPAWN_SECONDS
                    player.vy = 0.0

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
            if player.dead:
                # Keep acknowledging input while the player is down so the
                # client does not replay stale movement when it respawns.
                player.seq = seq
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
            if self.can_move(next_x, next_z, player.y, radius=0.3):
                player.x, player.z = next_x, next_z

            current_ground = self.ground_height(player.x, player.z, player.y)
            if current_ground is None:
                current_ground = player.y
            grounded = player.vy <= 0.0 and player.y <= current_ground + 0.15
            wants_jump = bool(command.get("jump")) and grounded
            if wants_jump:
                player.vy = 5.0
            if not grounded or wants_jump:
                player.vy = max(-30.0, player.vy - GRAVITY * dt)
                player.y += player.vy * dt

            next_ground = self.ground_height(player.x, player.z, current_ground)
            if next_ground is None:
                next_ground = current_ground
            if player.y <= next_ground:
                player.y = next_ground
                player.vy = 0.0
            player.yaw = yaw
            player.pitch = float(command.get("pitch", player.pitch))
            player.seq = seq
        if player.address:
            self.udp_transport.sendto(json.dumps({
                "type": "correction", "x": player.x, "y": player.y, "z": player.z,
                "yaw": player.yaw, "ack": player.seq
            }, separators=(",", ":")).encode(), player.address)

    def map_cell(self, x, z):
        if not self.map_data:
            return None
        try:
            width = int(self.map_data["width"])
            depth = int(self.map_data["depth"])
            origin_x = float(self.map_data["originX"])
            origin_z = float(self.map_data["originZ"])
            cell = float(self.map_data.get("cell", 1.0))
            ix = math.floor((x - origin_x) / cell + 0.5)
            iz = math.floor((z - origin_z) / cell + 0.5)
            if ix < 0 or iz < 0 or ix >= width or iz >= depth:
                return None
            return iz * width + ix
        except (KeyError, TypeError, ValueError, IndexError):
            return None

    def ground_height(self, x, z, fallback=None):
        index = self.map_cell(x, z)
        if index is None:
            return fallback
        walkable = self.map_data.get("walkable", [])
        heights = self.map_data.get("heights", [])
        if index >= len(walkable) or index >= len(heights) or not walkable[index]:
            return None
        return float(heights[index])

    def can_move(self, x, z, current_y, radius=0.0):
        """Validate an actor footprint against the shared collision grid."""
        if not self.map_data:
            return True
        samples = [(x, z)]
        if radius > 0.0:
            samples.extend(((x + radius, z), (x - radius, z),
                            (x, z + radius), (x, z - radius)))
        for sample_x, sample_z in samples:
            ground = self.ground_height(sample_x, sample_z, None)
            if ground is None or abs(ground - current_y) > 1.0:
                return False
        return True

    def can_move_segment(self, start_x, start_z, end_x, end_z, current_y, radius=0.0):
        distance = math.sqrt((end_x - start_x) ** 2 + (end_z - start_z) ** 2)
        samples = max(1, int(math.ceil(distance / 0.2)))
        for step in range(1, samples + 1):
            t = step / samples
            x = start_x + (end_x - start_x) * t
            z = start_z + (end_z - start_z) * t
            if not self.can_move(x, z, current_y, radius):
                return False
        return True

    def find_nearest_walkable(self, x, z, fallback_x, fallback_z):
        for radius in range(0, 12):
            for angle_index in range(16):
                angle = angle_index * math.pi * 2.0 / 16.0
                candidate_x = x + math.cos(angle) * radius
                candidate_z = z + math.sin(angle) * radius
                ground = self.ground_height(candidate_x, candidate_z, None)
                if ground is not None and self.can_move(candidate_x, candidate_z, ground, radius=0.45):
                    return candidate_x, candidate_z, ground
        ground = self.ground_height(fallback_x, fallback_z, None)
        if ground is not None:
            return fallback_x, fallback_z, ground
        return None

    def broadcast_snapshot(self):
        if not self.udp_transport:
            return
        players = [{
            "id": p.id, "name": p.name, "x": p.x, "y": p.y, "z": p.z,
            "yaw": p.yaw, "pitch": p.pitch, "ack": p.seq,
            "hp": p.hp, "maxHp": p.max_hp, "dead": p.dead,
            "respawn": max(0.0, p.respawn_at - time.monotonic()) if p.dead else 0.0
        } for p in self.players.values()]
        monsters = [{"id": m.id, "x": m.x, "y": m.y, "z": m.z, "hp": m.hp}
                    for m in self.monsters.values()]
        scores = self.score_snapshot()
        next_wave = self.current_wave + 1 if self.current_wave < TOTAL_WAVES else 0
        remaining = (max(0, self.next_wave_tick - self.tick) / TICK_RATE
                     if next_wave else 0.0)
        packet = json.dumps({"type": "snapshot", "tick": self.tick,
                             "players": players, "monsters": monsters,
                             "scores": scores,
                             "count": self.monster_count,
                             "health": self.monster_health,
                             "wave": self.current_wave,
                             "totalWaves": TOTAL_WAVES,
                             "nextWave": next_wave,
                             "waveRemaining": remaining,
                             "wavesComplete": self.waves_complete},
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


def clamp_int(value, fallback, minimum, maximum):
    """Parse a client setting and constrain it to the server's safe range."""
    try:
        # bool is an int subclass, but accepting true/false as a monster count
        # is surprising and can hide malformed packets.
        if isinstance(value, bool):
            raise ValueError
        parsed = int(value)
    except (TypeError, ValueError, OverflowError):
        parsed = int(fallback)
    return max(minimum, min(maximum, parsed))


def clean(value):
    return str(value).replace("|", " ").replace(";", " ").replace(",", " ").strip()[:16]


if __name__ == "__main__":
    try:
        asyncio.run(GameServer().start())
    except KeyboardInterrupt:
        print("服务器已停止")
