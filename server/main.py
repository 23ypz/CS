import asyncio
import heapq
import json
import math
import random
import secrets
import sys
import time
from collections import defaultdict

HOST = sys.argv[1] if len(sys.argv) > 1 else "0.0.0.0"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 9000
MAX_PLAYERS = 4
TICK_RATE = 30
SNAPSHOT_RATE = 20
MAX_SPEED = 6.0
GRAVITY = 9.81
# 服务端限制怪物设置范围，并对所有客户端保持权威。
DEFAULT_MONSTER_COUNT = 5
DEFAULT_MONSTER_HEALTH = 10
MIN_MONSTER_COUNT = 1
MAX_MONSTER_COUNT = 20
MIN_MONSTER_HEALTH = 1
MAX_MONSTER_HEALTH = 100
# 服务端计算子弹击退，确保所有客户端看到相同位移。
MONSTER_KNOCKBACK_SPEED = 2.8
MONSTER_KNOCKBACK_DAMPING = 10.0
# 客户端上传 81x81 碰撞图，单条 JSON 会超过 asyncio 默认限制；设置上限。
MAX_TCP_MESSAGE = 16 * 1024 * 1024
PLAYER_MAX_HEALTH = 100
MONSTER_ATTACK_DAMAGE = 10
MONSTER_ATTACK_RANGE = 2.2
PLAYER_RESPAWN_SECONDS = 2.0
TOTAL_WAVES = 3
WAVE_INTERVAL_TICKS = TICK_RATE * 20
MAGAZINE_CAPACITY = 50
RESERVE_CAPACITY = 200
RELOAD_SECONDS = 1.5
RESUPPLY_SECONDS = 2.0
# 与现有 CityNew Player 的 bulletInterval（600 RPM）保持一致。
SHOT_INTERVAL_SECONDS = 0.1

class _NewState:
    """区分参数省略与显式 None，确保可变默认值互不共享。"""

    def __repr__(self):
        return "<factory>"

_NEW_STATE = _NewState()

class Player:
    def __init__(self, id, name, writer, token, address=None,
                 x=0.0, y=0.0, z=0.0, yaw=0.0, pitch=0.0, vy=0.0,
                 seq=0, ready=False, map_data=None, score=0,
                 max_hp=PLAYER_MAX_HEALTH, hp=PLAYER_MAX_HEALTH,
                 dead=False, respawn_at=0.0, spawn_x=0.0, spawn_y=0.0,
                 spawn_z=0.0, magazine=MAGAZINE_CAPACITY,
                 reserve_ammo=RESERVE_CAPACITY, ammo_action="",
                 ammo_action_started=0.0, next_shot_at=0.0, weapon_ack=0,
                 life=0):
        self.id = id
        self.name = name
        self.writer = writer
        self.token = token
        self.address = address
        # 位姿、垂直速度及已处理的输入序号。
        self.x, self.y, self.z = x, y, z
        self.yaw, self.pitch = yaw, pitch
        self.vy = vy
        self.seq = seq
        self.ready = ready
        self.map_data = map_data
        self.score = score
        self.max_hp = max_hp
        self.hp = hp
        self.dead = dead
        self.respawn_at = respawn_at
        self.spawn_x, self.spawn_y, self.spawn_z = spawn_x, spawn_y, spawn_z
        # 服务端权威弹药状态与动作计时。
        self.magazine, self.reserve_ammo = magazine, reserve_ammo
        self.ammo_action = ammo_action
        self.ammo_action_started = ammo_action_started
        self.next_shot_at = next_shot_at
        # 确认号去重命令，生命代次隔离复活前输入。
        self.weapon_ack = weapon_ack
        self.life = life

