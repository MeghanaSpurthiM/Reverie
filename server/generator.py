"""
generator.py: ambient audio for bake_theme.py.

AUDIO_MODE selects the audio source:
  audioldm2   AudioLDM 2, as in the paper (default). Needs the reverie-audio conda env.
  mock        a quiet 60 Hz tone. Needs no GPU or extra packages, for testing the pipeline.
"""
import array
import logging
import math
import os
import struct
import time

log = logging.getLogger(__name__)

AUDIO_MODE = os.environ.get("AUDIO_MODE", "audioldm2")
NEGATIVE_PROMPT = ("music, instruments, human voices, speech, talking, crowd, "
                   "dialogue, singing, thunder, storm, distortion, static, noise")


def check_environment():
    """Raises ImportError now if this mode's packages are missing, not after hours of training."""
    if AUDIO_MODE == "audioldm2":
        import diffusers  # noqa: F401
        import scipy  # noqa: F401
        import torch  # noqa: F401


class EnvironmentGenerator:

    def __init__(self):
        self._audio_pipeline = None

    @staticmethod
    def _device():
        import torch
        return "cuda" if torch.cuda.is_available() else "cpu"

    def _load_audio_pipeline(self):
        """Loads the audio model on first use."""
        if self._audio_pipeline is not None or AUDIO_MODE == "mock":
            return
        import torch
        from diffusers import AudioLDM2Pipeline
        log.info("Loading AudioLDM2 pipeline...")
        model_id = os.environ.get("AUDIOLDM2_MODEL_ID", "cvssp/audioldm2")
        pipe = AudioLDM2Pipeline.from_pretrained(model_id, torch_dtype=torch.float16)
        self._audio_pipeline = pipe.to(self._device())

    def _generate_audio(self, prompt, output_path, progress_callback):
        if AUDIO_MODE == "mock":
            self._mock_audio(output_path, progress_callback)
        else:
            self._audioldm2_audio(prompt, output_path, progress_callback)

    def _mock_audio(self, output_path, progress_callback):
        progress_callback(0.2)
        sample_rate = 44100
        num_samples = sample_rate * 10
        samples = array.array("h", [int(500 * math.sin(2 * math.pi * 60 * i / sample_rate))
                                    for i in range(num_samples)])
        with open(output_path, "wb") as f:
            data_size = num_samples * 2
            f.write(b"RIFF" + struct.pack("<I", 36 + data_size) + b"WAVEfmt ")
            f.write(struct.pack("<IHHIIHH", 16, 1, 1, sample_rate, sample_rate * 2, 2, 16))
            f.write(b"data" + struct.pack("<I", data_size) + samples.tobytes())
        time.sleep(0.5)
        progress_callback(1.0)

    def _audioldm2_audio(self, prompt, output_path, progress_callback):
        import numpy as np
        import scipy.io.wavfile
        from scipy.signal import resample_poly

        progress_callback(0.1)
        audio = self._audio_pipeline(
            prompt,
            negative_prompt=NEGATIVE_PROMPT,
            num_inference_steps=200,
            audio_length_in_s=10.0,
        ).audios[0]   # float32, shape (samples,), range [-1, 1]
        progress_callback(0.9)

        audio = audio.astype(np.float64)
        max_val = np.abs(audio).max()
        if max_val > 0:
            audio = audio / max_val * 0.9

        # Resample 16 kHz to 44.1 kHz, then normalize again because resampling alters the amplitude.
        gcd = math.gcd(44100, 16000)
        audio = resample_poly(audio, 44100 // gcd, 16000 // gcd)
        max_val = np.abs(audio).max()
        if max_val > 0:
            audio = audio / max_val * 0.9

        audio_int16 = (audio * 32767).astype(np.int16)
        scipy.io.wavfile.write(output_path, rate=44100, data=audio_int16)
        log.info(f"AudioLDM2 audio saved to {output_path} ({len(audio_int16) / 44100:.1f} s, 44.1 kHz, 16-bit)")
