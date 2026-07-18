"""Memory-only stdin/stdout adapter for the local PaddleOCR 3 pipeline."""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import sys


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lang", required=True)
    parser.add_argument("--ocr-version", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--min-confidence", required=True, type=float)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    image_bytes = sys.stdin.buffer.read()
    if not image_bytes:
        raise ValueError("empty image input")

    # Biblioteki mogą wypisywać komunikaty inicjalizacji. Protokół stdout musi
    # pozostać pojedynczym obiektem JSON, więc diagnostyka trafia na stderr.
    with contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        from PIL import Image
        from paddleocr import PaddleOCR

        with Image.open(io.BytesIO(image_bytes)) as source:
            image = np.asarray(source.convert("RGB"))

        ocr = PaddleOCR(
            lang=args.lang,
            ocr_version=args.ocr_version,
            device=args.device,
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
        )
        results = ocr.predict(image, text_rec_score_thresh=args.min_confidence)

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
            if text and score >= args.min_confidence:
                lines.append(text)
                scores.append(score)

    response = {
        "text": "\n".join(lines),
        "line_count": len(lines),
        "mean_confidence": sum(scores) / len(scores) if scores else None,
    }
    json.dump(response, sys.stdout, ensure_ascii=False, separators=(",", ":"))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"PaddleOCR adapter failed: {error}", file=sys.stderr)
        raise SystemExit(1)
