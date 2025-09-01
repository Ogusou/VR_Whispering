using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UnityEngine.UI;

public class WhisperRelay : UdonSharpBehaviour
{
    [Header("受信側ゲート（m）")]
    public float listenerStartDistance = 0.45f;

    [Header("受信側の自動解除")]
    public float listenerTimeout = 1.6f;
    public float listenerEndDistance = 2.0f;

    [Header("Ping 間隔（秒）")]
    public float whisperPingInterval = 0.5f;

    // ───────── WhisperManager へのブリッジ（受信安定性ログ用） ─────────
    [Header("WhisperManager hook (optional)")]
    [Tooltip("受信距離(dR,dL)を WhisperManager に転送して安定性ログ/ReplyLabel を更新します")]
    public WhisperManager manager;
    public bool forwardToManager = true;

    // ───────── 見た目（リスナー側） ─────────
    [Header("Listener Vignette")]
    public Image vignetteImage;
    public RectTransform vignetteRect;
    public Color listenerVignetteTint = new Color(0.55f, 1f, 1f, 1f);
    [Range(0f,1f)] public float vignetteAlphaOn = 0.35f;
    [Range(0f,1f)] public float vignetteAlphaOff = 0f;
    public Vector2 listenerVignetteEarOffset = new Vector2(0.07f, 0f);

    [Header("Listener Icon")]
    public Image whisperedIconImage;
    public RectTransform whisperedIconRect;
    public Vector2 whisperedIconOffset = new Vector2(0.10f, -0.06f);
    [Range(0f,1f)] public float iconAlphaOn = 1f;
    [Range(0f,1f)] public float iconAlphaOff = 0f;

    [Header("Listener Ducking (Local)")]
    public AudioSource[] duckTargets;
    [Range(0f,1f)] public float duckLevelListener = 0.35f;

    // ───────── Logging ─────────
    [Header("Logging")]
    [Tooltip("ログを出す/出さない")]
    public bool logVerbose = true;
    [Tooltip("所有権イベントもログに出す")]
    public bool logOwnerTrace = true;
    [Tooltip("1人検証時は自分にも飛ばして受信ログを確認（本番はOFF推奨）")]
    public bool loopbackInSolo = true;
    [Tooltip("ログフィルタ用タグ")]
    public string logTag = "[Whisper]";

    private float[] _duckOrigVol;
    private Vector3 _vignetteBaseLocalPos; private bool _vigBaseCaptured = false;
    private Vector3 _iconBaseLocalPos;     private bool _iconBaseCaptured = false;

    private VRCPlayerApi _local;
    private bool  _listenerActive;
    private int   _speakerId = -1;
    private float _aliveUntil;
    private bool  _earRight = true;
    private float _nextPing;

    void Start()
    {
        _local = Networking.LocalPlayer;

        // Duck の元音量
        if (duckTargets != null && duckTargets.Length > 0)
        {
            _duckOrigVol = new float[duckTargets.Length];
            for (int i = 0; i < duckTargets.Length; i++)
                _duckOrigVol[i] = (duckTargets[i] != null) ? duckTargets[i].volume : 1f;
        }

        if (vignetteRect != null) { _vignetteBaseLocalPos = vignetteRect.localPosition; _vigBaseCaptured = true; }
        if (whisperedIconRect != null) { _iconBaseLocalPos = whisperedIconRect.localPosition; _iconBaseCaptured = true; }

        _SetListenerVisual(false, true);

        L($"Start | isOwner={Networking.IsOwner(gameObject)} players={VRCPlayerApi.GetPlayerCount()}");
    }

    // ==== 話し手から呼ぶAPI ====
    public void TalkerEnter()
    {
        if (_local == null) return;

        L($"SEND Enter (request owner transfer)");
        Networking.SetOwner(_local, gameObject);
        L($"Owner after SetOwner = {Networking.GetOwner(gameObject).playerId}:{Networking.GetOwner(gameObject).displayName}");

        var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
            ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
            : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

        SendCustomNetworkEvent(target, nameof(W_Enter));
        L($"SEND Enter -> {target}");

        SendCustomEventDelayedSeconds(nameof(_EchoEnter), 0.10f);
        _nextPing = Time.time + 0.20f;
    }

    public void TalkerTick()
    {
        if (_local == null) return;
        if (Time.time >= _nextPing)
        {
            Networking.SetOwner(_local, gameObject);
            var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
                ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
                : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

            SendCustomNetworkEvent(target, nameof(W_Ping));
            L($"SEND Ping -> {target}");
            _nextPing = Time.time + whisperPingInterval;
        }
    }

    public void TalkerExit()
    {
        if (_local == null) return;
        Networking.SetOwner(_local, gameObject);

        var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
            ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
            : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

        SendCustomNetworkEvent(target, nameof(W_Exit));
        L($"SEND Exit -> {target}");
    }

