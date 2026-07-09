from flask import Flask, request, jsonify
from openai import OpenAI

app = Flask(__name__)

client = OpenAI(
    base_url="http://localhost:1234/v1",
    api_key="lm-studio"
)

@app.route('/analyze', methods=['POST'])
def analyze_movement():
    try:
        data = request.get_json()
        
        genre = data.get('genre', 'バスケットボール')
        joint_name = data.get('joint_name', '右肘')
        angle_difference = data.get('angle_difference', 0)
        timing_delay = data.get('timing_delay', 0.0)

        # AIに渡す指示文（日本語を強制）
        prompt = f"""
あなたはプロの{genre}コーチです。
生徒のアバターの動きを解析した結果、以下のズレが見つかりました。

【解析データ】
・注目した部位: {joint_name}
・理想のフォームとの角度の差: {angle_difference} 度
・お手本とのタイミングのズレ: {timing_delay} 秒

この生徒に対して、ズレを修正するための具体的で分かりやすいアドバイスを「100文字程度」の日本語で作成してください。モチベーションが上がるような一言も含めてください。
"""

        response = client.chat.completions.create(
            model="meta-llama-3-8b-instruct",
            messages=[
                # ここで「絶対に日本語で出力する」というルールをAIに強く縛り付けます
                {"role": "system", "content": "あなたは優秀で熱血なバーチャルスポーツコーチです。必ず日本語のみを使用して返答してください。絶対に英語や他の言語を使わないでください。"},
                {"role": "user", "content": prompt}
            ],
            temperature=0.7,
        )

        advice_text = response.choices[0].message.content

        return jsonify({
            "status": "success",
            "advice": advice_text
        })

    except Exception as e:
        return jsonify({
            "status": "error",
            "message": str(e)
        }), 500

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5001, debug=True)