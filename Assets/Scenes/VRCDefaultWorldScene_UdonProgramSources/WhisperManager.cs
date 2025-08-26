using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using UnityEngine.UI;

public class WhisperManager : UdonSharpBehaviour
{
    // ───────── 基本設定 ─────────
    [Header("距離しきい値 (m)")]
    public float selfEarThreshold  = 0.12f;
    public float otherEarThreshold = 0.12f;

    [Header("掌法線の算出")]
    [Tooltip("指数/小指/中指prox から掌法線を再構成（推奨）")]
    public bool usePalmNormalFromFingers = true;

    [Header("手の向きベース（掌法線フォールバック）")]
    [Tooltip("掌法線の軸 0=Forward, 1=Up, 2=Right（再構成が失敗したときに使用）")]
    public int palmAxis = 0;

     [Header("掌向きの符号調整")]
   [Tooltip("手のひらが口を向くときに +1 になるように符号を調整（逆なら -1 を指定）")]
    public float palmDotSign = 1f;


    [Header("指先フォールバック (dy 推定)")]
    [Tooltip("指先が無効のとき回転ベースのフォールバックを使う")]
    public bool  useRotationFallbackForVertical = true;
    [Tooltip("手ローカルの “指方向” 軸 0=Forward,1=Up,2=Right（固定）")]
    public int   fingerAxis = 1;
    [Tooltip("フォールバック dy の最大振幅（真上≈+この値）")]
    public float pseudoTargetAmplitude = 0.12f;
    [Tooltip("“真上” とみなす upDot の目安")]
    public float pseudoDotAtUp         = 0.67f;
    [Tooltip("フォールバック dy の符号補正（+1/-1）")]
    public float pseudoDySign          = 1f;

    [Header("Whisper 音声設定 (m)")]
    public float whisperFar  = 0.25f;
    public float whisperNear = 0f;

    [Header("通常音声設定 (m)")]
    public float normalFar   = 25f;
    public float normalNear  = 0f;

    [Header("UI (TextMeshPro)")]
    public TextMeshProUGUI distanceLabel;
    public TextMeshProUGUI orientLabel;  // ← フィードバック: 掌向き OK/NG
    public TextMeshProUGUI fingerLabel;
    public TextMeshProUGUI assistLabel;
    public TextMeshProUGUI replyLabel;
    public TextMeshProUGUI stateLabel;

    [Header("背景/効果音（任意）")]
    public Image  whisperBgImage;
    public Color  whisperBgColor = new Color(1,0,0,0.35f);
    public Color  normalBgColor  = new Color(0,0,0,0);
    public AudioSource sfxSource;
    public AudioClip   sfxEnterWhisper;
    public AudioClip   sfxExitWhisper;
    [Range(0,1)] public float sfxEnterVolume = 1f;
    [Range(0,1)] public float sfxExitVolume  = 1f;

    [Header("検出する手")]
    [Tooltip("0=右のみ / 1=左のみ / 2=両手")]
    public int activeHandsMode = 2;

    [Header("グリップで手選択（VR）")]
    public bool enableGripSwitch = true;
    [Tooltip("グリップ押下と判定するしきい値（0〜1）")]
    public float gripPressThreshold = 0.8f;
    // 立ち上がり検出と選択手（-1:未選択 / 0:右 / 1:左）
    private bool _prevGripR = false, _prevGripL = false;
    private int _selectedHand = -1;

    [Header("ヒステリシス（解除側しきい値）")]
    [Tooltip("解除時は距離・dot・指本数をこちらの緩い条件で判定する")]
    public bool useExitLoosenedThresholds = true;
    [Tooltip("解除時の自分耳距離 (m)")]
    public float selfEarThresholdExit  = 0.24f;   // 例: 入0.12の2倍
    [Tooltip("解除時の相手耳距離 (m)")]
    public float otherEarThresholdExit = 0.24f;   // 例: 入0.12の2倍
    [Tooltip("解除時の掌向きdot範囲（手のひら→口で +1 を前提）")]
    public float exitDotMin = 0.0f;
    public float exitDotMax = 1.0f;

