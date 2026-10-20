# 双进程运行说明

当前实现需要同时运行 Stardew Valley 的 SMAPI、Terraria 的 tModLoader 和负责本机通信的 Python bridge。Terraria 仍在自己的进程里运行；tModLoader 将画面压缩后送到 SMAPI Mod，Stardew 用覆盖层显示，并把按键和鼠标控制经 bridge 转给 Terraria。使用 Host & Play 时，tModLoader 还会自动派生一个本地服务器子进程；这是正常现象。Stardew 是操作主窗口。

当前 macOS 联调结论：Terraria 必须通过 tModLoader 的“多人游戏 -> 创建并游玩（Host & Play）”进入世界。多人主机模式会让本地服务器持续推进世界，即使 Stardew 窗口位于前台；单人模式在 macOS 切换失焦后可能暂停世界更新，不适合作为融合运行模式。

## 启动

在项目根目录打开终端，先启动 bridge：

```bash
export SDTV_BRIDGE_PORT=28744
export SDTV_BRIDGE_PORT_ALIAS=28741
export SDTV_BRIDGE_STATE="$HOME/.local/share/sdtv-terraria-bridge/state.json"
export SDTV_FRAME_PORT=28751
export SDTV_COMMAND="$PWD/launch_stardew_smapi.sh"
export TERRARIA_COMMAND="$PWD/launch_tmodloader.sh"
./run_dual_bridge.sh
```

这个命令会由 bridge 启动两个游戏进程，并持续等待两边的 Mod 连接。若游戏已经由 Steam 或其他终端启动，则改用：

```bash
python3 bridge/bridge.py --no-launch --port 28744 --port-alias 28741
```

然后分别启动 SMAPI Stardew 和 tModLoader Terraria。手动启动 tModLoader 时要把三个端口变量传给它，测试观察端口可用于确认它是否进入世界更新：

```bash
SDTV_BRIDGE_PORT=28744 SDTV_FRAME_PORT=28751 SDTV_TEST_AGENT_PORT=28752 ./launch_tmodloader.sh
```

不要启动原版可执行文件，因为原版不会加载 bridge Mod。若 bridge 使用了其他端口，需同步替换 `SDTV_BRIDGE_PORT`。

## 进入游戏

1. 在 tModLoader 中选择“多人游戏 -> 创建并游玩（Host & Play）”，载入角色和世界；不要用“单人游戏”进入融合世界，也不要退出或停留在标题菜单。
2. 在 Stardew 中载入存档并确保两边 Mod 都连接到 bridge。
3. 在 Stardew 按 `F8` 打开 Terraria 画面，Stardew 游戏画面会切换成 Terraria 实时画面。
4. 在 Stardew 窗口中操作 Terraria：`A/D` 移动，`W/空格` 跳跃，鼠标左键攻击，鼠标右键副操作，`E` 打开背包，数字键切换快捷栏。输入采集按 TerraKnight 的方式分成两层：每个 Stardew 更新周期通过 MonoGame 轮询物理 `Held` 状态，并用前一周期的快照计算一次性的 `Pressed` 沿；SMAPI 事件只负责捕获按下沿和 suppress Stardew 原生操作，不参与 held 状态判断。
5. 按 `F7` 切换实验性同屏融合模式：Stardew 场景继续绘制，Stardew 玩家负责 `W/A/S/D` 四向移动和碰撞，Terraria 的人物、手持物品、投射物以透明层叠加在玩家脚下，Terraria 快捷栏固定显示在屏幕左上角（16, 16），并隐藏 Stardew 底部快捷栏。融合模式中 `空格` 发送给 Terraria 跳跃，鼠标左键在 Stardew 世界执行已映射的物品交互，点击或数字键切换 Terraria 快捷栏；再次按 `F7` 回到 Terraria 全屏画面。按 `F8` 返回 Stardew。
6. 再按 `F8` 返回 Stardew。击杀入口生成的强化蓝史莱姆会向 Stardew 发回 5 个煤炭奖励。

当前远程画面设计为 `400x225`、约 10 FPS，属于首版画面桥接，清晰度和帧率低于原生 Terraria 窗口。视频数据通过 `SDTV_FRAME_PORT` 直连两端 Mod；输入、入口事件、状态和奖励通过 `SDTV_BRIDGE_PORT` 经过 Python bridge。SMAPI 会在系统临时目录写入 `sdtv-stardew-bridge-status.json`，其中的帧接收/解码计数与输入计数可用于区分“仅连接成功”和“融合链路实际工作”。`held_buttons` 表示	当前仍按住的键，`pending_pressed_buttons` 表示尚未随控制包发出的按下沿，`recent_control_states` 中的方向和攻击值则是已发往 Terraria 的状态。

