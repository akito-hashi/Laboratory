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
        public Transform successorBone;
        public Transform craftmanBone;
    }

    [Header("【最重要】ズレを計算したい骨のオブジェクトを登録してください")]
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
    [Tooltip("位置ズレの最大許容値（センチメートル単位）")]
    public float maxErrorThreshold = 30f;

    [Header("4. AIコーチ連携")]
    public VirtualCoachClient aiClient;
    public string practiceGenre = "バスケットボールのフリースロー";
    [Tooltip("チェックを入れると、AIコーチの回答を音声で読み上げます")]
    public bool enableVoiceReading = true; 

    [Header("5. 動き出し同期設定")]
    public bool syncStartOnMovement = true;
    public float movementThreshold = 0.03f; 
    public float standbyDelay = 1.0f;

    [Header("6. タイミング判定の閾値設定")]
    [Tooltip("お手本より何秒遅れたら「遅すぎる」と判定するか（デフォルト: 0.18秒）")]
    public float timingLateThreshold = 0.18f;
    [Tooltip("お手本より何秒早かったら「早すぎる」と判定するか（デフォルト: 0.18秒）")]
    public float timingEarlyThreshold = 0.18f;

    [Header("★ 技術指導用・タイミング同期設定（100%解決用拡張）")]
    [Tooltip("【最確実】生徒のタイミングを検知するTransform（右手の骨などを直接ドラッグ＆ドロップしてください）")]
    public Transform successorKeyEventTransform;
    [Tooltip("【最確実】お手本のタイミングを検知するTransform（右手の骨などを直接ドラッグ＆ドロップしてください）")]
    public Transform craftmanKeyEventTransform;

    [Tooltip("上記が空欄の場合のみ、このフォールバック設定から骨を自動取得します")]
    public HumanBodyBones keyEventBone = HumanBodyBones.RightHand;
    [Tooltip("基準部位の速度がこの値を超えた瞬間を『決定的な瞬間』とみなす閾値")]
    public float keyEventVelocityThreshold = 1.0f;
    [Tooltip("チェックを入れると、生徒とお手本それぞれの現在の秒速をコンソールに表示し続けます（調整用）")]
    public bool showVelocityLog = true;

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
    private float standbyTimer = 0f; 
    private float currentAverageError = 0f; 
    
    private string worstBoneName = "未特定";
    private float worstBoneError = 0f;       
    private float worstBoneCurrentAngle = 0f; 

    private Material ghostMaterial;
    private Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
    private List<Material> createdMaterials = new List<Material>();
    private float lastAssignedSpeed = -1f;

    // タイミング計算用内部変数
    private float masterEventTime = -1f;   
    private float studentEventTime = -1f;  
    private bool studentEventDetected = false;
    private Vector3 lastStudentBonePos;
    private Vector3 lastMasterBonePos;

    // 実際に計測に使用する確定トランスフォーム
    private Transform actualStudentKeyEventBone;
    private Transform actualMasterKeyEventBone;

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

        if (syncStartOnMovement) { isPaused = false; hasStarted = false; standbyTimer = 0f; }
        else { hasStarted = true; }

        ResolveAndInitializeBones();

        StartCoroutine(EndOfFrameLoop());
    }

    private void ResolveAndInitializeBones()
    {
        if (successorKeyEventTransform != null) actualStudentKeyEventBone = successorKeyEventTransform;
        else if (successorAvatar != null) actualStudentKeyEventBone = successorAvatar.GetBoneTransform(keyEventBone);

        if (craftmanKeyEventTransform != null) actualMasterKeyEventBone = craftmanKeyEventTransform;
        else if (craftmanAvatar != null) actualMasterKeyEventBone = craftmanAvatar.GetBoneTransform(keyEventBone);

        if (actualStudentKeyEventBone != null) lastStudentBonePos = actualStudentKeyEventBone.position;
        if (actualMasterKeyEventBone != null) lastMasterBonePos = actualMasterKeyEventBone.position;
    }
    
    private void RecordInitialPositions()
    {
        initialPositions.Clear();
        foreach (var boneElement in targetBones)
        {
            if (boneElement.isActive && boneElement.successorBone != null)
                initialPositions[boneElement.successorBone] = boneElement.successorBone.position;
        }
    }

    private void ResetStandby()
    {
        isPaused = false; hasStarted = !syncStartOnMovement; virtualTime = 0f; maxRecordedTime = 0f;
        poseHistory.Clear(); ClearAllTrails(); currentAverageError = 0f; lastAssignedSpeed = -1f; standbyTimer = 0f; 
        masterEventTime = -1f; studentEventTime = -1f; studentEventDetected = false;
        if (craftmanAvatar) { craftmanAvatar.Rebind(); craftmanAvatar.speed = 0f; }
        if (craftmanGhost) { craftmanGhost.Rebind(); craftmanGhost.speed = 0f; }

        ResolveAndInitializeBones();
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
            
            if (standbyTimer < standbyDelay)
            {
                standbyTimer += Time.deltaTime; RecordInitialPositions(); return; 
            }
            if (DetectMovement()) 
            { 
                hasStarted = true; 
                lastAssignedSpeed = -1f; 
                ResolveAndInitializeBones();
            }
            else return;
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
                SetAnimatorsSpeed(0f); virtualTime += Time.deltaTime * playSpeed;
                if (virtualTime > maxRecordedTime) virtualTime = maxRecordedTime;
                ApplyPoseAtTime(virtualTime);
            }
            else
            {
                virtualTime += Time.deltaTime * playSpeed; maxRecordedTime = virtualTime;
                SetAnimatorsSpeed(playSpeed); CalculateAverageError(); RecordPoseSnapshot(virtualTime);
            }

            DetectKeyEvents();
        }
    }

    private void DetectKeyEvents()
    {
        if (!hasStarted || isPaused) return;

        bool isInitializationPhase = (virtualTime < 0.2f);

        // --- 生徒のキーイベント検知 ---
        if (actualStudentKeyEventBone != null)
        {
            float studentVelocity = Vector3.Distance(actualStudentKeyEventBone.position, lastStudentBonePos) / Time.deltaTime;
            lastStudentBonePos = actualStudentKeyEventBone.position;

            // 🌟 修正ポイント：現在の閾値と、検知済みかどうかのステータスも一緒に表示する
            if (showVelocityLog && !isInitializationPhase)
            {
                Debug.Log($"<color=yellow>【生徒】速度: {studentVelocity:F2} / 認識している閾値: {keyEventVelocityThreshold:F2} | 検知済フラグ: {studentEventDetected}</color>");
            }

            if (!studentEventDetected)
            {
                if (!isInitializationPhase && studentVelocity > keyEventVelocityThreshold)
                {
                    studentEventTime = virtualTime;
                    studentEventDetected = true;
                    Debug.Log($"<color=orange>🔥【同期システム】生徒の重要イベント検知: {studentEventTime:F2}秒 (速度: {studentVelocity:F1})</color>");
                }
            }
        }

        // --- お手本（マスター）のキーイベント検知 ---
        if (actualMasterKeyEventBone != null)
        {
            float masterVelocity = Vector3.Distance(actualMasterKeyEventBone.position, lastMasterBonePos) / Time.deltaTime;
            lastMasterBonePos = actualMasterKeyEventBone.position;

            if (showVelocityLog && !isInitializationPhase)
            {
                Debug.Log($"<color=cyan>【お手本】速度: {masterVelocity:F2} / 認識している閾値: {keyEventVelocityThreshold:F2} | 検知済フラグ: {(masterEventTime >= 0f)}</color>");
            }

            if (masterEventTime < 0f)
            {
                if (!isInitializationPhase && masterVelocity > keyEventVelocityThreshold)
                {
                    masterEventTime = virtualTime;
                    Debug.Log($"<color=blue>🔥【同期システム】お手本の重要イベント検知: {masterEventTime:F2}秒 (速度: {masterVelocity:F1})</color>");
                }
            }
        }
    }

    private bool DetectMovement()
    {
        if (initialPositions.Count == 0) return true; 
        foreach (var kvp in initialPositions)
        {
            if (kvp.Key != null && Vector3.Distance(kvp.Key.position, kvp.Value) > movementThreshold) return true; 
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
        successorAvatar.transform.position = closest.rootPosSuccessor; successorAvatar.transform.rotation = closest.rootRotSuccessor;
        craftmanAvatar.transform.position = closest.rootPosCraftman; craftmanAvatar.transform.rotation = closest.rootRotCraftman;

        if (successorGhost) { successorGhost.transform.position = closest.rootPosSuccessorGhost; successorGhost.transform.rotation = closest.rootRotSuccessorGhost; }
        if (craftmanGhost) { craftmanGhost.transform.position = closest.rootPosCraftmanGhost; craftmanGhost.transform.rotation = closest.rootRotCraftmanGhost; }

        for (int i = 0; i < allBones.Length; i++)
        {
            Transform bS = successorAvatar.GetBoneTransform(allBones[i]);
            Transform bC = craftmanAvatar.GetBoneTransform(allBones[i]);
            Transform bSG = successorGhost ? successorGhost.GetBoneTransform(allBones[i]) : null;
            Transform bCG = craftmanGhost ? craftmanGhost.GetBoneTransform(allBones[i]) : null;

            if (bS) { bS.localRotation = closest.rotationsSuccessor[i]; bS.localPosition = closest.positionsSuccessor[i]; }
            if (bC) { bC.localRotation = closest.rotationsCraftman[i]; bC.localPosition = closest.positionsCraftman[i]; }
            if (bSG) { bSG.localRotation = closest.rotationsSuccessorGhost[i]; bSG.localPosition = closest.positionsSuccessorGhost[i]; }
            if (bCG) { bCG.localRotation = closest.rotationsCraftmanGhost[i]; bCG.localPosition = closest.positionsCraftmanGhost[i]; }
        }

        CalculateAverageError();
    }

    private void JumpToWorstMoment()
    {
        if (poseHistory.Count == 0) return;
        float maxErr = -1f; float targetTime = 0f;
        foreach (var snap in poseHistory)
        {
            if (snap.averageError > maxErr) { maxErr = snap.averageError; targetTime = snap.timeStamp; }
        }
        virtualTime = targetTime; isPaused = true; ClearAllTrails(); ApplyPoseAtTime(virtualTime); 
    }

    private void ClearAllTrails()
    {
        foreach (var bone in trailBones)
        {
            Transform bS = successorAvatar.GetBoneTransform(bone); Transform bC = craftmanAvatar.GetBoneTransform(bone);
            if (bS) { var t = bS.GetComponent<TrailRenderer>(); if (t) t.Clear(); }
            if (bC) { var t = bC.GetComponent<TrailRenderer>(); if (t) t.Clear(); }
        }
    }

    private void CalculateAverageError()
    {
        float totalError = 0f; int count = 0; worstBoneError = 0f;

        for (int i = 0; i < targetBones.Count; i++)
        {
            TrackedBone boneElement = targetBones[i];
            if (!boneElement.isActive) continue;

            Transform bS = boneElement.successorBone; Transform bC = boneElement.craftmanBone;
            if (bS != null && bC != null)
            {
                Vector3 localPosS = successorAvatar.transform.InverseTransformPoint(bS.position);
                Vector3 localPosC = craftmanAvatar.transform.InverseTransformPoint(bC.position);
                
                float distanceDiff = Vector3.Distance(localPosS, localPosC) * 100f;

                totalError += distanceDiff; count++;
                if (distanceDiff > worstBoneError) 
                { 
                    worstBoneError = distanceDiff; 
                    worstBoneName = boneElement.boneNameJP;
                    worstBoneCurrentAngle = Quaternion.Angle(Quaternion.identity, bS.localRotation); 
                }
            }
        }
        currentAverageError = count > 0 ? totalError / count : 0f;
    }

    private string DetermineMotionPhase(float time)
    {
        if (time < 0.8f) return "セットアップ（構え）";
        if (time < 1.6f) return "フォワードスイング（振りかぶり）";
        return "リリース（ボールを離す直前）";
    }

    private string GenerateCoachJson(bool prettyPrint)
    {
        float timingOffset = 0.0f;
        string timingStatus = "ジャストタイミング（※完璧なタイミングなので、この件には絶対に言及せず姿勢のみアドバイスすること）";

        if (studentEventDetected && masterEventTime > 0f)
        {
            float rawOffset = studentEventTime - masterEventTime;
            
            if (rawOffset > timingLateThreshold) 
            {
                timingStatus = "あまりにも遅すぎる（ワンテンポ遅い）";
                timingOffset = rawOffset; // 閾値を超えた時だけ、実際のズレをAIに渡す
            }
            else if (rawOffset < -timingEarlyThreshold) 
            {
                timingStatus = "あまりにも早すぎる（焦りすぎている）";
                timingOffset = rawOffset; // 閾値を超えた時だけ、実際のズレをAIに渡す
            }
            // 閾値以内の場合は、timingOffsetは0.0のままAIに送信される
        }

        string voiceInstruction = enableVoiceReading ? "（※音声読み上げを行うため、改行は使わず、100文字以内の流暢な一文の日本語で簡潔にアドバイスしてください）" : "（※注意：必ずすべて日本語で回答してください。外国語や絵文字は絶対に使用しないでください。ポーズのズレについて短く指導してください。）";
        string strictInstruction = practiceGenre + voiceInstruction;

        AICoachRequestJSON requestData = new AICoachRequestJSON
        {
            task_info = new TaskInfo {
                genre = strictInstruction,
                motion_phase = DetermineMotionPhase(virtualTime),
                speed_ratio = Mathf.Round(playSpeed * 10f) / 10f
            },
            posture_analysis = new PostureAnalysis {
                average_error = Mathf.Round(currentAverageError * 10f) / 10f,
                critical_error_bone = worstBoneName,
                critical_error_angle = Mathf.Round(worstBoneError * 10f) / 10f, 
                critical_learner_absolute_angle = Mathf.Round(worstBoneCurrentAngle * 10f) / 10f, 
                timing_status = timingStatus,
                timing_offset = Mathf.Round(timingOffset * 100f) / 100f,
                all_bones_data = new List<AIBoneData>()
            }
        };

        foreach (var boneElement in targetBones)
        {
            if (!boneElement.isActive || boneElement.successorBone == null || boneElement.craftmanBone == null) continue;

            Vector3 localPosS = successorAvatar.transform.InverseTransformPoint(boneElement.successorBone.position);
            Vector3 localPosC = craftmanAvatar.transform.InverseTransformPoint(boneElement.craftmanBone.position);
            float distanceDiff = Vector3.Distance(localPosS, localPosC) * 100f;
            float currentAbsAngle = Quaternion.Angle(Quaternion.identity, boneElement.successorBone.localRotation);

            requestData.posture_analysis.all_bones_data.Add(new AIBoneData {
                boneName = boneElement.boneNameJP,
                errorAngle = Mathf.Round(distanceDiff * 10f) / 10f, 
                currentAngle = Mathf.Round(currentAbsAngle * 10f) / 10f 
            });
        }

        return JsonUtility.ToJson(requestData, prettyPrint);
    }

    void OnGUI()
    {
        GUIStyle timelineBoxStyle = new GUIStyle(GUI.skin.box);
        Color timelineColor = Color.Lerp(Color.green, Color.red, Mathf.InverseLerp(0f, maxErrorThreshold, currentAverageError));
        if (currentAverageError < 2f) timelineColor = Color.gray; 

        int width = 450; 
        int height = isPaused ? 565 : 285; 
        
        GUILayout.BeginArea(new Rect(Screen.width - width - 20, Screen.height - height - 20, width, height), timelineBoxStyle);
        Color originalColor = GUI.backgroundColor;
        
        GUILayout.BeginHorizontal();
        if (syncStartOnMovement && !hasStarted)
        {
            GUI.backgroundColor = new Color(1f, 0.7f, 0f); 
            GUILayout.Button(standbyTimer < standbyDelay ? "🔄 IK安定化待ち..." : "⏳ 動き出し待機中...", GUILayout.Width(150), GUILayout.Height(30));
        }
        else
        {
            GUI.backgroundColor = timelineColor;
            if (GUILayout.Button(isPaused ? "▶ 再生 (Play)" : "⏸ 一時停止 (Pause)", GUILayout.Width(150), GUILayout.Height(30))) isPaused = !isPaused;
        }

        GUI.backgroundColor = new Color(0.8f, 0.8f, 0.8f);
        if (GUILayout.Button("🔄 リセット(待機へ)", GUILayout.Width(130), GUILayout.Height(30))) ResetStandby();
        GUI.backgroundColor = originalColor;
        GUILayout.EndHorizontal();

        GUILayout.Space(5);
        GUILayout.Label($" <b>再生速度:</b> {playSpeed:F1}x  |  <b>平均ズレ:</b> <color=yellow>{currentAverageError:F1} cm</color>", new GUIStyle(GUI.skin.label) { richText = true, fontStyle = FontStyle.Bold, fontSize = 14 });

        playSpeed = GUILayout.HorizontalSlider(playSpeed, 0.1f, 2f);
        GUILayout.Space(10);

        GUIStyle subLabelStyle = new GUIStyle(GUI.skin.label) { richText = true };
        GUILayout.Label($"<b>【シークバー】</b> {virtualTime:F1}s / {maxRecordedTime:F1}s", subLabelStyle);
        
        GUI.backgroundColor = timelineColor;
        float sliderVal = GUILayout.HorizontalSlider(virtualTime, 0f, Mathf.Max(0.1f, maxRecordedTime));
        GUI.backgroundColor = originalColor;

        if (Mathf.Abs(sliderVal - virtualTime) > 0.01f) { virtualTime = sliderVal; isPaused = true; ClearAllTrails(); }

        GUILayout.Space(10);
        GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
        if (GUILayout.Button("🔥 最もズレが大きかった「弱点」にジャンプ", GUILayout.Height(30))) JumpToWorstMoment();
        
        GUILayout.Space(10);
        enableVoiceReading = GUILayout.Toggle(enableVoiceReading, " 🔊 AIコーチのアドバイスを音声で読み上げる", GUILayout.Height(20));

        GUILayout.Space(5);
        GUI.backgroundColor = new Color(0.2f, 0.8f, 1f); 
        
        if (GUILayout.Button("💡 現在のポーズについてAIコーチにアドバイスを求める", GUILayout.Height(35)))
        {
            if (aiClient != null)
            {
                aiClient.useVoice = enableVoiceReading;
                string jsonPayload = GenerateCoachJson(false);
                aiClient.RequestAdviceFromJson(jsonPayload);
            }
            else
            {
                Debug.LogWarning("VirtualCoachClient (aiClient) がアタッチされていません。");
            }
        }

        GUILayout.EndArea(); 
    }

    private void OnDestroy()
    {
        StopAllCoroutines();
        foreach (var mat in createdMaterials) if (mat != null) Destroy(mat);
        if (ghostMaterial != null) Destroy(ghostMaterial);
    }

    // ==============================================================================
    // 📦 シリアライズ用データ構造クラス群
    // ==============================================================================
    [System.Serializable]
    public class AICoachRequestJSON
    {
        public TaskInfo task_info;
        public PostureAnalysis posture_analysis;
        public int max_characters = 150;
    }

    [System.Serializable]
    public class TaskInfo
    {
        public string genre;
        public string motion_phase;
        public float speed_ratio;
    }

    [System.Serializable]
    public class PostureAnalysis
    {
        public float average_error;
        public string critical_error_bone;
        public float critical_error_angle;
        public float critical_learner_absolute_angle;
        public string timing_status; 
        public float timing_offset;
        public List<AIBoneData> all_bones_data;
    }

    [System.Serializable]
    public class AIBoneData
    {
        public string boneName;
        public float errorAngle;
        public float currentAngle;
    }
}