    [Header("指伸展 条件")]
    [Tooltip("指を伸ばしているとみなす角度しきい（小さいほど厳しい）")]
   public float fingerCurlThresholdDeg = 40f;
    [Tooltip("Whisperに“入る”ために必要な伸展本数")]
    public int minExtendedFingersEnter = 4;
    [Tooltip("Whisperを“維持”するために必要な伸展本数（解除側）")]
    public int minExtendedFingersExit  = 3;


    // ── しきい値切替：モード判定の有無
    [Header("モード判定（しきい値切替）")]
    [Tooltip("ON: coverDotSignedThresh & dyNormThresh を使用 / OFF: 固定しきい値(dot=0.25～1.00 & dyRaw≥0.09)")]
    public bool enableModeDetection = false;

    [Tooltip("符号付き dot の閾値（enableModeDetection=ON のとき適用）")]
    public float coverDotSignedThresh = 0.35f;
    [Tooltip("腕長正規化 dy の閾値（enableModeDetection=ON のとき適用）")]
    public float dyNormThresh         = 0.75f;

    [Header("固定しきい値（enableModeDetection=OFF時に使用）")]
    public float fixedDotMin  = 0.45f;   // dot 下限
    public float fixedDotMax  = 0.70f;   // dot 上限
    public float fixedDyRawMin = 0.09f;  // dyRaw 下限[m]

    [Header("返答モード（口元近接のみ・実験用）")]
    public bool  enableReplyLax     = true;
    public float replyNearThreshold = 0.22f;
    public float replyTestDuration  = 10f;

    [Header("デバッグ：キューブ Interact でトグル")]
    public bool interactToToggle = false;

    // ───────── 内部状態 ─────────
    private VRCPlayerApi localPlayer;
    private bool isWhispering;
    private bool debugForced = false;
    private float replyTestUntil = 0f;

    void Start()
    {
        localPlayer = Networking.LocalPlayer;
        UpdateStateLabel(false);
        if (whisperBgImage != null) whisperBgImage.color = normalBgColor;
        if (sfxSource != null) { sfxSource.spatialBlend = 0f; sfxSource.playOnAwake = false; }
    }

    public override void Interact()
    {
        if (!interactToToggle) return;
        _DebugToggleWhisper();
        _DebugEnableReplyTest();
    }