    public void _EchoEnter()
    {
        var target = (loopbackInSolo && VRCPlayerApi.GetPlayerCount() <= 1)
            ? VRC.Udon.Common.Interfaces.NetworkEventTarget.All
            : VRC.Udon.Common.Interfaces.NetworkEventTarget.Others;

        SendCustomNetworkEvent(target, nameof(W_Enter));
        L($"SEND Enter (echo) -> {target}");
    }

    // ==== 受信側（全員が動く） ====
    public void W_Enter()
    {
        var sp = Networking.GetOwner(gameObject);
        if (sp == null || sp.isLocal) return;

        bool earRight; float dR, dL;
        if (!_IsMyHeadNearSpeakersHandEx(sp, listenerStartDistance, out earRight, out dR, out dL))
        {
            L($"RECV Enter from {sp.playerId}:{sp.displayName} IGNORED (too far) dR={dR:F2} dL={dL:F2}");
            // manager へも「遠い」サンプルとして送らない（ノイズ抑制）
            return;
        }

        _speakerId = sp.playerId;
        _earRight = earRight;
        _MarkAlive();
        _SetListenerVisual(true, _earRight);

        // 受信安定性（耳の区別なし）: Enter サンプル
        if (forwardToManager && manager != null) manager.OnWhisperEnter(dR, dL);

        L($"RECV Enter from {sp.playerId}:{sp.displayName} OK ear={(earRight ? "R" : "L")} dR={dR:F2} dL={dL:F2}");
    }

    public void W_Ping()
    {
        var sp = Networking.GetOwner(gameObject);
        if (sp == null || sp.isLocal) return;

        if (_speakerId == sp.playerId)
        {
            bool earRight; float dR, dL;
            _IsMyHeadNearSpeakersHandEx(sp, listenerStartDistance * 1.25f, out earRight, out dR, out dL);
            _earRight = earRight;
            _MarkAlive();
            _SetListenerVisual(true, _earRight);

            // 受信安定性（keepAlive=true）
            if (forwardToManager && manager != null) manager.OnWhisperPing(dR, dL, true);

            // スパム防止で簡素ログ
            L($"RECV Ping from {sp.playerId} keepAlive ear={(earRight ? "R" : "L")} dR={dR:F2} dL={dL:F2}");
            return;
        }

        bool ok; bool ear; float dR2, dL2;
        ok = _IsMyHeadNearSpeakersHandEx(sp, listenerStartDistance, out ear, out dR2, out dL2);
        if (ok)
        {
            _speakerId = sp.playerId;
            _earRight = ear;
            _MarkAlive();
            _SetListenerVisual(true, _earRight);

            // 受信安定性（late activate / keepAlive=false）
            if (forwardToManager && manager != null) manager.OnWhisperPing(dR2, dL2, false);

            L($"RECV Ping (late activate) from {sp.playerId}:{sp.displayName} ear={(ear ? "R" : "L")} dR={dR2:F2} dL={dL2:F2}");
        }
        else
        {
            // 遠い場合：サンプル送らず（ヒステリシスは Manager 側で処理）
            L($"RECV Ping from {sp.playerId}:{sp.displayName} ignored (not near) dR={dR2:F2} dL={dL2:F2}");
        }
    }

    public void W_Exit()
    {
        var sp = Networking.GetOwner(gameObject);
        if (sp == null || sp.isLocal) return;

        if (_listenerActive && _speakerId == sp.playerId)
        {
            _listenerActive = false;
            _SetListenerVisual(false, _earRight);

            // 受信安定性：Exit 通知
            if (forwardToManager && manager != null) manager.OnWhisperExit();

            L($"RECV Exit from {sp.playerId}:{sp.displayName} -> listener OFF");
        }
        else
        {
            L($"RECV Exit from {sp.playerId}:{sp.displayName} ignored (not my speaker)");
        }
    }

    void Update()
    {
        if (!_listenerActive) return;

        if (Time.time >= _aliveUntil)
        {
            _listenerActive = false;
            _SetListenerVisual(false, _earRight);

            // タイムアウト時も Manager 側のタイムアウトが効く（Ping欠落で自動OFF）
            L("listener timeout -> OFF");
            return;
        }

        var sp = VRCPlayerApi.GetPlayerById(_speakerId);
        if (sp != null && sp.IsValid())
        {
            var myHead = _local.GetBonePosition(HumanBodyBones.Head);
            var spHead = sp.GetBonePosition(HumanBodyBones.Head);
            if (myHead != Vector3.zero && spHead != Vector3.zero)
            {
                float dd = Vector3.Distance(myHead, spHead);
                if (dd > listenerEndDistance)
                {
                    _listenerActive = false;
                    _SetListenerVisual(false, _earRight);

                    // 離れた場合も Exit を明示
                    if (forwardToManager && manager != null) manager.OnWhisperExit();

                    L($"listener end by distance dd={dd:F2} -> OFF");
                }
            }
        }
    }

