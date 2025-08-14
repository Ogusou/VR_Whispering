// WhisperManager.cs – 左右手対応 + 指伸展(左右どちらかOK) + 距離AND（self & other）
// + Whispering中は背景を赤に（Canvasの全画面Imageを切替）
// ★soloIgnoreDistance が ON のときは、ソロ検証だけでなく「通常モードでも距離判定を無視＝常に距離OK」にします。
// --------------------------------------------------------------------------------------------------

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using UnityEngine.UI; // 背景Image用

public class WhisperManager : UdonSharpBehaviour
{
    // ───────── インスペクタ設定 ─────────
    [Header("距離しきい値 (m)")]
    public float selfEarThreshold  = 0.12f;   // 自分耳
    public float otherEarThreshold = 0.12f;   // 他人耳

    [Header("向き判定パラメータ")]
    [Tooltip("手のひら法線に使う軸 0=Forward, 1=Up, 2=Right")] public int palmAxis = 0; // Forward 推奨
    [Tooltip("耳→手ベクトルと掌法線の内積しきい値 (>= これ以上でOK : ベース運用どおり)")]
    public float coverDotThreshold = -0.6f;
    [Tooltip("指先が手首より上にあることの高さ差 (m)")]
    public float verticalThreshold = 0.03f; // 3cm 以上推奨

    [Header("指先フォールバック設定 (HandTracking 対策)")]
    [Tooltip("指先が取得できない場合に回転ベースの上向き判定を使う")]
    public bool useRotationFallbackForVertical = true;
    [Tooltip("回転ベースで指先方向に使う軸 0=Forward, 1=Up, 2=Right")]
    public int fingerAxis = 1;
    [Tooltip("回転ベース dy をコントローラ有りの振幅に合わせる目標値 (m)。例: 真上=+0.12, 真下=-0.12")]
    public float pseudoTargetAmplitude = 0.12f;
    [Tooltip("“真上”ポーズ時に観測される upDot の目安（例: 0.67 なら cosθ≈0.67）")]
    public float pseudoDotAtUp = 0.67f;
    [Tooltip("回転ベース dy の符号補正。上を正にしたいなら +1、逆なら -1")]
    public float pseudoDySign = 1f;

    [Header("Whisper 音声設定 (m)")]
    public float whisperFar  = 0.25f;
    public float whisperNear = 0f;

    [Header("通常音声設定 (m)")]
    public float normalFar   = 25f;
    public float normalNear  = 0f;

    [Header("UI (TextMeshPro) 参照")]
    public TextMeshProUGUI distanceLabel; // 距離 OK/NG（AND or 無視）
    public TextMeshProUGUI orientLabel;   // 向き OK/NG（dot / dy）
    public TextMeshProUGUI fingerLabel;   // 指 OK/NG（左右OR）
    public TextMeshProUGUI stateLabel;    // Whispering / Normal

    [Header("フィードバック背景（任意）")]
    [Tooltip("画面全体を覆う Image (Panel) を割り当て。Whisper中に赤へ切り替えます")]
    public Image whisperBgImage; 
    public Color whisperBgColor = new Color(1f, 0f, 0f, 0.35f); // 半透明の赤
    public Color normalBgColor  = new Color(0f, 0f, 0f, 0f);    // 透明

    [Header("ソロ検証モード")]
    public bool soloPalmDebug = false;
    [Tooltip("ONで距離判定を無視（ソロ検証に限らず通常モードでも距離OK扱いにします）")]
    public bool soloIgnoreDistance = true;
    public bool soloIgnoreFinger = true;

    // ───────── 内部変数 ─────────
    private VRCPlayerApi localPlayer;
    private bool isWhispering;

