#!/usr/bin/env python3
# quick and messy server version that mirrors the main one but keeps the tone simple

import json
import socket
import sys
from pathlib import Path

import numpy as np
import torch

ROOT = Path(__file__).parent.resolve()
SRC_DIR = ROOT / "src"
if str(SRC_DIR) not in sys.path:
    sys.path.append(str(SRC_DIR))

from realtime_enhanced import normalize_landmarks_enhanced, extract_relative_features_single_frame
from models.bilstm_trend import build_bilstm_trend_model

HOST = "127.0.0.1"
PORT = 50007
DEFAULT_MAX = 100


def load_model():
    ckpt_path = ROOT / "artifacts" / "checkpoints" / "best.pt"
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    data = torch.load(ckpt_path, map_location=device)

    feature_dim = int(data.get("feature_dim", 20))
    max_len = int(data.get("max_len", DEFAULT_MAX))

    label_path = ckpt_path.parent.parent / "label_map.json"
    if not label_path.exists():
        label_path = ROOT / "artifacts" / "label_map.json"
    with label_path.open("r", encoding="utf-8") as f:
        labels_raw = json.load(f)
    labels = {int(k): v for k, v in labels_raw.items()}

    num_cls = int(data.get("num_classes", len(labels)))
    model = build_bilstm_trend_model(input_dim=feature_dim, num_classes=num_cls)
    model.load_state_dict(data["model_state_dict"])
    model = model.to(device)
    model.eval()

    return model, labels, device, feature_dim, max_len


def make_feature_seq(frames, feature_dim, max_len):
    entries = []
    for frm in frames:
        cleaned = normalize_landmarks_enhanced(frm)
        if cleaned is None:
            entries.append(np.zeros(feature_dim, dtype=np.float32))
        else:
            entries.append(extract_relative_features_single_frame(cleaned))

    if not entries:
        return None, None

    seq = np.asarray(entries, dtype=np.float32)
    vel = np.zeros(len(seq), dtype=np.float32)
    if len(seq) > 1:
        palm = seq[:, :5]
        vel[1:] = np.linalg.norm(np.diff(palm, axis=0), axis=1)
    seq[:, -1] = vel

    if len(seq) < max_len:
        pad = max_len - len(seq)
        seq = np.vstack([seq, np.zeros((pad, seq.shape[1]), dtype=np.float32)])
        mask = np.concatenate([np.ones(len(seq) - pad, dtype=np.float32), np.zeros(pad, dtype=np.float32)])
    else:
        seq = seq[-max_len:]
        mask = np.ones(max_len, dtype=np.float32)

    return seq, mask


def predict(frames, model, device, feature_dim, max_len):
    frames = np.asarray(frames, dtype=np.float32)
    if frames.ndim != 3 or frames.shape[1:] != (21, 3):
        return None

    seq, mask = make_feature_seq(frames, feature_dim, max_len)
    if seq is None:
        return None

    seq_tensor = torch.from_numpy(seq).unsqueeze(0).to(device)
    mask_tensor = torch.from_numpy(mask).unsqueeze(0).to(device)

    with torch.no_grad():
        logits, conf, _ = model(seq_tensor, mask_tensor)
        probs = torch.softmax(logits, dim=1)

    arr = probs[0].cpu().numpy()
    idx = int(np.argmax(arr))
    top_prob = float(arr[idx])
    confidence = float(conf.detach().view(-1).cpu().item())
    return idx, top_prob, confidence, arr


def handle_client(conn, model, labels, device, feature_dim, max_len):
    stream = conn.makefile("rwb")
    print("Client connected.")
    try:
        while True:
            raw = stream.readline()
            if not raw:
                print("Client left.")
                break

            line = raw.decode("utf-8").strip()
            if not line:
                continue

            try:
                data = json.loads(line)
            except json.JSONDecodeError:
                print("Bad JSON:", line)
                continue

            if data.get("command") == "stop":
                continue

            seq = data.get("sequence")
            if not seq:
                continue

            target_name = data.get("target_label", "")

            guess = predict(seq, model, device, feature_dim, max_len)
            if guess is None:
                continue

            top_id, top_prob, conf, prob_list = guess
            top_name = labels.get(top_id, str(top_id))

            target_prob = top_prob
            matched = False
            if isinstance(target_name, str) and target_name:
                for key, val in labels.items():
                    if isinstance(val, str) and val.lower() == target_name.lower():
                        target_prob = float(prob_list[int(key)])
                        matched = int(key) == top_id
                        break

            reply = {
                "top1": top_name,
                "top1_prob": top_prob,
                "target_label": target_name,
                "prob": target_prob,
                "confidence": conf,
                "match": bool(matched),
            }

            stream.write((json.dumps(reply) + "\n").encode("utf-8"))
            stream.flush()

            print("Window processed:", top_name, top_prob, conf)
    finally:
        stream.close()
        conn.close()
        print("Connection closed.")


def main():
    model, labels, device, feature_dim, max_len = load_model()
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as srv:
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind((HOST, PORT))
        srv.listen(1)
        print("Basic server listening on", HOST, PORT)

        try:
            while True:
                try:
                    conn, _ = srv.accept()
                except KeyboardInterrupt:
                    print("Stopping server.")
                    break

                try:
                    handle_client(conn, model, labels, device, feature_dim, max_len)
                except Exception as exc:
                    print("Error while handling client:", exc)
        except KeyboardInterrupt:
            print("Server stopped.")


if __name__ == "__main__":
    main()
