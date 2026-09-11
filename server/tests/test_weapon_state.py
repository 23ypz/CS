"""在项目根目录运行：python -m unittest discover -s server/tests -v。"""
import asyncio
import json
import sys
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

# main.py 接收位置参数，避免传入 unittest 的选项。
with patch.object(sys, "argv", ["server/main.py"]):
    from server import main as game


class WeaponStateTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        # 测试阶段：固定单调时钟，令射速和换弹边界可重复验证。
        self.now = Mock(return_value=100.0)
        self.clock = patch.object(game, "time", SimpleNamespace(monotonic=self.now))
        self.clock.start()
        self.addCleanup(self.clock.stop)
        self.server = game.GameServer()
        self.server.started = True
        self.server.broadcast_event = AsyncMock()
        self.player = game.Player(1, "player", None, "token")
        self.server.players[1] = self.player
        self.server.reset_player_ammo(self.player)
        self.sequence = 0

    def command(self, **values):
        # 每个武器包递增序号，并携带当前生命代次。
        self.sequence += 1
        return dict(weaponSeq=self.sequence, life=self.player.life, **values)

    async def shoot(self):
        # 使用统一方向构造最小射击包。
        await self.server.handle_shoot(self.player, self.command(dx=0, dy=0, dz=1))

    def action(self, action):
        self.server.handle_ammo_action(self.player, self.command(action=action))

    def assert_ammo(self, magazine, reserve):
        self.assertEqual((self.player.magazine, self.player.reserve_ammo), (magazine, reserve))

    async def test_shoot_once_spends_once_even_if_repeated(self):
        packet = self.command(dx=0, dy=0, dz=1)
        await self.server.handle_shoot(self.player, packet)
        self.now.return_value += 1
        await self.server.handle_shoot(self.player, packet)
        self.assert_ammo(49, 200)

    async def test_spamming_cannot_bypass_rate_limit(self):
        for _ in range(20):
            await self.shoot()
        self.assert_ammo(49, 200)
        self.assertEqual(self.player.weapon_ack, 20)

    async def test_normal_cadence_with_jitter_spends_all_50_and_auto_reloads(self):
        for index in range(50):
            self.now.return_value = 100 + index * .1 + (0.008 if index % 2 == 0 else -0.008)
            await self.shoot()
            self.assert_ammo(49 - index, 200)
        self.assertEqual(self.player.ammo_action, "reload")
        began = self.player.ammo_action_started
        self.server.update_player_ammo(self.player, began + 1.49)
        self.assert_ammo(0, 200)
        self.server.update_player_ammo(self.player, began + 1.5)
        self.assert_ammo(50, 150)

    async def test_jitter_tolerance_does_not_allow_sustained_faster_fire(self):
        for index in range(100):
            self.now.return_value = 100 + index * .02
            await self.shoot()
        # 计入首发与一帧抖动容差，1.98 秒内最多 21 发。
        self.assertGreaterEqual(self.player.magazine, 29)

    async def test_ten_rounds_per_second_without_releasing_trigger(self):
        self.assertEqual(game.SHOT_INTERVAL_SECONDS, .1)
        for index in range(10):
            self.now.return_value = 100 + index * .1
            await self.shoot()
        self.assert_ammo(40, 200)
        self.assertEqual(self.player.ammo_action, "")

    async def test_empty_without_reserve_never_fires_or_reloads(self):
        self.player.magazine = self.player.reserve_ammo = 0
        await self.shoot()
        self.assert_ammo(0, 0)
        self.assertEqual(self.player.ammo_action, "")

    async def test_single_remaining_round_hits_then_starts_reload(self):
        self.player.magazine = 1
        self.server.monsters[1] = game.Monster(1, 0, 0, 3, hp=2)
        await self.shoot()
        self.assert_ammo(0, 200)
        self.assertEqual(self.player.score, 10)
        self.assertFalse(self.server.monsters)
        self.assertEqual(self.player.ammo_action, "reload")

    async def test_short_r_cancels_hold_before_manual_reload(self):
        self.player.magazine = 17
        self.action("resupply_start")
        self.now.return_value = 100.1
        self.action("resupply_cancel")
        self.action("reload")
        self.assertEqual(self.player.ammo_action, "reload")
        self.server.update_player_ammo(self.player, 101.59)
        self.assert_ammo(17, 200)
        self.server.update_player_ammo(self.player, 101.6)
        self.assert_ammo(50, 167)

    async def test_hold_is_one_two_second_refill_of_both_counts(self):
        self.player.magazine, self.player.reserve_ammo = 7, 23
        self.action("resupply_start")
        self.server.update_player_ammo(self.player, 101.99)
        self.assert_ammo(7, 23)
        self.server.update_player_ammo(self.player, 102.0)
        self.assert_ammo(50, 200)
        self.assertEqual(self.player.ammo_action, "")

    async def test_hold_can_replace_automatic_reload_without_extra_wait(self):
        self.player.magazine, self.player.reserve_ammo = 0, 42
        self.action("reload")
        self.now.return_value = 100.2
        self.action("resupply_start")
        self.assertEqual(self.player.ammo_action, "resupply")
        self.server.update_player_ammo(self.player, 102.2)
        self.assert_ammo(50, 200)

    async def test_release_before_two_seconds_does_not_grant_refill(self):
        self.player.magazine, self.player.reserve_ammo = 7, 0
        self.action("resupply_start")
        self.now.return_value = 101.0
        self.action("resupply_cancel")
        self.server.update_player_ammo(self.player, 110.0)
        self.assert_ammo(7, 0)

    async def test_repeated_action_does_not_restart_timer(self):
        self.player.magazine = 7
        self.action("reload")
        self.now.return_value = 100.5
        self.action("reload")
        self.server.update_player_ammo(self.player, 101.5)
        self.assert_ammo(50, 157)

    async def test_partial_reserve_conserved(self):
        self.player.magazine, self.player.reserve_ammo = 47, 2
        self.action("reload")
        self.server.update_player_ammo(self.player, 101.5)
        self.assert_ammo(49, 0)

    async def test_cannot_shoot_while_reloading_or_resupplying(self):
        for action in ("reload", "resupply_start"):
            self.server.reset_player_ammo(self.player)
            self.player.magazine = 10
            self.action(action)
            self.now.return_value += .5
            await self.shoot()
            self.assert_ammo(10, 200)

    async def test_dead_player_does_not_complete_old_refill(self):
        self.player.magazine, self.player.reserve_ammo = 7, 23
        self.action("resupply_start")
        self.player.dead = True
        self.server.update_player_ammo(self.player, 103.0)
        self.assert_ammo(7, 23)
        self.assertEqual(self.player.ammo_action, "")

    async def test_respawn_restores_ammo_and_rejects_previous_life_commands(self):
        old_shot = self.command(dx=0, dy=0, dz=1)
        old_action = self.command(action="reload")
        self.player.magazine, self.player.reserve_ammo = 0, 3
        self.player.hp, self.player.dead, self.player.respawn_at = 0, True, 102.0
        self.player.ammo_action = "reload"
        self.server.find_random_respawn = Mock(return_value=(9, 12, 0))
        self.now.return_value = 101.99
        self.server._update_respawns()
        self.assertTrue(self.player.dead)
        self.now.return_value = 102.0
        self.server._update_respawns()
        self.assert_ammo(50, 200)
        self.assertEqual(self.player.hp, 100)
        self.assertEqual((self.player.x, self.player.z), (9, 12))
        self.assertEqual(self.player.ammo_action, "")
        await self.server.handle_shoot(self.player, old_shot)
        self.server.handle_ammo_action(self.player, old_action)
        self.assert_ammo(50, 200)
        await self.shoot()
        self.assert_ammo(49, 200)

    async def test_snapshot_includes_ack_life_and_remaining_action_time(self):
        # 快照阶段同时检查序号确认、生命代次和动作剩余时间。
        self.player.magazine = 12
        self.action("reload")
        self.player.address = ("127.0.0.1", 12345)
        self.server.udp_transport = Mock()
        self.now.return_value = 100.5
        self.server.broadcast_snapshot()
        packet = self.server.udp_transport.sendto.call_args.args[0]
        state = json.loads(packet)["players"][0]
        self.assertTrue(state["weaponState"])
        self.assertEqual(state["shotInterval"], .1)
        self.assertTrue(state["reloading"])
        self.assertFalse(state["resupplying"])
        self.assertEqual(state["ammoRemaining"], 1.0)
        self.assertEqual(state["weaponAck"], self.sequence)
        self.assertEqual(state["life"], self.player.life)
        self.assertEqual((state["ammo"], state["reserve"]), (12, 200))

    async def test_players_do_not_share_ammo(self):
        second = game.Player(2, "second", None, "token2")
        self.server.players[2] = second
        self.server.reset_player_ammo(second)
        await self.shoot()
        self.assertEqual((second.magazine, second.reserve_ammo), (50, 200))

    async def test_invalid_vector_never_spends_ammo(self):
        for direction in ((0, 0, 0), (float("nan"), 0, 1), (float("inf"), 0, 1)):
            await self.server.handle_shoot(self.player, self.command(
                dx=direction[0], dy=direction[1], dz=direction[2]))
        self.assert_ammo(50, 200)

    async def test_edge_target_is_not_rejected_by_navigation_height_check(self):
        # 末格较高但中途无遮挡，复现旧移动判定误拒绝射击的问题。
        self.server.map_data = {
            "width": 3, "depth": 1, "originX": 0.0, "originZ": 0.0,
            "cell": 1.0, "walkable": [1, 1, 1],
            "heights": [0.0, 0.0, 8.0]
        }
        self.player.x, self.player.y, self.player.z = 0.0, 0.0, 0.0
        self.server.monsters[1] = game.Monster(1, 2.0, 8.0, 0.0, hp=2)
        self.assertFalse(self.server.can_move_segment(0, 0, 2, 0, 0, radius=0.0))
        self.assertTrue(self.server.can_shoot_segment(0, 0, 2, 0))
        # 瞄准高处目标，地面高度差不应导致射击被拒绝。
        await self.server.handle_shoot(self.player, self.command(dx=2, dy=8.7, dz=0))
        self.assertFalse(self.server.monsters)
        self.assertEqual(self.player.score, 10)

    async def test_monster_segment_follows_local_ground_on_slope(self):
        self.server.map_data = {
            "width": 4, "depth": 1, "originX": 0.0, "originZ": 0.0,
            "cell": 1.0, "walkable": [1, 1, 1, 1],
            "heights": [0.0, 0.6, 1.2, 1.8]
        }
        self.assertFalse(self.server.can_move_segment(0, 0, 3, 0, 0, radius=0.0))
        self.assertTrue(self.server.can_monster_segment(0, 0, 3, 0, 0, radius=0.0))

    async def test_monster_can_leave_finite_collision_map_boundary(self):
        self.server.map_data = {
            "width": 3, "depth": 1, "originX": 0.0, "originZ": 0.0,
            "cell": 1.0, "walkable": [1, 1, 1],
            "heights": [0.0, 0.0, 0.0]
        }
        self.assertTrue(self.server.can_monster_segment(2, 0, 5, 0, 0, radius=0.0))

    async def test_steep_nonwalkable_terrain_does_not_hide_monster(self):
        self.server.map_data = {
            "width": 3, "depth": 1, "originX": 0.0, "originZ": 0.0,
            "cell": 1.0, "walkable": [1, 0, 1],
            "heights": [0.0, 1.0, 2.0]
        }
        self.assertTrue(self.server.can_shoot_segment(0, 0, 2, 0))

    async def test_blocked_cell_still_blocks_projectile(self):
        self.server.map_data = {
            "width": 3, "depth": 1, "originX": 0.0, "originZ": 0.0,
            "cell": 1.0, "walkable": [1, 0, 1],
            "heights": [0.0, 0.0, 0.0]
        }
        self.assertFalse(self.server.can_shoot_segment(0, 0, 2, 0))

    async def test_monster_spawn_never_falls_back_inside_player_or_overlaps_wall(self):
        self.player.x = self.player.z = 0.0
        self.server.map_data = {
            "width": 9, "depth": 1, "originX": -4.0, "originZ": 0.0,
            "cell": 1.0, "walkable": [0, 0, 0, 0, 1, 0, 0, 0, 0],
            "heights": [0.0] * 9
        }
        self.assertIsNone(self.server.find_nearest_walkable(0, 0, 0, 0))

    async def test_monster_spawn_rejects_diagonal_footprint_overlap(self):
        self.player.x = self.player.z = 0.0
        self.server.map_data = {
            "width": 21, "depth": 21, "originX": -10.0, "originZ": -10.0,
            "cell": 1.0, "walkable": [1] * 441, "heights": [0.0] * 441
        }
        self.server.monsters[1] = game.Monster(1, 5.0, 0.0, 5.0)
        spawn = self.server.find_nearest_walkable(5.0, 5.0, 0, 0)
        self.assertIsNotNone(spawn)
        self.assertGreaterEqual((spawn[0] - 5.0) ** 2 + (spawn[1] - 5.0) ** 2, 4.84)