    // ───────── ライフサイクル ─────────
    void Start()
    {
        localPlayer = Networking.LocalPlayer;
        UpdateStateLabel(false);
        if (whisperBgImage != null) whisperBgImage.color = normalBgColor;
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

        // ===== 通常モード：左右を独立に評価し、どちらか満たせば発動 =====
        // 右手
        bool rFingersOK = AreFingersExtended(true);
        float rDotS, rDyS; bool rSelfOrient = IsPalmFacingEar(localPlayer, true, out rDotS, out rDyS);
        bool rSelfDistOK = IsHandNearHead(localPlayer, selfEarThreshold, true);

        VRCPlayerApi rNearest = FindNearestPlayerToHand(otherEarThreshold, true, out bool rNearOther);
        float rDotO = 0f, rDyO = 0f; bool rOtherOrient = false, rOtherDistOK = false;
        if (rNearOther && rNearest != null)
        {
            rOtherOrient = IsPalmFacingEar(rNearest, true, out rDotO, out rDyO);
            rOtherDistOK = IsHandNearHead(rNearest, otherEarThreshold, true);
        }
        // ★距離AND。ただし soloIgnoreDistance が ON なら距離は常にOK
        bool rBothDistOK = soloIgnoreDistance || (rSelfDistOK && rOtherDistOK);
        bool rOrientOK   = rSelfOrient || rOtherOrient; // 向きOR
        float rShowDot   = rOtherOrient ? rDotO : rDotS;
        float rShowDy    = rOtherOrient ? rDyO  : rDyS;

        // 左手
        bool lFingersOK = AreFingersExtended(false);
        float lDotS, lDyS; bool lSelfOrient = IsPalmFacingEar(localPlayer, false, out lDotS, out lDyS);
        bool lSelfDistOK = IsHandNearHead(localPlayer, selfEarThreshold, false);

        VRCPlayerApi lNearest = FindNearestPlayerToHand(otherEarThreshold, false, out bool lNearOther);
        float lDotO = 0f, lDyO = 0f; bool lOtherOrient = false, lOtherDistOK = false;
        if (lNearOther && lNearest != null)
        {
            lOtherOrient = IsPalmFacingEar(lNearest, false, out lDotO, out lDyO);
            lOtherDistOK = IsHandNearHead(lNearest, otherEarThreshold, false);
        }
        // ★距離AND。ただし soloIgnoreDistance が ON なら距離は常にOK
        bool lBothDistOK = soloIgnoreDistance || (lSelfDistOK && lOtherDistOK);
        bool lOrientOK   = lSelfOrient || lOtherOrient; // 向きOR
        float lShowDot   = lOtherOrient ? lDotO : lDotS;
        float lShowDy    = lOtherOrient ? lDyO  : lDyS;

        // 指伸展は左右どちらかOK
        bool fingersOKAny = rFingersOK || lFingersOK;

        // 幾何（距離AND×向きOR）が成立している手
        bool rightGeomOK = rBothDistOK && rOrientOK;
        bool leftGeomOK  = lBothDistOK && lOrientOK;

        // 表示に使う“アクティブ手”の選択（幾何成立手を優先。両方成立なら |dy| 大きい方）
        bool useRight;
        if (rightGeomOK && !leftGeomOK) useRight = true;
        else if (!rightGeomOK && leftGeomOK) useRight = false;
        else useRight = (Mathf.Abs(rShowDy) >= Mathf.Abs(lShowDy));

        bool activeBothDistOK = useRight ? rBothDistOK : lBothDistOK;
        bool activeOrientOK   = useRight ? rOrientOK   : lOrientOK;
        float showDot         = useRight ? rShowDot    : lShowDot;
        float showDy          = useRight ? rShowDy     : lShowDy;

        // UI
        UpdateBoolTMP(distanceLabel, activeBothDistOK, "距離");  // AND or 無視
        if (orientLabel != null)
        {
            orientLabel.text = "向き: " + (activeOrientOK ? "Yes" : "No") +
                               "  dot=" + showDot.ToString("F2") +
                               "  dy="  + showDy.ToString("F2") + "m";
        }
        UpdateBoolTMP(fingerLabel, fingersOKAny, "指");          // 左右OR

        // 最終発動： (右GeomOK または 左GeomOK) × (指は左右どちらかOK)
        bool shouldWhisper = (rightGeomOK || leftGeomOK) && fingersOKAny;
        if (shouldWhisper && !isWhispering) EnableWhisper();
        else if (!shouldWhisper && isWhispering) DisableWhisper();
    }

