using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.InputSystem;
using TMPro;

public class VirtualCoachClient : MonoBehaviour
{
    private string serverUrl = "http://127.0.0.1:5001/analyze";

    [Header("AIからのアドバイスを表示するテキスト")]
    [SerializeField] private TextMeshProUGUI adviceTextDisplay; 

    [Header("音声読み上げ（TTS）設定")]
    public bool useVoice = true; // ★追加：インスペクターや画面UIでON/OFFできるチェックボックス

    public SimpleTTS ttsManager; 

    [Serializable]
    public class CoachRequestData
    {
        public string genre;
        public string joint_name;
        public float angle_difference;
        public float timing_delay;
    }

    [Serializable]
    public class CoachResponseData
    {
        public string status;
        public string advice;
    }

    public void RequestAdviceFromOutside(string genre, string joint, float angleDiff, float timeDelay)
    {
        if (adviceTextDisplay != null) 
        {
            adviceTextDisplay.text = "コーチが動きを分析中...";
        }
        
        StartCoroutine(SendMovementData(genre, joint, angleDiff, timeDelay));
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            RequestAdviceFromOutside("伝統工芸の動作", "テスト部位", 15.0f, 0.0f);
        }
    }

    IEnumerator SendMovementData(string genre, string joint, float angleDiff, float timeDelay)
    {
        CoachRequestData requestData = new CoachRequestData
        {
            genre = genre,
            joint_name = joint,
            angle_difference = angleDiff,
            timing_delay = timeDelay
        };

        string jsonRaw = JsonUtility.ToJson(requestData);
        byte[] jsonToSend = new UTF8Encoding().GetBytes(jsonRaw);

        using (UnityWebRequest request = new UnityWebRequest(serverUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(jsonToSend);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
            {
                Debug.LogError($"エラーが発生しました: {request.error}");
                if (adviceTextDisplay != null) 
                {
                    adviceTextDisplay.text = "エラーが発生しました。Pythonサーバーを確認してください。";
                }
            }
            else
            {
                string jsonResponse = request.downloadHandler.text;
                CoachResponseData responseData = JsonUtility.FromJson<CoachResponseData>(jsonResponse);

                if (adviceTextDisplay != null)
                {
                    adviceTextDisplay.text = responseData.advice;
                }
                
                Debug.Log($"<color=cyan>【AIコーチからのアドバイス】</color>\n{responseData.advice}");

                // ★修正：useVoice が ON の時だけ音声を再生する
                if (useVoice && ttsManager != null)
                {
                    ttsManager.Speak(responseData.advice);
                }
            }
        }
    }
}