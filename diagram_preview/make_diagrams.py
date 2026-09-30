# -*- coding: utf-8 -*-
"""生成报告可用的中文系统架构图和流程图预览。"""
from pathlib import Path
from textwrap import wrap

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parent
BG = "#F5F7FB"
NAVY = "#17324D"
BLUE = "#2F6DA6"
TEAL = "#2A9D8F"
ORANGE = "#E58A3A"
RED = "#C7524A"
INK = "#243447"
MUTED = "#64748B"
LINE = "#A9B8C8"
LIGHT_BLUE = "#E9F2FA"
LIGHT_TEAL = "#E8F6F2"
LIGHT_ORANGE = "#FFF2E5"
LIGHT_RED = "#FBECEB"
WHITE = "#FFFFFF"
FONT = r"C:\Windows\Fonts\msyh.ttc"
BOLD = r"C:\Windows\Fonts\msyhbd.ttc"


def f(size, bold=False):
    return ImageFont.truetype(BOLD if bold else FONT, size)


def text(draw, xy, value, size=24, fill=INK, bold=False, anchor=None):
    draw.text(xy, value, font=f(size, bold), fill=fill, anchor=anchor)


def wrapped(draw, box, value, size=22, fill=INK, bold=False, line_gap=8, align="left"):
    x0, y0, x1, y1 = box
    font = f(size, bold)
    max_width = x1 - x0
    lines = []
    for raw in value.split("\n"):
        current = ""
        for ch in raw:
            candidate = current + ch
            if draw.textbbox((0, 0), candidate, font=font)[2] <= max_width or not current:
                current = candidate
            else:
                lines.append(current)
                current = ch
        lines.append(current)
    height = draw.textbbox((0, 0), "中", font=font)[3]
    y = y0
    for line in lines:
        width = draw.textbbox((0, 0), line, font=font)[2]
        x = x0 if align == "left" else x0 + (max_width - width) / 2
        draw.text((x, y), line, font=font, fill=fill)
        y += height + line_gap
    return y


def header(draw, title, subtitle):
    draw.rectangle((0, 0, 1600, 110), fill=NAVY)
    text(draw, (70, 30), title, 38, WHITE, True)
    text(draw, (70, 76), subtitle, 18, "#DCE8F5")


def card(draw, xy, title, body="", fill=WHITE, edge=LINE, title_fill=NAVY, title_size=24, body_size=20):
    x0, y0, x1, y1 = xy
    draw.rounded_rectangle((x0, y0, x1, y1), radius=18, fill=fill, outline=edge, width=3)
    draw.rounded_rectangle((x0, y0, x1, y0 + 54), radius=18, fill=title_fill)
    draw.rectangle((x0, y0 + 28, x1, y0 + 54), fill=title_fill)
    text(draw, ((x0 + x1) / 2, y0 + 28), title, title_size, WHITE, True, "mm")
    if body:
        wrapped(draw, (x0 + 22, y0 + 76, x1 - 22, y1 - 16), body, body_size, INK, False, 7, "left")


def arrow(draw, a, b, color=BLUE, width=5, label=None, label_offset=(0, -15)):
    x0, y0 = a
    x1, y1 = b
    draw.line((x0, y0, x1, y1), fill=color, width=width)
    import math
    angle = math.atan2(y1 - y0, x1 - x0)
    size = 14
    p1 = (x1 - size * math.cos(angle - 0.45), y1 - size * math.sin(angle - 0.45))
    p2 = (x1 - size * math.cos(angle + 0.45), y1 - size * math.sin(angle + 0.45))
    draw.polygon([(x1, y1), p1, p2], fill=color)
    if label:
        text(draw, ((x0 + x1) / 2 + label_offset[0], (y0 + y1) / 2 + label_offset[1]), label, 17, color, True, "mm")


def dot(draw, xy, color=BLUE, radius=9):
    x, y = xy
    draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)