class WeaponTcpTests(unittest.IsolatedAsyncioTestCase):
    async def test_wire_command_order_and_protocol_version(self):
        # TCP 阶段：验证 hello 版本和连续武器命令的服务端确认顺序。
        server = game.GameServer()
        tcp = await asyncio.start_server(server.handle_tcp, "127.0.0.1", 0)
        reader, writer = await asyncio.open_connection("127.0.0.1", tcp.sockets[0].getsockname()[1])
        try:
            writer.write(b'{"type":"hello","version":3,"name":"test"}\n')
            await writer.drain()
            welcome = json.loads(await asyncio.wait_for(reader.readline(), 2))
            self.assertEqual(welcome["version"], 3)
            player = server.players[welcome["id"]]
            server.started = True
            server.reset_player_ammo(player)
            player.magazine = 10
            # 一次 TCP 写入模拟短按：开始、取消、换弹。
            for seq, action in enumerate(("resupply_start", "resupply_cancel", "reload"), 1):
                packet = {"type": "ammo_action", "weaponSeq": seq, "life": player.life, "action": action}
                writer.write((json.dumps(packet) + "\n").encode())
            await writer.drain()
            async def wait_for_ack():
                while player.weapon_ack < 3:
                    await asyncio.sleep(.001)
            await asyncio.wait_for(wait_for_ack(), 2)
            self.assertEqual(player.ammo_action, "reload")
            self.assertEqual(player.magazine, 10)
        finally:
            writer.close()
            await writer.wait_closed()
            tcp.close()
            await tcp.wait_closed()
            await asyncio.sleep(.01)


if __name__ == "__main__":
    unittest.main()
