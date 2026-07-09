import os
import traceback
import logging
from flask import Flask, request, jsonify, Response
from openai import OpenAI

app = Flask(__name__)

LM_STUDIO_API_URL = "http://127.0.0.1:1234/v1"
LM_STUDIO_MODEL_NAME = "meta-llama-3.1-8B-instruct" 
MANUAL_FILE = "coaching_manual_forRAG.txt"
DEFAULT_MAX_CHARACTERS = 100 

client = OpenAI(base_url=LM_STUDIO_API_URL, api_key="lm-studio")
logging.basicConfig(level=logging.INFO)

def round_floats(obj, decimals=1):
    """【ダイエット機能】辞書やリスト内の小数を指定した桁数に丸める"""
    if isinstance(obj, float):
        return round(obj, decimals)
    elif isinstance(obj, dict):
        return {k: round_floats(v, decimals) for k, v in obj.items()}
    elif isinstance(obj, list):
        return [round_floats(item, decimals) for item in obj]
    return obj

def load_coaching_manual():
    """既存の教本ファイルを読み込む"""
    if os.path.exists(MANUAL_FILE):
        with open(MANUAL_FILE, "r", encoding="utf-8") as f:
            return f.read()
    else:
        # ファイルが見つからない場合のフォールバック（エラー防止用）
        logging.error(f"{MANUAL_FILE} が見つかりません。")
        return "指導方針なし"

@app.route('/analyze', methods=['POST'])
def analyze():
    raw_data = request.json
    if not raw_data:
        return jsonify({"status": "error", "message": "データが空です"}), 400

    # 🌟 データのダイエット実行
    data = round_floats(raw_data, 1)

    task_info = data.get("task_info", {})
    posture_analysis = data.get("posture_analysis", {})

    genre = task_info.get("genre", "スポーツ")
    motion_phase = task_info.get("motion_phase", "未特定")
    timing_status = posture_analysis.get("timing_status", "ジャストタイミング")
    timing_offset = posture_analysis.get("timing_offset", 0.0)

    joint_name = posture_analysis.get("critical_error_bone", "未特定")
    distance_difference = posture_analysis.get("critical_error_angle", 0.0)
    average_error = posture_analysis.get("average_error", 0.0)

    max_characters = data.get("max_characters", DEFAULT_MAX_CHARACTERS)
    manual_context = load_coaching_manual()

    # システムプロンプト
    system_prompt = (
        f"# KNOWLEDGE\n{manual_context}\n\n"
        "Role: AI Coach. Output ONE short polite Japanese advice based on KNOWLEDGE.\n"
        "Rules: No intro/headers. Japanese ONLY. Under limit. Vary phrasing slightly."
    )

    # ユーザープロンプト
    user_prompt = (
        f"Sport:{genre}/Phase:{motion_phase}/Limit:{max_characters}chars\n"
        f"Timing:{timing_status}({timing_offset}s)\n"
        f"ErrorJoint:{joint_name}(Diff:{distance_difference}deg)/AvgError:{average_error}deg\n"
        "Output advice:"
    )

    try:
        response = client.chat.completions.create(
            model=LM_STUDIO_MODEL_NAME,
            messages=[
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt}
            ],
            temperature=0.4, 
            max_tokens=80,
            stream=True
        )

        def generate():
            for chunk in response:
                if chunk.choices and chunk.choices[0].delta.content:
                    content = chunk.choices[0].delta.content
                    content = content.replace("「", "").replace("」", "")
                    yield content

        return Response(generate(), mimetype='text/plain')

    except Exception as e:
        logging.error("通信エラー:\n%s", traceback.format_exc())
        return "（通信エラーが発生しました）", 500

if __name__ == '__main__':
    # サーバー起動時にファイルを事前読み込み
    load_coaching_manual()
    print(f"教本ファイル '{MANUAL_FILE}' を読み込みました。")
    app.run(host='127.0.0.1', port=5001, debug=True)