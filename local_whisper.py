"""Offline dictation worker. One JSON request and one JSON reply per line."""
import base64
import ctypes
import io
import json
import os
import re
import sys

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"


def simplified(text):
    if not text:
        return text
    out = ctypes.create_unicode_buffer(len(text.encode("utf-16-le")) // 2 + 1)
    count = ctypes.windll.kernel32.LCMapStringEx(
        "zh-CN", 0x02000000, text, -1, out, len(out), None, None, 0)
    return out.value if count else text


def main():
    import numpy as np
    from faster_whisper import WhisperModel
    from faster_whisper.audio import decode_audio
    from faster_whisper.vad import get_speech_timestamps, get_vad_model, VadOptions

    class DictationModel(WhisperModel):
        # For long recordings, automatic language detection and decoding normally
        # encode the very same first 30 seconds. Reuse only byte-equivalent input.
        remember_first = False
        first_window = None

        def encode(self, features):
            if self.first_window is not None:
                original, encoded = self.first_window
                self.first_window = None
                if np.array_equal(original, features):
                    return encoded
            encoded = super().encode(features)
            if self.remember_first:
                self.first_window = (features.copy(), encoded)
                self.remember_first = False
            return encoded

    model = DictationModel("small", device="cpu", compute_type="int8", cpu_threads=6,
                         local_files_only=True)
    get_vad_model()
    for line in sys.stdin.buffer:
        try:
            item = json.loads(line)
            wav = io.BytesIO(base64.b64decode(item["wave"], validate=True))
            audio = decode_audio(wav)
            text = ""
            if get_speech_timestamps(audio, VadOptions(min_speech_duration_ms=140)):
                # Gate silence but keep the entire utterance. Trimming individual VAD spans
                # can discard a short English product name between two Chinese phrases.
                words = [w.strip() for w in re.split(r"[,，、\r\n]+", item["words"]) if w.strip()]
                model.remember_first = len(audio) >= 30 * 16000
                segments, _ = model.transcribe(audio, task="transcribe", beam_size=5,
                    temperature=0.0, hotwords=", ".join(words) or None, condition_on_previous_text=False)
                text = simplified("".join(s.text for s in segments).strip())
                for word in words:
                    if word.isascii():
                        text = re.sub(r"(?<![A-Za-z0-9])" + re.escape(word) + r"(?![A-Za-z0-9])",
                                      lambda _: word, text, flags=re.I)
                if re.search(r"[\u3400-\u9fff]", text) and text[-1:].isalnum():
                    text += "。"
            result = {"text": text}
        except Exception:
            result = {"error": "本地模型无法处理这段录音。请重试。"}
        finally:
            model.first_window = None
            model.remember_first = False
        print(json.dumps(result), flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print(json.dumps({"error": "无法载入本机 faster-whisper small。请检查 Python 和离线模型缓存。"}), flush=True)