    // ======================================================================
    // ▼ ソロ検証（片手）
    // ======================================================================
    private bool SoloCheck(bool isRight)
    {
        bool condDistance = soloIgnoreDistance || IsHandNearHead(localPlayer, selfEarThreshold, isRight);
        bool condFinger   = soloIgnoreFinger   || AreFingersExtended(isRight);
        float dot, dy; bool condOrient = IsPalmFacingEar(localPlayer, isRight, out dot, out dy);

        UpdateBoolTMP(distanceLabel, condDistance, "距離");
        if (orientLabel != null) orientLabel.text = "向き: " + (condOrient ? "Yes" : "No") +
                                                    "  dot=" + dot.ToString("F2") +
                                                    "  dy=" + dy.ToString("F2") + "m";
        UpdateBoolTMP(fingerLabel, condFinger, "指");

        bool shouldWhisper = condDistance && condOrient && condFinger;
        if (shouldWhisper && !isWhispering) EnableWhisper();
        else if (!shouldWhisper && isWhispering) DisableWhisper();
        return shouldWhisper;
    }

    // ======================================================================
    // ▼ 判定メソッド（左右対応）
    // ======================================================================

    // 頭と（左右いずれかの）手首の距離がしきい値未満か
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

        Vector3 axis = (palmAxis == 1) ? Vector3.up : (palmAxis == 2 ? Vector3.right : Vector3.forward);
        Quaternion handRot = localPlayer.GetBoneRotation(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        Vector3 palmNormal = handRot * axis;
        float dot = Vector3.Dot(palmNormal, earToHand);
        bool cover = dot >= coverDotThreshold; // ベースどおり

        float dy = 0f;
        bool vertical = false;

        bool tipValid;
        Vector3 tip = GetValidFingerTip(isRight, out tipValid);
        if (tipValid)
        {
            dy = tip.y - wrist.y;
            vertical = dy >= verticalThreshold;
        }
        else if (useRotationFallbackForVertical)
        {
            Vector3 fingerAxisV = (fingerAxis == 1) ? Vector3.up : (fingerAxis == 2 ? Vector3.right : Vector3.forward);
            Vector3 fingerDir   = (handRot * fingerAxisV).normalized;
            float upDot = Vector3.Dot(fingerDir, Vector3.up);

            float norm = upDot / Mathf.Max(0.01f, pseudoDotAtUp);
            norm = Mathf.Clamp(norm, -1f, 1f);

            dy = norm * pseudoTargetAmplitude * pseudoDySign;
            vertical = dy >= verticalThreshold;
        }

        dotOut = dot;
        dyOut  = dy;
        return cover && vertical;
    }

    // 指先ボーンの有効な位置を順に探す（HT で (0,0,0) を返す対策）
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

    // 指伸展チェック（Index/Middle/Ring/Little の Proximal↔Distal の角度）
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

    // 最も近い他プレイヤーの頭までの距離を返す（手ごとに計算）
    private VRCPlayerApi FindNearestPlayerToHand(float maxDist, bool isRight, out bool near)
    {
        VRCPlayerApi[] list = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(list);

        Vector3 wrist = localPlayer.GetBonePosition(isRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        float min = maxDist;
        VRCPlayerApi best = null;

        foreach (VRCPlayerApi p in list)
        {
            if (p == null || !p.IsValid() || p.isLocal) continue;
            float d = Vector3.Distance(wrist, p.GetBonePosition(HumanBodyBones.Head));
            if (d < min) { min = d; best = p; }
        }
        near = (best != null);
        return best;
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

        if (whisperBgImage != null) whisperBgImage.color = whisperBgColor; // 背景を赤に
    }

    private void DisableWhisper()
    {
        localPlayer.SetVoiceDistanceNear(normalNear);
        localPlayer.SetVoiceDistanceFar(normalFar);
        localPlayer.SetVoiceLowpass(true);
        isWhispering = false;
        UpdateStateLabel(false);

        if (whisperBgImage != null) whisperBgImage.color = normalBgColor; // 背景を通常に
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
}
