#!/usr/bin/env python3
"""Export the BiLSTM gesture model to ONNX for Unity Sentis/Barracuda."""
import json
from pathlib import Path

import torch

ROOT = Path(__file__).parent.resolve()
SRC_DIR = ROOT / "src"
CHECKPOINT = ROOT / "artifacts" / "checkpoints" / "best.pt"
DEFAULT_EXPORT = ROOT / "artifacts" / "hand_gesture.onnx"


def main():
    import sys

    if str(SRC_DIR) not in sys.path:
        sys.path.append(str(SRC_DIR))

    from models.bilstm_trend import build_bilstm_trend_model

    device = torch.device("cpu")
    ckpt = torch.load(CHECKPOINT, map_location=device)

    feature_dim = int(ckpt.get("feature_dim", 20))
    max_len = int(ckpt.get("max_len", 100))
    num_classes = int(ckpt.get("num_classes", 0))

    if num_classes <= 0:
        label_map_path = CHECKPOINT.parent.parent / "label_map.json"
        if label_map_path.exists():
            with label_map_path.open("r", encoding="utf-8") as f:
                label_map = json.load(f)
            num_classes = len(label_map)
        else:
            raise RuntimeError("Failed to determine number of classes.")

    model = build_bilstm_trend_model(input_dim=feature_dim, num_classes=num_classes)
    model.load_state_dict(ckpt["model_state_dict"])
    if hasattr(model, "use_packed_sequence"):
        model.use_packed_sequence = False
    model.eval()

    dummy_seq = torch.zeros((1, max_len, feature_dim), dtype=torch.float32)
    dummy_mask = torch.ones((1, max_len), dtype=torch.float32)

    export_path = DEFAULT_EXPORT

    torch.onnx.export(
        model,
        (dummy_seq, dummy_mask),
        export_path.as_posix(),
        input_names=["input_seq", "input_mask"],
        output_names=["logits", "confidence", "hidden"],
        dynamic_axes={
            "input_seq": {1: "seq_len"},
            "input_mask": {1: "seq_len"},
            "logits": {1: "seq_len"},
        },
        opset_version=13,
    )

    print(f"Exported ONNX model to {export_path}")


if __name__ == "__main__":
    main()