    void Update()
    {
        if (localPlayer == null) return;

        // デバッグ強制 ON
        if (debugForced)
        {
            if (!isWhispering) EnableWhisper();
            UpdateBoolTMP(distanceLabel, true, "距離");
            UpdateBoolTMP(fingerLabel,   true, "指");
            if (orientLabel != null) orientLabel.text = "掌向き: Debug";
            if (assistLabel != null)
            {
                float remain = Mathf.Max(0f, replyTestUntil - Time.time);
                assistLabel.text = "Hands:" + (activeHandsMode==0?"Right":activeHandsMode==1?"Left":"Both")
                                 + "  ModeDet:" + (enableModeDetection ? "ON" : "OFF")
                                 + "  ReplyTest " + (remain > 0f ? ("ON " + remain.ToString("F1") + "s") : "OFF");
            }
            if (replyLabel != null) replyLabel.text = "Debug Forced ON";
            return;
        }

               // 手の選択（モード基本値）
        bool evalRight = (activeHandsMode != 1);
        bool evalLeft  = (activeHandsMode != 0);

        // ── グリップ入力で左右を明示選択（両手モードのみ）
        if (enableGripSwitch && activeHandsMode == 2 && localPlayer.IsUserInVR())
        {
            float gripR = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryHandTrigger"); // 右グリップ
            float gripL = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryHandTrigger");   // 左グリップ
            bool rNow = gripR >= gripPressThreshold;
            bool lNow = gripL >= gripPressThreshold;
            bool rDown = rNow && !_prevGripR;
            bool lDown = lNow && !_prevGripL;
            _prevGripR = rNow; _prevGripL = lNow;

            if (rDown && !lDown) _selectedHand = 0;
            else if (lDown && !rDown) _selectedHand = 1;
            else if (rDown && lDown) _selectedHand = 0; // 同時押しは右優先（必要なら好みに変更）

            // 選択された手以外は評価しない（未選択なら両方無効 = 誤作動防止）
            if      (_selectedHand == 0) { evalRight = true;  evalLeft = false; }
            else if (_selectedHand == 1) { evalRight = false; evalLeft = true;  }
            else                         { evalRight = false; evalLeft = false; }
        }
       // 手ごとの評価結果（ローカル変数を必ず初期化）
        bool  rOK = false, lOK = false, rOrient = false, lOrient = false;
        float rDot = 0f,   lDot = 0f,   rDy = 0f,       lDy = 0f;
        

          // Whisper中かどうかで しきい値を切替（未ON=入条件 / ON中=解除側の緩い条件）
        bool loosened = useExitLoosenedThresholds && isWhispering;
        if (evalRight) rOK = EvaluateHand(true,  loosened, out rDot, out rDy, out rOrient);
        if (evalLeft)  lOK = EvaluateHand(false, loosened, out lDot, out lDy, out lOrient);

        
          // ※ 距離優先・ハンドロック等は削除。選択はグリップのみ。

        bool anyWhisper = rOK || lOK;

        // 表示（代表手）
        bool useRight = rOK ? true : (lOK ? false : (evalRight && !evalLeft));
        float showDot   = useRight ? rDot : lDot;
        float showDyRaw = useRight ? rDy  : lDy;
        bool  showOrientOK = useRight ? rOrient : lOrient;

        if (orientLabel != null)
            orientLabel.text = "掌向き" + (loosened ? "(Exit)" : "(Enter)") + ": " + (showOrientOK ? "OK" : "NG") +
                               "  dot=" + showDot.ToString("F2") +
                               "  dy="  + showDyRaw.ToString("F2") + "m" +
                               (enableModeDetection
                                 ? $"  (dot≥{coverDotSignedThresh:F2}, dyNorm≥{dyNormThresh:F2})"
                                 : $"  (dot {fixedDotMin:F2}–{fixedDotMax:F2}, dy≥{fixedDyRawMin:F2})");

        // 返答モード
        bool replyActive = enableReplyLax && (Time.time < replyTestUntil);
        bool replyR=false, replyL=false;
        if (replyActive)
        {
            VRCPlayerApi dummy;
            if (evalRight) replyR = IsReplyLaxOK(true,  out dummy);
            if (evalLeft)  replyL = IsReplyLaxOK(false, out dummy);
        }
        bool replyAny = replyR || replyL;
        if (replyLabel != null)
        {
            replyLabel.text = "Reply: " + (replyActive ? "ON" : "OFF") +
                              " near=" + replyNearThreshold.ToString("F2") +
                              " R=" + (replyR ? "Y" : "n") +
                              " L=" + (replyL ? "Y" : "n");
        }

            if (assistLabel != null)
        {
           string selStr = (_selectedHand < 0) ? "None" : (_selectedHand==0 ? "Right" : "Left");
            string gripStr = (_prevGripR ? "R" : "-") + "/" + (_prevGripL ? "L" : "-");
            assistLabel.text = "Hands:" + (activeHandsMode==0?"Right":activeHandsMode==1?"Left":"Both")
                             + "  ModeDet:" + (enableModeDetection ? "ON" : "OFF")
                             + "  Sel:" + selStr
                             + "  Grip:" + gripStr;
        }
        bool shouldWhisper = anyWhisper || replyAny;
        if (shouldWhisper && !isWhispering) EnableWhisper();
        else if (!shouldWhisper && isWhispering) DisableWhisper();
    }

