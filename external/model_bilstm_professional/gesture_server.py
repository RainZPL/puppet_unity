#!/usr/bin/env python3
"""
Unity ↔ Python bridge server for fixed-window hand gesture inference.

Unity sends 21 hand landmarks per frame and batches them into a window.  This
server converts the window into model features and returns the BiLSTM prediction.
"""

import json
import socket
import sys
from pathlib import Path
from typing import Optional, Tuple

import numpy as np
import torch

ROOT = Path(__file__).parent.resolve()
SRC_DIR = ROOT / "src"
if str(SRC_DIR) not in sys.path:
    sys.path.append(str(SRC_DIR))

from realtime_enhanced import (
    normalize_landmarks_enhanced,
    extract_relative_features_single_frame,
)
from models.bilstm_trend import build_bilstm_trend_model

HOST = "127.0.0.1"
PORT = 50007
DEFAULT_MAX_LEN = 100


def load_model(
    checkpoint_path: Path,
    label_map_path: Optional[Path] = None,
) -> Tuple[torch.nn.Module, dict, torch.device, int, int]:
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    checkpoint = torch.load(checkpoint_path, map_location=device)

    feature_dim = int(checkpoint.get("feature_dim", 20))
    max_len = int(checkpoint.get("max_len", DEFAULT_MAX_LEN))

    if label_map_path is None:
        label_map_path = checkpoint_path.parent.parent / "label_map.json"
        if not label_map_path.exists():
            label_map_path = ROOT / "artifacts" / "label_map.json"

    if not label_map_path.exists():
        raise FileNotFoundError(f"Label map not found at {label_map_path}")

    with label_map_path.open("r", encoding="utf-8") as f:
        label_map = json.load(f)
    label_map = {int(k): v for k, v in label_map.items()}

    num_classes = int(checkpoint.get("num_classes", len(label_map)))

    model = build_bilstm_trend_model(input_dim=feature_dim, num_classes=num_classes)
    model.load_state_dict(checkpoint["model_state_dict"])
    model = model.to(device)
    model.eval()

    return model, label_map, device, feature_dim, max_len


def build_feature_sequence(frames: np.ndarray, feature_dim: int, max_len: int):
    """Convert a window of landmarks into padded feature sequence and mask."""
    if frames.size == 0:
        return None, None

    features = []
    for frame in frames:
        normalized = normalize_landmarks_enhanced(frame)
        if normalized is None:
            features.append(np.zeros(feature_dim, dtype=np.float32))
        else:
            features.append(extract_relative_features_single_frame(normalized))

    if not features:
        return None, None

    seq = np.asarray(features, dtype=np.float32)

    velocities = np.zeros(len(seq), dtype=np.float32)
    if len(seq) > 1:
        palm = seq[:, :5]
        palm_velocity = np.linalg.norm(np.diff(palm, axis=0), axis=1)
        velocities[1:] = palm_velocity
    seq[:, -1] = velocities

    length = len(seq)
    if length < max_len:
        pad_len = max_len - length
        seq = np.vstack([seq, np.zeros((pad_len, seq.shape[1]), dtype=np.float32)])
        mask = np.concatenate(
            [np.ones(length, dtype=np.float32), np.zeros(pad_len, dtype=np.float32)]
        )
    else:
        seq = seq[-max_len:]
        mask = np.ones(max_len, dtype=np.float32)

    return seq, mask


def predict_window(frames, model, device, feature_dim, max_len):
    frames = np.asarray(frames, dtype=np.float32)
    if frames.ndim != 3 or frames.shape[1:] != (21, 3):
        return None

    seq, mask = build_feature_sequence(frames, feature_dim, max_len)
    if seq is None:
        return None

    feat_tensor = torch.from_numpy(seq).unsqueeze(0).to(device)
    mask_tensor = torch.from_numpy(mask).unsqueeze(0).to(device)

    with torch.no_grad():
        logits, conf, _ = model(feat_tensor, mask_tensor)
        probs = torch.softmax(logits, dim=1)

    probabilities = probs[0].detach().cpu().numpy()
    top_index = int(np.argmax(probabilities))
    top_prob = float(probabilities[top_index])
    confidence = float(conf.detach().view(-1).cpu().item())

    return top_index, top_prob, confidence, probabilities


def handle_client(conn: socket.socket, model, label_map, device, feature_dim, max_len) -> None:
    file = conn.makefile("rwb")
    print("Client connected.")

    try:
        while True:
            chunk = file.readline()
            if not chunk:
                print("Client disconnected.")
                break

            line = chunk.decode("utf-8").strip()
            if not line:
                continue

            try:
                payload = json.loads(line)
            except json.JSONDecodeError:
                print(f"Invalid JSON received: {line}")
                continue

            if payload.get("command") == "stop":
                continue

            sequence = payload.get("sequence")
            if not sequence:
                continue

            target_label = payload.get("target_label")

            frame_count = int(payload.get("frame_count", len(sequence)))
            duration_seconds = float(payload.get("duration", 0.0))

            prediction = predict_window(
                sequence, model, device, feature_dim, max_len
            )
            if prediction is None:
                continue

            top_index, top_prob, confidence, probabilities = prediction

            top1_label = label_map.get(top_index, str(top_index))

            target_prob = top_prob
            matched_target = False

            if isinstance(target_label, str) and target_label:
                target_prob = 0.0
                for cls_id, name in label_map.items():
                    if isinstance(name, str) and name.lower() == target_label.lower():
                        target_prob = float(probabilities[int(cls_id)])
                        matched_target = cls_id == top_index
                        break

            response = {
                "top1": top1_label,
                "top1_prob": top_prob,
                "target_label": target_label or "",
                "prob": target_prob,
                "confidence": confidence,
                "match": bool(matched_target),
            }

            file.write((json.dumps(response) + "\n").encode("utf-8"))
            file.flush()

            print(
                f"Window processed: frames={frame_count}, duration={duration_seconds:.2f}s, "
                f"top1={top1_label}, top1_prob={top_prob:.2f}, target_prob={target_prob:.2f}, conf={confidence:.2f}"
            )
    finally:
        file.close()
        conn.close()
        print("Closed connection.")


def main() -> None:
    checkpoint = ROOT / "artifacts" / "checkpoints" / "best.pt"
    model, label_map, device, feature_dim, max_len = load_model(checkpoint)

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((HOST, PORT))
        server.listen(1)
        print(f"Gesture server listening on {HOST}:{PORT}")

        try:
            while True:
                try:
                    conn, _ = server.accept()
                except KeyboardInterrupt:
                    print("Keyboard interrupt received. Shutting down server.")
                    break

                try:
                    handle_client(conn, model, label_map, device, feature_dim, max_len)
                except Exception as exc:
                    print(f"Error while handling client: {exc}")
        except KeyboardInterrupt:
            print("Keyboard interrupt received. Shutting down server.")


if __name__ == "__main__":
    main()
