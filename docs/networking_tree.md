# 🌐 联机工作机制树杈图

下图根据当前源码中的关键模块整理，概览了《逃离鸭科夫》联机模组的主要工作路径。

```mermaid
mindmap
  root((联机系统工作流))
    初始化与组件挂载
      "Main/Loader/Loader.cs :: ModBehaviour\n- 启动 Harmony 补丁\n- 创建 NetService、SteamP2PLoader、SceneNet 等" 
      "Net/Steam/SteamP2PLoader.cs\n- 依据 SteamManager 初始化\n- 挂载 SteamP2PManager、SteamEndPointMapper、SteamLobbyManager" 
    传输模式管理
      "Main/NetService.cs :: TransportMode\n- Direct: LiteNetLib UDP (默认端口 9050)\n- SteamP2P: 切换到 SteamNetworking 渠道" 
      "Net/NetworkExtensions.cs\n- SendSmart 按 Op 选择 DeliveryMethod 与 Channel" 
    房间与连接流程
      "Net/Steam/SteamLobbyManager.cs\n- 创建/浏览 Lobby\n- 校验密码并监听邀请" 
      "Main/NetService.cs\n- 主机设置 SetId、缓存 PlayerStatus\n- 客户端发送 ClientStatus 并处理自动重连" 
      "Net/Steam/SteamEndPointMapper.cs\n- 维护 SteamID ↔ 虚拟 IPEndPoint 映射" 
    游戏状态同步
      "Main/LocalPlayer/SendLocalPlayerStatus.cs\n- 广播位置、装备、动画与死亡上报" 
      "Main/SceneService/SceneNet.cs\n- 关卡投票、Scene Gate、参与者就绪同步" 
      "Net/NetPack/*.cs\n- 定义同步数据结构与序列化逻辑" 
    运行期监控与维护
      "Main/NetService.cs\n- 连接超时、状态缓存、Latency/IsInGame 写入 PlayerInfoDatabase" 
      "Net/Steam/SteamP2PManager.cs\n- P2P 会话握手、队列深度与 Relay 诊断" 
      "Utils/Database/PlayerInfoDatabase.cs\n- 以 EndPoint/SteamId 持久化玩家信息" 
```

## 分版块说明

- **初始化与组件挂载**：`ModBehaviour` 在加载时创建 `NetService` 等核心组件，并在延迟初始化中激活 `SteamP2PLoader`、`SceneNet`、`SendLocalPlayerStatus` 等网络支撑模块；`SteamP2PLoader` 会在 Steam 可用时注入 P2P 相关的管理器。【F:EscapeFromDuckovCoopMod/Main/Loader/Loader.cs†L13-L66】【F:EscapeFromDuckovCoopMod/Net/Steam/SteamP2PLoader.cs†L8-L58】
- **传输模式管理**：`NetService` 维护 `NetworkTransportMode`，在 Direct 模式下以 LiteNetLib UDP 端口运行，在 SteamP2P 模式下与 `SteamP2PLoader` 同步；`NetworkExtensions.SendSmart` 根据操作码自动挑选信道与可靠性策略。【F:EscapeFromDuckovCoopMod/Main/NetService.cs†L24-L88】【F:EscapeFromDuckovCoopMod/Net/NetworkExtensions.cs†L1-L79】
- **房间与连接流程**：`SteamLobbyManager` 负责 Lobby 创建、筛选与密码校验；`NetService` 在连接阶段为客户端分配 ID、追踪 `PlayerStatus` 并支持自动重连，同时借助 `SteamEndPointMapper` 将 Steam ID 对应到虚拟 IP 以让 LiteNetLib 继续工作。【F:EscapeFromDuckovCoopMod/Net/Steam/SteamLobbyManager.cs†L1-L120】【F:EscapeFromDuckovCoopMod/Main/NetService.cs†L116-L227】【F:EscapeFromDuckovCoopMod/Net/Steam/SteamEndPointMapper.cs†L1-L88】
- **游戏状态同步**：`SendLocalPlayerStatus` 将位置、装备、动画等状态写入 `NetManager` 并根据主机/客户端路径选择发送目标；`SceneNet` 协调关卡投票与 Scene Gate；`NetPack` 目录下的结构体定义了同步数据的打包规则。【F:EscapeFromDuckovCoopMod/Main/LocalPlayer/SendLocalPlayerStatus.cs†L1-L130】【F:EscapeFromDuckovCoopMod/Main/SceneService/SceneNet.cs†L1-L84】【F:EscapeFromDuckovCoopMod/Net/NetPack/NetPack.cs†L1-L200】
- **运行期监控与维护**：`NetService` 同步玩家延迟与游戏状态到 `PlayerInfoDatabase` 并处理连接超时/重连；`SteamP2PManager` 维持 Steam P2P 会话、记录收发统计并输出诊断信息；`PlayerInfoDatabase` 则提供以 SteamId/EndPoint 为键的存储接口。【F:EscapeFromDuckovCoopMod/Main/NetService.cs†L720-L1040】【F:EscapeFromDuckovCoopMod/Net/Steam/SteamP2PManager.cs†L1-L140】【F:EscapeFromDuckovCoopMod/Utils/Database/PlayerInfoDatabase.cs†L1-L200】