def architecture():
    im = Image.new("RGB", (1600, 1000), BG)
    d = ImageDraw.Draw(im)
    header(d, "Unity 多人合作射击项目系统架构", "客户端表现 + TCP/UDP 应用层通信 + Python 服务端权威模拟")

    card(d, (55, 170, 430, 435), "Unity 客户端", "最多 4 名玩家\nPlayerControl\nWeaponControl\nPlayerHealth\nGameplayHud / 小地图 / 排行榜", LIGHT_BLUE, "#79A9D4", BLUE, 25, 21)
    card(d, (55, 500, 430, 755), "单机模式", "GameModeManager\nMonsterAI\nMonsterModeManager\n本地结算移动、射击、生命与波次", LIGHT_TEAL, "#76C7B6", TEAL, 25, 21)
    card(d, (555, 185, 1045, 455), "应用层通信", "TCP（可靠事件）\n大厅、hello/welcome、ready/start\n地图上传、射击、换弹、击杀事件\n\nUDP（实时状态）\n移动输入、绑定、20Hz 快照", WHITE, "#8EAAC5", BLUE, 26, 21)
    card(d, (555, 535, 1045, 800), "共享碰撞网格", "81×81 地图数据\n地面高度 + walkable 标记\n\n客户端预测 / 服务端校正\n玩家合法移动 / 怪物绕障\n出生点检查 / 射击遮挡", LIGHT_ORANGE, "#E5B47A", ORANGE, 26, 21)
    card(d, (1170, 170, 1545, 730), "Python asyncio 服务端", "TCP 监听 + UDP 协议\n\n30Hz 游戏循环\n20Hz 状态快照\n\n权威状态\n玩家位置、生命、弹药\n怪物血量、攻击、击退\n波次、击杀分数、复活\n\n客户端只提交输入和命令", LIGHT_BLUE, "#79A9D4", NAVY, 26, 21)
    arrow(d, (430, 280), (555, 280), BLUE, 6, "输入 / 命令")
    arrow(d, (1045, 280), (1170, 280), TEAL, 6, "TCP / UDP")
    arrow(d, (1170, 405), (1045, 405), ORANGE, 5, "快照 / 事件", (0, 18))
    arrow(d, (430, 630), (555, 630), TEAL, 5, "本地调用")
    arrow(d, (800, 455), (800, 535), ORANGE, 5, "地图")
    text(d, (800, 900), "多人模式：服务器最终裁决；单机模式：Unity 本地完成同一套玩法逻辑", 22, MUTED, True, "mm")
    im.save(ROOT / "01_系统总体架构.png", quality=95)


def flow():
    im = Image.new("RGB", (1600, 1200), BG)
    d = ImageDraw.Draw(im)
    header(d, "多人模式开局与战斗流程", "从连接服务器、地图同步到实时战斗、波次和玩家复活")
    nodes = [
        (80, 165, 340, 285, "进入多人模式", "", LIGHT_BLUE, BLUE),
        (430, 165, 720, 285, "填写地址、昵称", "怪物数量和血量", LIGHT_BLUE, BLUE),
        (810, 165, 1070, 285, "TCP hello / welcome", "", WHITE, TEAL),
        (1160, 165, 1510, 285, "大厅等待", "准备 / 房主开始", WHITE, TEAL),
        (180, 330, 500, 460, "上传 81×81 碰撞网格", "", LIGHT_ORANGE, ORANGE),
        (610, 330, 930, 460, "服务端验证地图", "生成第 1 波怪物", LIGHT_ORANGE, ORANGE),
        (1040, 330, 1420, 460, "UDP 绑定", "开始实时游戏", LIGHT_TEAL, TEAL),
        (80, 555, 430, 740, "客户端", "30Hz 预测移动\n发送输入 / 射击命令", LIGHT_BLUE, BLUE),
        (595, 555, 1005, 740, "服务端", "30Hz 模拟\n命中、生命、弹药、分数\n20Hz 广播快照", LIGHT_ORANGE, ORANGE),
        (1170, 555, 1515, 740, "客户端表现", "远端插值\nHUD、小地图、排行榜", LIGHT_TEAL, TEAL),
        (210, 800, 585, 925, "玩家血量 ≤ 0", "固定死亡位置，镜头下俯", LIGHT_RED, RED),
        (760, 800, 1050, 925, "2 秒复活倒计时", "显示动态进度条", LIGHT_RED, RED),
        (1195, 800, 1490, 925, "随机安全点", "满血、初始弹药复活", LIGHT_TEAL, TEAL),
    ]
    for x0, y0, x1, y1, title, body, fill, edge in nodes:
        card(d, (x0, y0, x1, y1), title, body, fill, edge, edge, 22, 18)
    arrow(d, (340, 205), (430, 205), BLUE, 5)
    arrow(d, (720, 205), (810, 205), BLUE, 5)
    arrow(d, (1070, 205), (1160, 205), TEAL, 5)
    arrow(d, (1290, 285), (1290, 330), TEAL, 5)
    arrow(d, (500, 377), (610, 377), ORANGE, 5)
    arrow(d, (930, 377), (1040, 377), ORANGE, 5)
    arrow(d, (1180, 460), (1180, 555), TEAL, 5)
    arrow(d, (430, 610), (595, 610), BLUE, 5, "TCP / UDP")
    arrow(d, (1005, 610), (1170, 610), TEAL, 5, "快照 / 事件")
    arrow(d, (400, 740), (400, 800), RED, 5, "受到攻击")
    arrow(d, (585, 850), (760, 850), RED, 5)
    arrow(d, (1050, 850), (1195, 850), TEAL, 5)
    draw = d
    draw.rounded_rectangle((70, 1010, 1530, 1130), radius=18, fill=WHITE, outline=LINE, width=3)
    text(draw, (95, 1030), "波次调度：", 22, NAVY, True)
    text(draw, (235, 1030), "每波间隔 20 秒，提前 5 秒在 HUD 提示；后续波次数量、血量、伤害和移速小幅增加。", 21, INK)
    im.save(ROOT / "02_多人模式流程图.png", quality=95)


