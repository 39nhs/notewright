"""WAV header reading (any RIFF/WAVE incl. float and EXTENSIBLE) and a mono 16-bit writer for generated instruments."""
import struct
import wave
from pathlib import Path


def info(path) -> dict:
    with open(path, "rb") as f:
        if f.read(4) != b"RIFF":
            raise ValueError(f"{path}: not a RIFF/WAVE file")
        f.read(4)
        if f.read(4) != b"WAVE":
            raise ValueError(f"{path}: not a WAVE file")
        fmt = data = None
        while True:
            head = f.read(8)
            if len(head) < 8:
                break
            chunk, size = head[:4], struct.unpack("<I", head[4:])[0]
            if chunk == b"fmt ":
                body = f.read(size)
                tag, channels, rate, _, _, bits = struct.unpack("<HHIIHH", body[:16])
                if tag == 0xFFFE and len(body) >= 26:
                    tag = struct.unpack("<H", body[24:26])[0]
                fmt = (tag, channels, rate, bits)
                f.seek(size & 1, 1)
            elif chunk == b"data":
                data = size
                break
            else:
                f.seek(size + (size & 1), 1)
    if not fmt or data is None:
        raise ValueError(f"{path}: missing fmt/data chunk")
    tag, channels, rate, bits = fmt
    if (tag, bits) not in {(1, 8), (1, 16), (1, 24), (1, 32), (3, 32), (3, 64)}:
        raise ValueError(f"{path}: unsupported WAV encoding (format {tag}, {bits}-bit); use PCM or float WAV")
    frames = data // max(1, channels * bits // 8)
    return {"channels": channels, "rate": rate, "bits": bits, "float": tag == 3, "frames": frames, "seconds": frames / rate}


def write_mono16(path, samples, rate=44100):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    peak = max(1e-9, max(abs(s) for s in samples))
    scale = 0.89 * 32767 / peak  # -1 dBFS
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(struct.pack(f"<{len(samples)}h", *(int(round(s * scale)) for s in samples)))
