"""Persistent memory-only stdin/stdout adapter for the local PaddleOCR 3 pipeline.

Protocol: each request is a 4-byte big-endian length prefix followed by the
image bytes on stdin; each response is a single strict-JSON line on stdout.
EOF (or a zero-length frame) ends the session. The model is loaded once per
process, so callers can reuse one runner for every page of a document.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import sys

MAX_IMAGE_BYTES = 64 * 1024 * 1024


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lang", required=True)
    parser.add_argument("--ocr-version", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--min-confidence", required=True, type=float)
    return parser.parse_args()


@contextlib.contextmanager
def redirect_native_stdout_to_stderr():
    """Keep native Paddle/oneDNN writes away from the JSON stdout protocol."""
    sys.stdout.flush()
    saved_stdout = os.dup(sys.stdout.fileno())
    try:
        os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
        yield
    finally:
        sys.stdout.flush()
        sys.stderr.flush()
        os.dup2(saved_stdout, sys.stdout.fileno())
        os.close(saved_stdout)


def read_exact(stream, count: int) -> bytes:
    data = bytearray()
    while len(data) < count:
        chunk = stream.read(count - len(data))
        if not chunk:
            break
        data.extend(chunk)
    return bytes(data)


def write_response(payload: dict) -> None:
    encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    sys.stdout.buffer.write(encoded + b"\n")
    sys.stdout.buffer.flush()


def recognize(ocr, image_bytes: bytes, min_confidence: float) -> dict:
    with redirect_native_stdout_to_stderr(), contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        from PIL import Image

        with Image.open(io.BytesIO(image_bytes)) as source:
            image = np.asarray(source.convert("RGB"))
        results = ocr.predict(image, text_rec_score_thresh=min_confidence)

    lines: list[str] = []
    scores: list[float] = []
    for result in results:
        payload = result.json
        data = payload.get("res", payload)
        texts = data.get("rec_texts", [])
        confidence = data.get("rec_scores", [])
        for index, value in enumerate(texts):
            text = str(value).strip()
            score = float(confidence[index]) if index < len(confidence) else 0.0
            if text and score >= min_confidence:
                lines.append(text)
                scores.append(score)

    return {
        "text": "\n".join(lines),
        "line_count": len(lines),
        "mean_confidence": sum(scores) / len(scores) if scores else None,
    }


def main() -> int:
    args = parse_args()

    # Biblioteki mogą wypisywać komunikaty inicjalizacji. Protokół stdout musi
    # pozostać strumieniem pojedynczych obiektów JSON (jedna linia na obraz),
    # więc diagnostyka trafia na stderr. Model ładuje się raz na proces.
    with redirect_native_stdout_to_stderr(), contextlib.redirect_stdout(sys.stderr):
        from paddleocr import PaddleOCR

        ocr = PaddleOCR(
            lang=args.lang,
            ocr_version=args.ocr_version,
            device=args.device,
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
        )

    stdin = sys.stdin.buffer
    while True:
        header = read_exact(stdin, 4)
        if not header:
            break
        if len(header) < 4:
            raise ValueError("truncated frame header")
        length = int.from_bytes(header, "big")
        if length == 0:
            break
        if length > MAX_IMAGE_BYTES:
            raise ValueError(f"image frame of {length} bytes exceeds the {MAX_IMAGE_BYTES} byte limit")
        image_bytes = read_exact(stdin, length)
        if len(image_bytes) < length:
            raise ValueError("truncated image frame")
        try:
            response = recognize(ocr, image_bytes, args.min_confidence)
        except Exception as error:
            # Błąd pojedynczego obrazu nie kończy procesu — kolejne strony
            # nadal korzystają z załadowanego modelu.
            response = {"error": f"{type(error).__name__}: {error}"}
        write_response(response)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"PaddleOCR adapter failed: {error}", file=sys.stderr)
        raise SystemExit(1)