def smoothing():
    im = Image.new("RGB", (1600, 1060), BG)
    d = ImageDraw.Draw(im)
    header(d, "网络平滑处理与状态一致性", "本地先响应操作，服务器最终裁决，客户端按误差分段恢复")
    card(d, (55, 180, 360, 395), "玩家输入", "WASD / 鼠标\n移动、跳跃、视角", LIGHT_BLUE, "#79A9D4", BLUE, 24, 21)
    card(d, (455, 180, 780, 395), "本地预测 30Hz", "NetworkMap.Step\n预测位置和速度\nRigidbody 连续移动", LIGHT_TEAL, "#76C7B6", TEAL, 24, 21)
    card(d, (875, 180, 1195, 395), "UDP input", "seq / tick\n位置、方向、跳跃、冲刺", WHITE, "#8EAAC5", BLUE, 24, 21)
    card(d, (1290, 180, 1545, 395), "服务端验证", "地图碰撞\n速度、重力、边界\n生成权威 ack", LIGHT_ORANGE, "#E5B47A", ORANGE, 24, 21)
    arrow(d, (360, 255), (455, 255), BLUE, 5)
    arrow(d, (780, 255), (875, 255), TEAL, 5)
    arrow(d, (1195, 255), (1290, 255), ORANGE, 5)

    card(d, (180, 465, 595, 700), "权威快照", "tick、ack、玩家和怪物状态", WHITE, "#8EAAC5", NAVY, 24, 21)
    card(d, (675, 430, 1040, 710), "误差分段校正", "误差 ≤ 0.75m：保持连续\n0.75m - 3m：平滑校正\n误差 > 3m 或复活：直接接受\n\n避免正常抖动，也保证状态能归位", LIGHT_ORANGE, "#E5B47A", ORANGE, 24, 20)
    card(d, (1120, 465, 1515, 700), "远端对象插值", "NetworkPlayerView\nSmoothDamp 位置\nSlerp 旋转\n怪物代理同样处理", LIGHT_TEAL, "#76C7B6", TEAL, 24, 21)
    arrow(d, (1415, 395), (1415, 465), ORANGE, 5, "snapshot", (0, -18))
    arrow(d, (595, 580), (675, 580), BLUE, 5)
    arrow(d, (1040, 580), (1120, 580), TEAL, 5)

    card(d, (120, 785, 545, 980), "资源命令走 TCP", "射击、换弹、补给\nweaponSeq + life\n服务端检查射速和弹药", LIGHT_BLUE, "#79A9D4", BLUE, 24, 21)
    card(d, (645, 785, 1010, 980), "旧状态过滤", "snapshot tick\nscore tick\n拒绝旧序号和旧生命命令", LIGHT_RED, "#D88A83", RED, 24, 21)
    card(d, (1110, 785, 1480, 980), "最终一致性", "弹药、生命、命中、击杀分数\n最终以服务端状态更新 HUD", LIGHT_TEAL, "#76C7B6", TEAL, 24, 21)
    arrow(d, (545, 865), (645, 865), BLUE, 5)
    arrow(d, (1010, 865), (1110, 865), RED, 5)
    text(d, (800, 1010), "核心原则：移动可以预测，资源和战斗结果不能由客户端单独决定。", 23, NAVY, True, "mm")
    im.save(ROOT / "03_网络平滑处理流程图.png", quality=95)


if __name__ == "__main__":
    architecture()
    flow()
    smoothing()
    print("generated", ROOT)