已知验证边界（2026-10-18）：采样发现 SMAPI suppress 会使 `IInputHelper.IsDown` 下一帧返回 false，因此输入端改为在 Stardew 进程中直接读取 MonoGame 键鼠状态；Terraria 失焦时 `SetControls` 也不会持续调用，因此控制字段改在每次玩家世界更新的 `PreUpdateBuffs` 写入。当前已在 Stardew 的 Terraria 全屏覆盖画面中验证 `A/D` 持续移动和鼠标左键使用物品均生效；地图键也可正常打开。F7 截图已由用户确认保留 Stardew 场景，并显示 Terraria 角色层和物品栏；先前版本中 Stardew 农夫与 Terraria 角色重叠。程序集检查确认 Farmer 没有不可见属性，因此当前修复改为在 F7 开启时通过 Harmony 跳过本地 `Farmer.draw(SpriteBatch)`，并保留农夫移动与碰撞；修复已构建、部署并重启 SMAPI，启动日志确认绘制补丁已安装。已验证：新进程载入存档后，F7 场景中 Stardew 农夫隐藏、Terraria 角色正常显示，两个快捷栏分离；F7 下移动、鼠标左键使用物品、数字键切换快捷栏均有效。若输入失效，优先检查 tModLoader 是否仍处于多人 Host & Play 世界，以及 bridge 两端是否持续收到控制包。

### F7 物品与星露谷世界交互

- F7 中用数字键或点击左上角 Terraria 快捷栏选择物品。左键目标由 Stardew 世界执行，不会在隐藏的 Terraria 世界里额外触发原生效果。对开启 Terraria 自动复用的镐、斧、锤按住左键，会按物品 `useTime` 连续执行；其他一次性物品仍每次点击只使用一次。镐/锤会先调用星露谷原生动作处理石头、矿点、作物和资源团块；如果没有原生目标，再尝试按星露谷规则拆下可回收家具，然后挖掉光标所在的单格地图瓦片（包括普通室内地板）。家具遵守原生占用/移除条件；家具内部或上方有物品时保留原位，需先取出物品；建筑、动物、角色、物件、传送点、树桩标记和带交互属性的地图瓦片不会被挖掉。斧头继续使用星露谷砍树/树桩动作；剑和召唤武器需要鼠标指向星露谷怪物。盔甲按防御值施加短时星露谷防御增益。
- 当前适配：镐/斧调用星露谷原生工具动作，并比较周边地图瓦片、物件、地形、大型资源团块、家具和大型地形物件的状态，避免一次挥动误拆底层瓦片；锤子沿用镐类地形动作。近战、投掷、远程/魔法/召唤武器和单独弹药在光标位置命中星露谷怪物；投掷型消耗武器和单独弹药在命中后消耗选中栏位的一件。生命/魔力恢复映射为星露谷生命/体力，已映射的增益药水会施加星露谷增益。
- 钓竿指向六格范围内的星露谷水面时调用当前地点的鱼种规则并把捕获物加入星露谷背包；钓竿成功钓鱼后会由 Terraria server 校验并扣除快捷栏最前面的鱼饵。火把、工作台、箱子、营火、熔炉以及木材/石块的地板等价物映射为星露谷对应物件；其他可放置方块会映射为石地板，木墙、硬木墙、石墙则映射为相应栅栏。泰拉瑞亚炸弹、黏性炸弹、弹力炸弹、炸药和手榴弹映射为星露谷炸弹，Molotov Cocktail 按投掷武器处理。魔镜、冰雪镜、回忆药水、归途药水、手机和贝壳手机映射为回到农舍。投射物飞行轨迹及泰拉瑞亚免弹药概率暂未复刻；远程命中成功后由 Terraria server 校验并扣除弹药。
- 常见饰品（靴子、翅膀、气球、护盾、磁铁、幸运饰品、徽章）映射为短时星露谷增益，坐骑物品映射为短时速度增益。其他饰品的独特被动效果仍需要专门映射。
- 消耗品只有在星露谷交互成功后才由 Terraria server 校验并扣除，动作 ID 会去重。需要扣库存的动作要求 Terraria 已连接。详细钓鱼时间、饰品被动效果、弹药作为独立物品使用、任意方块布局和模组物品行为仍需要专门映射。
- F8 全屏模式仍由 Terraria 自己处理全部物品。构建和桥接协议测试通过不代表每类物品的游戏内行为均已验证；尚未完成实测的项目会继续标记为待验证。

协议测试会用模拟客户端占用它连接的 bridge 身份，不要把它指向正在运行的游戏 bridge。要单独验证协议，先在一个终端启动隔离 bridge：

```bash
python3 bridge/bridge.py --no-launch --no-focus --port 28749 --state-file /tmp/sdtt-protocol-test-state.json
```

再在另一个终端运行测试：

```bash
SDTV_BRIDGE_PORT=28749 SDTV_BRIDGE_STATE=/tmp/sdtt-protocol-test-state.json python3 bridge/test_protocol.py
```

## 停止

在 bridge 终端按 `Ctrl-C`。由 bridge 启动的两个游戏会一起收到终止信号；若游戏是从 Steam 独立启动的，则需要按各自窗口的退出命令关闭。

## 方案取舍

当前方案是两个游戏进程同时运行，Stardew 负责显示和输入，Terraria 负责世界模拟、AI、碰撞与存档。视频帧叠加在 Stardew 的游戏画面上，输入经本机 bridge 转发；游戏世界和存档仍分别由各自进程拥有。

当前覆盖画面仍是低分辨率视频流，没有把 Terraria 对象、碰撞或世界数据合并进 Stardew 引擎。后续可以提高视频帧率/分辨率，补齐手柄支持，再增加双游戏暂停与存档边界处理。
