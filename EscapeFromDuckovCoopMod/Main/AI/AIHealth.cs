// Escape-From-Duckov-Coop-Mod-Preview
// Copyright (C) 2025  Mr.sans and InitLoader's team
//
// This program is not a free software.
// It's distributed under a license based on AGPL-3.0,
// with strict additional restrictions:
//  YOU MUST NOT use this software for commercial purposes.
//  YOU MUST NOT use this software to run a headless game server.
//  YOU MUST include a conspicuous notice of attribution to
//  Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview as the original author.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.

using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Duckov.UI;
using EscapeFromDuckovCoopMod.Net;  // 引入智能发送扩展方法
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeFromDuckovCoopMod;

public class AIHealth
{
    // 反射字段（Health 反编译字段）研究了20年研究出来的
    private static readonly FieldInfo FI_defaultMax =
        typeof(Health).GetField("defaultMaxHealth", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo FI_lastMax =
        typeof(Health).GetField("lastMaxHealth", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo FI__current =
        typeof(Health).GetField("_currentHealth", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo FI_characterCached =
        typeof(Health).GetField("characterCached", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo FI_hasCharacter =
        typeof(Health).GetField("hasCharacter", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo FI_healthIsDead =
        typeof(Health).GetField("isDead", BindingFlags.NonPublic | BindingFlags.Instance) ??
        typeof(Health).GetField("_isDead", BindingFlags.NonPublic | BindingFlags.Instance);

    // 【优化】缓存反射方法，避免每次死亡都调用 AccessTools.DeclaredMethod
    private static readonly MethodInfo MI_GetActiveHealthBar;
    private static readonly MethodInfo MI_ReleaseHealthBar;
    private static readonly MethodInfo MI_CmcOnDead =
        AccessTools.DeclaredMethod(typeof(CharacterMainControl), "OnDead", new[] { typeof(DamageInfo) });

    private static readonly FieldInfo FI_CmcIsDead =
        AccessTools.Field(typeof(CharacterMainControl), "isDead") ??
        AccessTools.Field(typeof(CharacterMainControl), "_isDead");

    private static readonly PropertyInfo PI_CmcIsDead =
        AccessTools.Property(typeof(CharacterMainControl), "IsDead");

    // 【优化】静态构造函数，初始化时缓存反射方法
    static AIHealth()
    {
        try
        {
            MI_GetActiveHealthBar = AccessTools.DeclaredMethod(typeof(HealthBarManager), "GetActiveHealthBar", new[] { typeof(Health) });
        }
        catch
        {
            MI_GetActiveHealthBar = null;
        }

        try
        {
            MI_ReleaseHealthBar = AccessTools.DeclaredMethod(typeof(HealthBar), "Release", Type.EmptyTypes);
        }
        catch
        {
            MI_ReleaseHealthBar = null;
        }
    }

    private readonly Dictionary<int, float> _cliLastAiHp = new();
    private readonly Dictionary<int, float> _cliLastReportedHp = new();
    private readonly Dictionary<int, float> _cliNextReportAt = new();
    private readonly HashSet<int> _srvProcessedAiDeaths = new();
    private static readonly HashSet<int> _scheduledDisable = new();

    private static readonly System.Type T_BehaviourTreeOwner = AccessTools.TypeByName("NodeCanvas.Framework.BehaviourTreeOwner");
    private static readonly System.Type T_FSMOwner = AccessTools.TypeByName("NodeCanvas.StateMachines.FSMOwner");
    private static readonly System.Type T_Blackboard = AccessTools.TypeByName("NodeCanvas.Framework.Blackboard");
    private static readonly System.Type T_AIPathControl = AccessTools.TypeByName("AI_PathControl") ??
                                                         AccessTools.TypeByName("AIPathControl");
    private static readonly System.Type T_AIPathController = AccessTools.TypeByName("AI_PathController") ??
                                                             AccessTools.TypeByName("AIPathController");
    private static readonly System.Type T_AIMoveControl = AccessTools.TypeByName("AIMoveControl") ??
                                                          AccessTools.TypeByName("AI_MoveControl");
    private static readonly System.Type T_AIStateControl = AccessTools.TypeByName("AIStateControl") ??
                                                           AccessTools.TypeByName("AI_StateControl");
    private static readonly System.Type T_AIWeaponControl = AccessTools.TypeByName("AIWeaponControl") ??
                                                            AccessTools.TypeByName("AI_WeaponControl");
    private static readonly System.Type T_MagicBlend = AccessTools.TypeByName("CharacterAnimationControl_MagicBlend");

    internal static bool IsCharacterMarkedDead(CharacterMainControl cmc)
    {
        if (!cmc) return false;

        try
        {
            if (PI_CmcIsDead != null)
            {
                var value = PI_CmcIsDead.GetValue(cmc);
                if (value is bool b) return b;
            }
        }
        catch
        {
        }

        try
        {
            if (FI_CmcIsDead != null)
            {
                var value = FI_CmcIsDead.GetValue(cmc);
                if (value is bool b) return b;
            }
        }
        catch
        {
        }

        try
        {
            return cmc.Health != null && cmc.Health.IsDead;
        }
        catch
        {
        }

        return false;
    }

    private static void MarkCharacterDead(CharacterMainControl cmc)
    {
        if (!cmc) return;

        try
        {
            FI_CmcIsDead?.SetValue(cmc, true);
        }
        catch
        {
        }

        try
        {
            if (PI_CmcIsDead != null && PI_CmcIsDead.CanWrite)
                PI_CmcIsDead.SetValue(cmc, true);
        }
        catch
        {
        }
    }

    private static void MarkHealthDead(Health health)
    {
        if (!health) return;

        try
        {
            FI_healthIsDead?.SetValue(health, true);
        }
        catch
        {
        }
    }

    private static void DisableBehaviourIfPresent(CharacterMainControl cmc, Behaviour behaviour)
    {
        if (!cmc || !behaviour) return;

        try
        {
            behaviour.enabled = false;
        }
        catch
        {
        }
    }

    private static void DisableBehaviourType(CharacterMainControl cmc, System.Type type)
    {
        if (!cmc || type == null) return;

        try
        {
            var comps = cmc.GetComponentsInChildren(type, true);
            foreach (var comp in comps)
                if (comp is Behaviour behaviour)
                    behaviour.enabled = false;
        }
        catch
        {
        }
    }

    private static void StopNavMesh(CharacterMainControl cmc)
    {
        if (!cmc) return;

        try
        {
            var nav = cmc.GetComponent<NavMeshAgent>() ??
                      cmc.GetComponentInChildren<NavMeshAgent>(true);
            if (nav)
            {
                nav.isStopped = true;
                nav.ResetPath();
                nav.enabled = false;
            }
        }
        catch
        {
        }

        try
        {
            var obstacle = cmc.GetComponent<NavMeshObstacle>() ??
                           cmc.GetComponentInChildren<NavMeshObstacle>(true);
            if (obstacle) obstacle.enabled = false;
        }
        catch
        {
        }
    }

    private static void DisableAnimator(CharacterMainControl cmc)
    {
        if (!cmc) return;

        try
        {
            var animators = cmc.GetComponentsInChildren<Animator>(true);
            foreach (var anim in animators)
            {
                if (!anim) continue;
                anim.applyRootMotion = false;
                anim.speed = 0f;
            }
        }
        catch
        {
        }
    }

    private static void ScheduleDisableAfterDeath(CharacterMainControl cmc)
    {
        if (!cmc) return;

        var id = cmc.GetInstanceID();
        if (!_scheduledDisable.Add(id)) return;

        UniTask.Void(async () =>
        {
            try
            {
                await UniTask.Delay(150);
            }
            catch
            {
            }

            try
            {
                if (!cmc) return;

                if (cmc.characterModel && cmc.characterModel.gameObject.activeSelf)
                {
                    cmc.characterModel.gameObject.SetActive(false);
                    return;
                }

                if (cmc.gameObject.activeSelf)
                    cmc.gameObject.SetActive(false);
            }
            catch
            {
            }
        });
    }

    private void ApplyServerAuthoritativeKill(CharacterMainControl cmc, Health h, float applyMax, DamageInfo di)
    {
        if (!h) return;

        try
        {
            HealthM.Instance?.ForceSetHealth(h, applyMax, 0f, false);
        }
        catch
        {
        }

        MarkHealthDead(h);
        MarkCharacterDead(cmc);

        FireServerDeathCallbacks(cmc, di);
    }

    internal static void EnsureAiMovementStopped(CharacterMainControl cmc)
    {
        if (!cmc) return;

        try
        {
            cmc.enabled = false;
        }
        catch
        {
        }

        DisableBehaviourIfPresent(cmc, cmc.GetComponent<AICharacterController>());
        DisableBehaviourIfPresent(cmc, cmc.GetComponentInChildren<AICharacterController>(true));
        DisableBehaviourIfPresent(cmc, cmc.GetComponent<NetAiFollower>());

        StopNavMesh(cmc);

        try
        {
            var cc = cmc.GetComponent<CharacterController>() ??
                     cmc.GetComponentInChildren<CharacterController>(true);
            if (cc) cc.enabled = false;
        }
        catch
        {
        }

        try
        {
            foreach (var collider in cmc.GetComponentsInChildren<Collider>(true))
            {
                if (!collider) continue;
                if (!collider.isTrigger) collider.enabled = false;
            }
        }
        catch
        {
        }

        try
        {
            var rb = cmc.GetComponent<Rigidbody>() ??
                     cmc.GetComponentInChildren<Rigidbody>(true);
            if (rb)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }
        catch
        {
        }

        DisableAnimator(cmc);

        DisableBehaviourType(cmc, T_BehaviourTreeOwner);
        DisableBehaviourType(cmc, T_FSMOwner);
        DisableBehaviourType(cmc, T_Blackboard);
        DisableBehaviourType(cmc, T_AIPathControl);
        DisableBehaviourType(cmc, T_AIPathController);
        DisableBehaviourType(cmc, T_AIMoveControl);
        DisableBehaviourType(cmc, T_AIStateControl);
        DisableBehaviourType(cmc, T_AIWeaponControl);
        DisableBehaviourType(cmc, T_MagicBlend);

        MarkCharacterDead(cmc);
        MarkHealthDead(cmc.Health);

        ScheduleDisableAfterDeath(cmc);
    }

    private DamageInfo BuildServerKillDamageInfo(CharacterMainControl cmc, float applyMax, NetPeer sender)
    {
        var di = new DamageInfo();

        try
        {
            di.damageValue = Mathf.Max(1f, applyMax > 0f ? applyMax : 1f);
        }
        catch
        {
        }

        try
        {
            di.finalDamage = di.damageValue;
        }
        catch
        {
        }

        try
        {
            if (cmc)
                di.damagePoint = cmc.transform.position;
        }
        catch
        {
        }

        try
        {
            di.damageNormal = Vector3.up;
        }
        catch
        {
        }

        try
        {
            if (cmc) di.toDamageReceiver = cmc.mainDamageReceiver;
        }
        catch
        {
        }

        try
        {
            if (remoteCharacters != null && sender != null && remoteCharacters.TryGetValue(sender, out var go) && go)
            {
                var remoteCmc = go.GetComponent<CharacterMainControl>();
                if (remoteCmc)
                    di.fromCharacter = remoteCmc;
            }
            else if (!di.fromCharacter && playerStatuses != null && sender != null && playerStatuses.TryGetValue(sender, out _))
            {
                di.fromCharacter = CharacterMainControl.Main;
            }
        }
        catch
        {
        }

        try
        {
            if (!di.fromCharacter)
                di.fromCharacter = CharacterMainControl.Main;
        }
        catch
        {
        }

        return di;
    }

    private static bool TryKillViaHurt(Health h, DamageInfo di)
    {
        if (!h) return false;

        try
        {
            h.Hurt(di);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void FireServerDeathCallbacks(CharacterMainControl cmc, DamageInfo di)
    {
        if (!cmc) return;

        try
        {
            cmc.Health.OnDeadEvent?.Invoke(di);
        }
        catch
        {
        }

        try
        {
            AITool.TryFireOnDead(cmc.Health, di);
        }
        catch
        {
        }

        try
        {
            if (MI_CmcOnDead != null && !IsCharacterMarkedDead(cmc))
                MI_CmcOnDead.Invoke(cmc, new object[] { di });
        }
        catch
        {
        }
    }

    // 🛡️ 日志频率限制
    private static int _pendingAiWarningCount = 0;
    private const int PENDING_AI_WARNING_INTERVAL = 200;  // 每200次只警告1次

    private NetService Service => NetService.Instance;

    private bool IsServer => Service != null && Service.IsServer;
    private NetManager netManager => Service?.netManager;
    private NetDataWriter writer => Service?.writer;
    private NetPeer connectedPeer => Service?.connectedPeer;
    private PlayerStatus localPlayerStatus => Service?.localPlayerStatus;
    private bool networkStarted => Service != null && Service.networkStarted;
    private Dictionary<NetPeer, GameObject> remoteCharacters => Service?.remoteCharacters;
    private Dictionary<NetPeer, PlayerStatus> playerStatuses => Service?.playerStatuses;
    private Dictionary<string, GameObject> clientRemoteCharacters => Service?.clientRemoteCharacters;

    /// <summary>
    ///     /////////////AI血量同步//////////////AI血量同步//////////////AI血量同步//////////////AI血量同步//////////////AI血量同步////////
    /// </summary>
    public void Server_BroadcastAiHealth(int aiId, float maxHealth, float currentHealth)
    {
        if (!networkStarted || !IsServer) return;
        var w = new NetDataWriter();
        w.Put((byte)Op.AI_HEALTH_SYNC);
        w.Put(aiId);
        w.Put(maxHealth);
        w.Put(currentHealth);
        // 使用 SendSmart 自动选择传输方式（AI_HEALTH_SYNC → Critical → ReliableOrdered）
        netManager.SendSmart(w, Op.AI_HEALTH_SYNC);
    }


    public void Client_ReportAiHealth(int aiId, float max, float cur)
    {
        if (!networkStarted || IsServer || connectedPeer == null || aiId == 0) return;

        var now = Time.time;
        if (_cliNextReportAt.TryGetValue(aiId, out var next) && now < next)
        {
            if (_cliLastReportedHp.TryGetValue(aiId, out var last) && Mathf.Abs(last - cur) < 0.01f)
                return;
        }

        var w = new NetDataWriter();
        w.Put((byte)Op.AI_HEALTH_REPORT);
        w.Put(aiId);
        w.Put(max);
        w.Put(cur);
        connectedPeer.Send(w, DeliveryMethod.ReliableOrdered);

        _cliNextReportAt[aiId] = now + 0.05f;
        _cliLastReportedHp[aiId] = cur;

        if (ModBehaviourF.LogAiHpDebug)
            Debug.Log($"[AI-HP][CLIENT] report aiId={aiId} max={max} cur={cur}");
    }

    public void HandleAiHealthReport(NetPeer sender, NetDataReader r)
    {
        if (!networkStarted || !IsServer) return;

        if (r.AvailableBytes < 12) return;

        var aiId = r.GetInt();
        var max = r.GetFloat();
        var cur = r.GetFloat();

        if (!AITool.aiById.TryGetValue(aiId, out var cmc) || !cmc)
        {
            if (ModBehaviourF.LogAiHpDebug)
                Debug.LogWarning($"[AI-HP][SERVER] report missing AI aiId={aiId} from={sender?.EndPoint}");
            return;
        }

        var h = cmc.Health;
        if (!h)
        {
            if (ModBehaviourF.LogAiHpDebug)
                Debug.LogWarning($"[AI-HP][SERVER] report aiId={aiId} has no Health");
            return;
        }

        var applyMax = max > 0f ? max : h.MaxHealth;
        var maxForClamp = applyMax > 0f ? applyMax : h.MaxHealth;
        var clampedCur = maxForClamp > 0f ? Mathf.Clamp(cur, 0f, maxForClamp) : Mathf.Max(0f, cur);

        var wasDead = false;
        try
        {
            wasDead = h.IsDead;
        }
        catch
        {
        }

        var firstDeathReport = false;
        if (clampedCur <= 0f)
        {
            firstDeathReport = _srvProcessedAiDeaths.Add(aiId);
        }
        else
        {
            _srvProcessedAiDeaths.Remove(aiId);
        }

        var appliedCur = clampedCur;

        if (clampedCur <= 0f)
        {
            if (!wasDead || firstDeathReport)
            {
                var di = BuildServerKillDamageInfo(cmc, applyMax, sender);
                var killedViaHurt = TryKillViaHurt(h, di);
                if (!killedViaHurt)
                {
                    ApplyServerAuthoritativeKill(cmc, h, applyMax, di);
                }
                else
                {
                    MarkHealthDead(h);
                    MarkCharacterDead(cmc);
                }

                appliedCur = 0f;
            }
            else
            {
                try
                {
                    HealthM.Instance?.ForceSetHealth(h, applyMax, 0f, false);
                }
                catch
                {
                }

                MarkHealthDead(h);
                MarkCharacterDead(cmc);
                appliedCur = 0f;
            }

            EnsureAiMovementStopped(cmc);

            if (IsServer)
            {
                try
                {
                    COOPManager.AIHandle?.Server_OnAiDeathConfirmed(aiId, cmc);
                }
                catch
                {
                }
            }
        }
        else
        {
            HealthM.Instance.ForceSetHealth(h, applyMax, clampedCur, false);
        }

        if (ModBehaviourF.LogAiHpDebug)
            Debug.Log($"[AI-HP][SERVER] apply report aiId={aiId} max={applyMax} cur={appliedCur} from={sender?.EndPoint}");

        Server_BroadcastAiHealth(aiId, applyMax, appliedCur);
    }


    public void Client_ApplyAiHealth(int aiId, float max, float cur)
    {
        if (IsServer) return;

        // AI 尚未注册：缓存 max/cur，等 RegisterAi 时一起冲
        if (!AITool.aiById.TryGetValue(aiId, out var cmc) || !cmc)
        {
            COOPManager.AIHandle._cliPendingAiHealth[aiId] = cur;
            if (max > 0f) COOPManager.AIHandle._cliPendingAiMax[aiId] = max;

            // 🛡️ 限制日志频率：每200次只输出1次，避免刷屏
            _pendingAiWarningCount++;
            // 注释掉刷屏日志，避免干扰 Debug 输出
            // if (_pendingAiWarningCount == 1 || _pendingAiWarningCount % PENDING_AI_WARNING_INTERVAL == 0)
            // {
            //     Debug.Log($"[AI-HP][CLIENT] pending aiId={aiId} max={max} cur={cur} (已发生 {_pendingAiWarningCount} 次)");
            // }
            return;
        }

        var h = cmc.Health;
        if (!h) return;

        try
        {
            var prev = 0f;
            _cliLastAiHp.TryGetValue(aiId, out prev);
            _cliLastAiHp[aiId] = cur;

            var delta = prev - cur; // 掉血为正
            if (delta > 0.01f)
            {
                var pos = cmc.transform.position + Vector3.up * 1.1f;
                var di = new DamageInfo();
                di.damagePoint = pos;
                di.damageNormal = Vector3.up;
                di.damageValue = delta;
                // 如果运行库里有 finalDamage 字段就能显示更准的数值（A 节已经做了优先显示）
                try
                {
                    di.finalDamage = delta;
                }
                catch
                {
                }

                LocalHitKillFx.PopDamageText(pos, di);
            }
        }
        catch
        {
        }

        // 写入/更新 Max 覆盖（只在给到有效 max 时）
        if (max > 0f)
        {
            COOPManager.AIHandle._cliAiMaxOverride[h] = max;
            // 顺便把 defaultMaxHealth 调大，触发一次 OnMaxHealthChange（即使有 item stat，我也同步一下，保险）
            try
            {
                FI_defaultMax?.SetValue(h, Mathf.RoundToInt(max));
            }
            catch
            {
            }

            try
            {
                FI_lastMax?.SetValue(h, -12345f);
            }
            catch
            {
            }

            try
            {
                h.OnMaxHealthChange?.Invoke(h);
            }
            catch
            {
            }
        }

        // 读一下当前 client 视角的 Max（注意：此时 get_MaxHealth 已有 Harmony 覆盖，能拿到“权威 max”）
        var nowMax = 0f;
        try
        {
            nowMax = h.MaxHealth;
        }
        catch
        {
        }

        // ——避免被 SetHealth() 按“旧 Max”夹住：当 cur>nowMax 时，直接反射写 _currentHealth —— 
        if (nowMax > 0f && cur > nowMax + 0.0001f)
        {
            try
            {
                FI__current?.SetValue(h, cur);
            }
            catch
            {
            }

            try
            {
                h.OnHealthChange?.Invoke(h);
            }
            catch
            {
            }
        }
        else
        {
            // 常规路径
            try
            {
                h.SetHealth(Mathf.Max(0f, cur));
            }
            catch
            {
                try
                {
                    FI__current?.SetValue(h, Mathf.Max(0f, cur));
                }
                catch
                {
                }
            }

            try
            {
                h.OnHealthChange?.Invoke(h);
            }
            catch
            {
            }
        }

        // 起血条兜底
        try
        {
            h.showHealthBar = true;
        }
        catch
        {
        }

        try
        {
            h.RequestHealthBar();
        }
        catch
        {
        }

        // 死亡则本地立即隐藏，防"幽灵AI"
        if (cur <= 0f)
        {
            EnsureAiMovementStopped(cmc);

            // 【优化】延迟清理操作，避免死亡瞬间卡顿
            UniTask.Void(async () =>
            {
                try
                {
                    await UniTask.Delay(50); // 延迟 50ms

                    // 释放/隐藏血条（使用缓存的反射方法）
                    try
                    {
                        var hb = MI_GetActiveHealthBar?.Invoke(HealthBarManager.Instance, new object[] { h }) as HealthBar;
                        if (hb != null)
                        {
                            if (MI_ReleaseHealthBar != null)
                                MI_ReleaseHealthBar.Invoke(hb, null);
                            else
                                hb.gameObject.SetActive(false);
                        }
                    }
                    catch
                    {
                    }

                    // 禁用 GameObject
                    try
                    {
                        if (cmc != null)
                            cmc.gameObject.SetActive(false);
                    }
                    catch
                    {
                    }
                }
                catch
                {
                    // 延迟操作失败不影响游戏
                }
            });

            // 播放死亡特效（保持原有逻辑）
            if (AITool._cliAiDeathFxOnce.Add(aiId))
                FxManager.Client_PlayAiDeathFxAndSfx(cmc);
        }
    }
}