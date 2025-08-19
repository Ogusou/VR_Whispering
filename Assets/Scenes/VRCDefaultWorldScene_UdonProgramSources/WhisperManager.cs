// WhisperManager.cs – 可変しきい値アシスト（厳しめ視線＋注視時間）+ Assist:ONに向き/指を含める版
// ・左右手どちらでも発動
// ・指伸展は左右どちらかOK（Whisper最終判定）
// ・距離は self & other の AND（ただし soloIgnoreDistance なら距離無視で常にOK）
// ・Other ear しきい値は身長差と視線固定で可変（アシスト）
// ・視線は HMD トラッキングの forward を使用し、角度＋注視時間で判定（より厳しく）
// ・Assist: ON は「(ベースでは届かないが可変なら届く) ＆ gaze固定 ＆ 向きOK(対相手) ＆ 指OK(その手)」

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using UnityEngine.UI;

public class WhisperManager : UdonSharpBehaviour
{
    // ───────── インスペクタ設定 ─────────
    [Header("距離しきい値 (m)")]
    public float selfEarThreshold  = 0.12f;   // 自分耳（固定）
    public float otherEarThreshold = 0.12f;   // 未使用（可変は下の otherThresholdBase から生成）

    [Header("向き判定パラメータ")]
    [Tooltip("手のひら法線に使う軸 0=Forward, 1=Up, 2=Right")] public int palmAxis = 0; // Forward 推奨
    [Tooltip("耳→手ベクトルと掌法線の内積しきい値 (>= これ以上でOK)")]
    public float coverDotThreshold = -0.6f;
    [Tooltip("指先が手首より上にあることの高さ差 (m)")]
    public float verticalThreshold = 0.03f;

    [Header("指先フォールバック設定 (HandTracking 対策)")]
    [Tooltip("指先が取得できない場合に回転ベースの上向き判定を使う")]
    public bool useRotationFallbackForVertical = true;
    [Tooltip("回転ベースで指先方向に使う軸 0=Forward, 1=Up, 2=Right")]
    public int fingerAxis = 1;
    [Tooltip("回転ベース dy をコントローラ有りの振幅に合わせる目標値 (m)")]
    public float pseudoTargetAmplitude = 0.12f;
    [Tooltip("“真上”時に観測される upDot の目安（例: 0.67）")]
    public float pseudoDotAtUp = 0.67f;
    [Tooltip("回転ベース dy の符号補正。上を正にしたいなら +1、逆なら -1")]
    public float pseudoDySign = 1f;

    [Header("Other Ear 閾値の可変設定（＝身長差アシスト）")]
    [Tooltip("ON のとき、身長差と視線（角度＋注視）で other しきい値を拡張")]
    public bool adaptOtherThreshold = true;
    [Tooltip("ベースの other 耳しきい値 (m)")]
    public float otherThresholdBase = 0.32f;
    [Tooltip("身長差 1m あたりの増分 (m/m)")]
    public float otherThresholdPerMeter = 0.70f;
    [Tooltip("可変しきい値の下限/上限 (m)")]
    public float otherThresholdMin = 0.20f;
    public float otherThresholdMax = 0.90f;
    [Tooltip("アシストを検討する最小身長差 (m)")]
    public float assistMinHeightDelta = 0.10f;

    [Header("視線（より厳しく）")]
    [Tooltip("視線と相手頭方向の半角(°)。小さいほど厳しい")]
    public float assistGazeHalfAngle = 15f;
    [Tooltip("視線がこの角度内で連続した最小注視時間 (s)")]
    public float assistGazeMinFixTime = 0.30f;

    [Header("Whisper 音声設定 (m)")]
    public float whisperFar  = 0.25f;
    public float whisperNear = 0f;

    [Header("通常音声設定 (m)")]
    public float normalFar   = 25f;
    public float normalNear  = 0f;

    [Header("UI (TextMeshPro) 参照")]
    public TextMeshProUGUI distanceLabel; // 距離 OK/NG
    public TextMeshProUGUI orientLabel;   // 向き OK/NG（dot / dy）
    public TextMeshProUGUI fingerLabel;   // 指 OK/NG
    public TextMeshProUGUI assistLabel;   // ΔH / thr / d / gain / gaze / Assist
    public TextMeshProUGUI stateLabel;    // Whispering / Normal

    [Header("背景/効果音（任意）")]
    public Image  whisperBgImage;
    public Color  whisperBgColor = new Color(1,0,0,0.35f);
    public Color  normalBgColor  = new Color(0,0,0,0);
    public AudioSource sfxSource;
    public AudioClip   sfxEnterWhisper;
    public AudioClip   sfxExitWhisper;
    [Range(0,1)] public float sfxEnterVolume = 1f;
    [Range(0,1)] public float sfxExitVolume  = 1f;