    // ───────────────── 判定ひとまとめ ─────────────────
    private bool EvaluateHand(bool isRight, bool loosened, out float dotSigned, out float dyRaw, out bool orientPass)
    {
        dotSigned = 0f; dyRaw = 0f; orientPass = false;

         // 指伸展（入:4本 / 維持(解除側):3本 など）
         int needFingers = loosened ? Mathf.Max(1, minExtendedFingersExit) : Mathf.Max(1, minExtendedFingersEnter);
         bool fingersOK = AreFingersExtended(isRight, needFingers);

        // 自分耳：向き（dot,dy）＆ 距離
        float dotS, dyRawS, dyNormS;
        bool orientSelf = IsPalmFacingEarByThreshold(localPlayer, isRight, loosened, out dotS, out dyRawS, out dyNormS);
        float selfThr   = loosened ? selfEarThresholdExit : selfEarThreshold;
        bool distSelf   = IsHandNearHead(localPlayer, selfThr, isRight);

        // 相手耳（最寄り）
        VRCPlayerApi other = FindNearestAny(isRight);
        bool orientOther=false; float dotO=0f, dyRawO=0f, dyNormO=0f; bool distOther=false;
        if (other != null)
        {
            orientOther = IsPalmFacingEarByThreshold(other, isRight, loosened, out dotO, out dyRawO, out dyNormO);
            float otherThr = loosened ? otherEarThresholdExit : otherEarThreshold;
            distOther   = IsOtherDistanceWithThreshold(other, isRight, otherThr);
        }

        bool bothDistOK = (distSelf && distOther);
        orientPass = (orientSelf || orientOther);
        bool geomOK = bothDistOK && orientPass;

        // UI（距離/指）
        UpdateBoolTMP(distanceLabel, bothDistOK, "距離");
        UpdateBoolTMP(fingerLabel,   fingersOK,  "指");

        // 表示用
        dotSigned = orientOther ? dotO : dotS;
        dyRaw     = orientOther ? dyRawO : dyRawS;

        return geomOK && fingersOK;
    }

    // ───────────────── 向き＆しきい値 ─────────────────
    private bool IsPalmFacingEarByThreshold(VRCPlayerApi target, bool isRight, bool loosened,
                                            out float dotSigned, out float dyRaw, out float dyNorm)
    {
          // 口位置を基準に判定（手のひらが口を向くほど +1）
        Vector3 head   = target.GetBonePosition(HumanBodyBones.Head);
        Vector3 wrist  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);

        // 口基準（C案）：手→口ベクトルに掌法線を合わせる
        Quaternion headRot = target.GetBoneRotation(HumanBodyBones.Head);
        Vector3 mouthPos = head + headRot * new Vector3(0f, -0.07f, 0.10f);
        Vector3 handToMouth = (mouthPos - wrist).normalized;

        Vector3 palmNormal = usePalmNormalFromFingers ? ComputePalmNormal(isRight) : ComputePalmNormalFallback(isRight);
        // palmDotSign を +1/-1 に正規化してから適用（Inspector で ±1 を指定）
        float sign = (palmDotSign >= 0f) ? 1f : -1f;
        dotSigned = sign * Vector3.Dot(palmNormal, handToMouth); // 手のひら→口 で +1
        
        //ここを編集する可能性あり
        //         // 手のひら→口 で +1 になる前提（palmDotSign 等を適用している想定ならここに反映させてもOK）
        //         dotSigned = Vector3.Dot(palmNormal, handToMouth);


        // dyRaw（指先→上向き）
        dyRaw = ComputeDyRaw(isRight);

