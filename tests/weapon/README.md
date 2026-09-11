# 武器客户端回归测试

无需打开 Unity，在 PowerShell 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/weapon/run.ps1
```

可通过 `-CompilerPath` 指定其他 Roslyn 编译器。测试直接编译
`Assets/Scripts/WeaponControl.cs` 和 `NetworkProtocol.cs`，不复制武器算法。
构建产物保存在独立临时目录，运行后自动清理，不修改 Unity 资源、场景、预制件或 AudioSource。

覆盖权威弹药、单次射击请求、拒绝射击时的效果、快照就绪、R 键短按顺序、
2 秒补给、联机不预测补弹、进度防回退、过期确认与生命代次、重置与模式切换、
菜单和死亡时的输入屏蔽，以及单机射击、自动换弹、手动换弹和补给。

连发测试使用 CityNew 的 `0.1s` 序列化射速，验证单机和联机连续开火、松开停止、
换弹与死亡中断。联机测试独立改变 Inspector 值和快照 `shotInterval`，
确保射速由服务器控制，而非旧 `0.3s` 常量或本地覆盖值。

## 测试边界

`UnityStubs.cs` 仅替代引擎与依赖边界：输入、时间、组件查找、数学运算、
子弹/音频/后坐力效果，以及 NetworkClient 调用。测试通过反射调用实际 Unity 回调。
它不验证真实 TCP、服务器处理、Unity 更新顺序、HUD 渲染、物理或其他组件重复发射。
仍需执行完整 C# 构建、服务端测试与游戏内联调。