    [Header("ソロ検証")]
    public bool soloPalmDebug = false;
    public bool soloIgnoreDistance = true;
    public bool soloIgnoreFinger   = true;

    // ───────── 内部変数 ─────────
    private VRCPlayerApi localPlayer;
    private bool isWhispering;

    // 視線の注視時間トラッキング（左右手で別管理）
    private int   lastGazeTargetIdR = -1, lastGazeTargetIdL = -1;
    private float gazeFixTimerR = 0f, gazeFixTimerL = 0f;

    // ───────── ライフサイクル ─────────
    void Start()
    {
        localPlayer = Networking.LocalPlayer;
        UpdateStateLabel(false);
        if (whisperBgImage != null) whisperBgImage.color = normalBgColor;
        if (sfxSource != null) { sfxSource.spatialBlend = 0f; sfxSource.playOnAwake = false; }
    }

    void Update()
    {
        if (localPlayer == null) return;

        // ===== ソロ検証：片手だけで自分耳判定（右→左の順） =====
        if (soloPalmDebug)
        {
            if (!SoloCheck(true)) SoloCheck(false);
            return;
        }

        // 右手
        bool rFingersOK   = AreFingersExtended(true);
        float rDotS, rDyS; bool rSelfOrient  = IsPalmFacingEar(localPlayer, true, out rDotS, out rDyS);
        bool rSelfDistOK  = IsHandNearHead(localPlayer, selfEarThreshold, true);

        VRCPlayerApi rOther = FindNearestAny(true);
        float rDeltaH, rThr, rD, rGain; bool rGazeOK;
        bool rOtherDistOK = IsOtherDistanceOK(rOther, true, out rDeltaH, out rThr, out rD, out rGain, out rGazeOK);
        bool rOtherOrient = false; float rDotO=0f, rDyO=0f;
        if (rOther != null) rOtherOrient = IsPalmFacingEar(rOther, true, out rDotO, out rDyO);

        // 左手
        bool lFingersOK   = AreFingersExtended(false);
        float lDotS, lDyS; bool lSelfOrient  = IsPalmFacingEar(localPlayer, false, out lDotS, out lDyS);
        bool lSelfDistOK  = IsHandNearHead(localPlayer, selfEarThreshold, false);

        VRCPlayerApi lOther = FindNearestAny(false);
        float lDeltaH, lThr, lD, lGain; bool lGazeOK;
        bool lOtherDistOK = IsOtherDistanceOK(lOther, false, out lDeltaH, out lThr, out lD, out lGain, out lGazeOK);
        bool lOtherOrient = false; float lDotO=0f, lDyO=0f;
        if (lOther != null) lOtherOrient = IsPalmFacingEar(lOther, false, out lDotO, out lDyO);

        // 指伸展（Whisper最終判定では左右どちらかOK）
        bool fingersOKAny = rFingersOK || lFingersOK;

        // 距離AND（soloIgnoreDistanceなら無視）
        bool rBothDistOK = soloIgnoreDistance || (rSelfDistOK && rOtherDistOK);
        bool lBothDistOK = soloIgnoreDistance || (lSelfDistOK && lOtherDistOK);

        // 向きOR
        bool rOrientOK = rSelfOrient || rOtherOrient;
        bool lOrientOK = lSelfOrient || lOtherOrient;

        // Assist: ON（「ベースでは届かないが可変なら届く」＋ gaze固定 ＋ 向きOK(対相手) ＋ 指OK(その手)）
        bool rAssistFinal = (rOther != null) &&
                            IsAssistCoreOn(rD, rThr) &&
                            rGazeOK && rOtherOrient && rFingersOK;

        bool lAssistFinal = (lOther != null) &&
                            IsAssistCoreOn(lD, lThr) &&
                            lGazeOK && lOtherOrient && lFingersOK;

        // 幾何（距離AND×向きOR）
        bool rightGeomOK = rBothDistOK && rOrientOK;
        bool leftGeomOK  = lBothDistOK && lOrientOK;

        // 表示に使う“アクティブ手”
        bool useRight;
        if (rightGeomOK && !leftGeomOK) useRight = true;
        else if (!rightGeomOK && leftGeomOK) useRight = false;
        else useRight = (Mathf.Abs(rDyS) >= Mathf.Abs(lDyS));

        bool  activeBothDistOK = useRight ? rBothDistOK : lBothDistOK;
        bool  activeOrientOK   = useRight ? rOrientOK   : lOrientOK;
        float showDot          = useRight ? (rOtherOrient ? rDotO : rDotS) : (lOtherOrient ? lDotO : lDotS);
        float showDy           = useRight ? (rOtherOrient ? rDyO  : rDyS ) : (lOtherOrient ? lDyO  : lDyS );
        float dH               = useRight ? rDeltaH : lDeltaH;
        float thr              = useRight ? rThr    : lThr;
        float dd               = useRight ? rD      : lD;
        float gain             = useRight ? rGain   : lGain;
        bool  gz               = useRight ? rGazeOK : lGazeOK;
        bool  assistOnFinal    = useRight ? rAssistFinal : lAssistFinal;

        // UI
        UpdateBoolTMP(distanceLabel, activeBothDistOK, "距離");
        if (orientLabel != null)
            orientLabel.text = "向き: " + (activeOrientOK ? "Yes" : "No") +
                               "  dot=" + showDot.ToString("F2") +
                               "  dy="  + showDy.ToString("F2") + "m";
        UpdateBoolTMP(fingerLabel, fingersOKAny, "指");

        if (assistLabel != null)
            assistLabel.text = "ΔH=" + dH.ToString("F2") +
                               "  thr=" + thr.ToString("F2") +
                               "  d=" + dd.ToString("F2") +
                               "  gain=" + gain.ToString("F2") +
                               "  gaze=" + (gz ? "Yes" : "No") +
                               "  Assist: " + (assistOnFinal ? "ON" : "OFF");

        // 最終発動（左右どちらかの幾何OK × 指は左右どちらかOK）
        bool shouldWhisper = (rightGeomOK || leftGeomOK) && fingersOKAny;
        if (shouldWhisper && !isWhispering) EnableWhisper();
        else if (!shouldWhisper && isWhispering) DisableWhisper();
    }