    // 所有権ログ
    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (logOwnerTrace)
            L($"OnOwnershipTransferred -> now owner {player.playerId}:{player.displayName}");
    }

    // ==== 補助 ====
    private void _MarkAlive()
    {
        _listenerActive = true;
        _aliveUntil = Time.time + listenerTimeout;
    }

    private bool _IsMyHeadNearSpeakersHand(VRCPlayerApi speaker, float thr, out bool rightEar)
    {
        rightEar = true;
        if (speaker == null || _local == null) return false;

        Vector3 myHead = _local.GetBonePosition(HumanBodyBones.Head);
        if (myHead == Vector3.zero) return false;

        Vector3 rh = speaker.GetBonePosition(HumanBodyBones.RightHand);
        Vector3 lh = speaker.GetBonePosition(HumanBodyBones.LeftHand);

        float dR = (rh == Vector3.zero) ? 1e9f : Vector3.Distance(rh, myHead);
        float dL = (lh == Vector3.zero) ? 1e9f : Vector3.Distance(lh, myHead);

        if (dR <= dL) { rightEar = true;  return dR < thr; }
        else          { rightEar = false; return dL < thr; }
    }

    // ログ＆Manager転送用に距離も取りたい版
    private bool _IsMyHeadNearSpeakersHandEx(VRCPlayerApi speaker, float thr, out bool rightEar, out float dR, out float dL)
    {
        rightEar = true; dR = 1e9f; dL = 1e9f;
        if (speaker == null || _local == null) return false;

        Vector3 myHead = _local.GetBonePosition(HumanBodyBones.Head);
        if (myHead == Vector3.zero) return false;

        Vector3 rh = speaker.GetBonePosition(HumanBodyBones.RightHand);
        Vector3 lh = speaker.GetBonePosition(HumanBodyBones.LeftHand);

        dR = (rh == Vector3.zero) ? 1e9f : Vector3.Distance(rh, myHead);
        dL = (lh == Vector3.zero) ? 1e9f : Vector3.Distance(lh, myHead);

        if (dR <= dL) { rightEar = true;  return dR < thr; }
        else          { rightEar = false; return dL < thr; }
    }

    private void _SetListenerVisual(bool on, bool earRight)
    {
        // ── ビネット
        if (vignetteImage != null)
        {
            var c = listenerVignetteTint;
            c.a = on ? vignetteAlphaOn : vignetteAlphaOff;
            vignetteImage.color = c;
        }
        if (vignetteRect != null)
        {
            if (!_vigBaseCaptured) { _vignetteBaseLocalPos = vignetteRect.localPosition; _vigBaseCaptured = true; }
            float sx = Mathf.Abs(listenerVignetteEarOffset.x) * (earRight ? +1f : -1f);
            Vector3 offset = new Vector3(sx, listenerVignetteEarOffset.y, 0f);
            vignetteRect.localPosition = on ? (_vignetteBaseLocalPos + offset) : _vignetteBaseLocalPos;
        }

        // ── アイコン
        if (whisperedIconImage != null)
        {
            var ic = whisperedIconImage.color;
            ic.a = on ? iconAlphaOn : iconAlphaOff;
            whisperedIconImage.color = ic;
        }
        if (whisperedIconRect != null)
        {
            if (!_iconBaseCaptured) { _iconBaseLocalPos = whisperedIconRect.localPosition; _iconBaseCaptured = true; }
            float sx = Mathf.Abs(whisperedIconOffset.x) * (earRight ? +1f : -1f);
            Vector3 offset = new Vector3(sx, whisperedIconOffset.y, 0f);
            whisperedIconRect.localPosition = on ? (_iconBaseLocalPos + offset) : _iconBaseLocalPos;
        }

        // ── Ducking
        if (duckTargets != null && duckTargets.Length > 0)
        {
            for (int i = 0; i < duckTargets.Length; i++)
            {
                var a = duckTargets[i];
                if (a == null) continue;
                float baseVol = (_duckOrigVol != null && i < _duckOrigVol.Length) ? _duckOrigVol[i] : a.volume;
                a.volume = on ? (baseVol * duckLevelListener) : baseVol;
            }
        }

        L($"visual {(on ? "ON" : "OFF")} ear={(earRight? "R":"L")}");
    }

    // 短縮ログ
    private void L(string msg)
    {
        if (!logVerbose) return;
        string who = (_local != null) ? $"{_local.playerId}:{_local.displayName}" : "?(local)";
        Debug.Log($"{logTag} {who} | {msg}");
    }
}
