using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class MotionVisualOverlay : MonoBehaviour
{
    [System.Serializable]
    public class TrackedBone
    {
        [Tooltip("チェックを入れると、この骨のズレを計算対象に含めます")]
        public bool isActive = true;
        public string boneNameJP;   
        [Tooltip("画面上で実際に動いている「新規」アバターの骨オブジェクト")]
        public Transform successorBone;
        [Tooltip("画面上で実際に動いている「職人」アバターの骨オブジェクト")]
        public Transform craftmanBone;
    }

    [Header("【最重要】ズレを計算したい骨のオブジェクトを登録してください（チェックボックスでON/OFF可能）")]
    public List<TrackedBone> targetBones = new List<TrackedBone>();

    [Header("【表示用アバター】（シークバー・軌跡用）")]
    public Animator successorAvatar;
    public Animator craftmanAvatar;

    [Header("【過去】ゴーストアバター")]
    public Animator successorGhost;
    public Animator craftmanGhost;

    [Header("1. ゴースト表示設定")]
    [Range(0.1f, 0.9f)] public float ghostAlpha = 0.4f;
    public Color ghostColor = new Color(0.0f, 0.5f, 1.0f);

    [Header("2. 移動軌跡（トレイル）設定")]
    public float trailTime = 1.0f;
    public float trailWidth = 0.02f;

    [Header("3. タイムライン・感度設定")]
    [Range(0.1f, 2f)] public float playSpeed = 1.0f;
    public float maxErrorThreshold = 30f;

    [Header("4. AIコーチ連携")]
    public VirtualCoachClient aiClient;
    public string practiceGenre = "伝統工芸の動作";

    [Header("5. 動き出し同期設定")]
    [Tooltip("動き出しを検知して同期をスタートするかどうか")]
    public bool syncStartOnMovement = true;
    [Tooltip("動き出しと判定する移動量（メートル）")]
    public float movementThreshold = 0.03f; 
    [Tooltip("【重要】シーン開始・リセット直後のIK瞬間移動を無視する時間（秒）")]
    public float standbyDelay = 1.0f;

    private HumanBodyBones[] allBones;
    private readonly HumanBodyBones[] trailBones = new HumanBodyBones[]
    {
        HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot
    };

    private class PoseSnapshot
    {
        public float timeStamp; 
        public Quaternion[] rotationsSuccessor;
        public Quaternion[] rotationsCraftman;
        public Quaternion[] rotationsSuccessorGhost;
        public Quaternion[] rotationsCraftmanGhost;

        public Vector3[] positionsSuccessor;
        public Vector3[] positionsCraftman;
        public Vector3[] positionsSuccessorGhost;
        public Vector3[] positionsCraftmanGhost;

        public Vector3 rootPosSuccessor, rootPosCraftman, rootPosSuccessorGhost, rootPosCraftmanGhost;
        public Quaternion rootRotSuccessor, rootRotCraftman, rootRotSuccessorGhost, rootRotCraftmanGhost;
        
        public float averageError; 
    }

    private List<PoseSnapshot> poseHistory = new List<PoseSnapshot>();
    private float virtualTime = 0f;       
    private float maxRecordedTime = 0f;   
    private bool isPaused = false;        
    
    private bool hasStarted = false;
    private Dictionary<Transform, Vector3> initialPositions = new Dictionary<Transform, Vector3>();
    private float standbyTimer = 0f; // 🌟追加：安定待ちタイマー

    private float currentAverageError = 0f;
    private string worstBoneName = "未特定";
    private float worstBoneError = 0f;

    private Material ghostMaterial;
    private Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
    private List<Material> createdMaterials = new List<Material>();
    private float lastAssignedSpeed = -1f;

    void Start()
    {
        if (successorAvatar == null || craftmanAvatar == null) return;

        var boneList = new List<HumanBodyBones>();
        foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone != HumanBodyBones.LastBone) boneList.Add(bone);
        }
        allBones = boneList.ToArray();

        Shader standardShader = Shader.Find("Standard");
        if (standardShader != null)
        {
            ghostMaterial = new Material(standardShader);
            ghostMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            ghostMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            ghostMaterial.SetInt("_ZWrite", 0);
            ghostMaterial.renderQueue = 3000;
            Color c = ghostColor; c.a = ghostAlpha;
            ghostMaterial.SetColor("_Color", c);
            ApplyGhostMaterial(craftmanGhost);
        }

        SetupTrails(successorAvatar, new Color(1f, 0.2f, 0.2f, 0.8f)); 
        SetupTrails(craftmanAvatar, new Color(0.2f, 0.6f, 1f, 0.8f));  

        if (syncStartOnMovement)
        {
            isPaused = false; 
            hasStarted = false;
            standbyTimer = 0f; // 🌟タイマーリセット
        }
        else
        {
            hasStarted = true; 
        }

        StartCoroutine(EndOfFrameLoop());
    }

    private void RecordInitialPositions()
    {
        initialPositions.Clear();
        foreach (var boneElement in targetBones)
        {
            if (boneElement.isActive && boneElement.successorBone != null)
            {
                initialPositions[boneElement.successorBone] = boneElement.successorBone.position;
            }
        }
    }

    private void ResetStandby()
    {
        isPaused = false;
        hasStarted = !syncStartOnMovement;
        virtualTime = 0f;
        maxRecordedTime = 0f;
        poseHistory.Clear();
        ClearAllTrails();
        currentAverageError = 0f;
        lastAssignedSpeed = -1f;
        standbyTimer = 0f; // 🌟リセット時もタイマーをゼロに戻して安定待ちする

        if (craftmanAvatar != null)
        {
            craftmanAvatar.Rebind();
            craftmanAvatar.speed = 0f;
        }
        if (craftmanGhost != null)
        {
            craftmanGhost.Rebind();
            craftmanGhost.speed = 0f;
        }
    }

    private void ApplyGhostMaterial(Animator ghostAnim)
    {
        if (ghostAnim == null || ghostMaterial == null) return;
        Renderer[] renderers = ghostAnim.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers)
        {
            originalMaterials[r] = r.sharedMaterials;
            Material[] ghostMats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < ghostMats.Length; i++) ghostMats[i] = ghostMaterial;
            r.sharedMaterials = ghostMats;
        }
    }

    void SetupTrails(Animator animator, Color color)
    {
        if (animator == null) return;
        foreach (var bone in trailBones)
        {
            Transform boneT = animator.GetBoneTransform(bone);
            if (boneT == null) continue;
            TrailRenderer trail = boneT.GetComponent<TrailRenderer>();
            if (trail == null) trail = boneT.gameObject.AddComponent<TrailRenderer>();
            trail.time = trailTime; trail.startWidth = trailWidth; trail.endWidth = 0f;
            Material trailMat = new Material(Shader.Find("Sprites/Default"));
            createdMaterials.Add(trailMat); trail.material = trailMat;
            trail.startColor = color; Color ec = color; ec.a = 0f; trail.endColor = ec;
        }
    }

    void LateUpdate()
    {
        if (successorAvatar == null || craftmanAvatar == null) return;

        if (syncStartOnMovement && !hasStarted)
        {
            if (craftmanAvatar) craftmanAvatar.speed = 0f;
            if (craftmanGhost) craftmanGhost.speed = 0f;
            if (successorAvatar) successorAvatar.speed = 1f;
            if (successorGhost) successorGhost.speed = 1f;
            
            // 🌟追加：指定した時間が経過するまでは、初期位置を常に上書きしてトラッカーの安定を待つ
            if (standbyTimer < standbyDelay)
            {
                standbyTimer += Time.deltaTime;
                RecordInitialPositions();
                return; // この間は動きの判定を行わない
            }

            if (DetectMovement())
            {
                hasStarted = true;
                lastAssignedSpeed = -1f; 
            }
            else
            {
                return; 
            }
        }

        if (isPaused)
        {
            SetAnimatorsSpeed(0f);
            if (poseHistory.Count > 0) ApplyPoseAtTime(virtualTime);
        }
        else
        {
            if (virtualTime < maxRecordedTime - 0.01f)
            {
                SetAnimatorsSpeed(0f);
                virtualTime += Time.deltaTime * playSpeed;
                if (virtualTime > maxRecordedTime) virtualTime = maxRecordedTime;
                ApplyPoseAtTime(virtualTime);
            }
            else
            {
                virtualTime += Time.deltaTime * playSpeed;
                maxRecordedTime = virtualTime;
                SetAnimatorsSpeed(playSpeed);
                
                CalculateAverageError();
                RecordPoseSnapshot(virtualTime);
            }
        }
    }

    private bool DetectMovement()
    {
        if (initialPositions.Count == 0) return true; 

        foreach (var kvp in initialPositions)
        {
            if (kvp.Key == null) continue;
            
            if (Vector3.Distance(kvp.Key.position, kvp.Value) > movementThreshold)
            {
                return true; 
            }
        }
        return false;
    }

    private IEnumerator EndOfFrameLoop()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            if (!isPaused && hasStarted) CalculateAverageError();
        }
    }

    private void SetAnimatorsSpeed(float speed)
    {
        if (Mathf.Approximately(lastAssignedSpeed, speed)) return;
        lastAssignedSpeed = speed;
        if (successorAvatar) successorAvatar.speed = speed;
        if (craftmanAvatar) craftmanAvatar.speed = speed;
        if (successorGhost) successorGhost.speed = speed;
        if (craftmanGhost) craftmanGhost.speed = speed;
    }

    private void RecordPoseSnapshot(float time)
    {
        PoseSnapshot snapshot = new PoseSnapshot
        {
            timeStamp = time,
            rotationsSuccessor = new Quaternion[allBones.Length],
            rotationsCraftman = new Quaternion[allBones.Length],
            rotationsSuccessorGhost = new Quaternion[allBones.Length],
            rotationsCraftmanGhost = new Quaternion[allBones.Length],

            positionsSuccessor = new Vector3[allBones.Length],
            positionsCraftman = new Vector3[allBones.Length],
            positionsSuccessorGhost = new Vector3[allBones.Length],
            positionsCraftmanGhost = new Vector3[allBones.Length],

            rootPosSuccessor = successorAvatar.transform.position,
            rootPosCraftman = craftmanAvatar.transform.position,
            rootPosSuccessorGhost = successorGhost ? successorGhost.transform.position : Vector3.zero,
            rootPosCraftmanGhost = craftmanGhost ? craftmanGhost.transform.position : Vector3.zero,
            rootRotSuccessor = successorAvatar.transform.rotation,
            rootRotCraftman = craftmanAvatar.transform.rotation,
            rootRotSuccessorGhost = successorGhost ? successorGhost.transform.rotation : Quaternion.identity,
            rootRotCraftmanGhost = craftmanGhost ? craftmanGhost.transform.rotation : Quaternion.identity,
            
            averageError = currentAverageError 
        };

        for (int i = 0; i < allBones.Length; i++)
        {
            Transform bS = successorAvatar.GetBoneTransform(allBones[i]);
            Transform bC = craftmanAvatar.GetBoneTransform(allBones[i]);
            Transform bSG = successorGhost ? successorGhost.GetBoneTransform(allBones[i]) : null;
            Transform bCG = craftmanGhost ? craftmanGhost.GetBoneTransform(allBones[i]) : null;

            snapshot.rotationsSuccessor[i] = bS != null ? bS.localRotation : Quaternion.identity;
            snapshot.rotationsCraftman[i] = bC != null ? bC.localRotation : Quaternion.identity;
            snapshot.rotationsSuccessorGhost[i] = bSG != null ? bSG.localRotation : Quaternion.identity;
            snapshot.rotationsCraftmanGhost[i] = bCG != null ? bCG.localRotation : Quaternion.identity;

            snapshot.positionsSuccessor[i] = bS != null ? bS.localPosition : Vector3.zero;
            snapshot.positionsCraftman[i] = bC != null ? bC.localPosition : Vector3.zero;
            snapshot.positionsSuccessorGhost[i] = bSG != null ? bSG.localPosition : Vector3.zero;
            snapshot.positionsCraftmanGhost[i] = bCG != null ? bCG.localPosition : Vector3.zero;
        }
        poseHistory.Add(snapshot);
    }

    private void ApplyPoseAtTime(float time)
    {
        if (poseHistory.Count < 10) return;
        int low = 0, high = poseHistory.Count - 1, closestIndex = 0;
        float minDiff = float.MaxValue;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            float diff = Mathf.Abs(poseHistory[mid].timeStamp - time);
            if (diff < minDiff) { minDiff = diff; closestIndex = mid; }
            if (poseHistory[mid].timeStamp < time) low = mid + 1;
            else if (poseHistory[mid].timeStamp > time) high = mid - 1;
            else break;
        }

        PoseSnapshot closest = poseHistory[closestIndex];
        successorAvatar.transform.position = closest.rootPosSuccessor;
        successorAvatar.transform.rotation = closest.rootRotSuccessor;
        craftmanAvatar.transform.position = closest.rootPosCraftman;
        craftmanAvatar.transform.rotation = closest.rootRotCraftman;

        if (successorGhost != null) { successorGhost.transform.position = closest.rootPosSuccessorGhost; successorGhost.transform.rotation = closest.rootRotSuccessorGhost; }
        if (craftmanGhost != null) { craftmanGhost.transform.position = closest.rootPosCraftmanGhost; craftmanGhost.transform.rotation = closest.rootRotCraftmanGhost; }

        for (int i = 0; i < allBones.Length; i++)
        {
            Transform bS = successorAvatar.GetBoneTransform(allBones[i]);
            Transform bC = craftmanAvatar.GetBoneTransform(allBones[i]);
            Transform bSG = successorGhost ? successorGhost.GetBoneTransform(allBones[i]) : null;
            Transform bCG = craftmanGhost ? craftmanGhost.GetBoneTransform(allBones[i]) : null;

            if (bS != null) { bS.localRotation = closest.rotationsSuccessor[i]; bS.localPosition = closest.positionsSuccessor[i]; }
            if (bC != null) { bC.localRotation = closest.rotationsCraftman[i]; bC.localPosition = closest.positionsCraftman[i]; }
            if (bSG != null) { bSG.localRotation = closest.rotationsSuccessorGhost[i]; bSG.localPosition = closest.positionsSuccessorGhost[i]; }
            if (bCG != null) { bCG.localRotation = closest.rotationsCraftmanGhost[i]; bCG.localPosition = closest.positionsCraftmanGhost[i]; }
        }
    }

    private void JumpToWorstMoment()
    {
        if (poseHistory.Count == 0) return;

        float maxErr = -1f;
        float targetTime = 0f;

        foreach (var snap in poseHistory)
        {
            if (snap.averageError > maxErr)
            {
                maxErr = snap.averageError;
                targetTime = snap.timeStamp;
            }
        }

        virtualTime = targetTime;
        isPaused = true;
        ClearAllTrails();
        ApplyPoseAtTime(virtualTime);
        CalculateAverageError(); 
    }

    private void ClearAllTrails()
    {
        foreach (var bone in trailBones)
        {
            Transform bS = successorAvatar.GetBoneTransform(bone);
            Transform bC = craftmanAvatar.GetBoneTransform(bone);
            if (bS != null) { var t = bS.GetComponent<TrailRenderer>(); if (t != null) t.Clear(); }
            if (bC != null) { var t = bC.GetComponent<TrailRenderer>(); if (t != null) t.Clear(); }
        }
    }

    private void CalculateAverageError()
    {
        float totalError = 0f;
        int count = 0;
        worstBoneError = 0f;

        for (int i = 0; i < targetBones.Count; i++)
        {
            TrackedBone boneElement = targetBones[i];
            if (!boneElement.isActive) continue;

            Transform bS = boneElement.successorBone;
            Transform bC = boneElement.craftmanBone;

            if (bS != null && bC != null)
            {
                Quaternion charRotS = Quaternion.Inverse(successorAvatar.transform.rotation) * bS.rotation;
                Quaternion charRotC = Quaternion.Inverse(craftmanAvatar.transform.rotation) * bC.rotation;
                
                float worldAngleDiff = Quaternion.Angle(charRotS, charRotC);
                float localAngleDiff = Quaternion.Angle(bS.localRotation, bC.localRotation);
                
                float angleDiff = Mathf.Max(worldAngleDiff, localAngleDiff);

                totalError += angleDiff;
                count++;

                if (angleDiff > worstBoneError)
                {
                    worstBoneError = angleDiff;
                    worstBoneName = boneElement.boneNameJP;
                }
            }
        }
        
        currentAverageError = count > 0 ? totalError / count : 0f;
    }

    void OnGUI()
    {
        GUIStyle timelineBoxStyle = new GUIStyle(GUI.skin.box);
        float errorNormalized = Mathf.InverseLerp(0f, maxErrorThreshold, currentAverageError);
        Color timelineColor = Color.Lerp(Color.green, Color.red, errorNormalized);
        if (currentAverageError < 5f) timelineColor = Color.gray; 

        int width = 450; int height = 260; 
        GUILayout.BeginArea(new Rect(Screen.width - width - 20, Screen.height - height - 20, width, height), timelineBoxStyle);
        
        Color originalColor = GUI.backgroundColor;
        
        GUILayout.BeginHorizontal();
        if (syncStartOnMovement && !hasStarted)
        {
            // 🌟UIもわかりやすく「準備中」と「待機中」で色分け
            if (standbyTimer < standbyDelay)
            {
                GUI.backgroundColor = new Color(0.5f, 0.5f, 0.5f); 
                GUILayout.Button("🔄 IK安定化待ち...", GUILayout.Width(150), GUILayout.Height(30));
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.7f, 0f); 
                GUILayout.Button("⏳ 動き出し待機中...", GUILayout.Width(150), GUILayout.Height(30));
            }
        }
        else
        {
            GUI.backgroundColor = timelineColor;
            string btnText = isPaused ? "▶ 再生 (Play)" : "⏸ 一時停止 (Pause)";
            if (GUILayout.Button(btnText, GUILayout.Width(150), GUILayout.Height(30))) isPaused = !isPaused;
        }

        GUI.backgroundColor = new Color(0.8f, 0.8f, 0.8f);
        if (GUILayout.Button("🔄 リセット(待機へ)", GUILayout.Width(130), GUILayout.Height(30)))
        {
            ResetStandby();
        }
        GUI.backgroundColor = originalColor;
        GUILayout.EndHorizontal();

        GUILayout.Space(5);

        GUILayout.BeginHorizontal();
        GUIStyle labelStyle = new GUIStyle(GUI.skin.label) { richText = true, fontStyle = FontStyle.Bold, fontSize = 14 };
        GUILayout.Label(" <b>再生速度:</b> " + playSpeed.ToString("F1") + "x  |  <b>平均ズレ:</b> <color=yellow>" + currentAverageError.ToString("F1") + "°</color>", labelStyle);
        GUILayout.EndHorizontal();

        bool hasActiveBone = false;
        foreach (var b in targetBones) { if (b.isActive) hasActiveBone = true; }

        if (targetBones.Count == 0 || !hasActiveBone)
        {
            GUIStyle warningStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
            GUILayout.Label("<color=orange>⚠ 計測対象の骨が登録されていないか、すべてチェックが外れています！</color>", warningStyle);
        }

        playSpeed = GUILayout.HorizontalSlider(playSpeed, 0.1f, 2f);
        GUILayout.Space(10);

        GUIStyle subLabelStyle = new GUIStyle(GUI.skin.label) { richText = true };
        GUILayout.Label("<b>【シークバー】</b> " + virtualTime.ToString("F1") + "s / " + maxRecordedTime.ToString("F1") + "s", subLabelStyle);
        
        GUI.backgroundColor = timelineColor;
        float sliderVal = GUILayout.HorizontalSlider(virtualTime, 0f, Mathf.Max(0.1f, maxRecordedTime));
        GUI.backgroundColor = originalColor;

        if (Mathf.Abs(sliderVal - virtualTime) > 0.01f)
        {
            virtualTime = sliderVal;
            isPaused = true; 
            ClearAllTrails(); 
        }

        GUILayout.Space(10);

        GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
        if (GUILayout.Button("🔥 最もズレが大きかった「弱点」にジャンプ", GUILayout.Height(30)))
        {
            JumpToWorstMoment();
        }
        
        GUILayout.Space(5);

        GUI.backgroundColor = new Color(0.2f, 0.8f, 1f); 
        if (GUILayout.Button("💡 現在のポーズについてAIコーチにアドバイスを求める", GUILayout.Height(35)))
        {
            if (aiClient != null)
            {
                isPaused = true;
                string strictJapaneseGenre = practiceGenre + "（※注意：必ずすべて日本語で親しみやすく回答してください。英語などの外国語や絵文字・顔文字は絶対に使用しないでください。また、動きの遅れや早さなどの「タイミングのズレ（〇秒など）」については一切言及せず、「角度やポーズのズレ」についてのみアドバイスをしてください。）";
                aiClient.RequestAdviceFromOutside(strictJapaneseGenre, worstBoneName, Mathf.Round(worstBoneError * 10.0f) / 10.0f, virtualTime);
            }
            else
            {
                Debug.LogWarning("Ai Clientがアタッチされていません！");
            }
        }
        GUI.backgroundColor = originalColor;

        GUILayout.EndArea();
    }

    private void OnDestroy()
    {
        foreach (var kvp in originalMaterials)
        {
            if (kvp.Key != null) kvp.Key.sharedMaterials = kvp.Value;
        }
        if (ghostMaterial != null) Destroy(ghostMaterial);
        foreach (Material mat in createdMaterials) { if (mat != null) Destroy(mat); }
    }
}