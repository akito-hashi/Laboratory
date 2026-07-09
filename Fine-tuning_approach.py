from flask import Flask, request, jsonify
from openai import OpenAI

app = Flask(__name__)

# ==============================================================================
# ⚙️ LM Studioのローカル設定
# ==============================================================================
# LM Studioが立ち上げるローカルサーバーのURL（標準はポート1234）
LM_STUDIO_API_URL = "http://localhost:1234/v1"

# あなたが追加学習（ファインチューニング）させて、LM Studioに読み込ませたカスタムモデルの名前
# ※もしまだ学習・変換を行っていない場合は、LM Studioで今ロードしているモデル名（"meta-llama-3-8b-instruct" など）をそのまま書いてテストしてください。
FINE_TUNED_MODEL_NAME = "meta-llama-3-8b-instruct"
# ==============================================================================

# クライアントの初期化（接続先をLM StudioのローカルURLに変更、APIキーはダミーでOK）
client = OpenAI(base_url=LM_STUDIO_API_URL, api_key="lm-studio")

@app.route('/analyze', methods=['POST'])
def analyze():
    data = request.json
    if not data:
        return jsonify({"status": "error", "message": "データが空です"}), 400

    genre = data.get("genre", "伝統工芸の動作")
    joint_name = data.get("joint_name", "未特定")
    angle_difference = data.get("angle_difference", 0.0)
    timing_delay = data.get("timing_delay", 0.0)

    # 生徒のミス状況の整理
    student_status = f"【分析対象の瞬間】{timing_delay}秒時点\n【最もズレている部位】{joint_name}\n【職人との角度のズレ】{angle_difference}度"

    # 追加学習済みモデルを前提とするため、システムプロンプト（指示文）は最小限にしています
    # AIの脳みそ自体にすでに独自の指導スタイルや口調が染み込んでいる想定です
    system_prompt = f"あなたは「{genre}」の専門AIコーチです。これまでに学習した独自の指導スタイルと知識をフルに活用し、生徒にアドバイスをしてください。"
    
    try:
        # LM Studioにリクエストを送信
        response = client.chat.completions.create(
            model=FINE_TUNED_MODEL_NAME,  # LM Studio側で選ばれているモデルを呼び出す
            messages=[
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": f"生徒データ:\n{student_status}"}
            ],
            temperature=0.7
        )
        advice_text = response.choices[0].message.content

    except Exception as e:
        print(f"エラーが発生しました: {e}")
        advice_text = "（LM Studioとの接続エラーが発生しました。LM Studio側でサーバーが開始されているか確認してください。）"

    return jsonify({
        "status": "success",
        "approach": "B (Local Fine-tuning)",
        "advice": advice_text
    })

if __name__ == '__main__':
    # UnityのVirtualCoachClientがアクセスするポート5001で起動
    app.run(host='127.0.0.1', port=5001, debug=True)