    // ──────────────────────────────────────────────────────────────
    // 可変しきい値（身長差アシスト）距離判定
    //   視線は HMD トラッキング forward を用い、角度＋注視時間で「gazeOK」を返す
    //   ※ ここでは「距離OK(d<thr)」「gazeOK」「ΔH」「thr」「d」「gain」を返すだけ。
    //      Assist:ON の最終判定は Update() 内で「向きOK＋指OK」を含めて行う。
    // ──────────────────────────────────────────────────────────────
    private bool IsOtherDistanceOK(VRCPlayerApi other, bool isRight,
                                   out float deltaH, out float thr, out float d, out float gain, out bool gazeOK)
    {
        deltaH=0f; thr=otherThresholdBase; d=999f; gain=0f; gazeOK=false;
        if (other == null || !other.IsValid()) return false;

        // 身長差
        float myHeadY    = localPlayer.GetBonePosition(HumanBodyBones.Head).y;
        float otherHeadY = other.GetBonePosition(HumanBodyBones.Head).y;
        deltaH = Mathf.Abs(otherHeadY - myHeadY);

        // 手→相手頭の距離
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 otherHead = other.GetBonePosition(HumanBodyBones.Head);
        d = Vector3.Distance(wrist, otherHead);

        // 視線（HMD トラッキング）
        var td = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Vector3 eyePos = td.position;
        Vector3 eyeFwd = td.rotation * Vector3.forward;
        Vector3 toOther = (otherHead - eyePos).normalized;

        // 角度チェック
        float angle = Vector3.Angle(eyeFwd, toOther);
        bool angleOK = angle <= assistGazeHalfAngle;

        // 注視時間の更新（手別・相手別）
        int oid = other.playerId;
        if (isRight)
        {
            if (lastGazeTargetIdR != oid) { lastGazeTargetIdR = oid; gazeFixTimerR = 0f; }
            gazeFixTimerR = angleOK ? (gazeFixTimerR + Time.deltaTime) : 0f;
            gazeOK = gazeFixTimerR >= assistGazeMinFixTime;
        }
        else
        {
            if (lastGazeTargetIdL != oid) { lastGazeTargetIdL = oid; gazeFixTimerL = 0f; }
            gazeFixTimerL = angleOK ? (gazeFixTimerL + Time.deltaTime) : 0f;
            gazeOK = gazeFixTimerL >= assistGazeMinFixTime;
        }

        // 可変しきい値生成（gazeOK かつ ΔH が閾値以上のときだけ拡張）
        float baseThr = otherThresholdBase;
        thr = baseThr;
        if (adaptOtherThreshold && gazeOK && deltaH >= assistMinHeightDelta)
        {
            thr = baseThr + otherThresholdPerMeter * deltaH;
            if (thr < otherThresholdMin) thr = otherThresholdMin;
            if (thr > otherThresholdMax) thr = otherThresholdMax;
            gain = thr - baseThr;
        }

        // 最終：距離OKは thr 基準
        return (d < thr);
    }