class Monster:
    def __init__(self, id, x, y, z, hp=10, move_speed=2.5,
                 attack_damage=MONSTER_ATTACK_DAMAGE, knockback_x=0.0,
                 knockback_z=0.0, attack_ticks=_NEW_STATE, path=_NEW_STATE,
                 path_goal=None, path_tick=0, stuck_ticks=0):
        self.id = id
        self.x, self.y, self.z = x, y, z
        self.hp = hp
        self.move_speed = move_speed
        self.attack_damage = attack_damage
        self.knockback_x, self.knockback_z = knockback_x, knockback_z
        # 每只怪物独立保存攻击冷却和寻路状态。
        self.attack_ticks = {} if attack_ticks is _NEW_STATE else attack_ticks
        self.path = [] if path is _NEW_STATE else path
        self.path_goal = path_goal
        self.path_tick = path_tick
        self.stuck_ticks = stuck_ticks

class GameServer:
    def __init__(self):
        self.players = {}
        self.next_id = 1
        self.started = False
        self.tick = 0
        self.map_data = None
        self.udp_transport = None
        self.monsters = {}
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
        # TCP 主循环：逐行解码 JSON，再按命令类型更新大厅或比赛状态。
        player = None
        try:
            while True:
                try:
                    line = await reader.readline()
                except (ValueError, asyncio.LimitOverrunError) as exc:
                    # 超长消息的帧边界无法安全恢复，只断开当前客户端。
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
                    # 首帧必须是 hello；随后为玩家分配令牌和出生点。
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
                    await self.send(writer, {"type": "welcome", "version": 3,
                                             "id": player.id, "token": player.token})
                    await self.broadcast_lobby()
                    print(f"玩家加入: {player.id} {player.name}")
                    continue
                if command == "ready":
                    # 准备阶段只由房主写入怪物数量和血量，服务端统一限幅。
                    player.ready = True
                    # 房主可随“准备”上传配置，兼容全员先准备的流程。
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
                    # 仅房主可设置比赛参数，服务端统一校验范围。
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
                    # 地图帧到达后重试此前等待地图的自动开局。
                    if isinstance(msg.get("map"), dict):
                        player.map_data = msg["map"]
                        if self.map_data is None:
                            self.map_data = player.map_data
                        await self.send(writer, {"type": "info", "text": "地图数据已收到"})
                        if not self.started:
                            await self.broadcast_map()
                            # 地图上传后重试此前因缺图推迟的自动开局。
                            if self.pending_start:
                                await self.start_game(self.pending_count, self.pending_health)
                            elif self.players and all(item.ready for item in self.players.values()):
                                await self.start_game()
                elif command == "shoot":
                    # 射击和弹药命令仍在 TCP 顺序流中校验序号。
                    await self.handle_shoot(player, msg)
                elif command == "ammo_action":
                    self.handle_ammo_action(player, msg)
                elif command == "quit":
                    break
        except (ConnectionError, asyncio.IncompleteReadError, asyncio.CancelledError):
            pass
        finally:
            if player is not None:
                # 断线阶段：移除玩家，并在空房间时重置波次和配置。
                was_host = bool(self.players) and player.id == min(self.players)
                self.players.pop(player.id, None)
                if was_host and not self.started:
                    # 清除离开房主留下的待开局配置。
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
        # 开局阶段：重置玩家生命/弹药，立即生成第一波并发送 start。
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
            self.reset_player_ammo(player)
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
        # 波次阶段：按服务端 tick 补齐到期波次，不等待旧怪物清空。
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
            # 循环半径让 1—20 的配置始终落在 81x81 碰撞图内。
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
        """按分数降序、玩家编号升序生成稳定排行榜。"""
        return [{"id": p.id, "name": p.name, "score": getattr(p, "score", 0)}
                for p in sorted(self.players.values(),
                                key=lambda item: (-getattr(item, "score", 0), item.id))]

    async def handle_shoot(self, shooter, message):
        # 射击阶段：先验武器序号，再限速扣弹，最后做权威射线命中。
        if not self.accept_weapon_command(shooter, message):
            return
        now = time.monotonic()
        self.update_player_ammo(shooter, now)
        if shooter.ammo_action:
            return
        # 容忍一帧网络抖动，同时维持武器射速。
        if now + 1.0 / TICK_RATE + 1e-6 < shooter.next_shot_at:
            return
        if shooter.magazine <= 0:
            self.start_ammo_action(shooter, "reload", now)
            return
        try:
            ox, oy, oz = (float(message.get("x", shooter.x)),
                          float(message.get("y", shooter.y)),
                          float(message.get("z", shooter.z)))
            dx, dy, dz = (float(message.get("dx", 0.0)),
                          float(message.get("dy", 0.0)),
                          float(message.get("dz", 1.0)))
        except (TypeError, ValueError, OverflowError):
            return
        if not all(math.isfinite(value) for value in (ox, oy, oz, dx, dy, dz)):
            return
        # 枪口必须靠近服务端记录的玩家位置。
        if (ox - shooter.x) ** 2 + (oy - shooter.y) ** 2 + (oz - shooter.z) ** 2 > 9.0:
            ox, oy, oz = shooter.x, shooter.y + 0.8, shooter.z
        length = math.hypot(dx, dy, dz)
        if not math.isfinite(length) or length < 1e-6:
            return
        dx, dy, dz = dx / length, dy / length, dz / length
        shooter.magazine -= 1
        shooter.next_shot_at = max(now, shooter.next_shot_at) + SHOT_INTERVAL_SECONDS
        if shooter.magazine == 0:
            self.start_ammo_action(shooter, "reload", now)
        hit = None
        best = 1e9
        for monster in self.monsters.values():
            vx, vy, vz = monster.x - ox, monster.y + 0.7 - oy, monster.z - oz
            proj = vx * dx + vy * dy + vz * dz
            if proj < 0.0 or proj > 100.0:
                continue
            dist_sq = vx * vx + vy * vy + vz * vz - proj * proj
            if dist_sq <= 1.0 and proj < best:
                if self.map_data and not self.can_shoot_segment(
                        shooter.x, shooter.z, monster.x, monster.z):
                    continue
                hit, best = monster, proj
        if hit is not None:
            # 统一计算水平击退，并在下一帧快照同步。
            hit.knockback_x += dx * MONSTER_KNOCKBACK_SPEED
            hit.knockback_z += dz * MONSTER_KNOCKBACK_SPEED
            kb_sq = hit.knockback_x * hit.knockback_x + hit.knockback_z * hit.knockback_z
            max_kb_sq = (MONSTER_KNOCKBACK_SPEED * 1.8) ** 2
            if kb_sq > max_kb_sq:
                scale = (max_kb_sq / kb_sq) ** 0.5
                hit.knockback_x *= scale
                hit.knockback_z *= scale
            hit.hp -= 2
            if hit.hp <= 0:
                self.monsters.pop(hit.id, None)
                # 击杀者获得 10 分，快照内同步稳定排序后的成绩。
                shooter.score = getattr(shooter, "score", 0) + 10
                await self.broadcast_event({
                    "type": "event",
                    "tick": self.tick,
                    "text": f"怪物 {hit.id} 已被消灭",
                    "id": shooter.id,
                    "score": shooter.score,
                    "scores": self.score_snapshot()
                })

    @staticmethod
    def reset_player_ammo(player):
        player.magazine = MAGAZINE_CAPACITY
        player.reserve_ammo = RESERVE_CAPACITY
        player.ammo_action = ""
        player.ammo_action_started = 0.0
        player.next_shot_at = 0.0
        # 死亡前排队的命令不能消耗新生命的弹药。
        player.life += 1

    def accept_weapon_command(self, player, message):
        if not self.started or player.id not in self.players:
            return False
        seq = message.get("weaponSeq")
        life = message.get("life")
        if (type(seq) is not int or type(life) is not int or
                seq <= player.weapon_ack or life != player.life):
            return False
        player.weapon_ack = seq
        return not player.dead

    def start_ammo_action(self, player, action, now=None):
        if now is None:
            now = time.monotonic()
        if player.dead:
            return
        if action == "reload":
            if player.ammo_action:
                return
            if player.magazine >= MAGAZINE_CAPACITY or player.reserve_ammo <= 0:
                return
        elif action == "resupply":
            if player.ammo_action == "resupply":
                return
            if (player.magazine >= MAGAZINE_CAPACITY and
                    player.reserve_ammo >= RESERVE_CAPACITY):
                return
        else:
            return
        player.ammo_action = action
        player.ammo_action_started = now

    def handle_ammo_action(self, player, message):
        if not self.accept_weapon_command(player, message):
            return
        action = message.get("action", "")
        now = time.monotonic()
        self.update_player_ammo(player, now)
        if action == "reload":
            self.start_ammo_action(player, "reload", now)
        elif action == "resupply_start":
            self.start_ammo_action(player, "resupply", now)
        elif action == "resupply_cancel" and player.ammo_action == "resupply":
            player.ammo_action = ""
            player.ammo_action_started = 0.0

    @staticmethod
    def update_player_ammo(player, now=None):
        if now is None:
            now = time.monotonic()
        if player.dead:
            player.ammo_action = ""
            player.ammo_action_started = 0.0
            return
        if not player.ammo_action:
            return
        duration = (RELOAD_SECONDS if player.ammo_action == "reload"
                    else RESUPPLY_SECONDS)
        if now - player.ammo_action_started < duration:
            return
        if player.ammo_action == "reload":
            amount = min(MAGAZINE_CAPACITY - player.magazine, player.reserve_ammo)
            player.magazine += amount
            player.reserve_ammo -= amount
        else:
            player.magazine = MAGAZINE_CAPACITY
            player.reserve_ammo = RESERVE_CAPACITY
        player.ammo_action = ""
        player.ammo_action_started = 0.0

    async def send(self, writer, message):
        # TCP 发送阶段：统一 JSON 紧凑编码并追加换行帧标记。
        try:
            writer.write((json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
            await writer.drain()
        except ConnectionError:
            pass

    async def game_loop(self):
        # 服务端 tick：先推进怪物和波次，再按快照频率广播，最后补足睡眠时间。
        interval = 1.0 / TICK_RATE
        snap_clock = 0.0
        while True:
            started = time.monotonic()
            self.tick += 1
            if self.started:
                self.step_monsters()
                snap_clock += interval
                if snap_clock >= 1.0 / SNAPSHOT_RATE:
                    snap_clock = 0.0
                    self.broadcast_snapshot()
            await asyncio.sleep(max(0.0, interval - (time.monotonic() - started)))

    def step_monsters(self):
        # 怪物 tick：刷新弹药/复活，推进定时波次，再移动和攻击。
        if not self.players:
            return
        dt = 1.0 / TICK_RATE
        now = time.monotonic()
        for player in self.players.values():
            self.update_player_ammo(player, now)
        self._update_respawns()
        self._advance_waves()
        alive = [p for p in self.players.values() if not p.dead]
        if not self.monsters or not alive:
            if self.current_wave >= TOTAL_WAVES and not self.monsters:
                self.waves_complete = True
            return
        for monster in self.monsters.values():
            old_x, old_z = monster.x, monster.z
            target = min(alive, key=lambda p: (p.x - monster.x) ** 2 + (p.z - monster.z) ** 2)
            # 旧快照或边缘采样可能把怪物留在无效格，先拉回最近安全点。
            if self.map_data and not self.can_move(monster.x, monster.z, monster.y, radius=0.55):
                safe = self.find_nearest_walkable(monster.x, monster.z, target.x, target.z) if alive else None
                if safe is not None:
                    monster.x, monster.z, monster.y = safe
                    monster.path = []
            # 先结算击退，并用碰撞检测阻止怪物穿墙。
            impulse_x = monster.knockback_x * dt
            impulse_z = monster.knockback_z * dt
            if abs(impulse_x) > 1e-6 or abs(impulse_z) > 1e-6:
                next_x = monster.x + impulse_x
                next_z = monster.z + impulse_z
                if self.can_monster_segment(monster.x, monster.z, next_x, next_z,
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
            dx, dz = target.x - monster.x, target.z - monster.z
            distance = math.sqrt(dx * dx + dz * dz)
            if distance > 1.4:
                # 击退期间降低追击比例，避免同帧抵消位移。
                kb_speed = math.sqrt(monster.knockback_x ** 2 + monster.knockback_z ** 2)
                chase_scale = 0.35 if kb_speed > 0.25 else 1.0
                step = min(monster.move_speed * dt * chase_scale, distance)
                direction_x, direction_z = dx / distance, dz / distance
                side_x, side_z = -direction_z, direction_x
                candidates = [
                    (direction_x, direction_z),
                    (side_x, side_z),
                    (-side_x, -side_z),
                    (direction_x + side_x, direction_z + side_z),
                    (direction_x - side_x, direction_z - side_z),
                    (-direction_x, -direction_z),
                    (-direction_x + side_x, -direction_z + side_z),
                    (-direction_x - side_x, -direction_z - side_z),
                ]
                path_used = False
                # 直线路径受阻时优先全局寻路，避免在长墙边反复侧移。
                if self.map_data and not self.can_monster_segment(
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
                            if self.can_monster_segment(monster.x, monster.z, nx, nz,
                                                     monster.y, radius=0.55):
                                monster.x, monster.z = nx, nz
                                path_used = True
                best = None
                best_dist = float("inf")
                for cand_x, cand_z in ([] if path_used else candidates):
                    length = math.sqrt(cand_x * cand_x + cand_z * cand_z) or 1.0
                    next_x = monster.x + cand_x / length * step
                    next_z = monster.z + cand_z / length * step
                    if not self.can_monster_segment(monster.x, monster.z, next_x, next_z,
                                                 monster.y, radius=0.55):
                        continue
                    cand_dist = (target.x - next_x) ** 2 + (target.z - next_z) ** 2
                    if cand_dist < best_dist:
                        best_dist = cand_dist
                        best = (next_x, next_z)
                if best is not None:
                    monster.x, monster.z = best
                # 局部转向失败后，沿碰撞图的全局路径绕过大型障碍。
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
                            if self.can_monster_segment(monster.x, monster.z, nx, nz,
                                                     monster.y, radius=0.55):
                                monster.x, monster.z = nx, nz
            # 连续一段时间没有位移时重找落脚点，避免卡在墙角永久等待。
            if (monster.x - old_x) ** 2 + (monster.z - old_z) ** 2 < 1e-8:
                monster.stuck_ticks += 1
            else:
                monster.stuck_ticks = 0
            if monster.stuck_ticks >= TICK_RATE * 2:
                # 从当前点外侧取样，避免 find_nearest_walkable 又返回原地。
                safe = None
                for a_idx in range(8):
                    angle = a_idx * math.pi * 2.0 / 8.0
                    probe_x = monster.x + math.cos(angle) * 2.0
                    probe_z = monster.z + math.sin(angle) * 2.0
                    candidate = self.find_nearest_walkable(probe_x, probe_z,
                                                           target.x, target.z)
                    if candidate is None:
                        continue
                    if ((candidate[0] - monster.x) ** 2 +
                            (candidate[1] - monster.z) ** 2 > 0.25):
                        safe = candidate
                        break
                if safe is not None:
                    monster.x, monster.z, monster.y = safe
                    monster.path = []
                monster.stuck_ticks = 0
            ground = self.ground_height(monster.x, monster.z, monster.y)
            if ground is not None:
                monster.y = ground
        self._apply_monster_attacks()

    def _monster_waypoint(self, monster, target):
        """返回追击受阻时的下一个 A* 可行走路点。"""
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
        # 玩家可能贴墙，终点需容纳怪物完整碰撞半径。
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
        """在上传的可行走网格上执行有限范围八邻域 A*。"""
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
            wx, wz = self._cell_world(x, z, width)
            ground = self.ground_height(wx, wz, None)
            # 按怪物半径扩张障碍，避免生成无法抵达的墙角路点。
            return ground is not None and self.can_move(wx, wz, ground, radius=0.55)
        if not valid(*start) or not valid(*goal):
            return []
        # 优先队列、前驱表和起点距离。
        q = [(0.0, start)]
        pre = {start: None}
        dist = defaultdict(lambda: float("inf"))
        dist[start] = 0.0
        while q and len(pre) < 6000:
            _, cur = heapq.heappop(q)
            if cur == goal:
                result = []
                while cur != start:
                    result.append(cur)
                    cur = pre[cur]
                result.reverse()
                return result
            cx, cz = cur
            for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1),
                           (1, 1), (1, -1), (-1, 1), (-1, -1)):
                nx, nz = cx + dx, cz + dz
                if not valid(nx, nz):
                    continue
                # 禁止斜向穿越封闭墙角。
                if dx and dz and (not valid(cx + dx, cz) or not valid(cx, cz + dz)):
                    continue
                nd = dist[cur] + (1.41421356 if dx and dz else 1.0)
                nxt = (nx, nz)
                if nd >= dist[nxt]:
                    continue
                dist[nxt] = nd
                h = abs(goal[0] - nx) + abs(goal[1] - nz)
                pre[nxt] = cur
                heapq.heappush(q, (nd + h, nxt))
        return []

    def _nearest_clear_cell(self, x, z, width, depth, current_y, max_radius):
        """查找附近能容纳怪物的网格。"""
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
        """服务端计时两秒后复活死亡玩家。"""
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
            self.reset_player_ammo(player)

    def find_random_respawn(self, player):
        """在玩家初始点附近选择另一个可行走复活点。"""
        origin_x, origin_z = player.spawn_x, player.spawn_z
        # 多次独立采样，保持随机性并限制在碰撞图内。
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
        """近距离内每对怪物与玩家每秒结算一次伤害。"""
        range_sq = MONSTER_ATTACK_RANGE * MONSTER_ATTACK_RANGE
        for monster in self.monsters.values():
            for player in self.players.values():
                if player.dead:
                    continue
                dx = player.x - monster.x
                dz = player.z - monster.z
                if dx * dx + dz * dz > range_sq:
                    continue
                last = monster.attack_ticks.get(player.id, self.tick - TICK_RATE)
                if self.tick - last < TICK_RATE:
                    continue
                monster.attack_ticks[player.id] = self.tick
                player.hp = max(0, player.hp - monster.attack_damage)
                if player.hp <= 0:
                    player.dead = True
                    player.ammo_action = ""
                    player.ammo_action_started = 0.0
                    player.respawn_at = time.monotonic() + PLAYER_RESPAWN_SECONDS
                    player.vy = 0.0

    def bind_udp(self, player_id, token, address):
        player = self.players.get(player_id)
        if player and secrets.compare_digest(player.token, token):
            player.address = address

    def apply_input(self, player_id, token, msg):
        # 输入阶段：按 seq 去重，服务端复算移动，再回传 correction。
        player = self.players.get(player_id)
        if player is None or player.token != token:
            return
        inputs = msg.get("inputs") or []
        for command in inputs[-3:]:
            seq = int(command.get("seq", 0))
            if seq <= player.seq:
                continue
            if player.dead:
                # 死亡期间仍确认输入，避免复活后重放旧移动。
                player.seq = seq
                continue
            x = float(command.get("x", 0.0))
            z = float(command.get("z", 0.0))
            yaw = float(command.get("yaw", player.yaw))
            move_len = math.sqrt(x * x + z * z)
            if move_len > 1.0:
                x, z = x / move_len, z / move_len
            dt = 1.0 / TICK_RATE
            speed = 5.0 if command.get("run") else 3.0
            rad = math.radians(yaw)
            next_x = player.x + (math.cos(rad) * x + math.sin(rad) * z) * speed * dt
            next_z = player.z + (-math.sin(rad) * x + math.cos(rad) * z) * speed * dt
            if self.can_move(next_x, next_z, player.y, radius=0.3):
                player.x, player.z = next_x, next_z

            ground = self.ground_height(player.x, player.z, player.y)
            if ground is None:
                ground = player.y
            grounded = player.vy <= 0.0 and player.y <= ground + 0.15
            jumping = bool(command.get("jump")) and grounded
            if jumping:
                player.vy = 5.0
            if not grounded or jumping:
                player.vy = max(-30.0, player.vy - GRAVITY * dt)
                player.y += player.vy * dt

            next_y = self.ground_height(player.x, player.z, ground)
            if next_y is None:
                next_y = ground
            if player.y <= next_y:
                player.y = next_y
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
        # 网格阶段：把世界坐标转换为共享碰撞图中的线性索引。
        if not self.map_data:
            return None
        try:
            width = int(self.map_data["width"])
            depth = int(self.map_data["depth"])
            ox = float(self.map_data["originX"])
            oz = float(self.map_data["originZ"])
            cell = float(self.map_data.get("cell", 1.0))
            ix = math.floor((x - ox) / cell + 0.5)
            iz = math.floor((z - oz) / cell + 0.5)
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
        """按共享碰撞图校验角色占地范围。"""
        if not self.map_data:
            return True
        samples = [(x, z)]
        if radius > 0.0:
            samples.extend(((x + radius, z), (x - radius, z),
                            (x, z + radius), (x, z - radius)))
        for sx, sz in samples:
            # 碰撞图只覆盖首个玩家附近区域，越界部分交给真实场景碰撞。
            if self.map_cell(sx, sz) is None:
                continue
            ground = self.ground_height(sx, sz, None)
            if ground is None or abs(ground - current_y) > 1.0:
                return False
        return True

    def can_move_segment(self, start_x, start_z, end_x, end_z, current_y, radius=0.0):
        dist = math.sqrt((end_x - start_x) ** 2 + (end_z - start_z) ** 2)
        steps = max(1, int(math.ceil(dist / 0.2)))
        for i in range(1, steps + 1):
            t = i / steps
            x = start_x + (end_x - start_x) * t
            z = start_z + (end_z - start_z) * t
            if not self.can_move(x, z, current_y, radius):
                return False
        return True

    def can_monster_segment(self, start_x, start_z, end_x, end_z, current_y, radius=0.0):
        """怪物沿坡移动时，每个采样点使用当地面高度。"""
        dist = math.sqrt((end_x - start_x) ** 2 + (end_z - start_z) ** 2)
        steps = max(1, int(math.ceil(dist / 0.2)))
        for i in range(1, steps + 1):
            t = i / steps
            x = start_x + (end_x - start_x) * t
            z = start_z + (end_z - start_z) * t
            floor = self.ground_height(x, z, current_y)
            if floor is None or not self.can_move(x, z, floor, radius):
                return False
        return True

    def can_shoot_segment(self, start_x, start_z, end_x, end_z):
        """校验射线视野，不套用角色移动的高度和占地规则。"""
        if not self.map_data:
            return True
        dist = math.hypot(end_x - start_x, end_z - start_z)
        steps = max(1, int(math.ceil(dist / 0.2)))
        walkable = self.map_data.get("walkable", [])
        for i in range(1, steps):
            t = i / steps
            x = start_x + (end_x - start_x) * t
            z = start_z + (end_z - start_z) * t
            index = self.map_cell(x, z)
            # 碰撞图边界不应成为阻挡边缘目标的隐形墙。
            if index is None or index >= len(walkable):
                continue
            if not walkable[index]:
                # 陡坡也可能被采集成不可走，但不能把整片坡面当墙挡住子弹。
                heights = self.map_data.get("heights", [])
                width = int(self.map_data.get("width", 0))
                depth = int(self.map_data.get("depth", 0))
                steep = False
                if width > 0 and depth > 0 and index < len(heights):
                    ix, iz = index % width, index // width
                    h = heights[index]
                    for nx, nz in ((ix - 1, iz), (ix + 1, iz), (ix, iz - 1), (ix, iz + 1)):
                        ni = nz * width + nx
                        if 0 <= nx < width and 0 <= nz < depth and ni < len(heights):
                            if abs(heights[ni] - h) > 0.55:
                                steep = True
                                break
                if steep:
                    continue
                return False
        return True

    def find_nearest_walkable(self, x, z, fallback_x, fallback_z):
        # 找不到位置时不退回玩家坐标，避免怪物生成在玩家或墙内。
        for radius in range(0, 18):
            for a_idx in range(16):
                angle = a_idx * math.pi * 2.0 / 16.0
                cx = x + math.cos(angle) * radius
                cz = z + math.sin(angle) * radius
                ground = self.ground_height(cx, cz, None)
                if ground is not None and self.can_spawn_at(
                        cx, cz, ground, fallback_x, fallback_z):
                    return cx, cz, ground
        return None

    def can_spawn_at(self, x, z, ground, player_x, player_z):
        """检查怪物完整占地范围及与所有角色的间距。"""
        if (x - player_x) ** 2 + (z - player_z) ** 2 < 25.0:
            return False
        # 同时采样正交和对角方向，防止怪物卡在墙角。
        radius = 0.62
        samples = [(x, z), (x + radius, z), (x - radius, z),
                   (x, z + radius), (x, z - radius)]
        diagonal = radius * 0.70710678
        samples.extend(((x + diagonal, z + diagonal),
                        (x + diagonal, z - diagonal),
                        (x - diagonal, z + diagonal),
                        (x - diagonal, z - diagonal)))
        for sx, sz in samples:
            floor = self.ground_height(sx, sz, None)
            if floor is None or abs(floor - ground) > 1.0:
                return False
        if not self.can_move(x, z, ground, radius=0.55):
            return False
        for player in self.players.values():
            if ((x - player.x) ** 2 + (z - player.z) ** 2 < 16.0):
                return False
        for monster in self.monsters.values():
            if ((x - monster.x) ** 2 + (z - monster.z) ** 2 < 4.84):
                return False
        return True

    def broadcast_snapshot(self):
        # 快照阶段：组装玩家、怪物、分数和波次状态，再按 UDP 地址广播。
        if not self.udp_transport:
            return
        now = time.monotonic()
        players = [{
            "id": p.id, "name": p.name, "x": p.x, "y": p.y, "z": p.z,
            "yaw": p.yaw, "pitch": p.pitch, "ack": p.seq,
            "hp": p.hp, "maxHp": p.max_hp, "dead": p.dead,
            "respawn": max(0.0, p.respawn_at - time.monotonic()) if p.dead else 0.0,
            "ammo": p.magazine, "reserve": p.reserve_ammo,
            "reloading": p.ammo_action == "reload",
            "resupplying": p.ammo_action == "resupply",
            "weaponState": True, "life": p.life, "weaponAck": p.weapon_ack,
            "shotInterval": SHOT_INTERVAL_SECONDS,
            "ammoRemaining": (max(0.0, (RELOAD_SECONDS if p.ammo_action == "reload"
                                       else RESUPPLY_SECONDS) - (now - p.ammo_action_started))
                              if p.ammo_action else 0.0)
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
        # UDP 协议阶段：只接受 bind/input 两类包，非法 JSON 静默丢弃。
        try:
            message = json.loads(data.decode("utf-8"))
            if message.get("type") == "bind":
                self.server.bind_udp(int(message["id"]), message.get("token", ""), address)
            elif message.get("type") == "input":
                self.server.apply_input(int(message["id"]), message.get("token", ""), message)
        except (ValueError, KeyError, TypeError, UnicodeDecodeError):
            pass

def clamp_int(value, fallback, minimum, maximum):
    """解析客户端设置并限制在服务端安全范围内。"""
    try:
        # bool 是 int 的子类，但不应作为怪物数量或血量。
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