        // 腕長で正規化
        dyNorm = GetDyNorm(dyRaw, isRight);
 // しきい値判定（入/解除で切替）
        if (!loosened)
        {
            if (!enableModeDetection)
            {
                bool cover    = (dotSigned >= fixedDotMin) && (dotSigned <= fixedDotMax);
                bool vertical = (dyRaw >= fixedDyRawMin);
                return cover && vertical;
            }
            else
            {
                bool cover    = dotSigned >= coverDotSignedThresh;
                bool vertical = dyNorm    >= dyNormThresh;
                return cover && vertical;
            }
        }
        else
        {
            // 解除側（緩め）：dot 範囲のみ緩和（例: 0〜1.0）。縦方向は従来と同等に維持。
            bool coverExit = (dotSigned >= exitDotMin) && (dotSigned <= exitDotMax);
            if (!enableModeDetection)
            {
                bool vertical = (dyRaw >= fixedDyRawMin);
                return coverExit && vertical;
            }
            else
            {
                bool vertical = (dyNorm >= dyNormThresh);
                return coverExit && vertical;
            }
        }
     }

    // ── dyRaw を取得（優先度：指ボーン→掌ベース指方向→手軸フォールバック）
    private float ComputeDyRaw(bool isRight)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);

        // 1) 指ボーンが取れるなら高さ差
        bool tipValid; Vector3 tip = GetValidFingerTip(isRight, out tipValid);
        if (tipValid) return tip.y - wrist.y;

        if (!useRotationFallbackForVertical) return 0f;

        // 2) 掌ベースの「指方向」（wrist→middleProx の方向）
        Vector3 midP  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
        if (midP != Vector3.zero)
        {
            Vector3 fingerDirFromPalm = (midP - wrist).normalized;
            float upDotPalm = Vector3.Dot(fingerDirFromPalm, Vector3.up);
            float normPalm  = (pseudoDotAtUp > 0.01f) ? Mathf.Clamp(upDotPalm / pseudoDotAtUp, -1f, 1f) : upDotPalm;
            return normPalm * pseudoTargetAmplitude * 1f; // 掌由来は +1 固定
        }

        // 3) 固定の手ローカル軸
        Quaternion handRot = localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 baseAxis = (fingerAxis==1) ? Vector3.up : (fingerAxis==2 ? Vector3.right : Vector3.forward);
        Vector3 fingerDir = (handRot * baseAxis).normalized;
        float upDot = Vector3.Dot(fingerDir, Vector3.up);
        float norm  = (pseudoDotAtUp > 0.01f) ? Mathf.Clamp(upDot / pseudoDotAtUp, -1f, 1f) : upDot;
        return norm * pseudoTargetAmplitude * pseudoDySign;
    }

    // ── 掌法線：指数/小指/中指prox + 手首 から再構成
    private Vector3 ComputePalmNormal(bool isRight)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 idxP  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightIndexProximal  : HumanBodyBones.LeftIndexProximal);
        Vector3 litP  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal);
        Vector3 midP  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);

        if (wrist == Vector3.zero || idxP == Vector3.zero || litP == Vector3.zero || midP == Vector3.zero)
            return ComputePalmNormalFallback(isRight);

        // 左右で法線の符号が反転しないように across の向きを統一
        Vector3 across = isRight ? (idxP - litP) : (litP - idxP);
        Vector3 upPalm = (midP - wrist);
        if (across.sqrMagnitude < 1e-6f || upPalm.sqrMagnitude < 1e-6f)
            return ComputePalmNormalFallback(isRight);

        across.Normalize(); upPalm.Normalize();
        Vector3 n = Vector3.Cross(across, upPalm);
        if (n.sqrMagnitude < 1e-6f) return ComputePalmNormalFallback(isRight);
        return n.normalized;
    }

    // フォールバック：handRot * palmAxis
    private Vector3 ComputePalmNormalFallback(bool isRight)
    {
        Quaternion handRot = localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 axis = (palmAxis == 1) ? Vector3.up : (palmAxis == 2 ? Vector3.right : Vector3.forward);
        Vector3 n = (handRot * axis);
        return (n.sqrMagnitude < 1e-6f) ? Vector3.forward : n.normalized;
    }

    // ───────────────── 補助 ─────────────────

    private float GetDyNorm(float dyRaw, bool isRight)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 fore  = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
        float refLen = (wrist != Vector3.zero && fore != Vector3.zero) ? Vector3.Distance(wrist, fore) : 0.11f;
        if (refLen < 0.07f) refLen = 0.11f;
        float n = dyRaw / refLen;
        if (n < 0f) n = 0f;
        if (n > 1.5f) n = 1.5f;
        return n;
    }

    private bool IsOtherDistanceFixed(VRCPlayerApi other, bool isRight)
    {
        if (other == null) return false;
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 head  = other.GetBonePosition(HumanBodyBones.Head);
        return Vector3.Distance(wrist, head) < otherEarThreshold;
    }
    
       // 解除側しきい等、閾値を指定して判定したい場合はこちらを使用
    private bool IsOtherDistanceWithThreshold(VRCPlayerApi other, bool isRight, float threshold)
    {
        if (other == null) return false;
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 head = other.GetBonePosition(HumanBodyBones.Head);
        return Vector3.Distance(wrist, head) < threshold;
    }


    private bool IsHandNearHead(VRCPlayerApi target, float threshold, bool isRight)
    {
        Vector3 headPos = target.GetBonePosition(HumanBodyBones.Head);
        Vector3 wristPos = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        return Vector3.Distance(headPos, wristPos) < threshold;
    }

    private Vector3 GetValidFingerTip(bool isRight, out bool valid)
    {
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);

        Vector3 tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleDistal : HumanBodyBones.LeftMiddleDistal);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightMiddleIntermediate : HumanBodyBones.LeftMiddleIntermediate);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        tip = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightIndexIntermediate : HumanBodyBones.LeftIndexIntermediate);
        if (tip != Vector3.zero && (tip - wrist).sqrMagnitude > 1e-5f) { valid = true; return tip; }

        valid = false;
        return Vector3.zero;
    }

    // 返答ゆる条件
    private bool IsReplyLaxOK(bool isRight, out VRCPlayerApi target)
    {
        target = FindNearestAny(isRight);
        if (target == null) return false;

        Vector3 headPos = target.GetBonePosition(HumanBodyBones.Head);
        Quaternion headRot = target.GetBoneRotation(HumanBodyBones.Head);
        Vector3 mouthPos = headPos + headRot * new Vector3(0f, -0.07f, 0.10f);

        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        float d = Vector3.Distance(wrist, mouthPos);
        return d < replyNearThreshold;
    }

    // 近傍相手（手首から最も近い頭）
    private VRCPlayerApi FindNearestAny(bool isRight)
    {
        VRCPlayerApi[] list = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(list);
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        float min = 1e9f; VRCPlayerApi best = null;
        foreach (var p in list)
        {
            if (p == null || !p.IsValid() || p.isLocal) continue;
            float dist = Vector3.Distance(wrist, p.GetBonePosition(HumanBodyBones.Head));
            if (dist < min) { min = dist; best = p; }
        }
        return best;
    }

    // ───────────────── 音声制御 & UI ─────────────────
    private void EnableWhisper()
    {
        localPlayer.SetVoiceDistanceNear(whisperNear);
        localPlayer.SetVoiceDistanceFar(whisperFar);
        localPlayer.SetVoiceLowpass(false);
        isWhispering = true;
        UpdateStateLabel(true);

        if (whisperBgImage != null) whisperBgImage.color = whisperBgColor;
        if (sfxSource != null && sfxEnterWhisper != null) sfxSource.PlayOneShot(sfxEnterWhisper, sfxEnterVolume);
    }

    private void DisableWhisper()
    {
        localPlayer.SetVoiceDistanceNear(normalNear);
        localPlayer.SetVoiceDistanceFar(normalFar);
        localPlayer.SetVoiceLowpass(false);
        isWhispering = false;
        UpdateStateLabel(false);

        if (whisperBgImage != null) whisperBgImage.color = normalBgColor;
        if (sfxSource != null && sfxExitWhisper != null) sfxSource.PlayOneShot(sfxExitWhisper, sfxExitVolume);
    }

    private void UpdateBoolTMP(TextMeshProUGUI tmp, bool ok, string label)
    {
        if (tmp == null) return;
        tmp.text = label + ": " + (ok ? "Yes" : "No");
    }

    private void UpdateStateLabel(bool on)
    {
        if (stateLabel == null) return;
        stateLabel.text = on ? "Whispering" : "Normal";
    }

    // ───────────────── デバッグ ─────────────────
    public void _DebugToggleWhisper()
    {
        if (debugForced) { debugForced = false; if (isWhispering) DisableWhisper(); }
        else             { debugForced = true;  if (!isWhispering) EnableWhisper(); }
    }
    public void _DebugEnableReplyTest()
    {
        replyTestUntil = Time.time + replyTestDuration;
    }

    // ───────────────── 指伸展 ─────────────────
      private bool AreFingersExtended(bool isRight, int requiredCount)
    {
        float th = Mathf.Max(1f, fingerCurlThresholdDeg);
        int count = 0;
        // 各指：proximal→distal の向き差（小さい＝伸び）
        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightIndexProximal  : HumanBodyBones.LeftIndexProximal)  * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightIndexDistal    : HumanBodyBones.LeftIndexDistal)     * Vector3.forward) <= th) count++;
        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal) * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightMiddleDistal   : HumanBodyBones.LeftMiddleDistal)    * Vector3.forward) <= th) count++;
        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightRingProximal   : HumanBodyBones.LeftRingProximal)   * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightRingDistal     : HumanBodyBones.LeftRingDistal)      * Vector3.forward) <= th) count++;
        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal) * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightLittleDistal   : HumanBodyBones.LeftLittleDistal)    * Vector3.forward) <= th) count++;
        return count >= Mathf.Clamp(requiredCount, 1, 4);
    }
}