    // 「ベースでは届かないが可変なら届く」か？
    private bool IsAssistCoreOn(float d, float thr)
    {
        return (d >= otherThresholdBase) && (d < thr);
    }

    // ── 近傍相手検索（手首から最も近い頭）
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

    private bool IsHandNearHead(VRCPlayerApi target, float threshold, bool isRight)
    {
        Vector3 headPos  = target.GetBonePosition(HumanBodyBones.Head);
        Vector3 wristPos = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        return Vector3.Distance(headPos, wristPos) < threshold;
    }

    // 掌法線が耳→手方向と反対向き & 指先が上（dy）
    private bool IsPalmFacingEar(VRCPlayerApi target, bool isRight, out float dotOut, out float dyOut)
    {
        Vector3 head  = target.GetBonePosition(HumanBodyBones.Head);
        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 earToHand = (wrist - head).normalized;

        Vector3 axis = (palmAxis == 1) ? Vector3.up : (palmAxis == 2) ? Vector3.right : Vector3.forward;
        Quaternion handRot = localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 palmNormal = handRot * axis;
        float dot = Vector3.Dot(palmNormal, earToHand);
        bool cover = dot >= coverDotThreshold;

        float dy = 0f; bool vertical = false;
        bool tipValid; Vector3 tip = GetValidFingerTip(isRight, out tipValid);
        if (tipValid)
        {
            dy = tip.y - wrist.y;
            vertical = dy >= verticalThreshold;
        }
        else if (useRotationFallbackForVertical)
        {
            Vector3 fingerAxisV = (fingerAxis == 1) ? Vector3.up : (fingerAxis == 2) ? Vector3.right : Vector3.forward;
            Vector3 fingerDir   = (handRot * fingerAxisV).normalized;
            float upDot = Vector3.Dot(fingerDir, Vector3.up);
            float norm = upDot / (pseudoDotAtUp > 0.01f ? pseudoDotAtUp : 0.01f);
            if (norm > 1f) norm = 1f; else if (norm < -1f) norm = -1f;
            dy = norm * pseudoTargetAmplitude * pseudoDySign;
            vertical = dy >= verticalThreshold;
        }

        dotOut = dot;
        dyOut  = dy;
        return cover && vertical;
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

    private bool AreFingersExtended(bool isRight)
    {
        const float CURL_THRESHOLD = 40f;

        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightIndexProximal  : HumanBodyBones.LeftIndexProximal)  * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightIndexDistal     : HumanBodyBones.LeftIndexDistal)     * Vector3.forward) > CURL_THRESHOLD) return false;

        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal) * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightMiddleDistal    : HumanBodyBones.LeftMiddleDistal)    * Vector3.forward) > CURL_THRESHOLD) return false;

        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightRingProximal   : HumanBodyBones.LeftRingProximal)   * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightRingDistal      : HumanBodyBones.LeftRingDistal)      * Vector3.forward) > CURL_THRESHOLD) return false;

        if (Vector3.Angle(
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightLittleProximal : HumanBodyBones.LeftLittleProximal) * Vector3.forward,
                localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightLittleDistal    : HumanBodyBones.LeftLittleDistal)    * Vector3.forward) > CURL_THRESHOLD) return false;

        return true;
    }

    // ======================================================================
    // ▼ 音声制御 & UI
    // ======================================================================
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
        localPlayer.SetVoiceLowpass(true);
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

    // ───────────────── ソロ検証（片手） ─────────────────
    private bool SoloCheck(bool isRight)
    {
        bool condDistance = soloIgnoreDistance || IsHandNearHead(localPlayer, selfEarThreshold, isRight);
        bool condFinger   = soloIgnoreFinger   || AreFingersExtended(isRight);
        float dot, dy; bool condOrient = IsPalmFacingEar(localPlayer, isRight, out dot, out dy);

        UpdateBoolTMP(distanceLabel, condDistance, "距離");
        if (orientLabel != null) orientLabel.text = "向き: " + (condOrient ? "Yes" : "No") +
                                                    "  dot=" + dot.ToString("F2") +
                                                    "  dy="  + dy.ToString("F2") + "m";
        UpdateBoolTMP(fingerLabel, condFinger, "指");

        bool shouldWhisper = condDistance && condOrient && condFinger;
        if (shouldWhisper && !isWhispering) EnableWhisper();
        else if (!shouldWhisper && isWhispering) DisableWhisper();
        return shouldWhisper;
    }
}
