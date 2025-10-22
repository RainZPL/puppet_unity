#!/usr/bin/env python3
"""
Real-time gesture recognition with top-3 predictions.

Following PDF guidance:
- Whole movement trend recognition
- Top-3 predictions with confidence intervals
- Relative/normalized features
"""
import json
import numpy as np
import torch
from pathlib import Path
from collections import deque
import sys

sys.path.append(str(Path(__file__).parent))

from models.bilstm_trend import build_bilstm_trend_model, predict_top_k


def normalize_landmarks_enhanced(landmarks):
    """Enhanced normalization following PDF."""
    if landmarks is None:
        return None

    landmarks = landmarks.copy()

    # Center by wrist
    wrist = landmarks[0:1, :2]
    landmarks[:, :2] -= wrist

    # Scale by hand size
    finger_bases = [5, 9, 13, 17]
    base_distances = [np.linalg.norm(landmarks[i] - landmarks[0]) for i in finger_bases]
    hand_size = np.mean(base_distances) + 1e-6

    landmarks = landmarks / hand_size

    return landmarks


def extract_relative_features_single_frame(landmarks):
    """
    Extract relative features from single frame.

    MUST match prepare_dataset_enhanced.py feature extraction:
    - 5 fingertip distances from palm
    - 10 joint angles (5 fingers × 2 angles each)
    - 4 inter-fingertip distances
    - 1 velocity (placeholder, computed from buffer)
    Total: 20 features
    """
    features = []

    # Palm center
    palm_indices = [0, 5, 9, 13, 17]
    palm_center = np.mean(landmarks[palm_indices], axis=0)

    # 1. Fingertip distances from palm (5 features)
    fingertips = [4, 8, 12, 16, 20]
    for tip_idx in fingertips:
        dist = np.linalg.norm(landmarks[tip_idx] - palm_center)
        features.append(dist)

    # 2. Joint angles (10 features: 5 fingers × 2 angles each)
    # Each finger has 4 joints, so len(chain)-2 = 2 angles
    finger_chains = [
        [1, 2, 3, 4],     # Thumb
        [5, 6, 7, 8],     # Index
        [9, 10, 11, 12],  # Middle
        [13, 14, 15, 16], # Ring
        [17, 18, 19, 20], # Pinky
    ]

    for chain in finger_chains:
        # This loop runs 2 times per finger (i=0, i=1)
        for i in range(len(chain) - 2):
            v1 = landmarks[chain[i]] - landmarks[chain[i+1]]
            v2 = landmarks[chain[i+2]] - landmarks[chain[i+1]]

            v1_norm = np.linalg.norm(v1) + 1e-8
            v2_norm = np.linalg.norm(v2) + 1e-8
            v1 = v1 / v1_norm
            v2 = v2 / v2_norm

            cos_angle = np.dot(v1, v2)
            cos_angle = np.clip(cos_angle, -1.0, 1.0)
            angle = np.arccos(cos_angle)

            features.append(angle)

    # 3. Inter-fingertip distances (4 features)
    for i in range(len(fingertips) - 1):
        dist = np.linalg.norm(landmarks[fingertips[i]] - landmarks[fingertips[i+1]])
        features.append(dist)

    # 4. Velocity placeholder (1 feature, will be computed from buffer)
    features.append(0.0)

    return np.array(features, dtype=np.float32)


class RealtimeEnhancedDetector:
    """Real-time detector with top-3 predictions."""

    def __init__(self, model, label_map, device, buffer_size=100, confidence_thresh=0.6, output_file=None):
        self.model = model
        self.label_map = label_map
        self.device = device
        self.buffer_size = buffer_size
        self.confidence_thresh = confidence_thresh
        self.output_file = output_file

        self.feature_buffer = deque(maxlen=buffer_size)
        self.cooldown_frames = 0

        self.top3_predictions = []
        self.top3_probabilities = []
        self.model_confidence = 0.0

    def process_frame(self, landmarks):
        """Process one frame and return top-3 predictions."""
        # Normalize and extract features
        if landmarks is not None:
            normalized = normalize_landmarks_enhanced(landmarks)
            features = extract_relative_features_single_frame(normalized)
        else:
            features = np.zeros(20, dtype=np.float32)  # Placeholder (20 features)

        self.feature_buffer.append(features)

        # Decrement cooldown
        if self.cooldown_frames > 0:
            self.cooldown_frames -= 1
            return {
                'top3_classes': self.top3_predictions,
                'top3_probs': self.top3_probabilities,
                'model_confidence': self.model_confidence,
                'buffer_fill': len(self.feature_buffer) / self.buffer_size
            }

        # Wait for buffer
        if len(self.feature_buffer) < self.buffer_size * 0.5:
            return {
                'top3_classes': [],
                'top3_probs': [],
                'model_confidence': 0.0,
                'buffer_fill': len(self.feature_buffer) / self.buffer_size
            }

        # Prepare input - IMPORTANT: ensure float32 dtype
        features_seq = np.array(list(self.feature_buffer), dtype=np.float32)  # [T, D]

        # Compute velocities
        velocities = np.zeros(len(features_seq), dtype=np.float32)
        if len(features_seq) > 1:
            palm_features = features_seq[:, :5]  # First 5 are fingertip distances
            palm_velocity = np.linalg.norm(np.diff(palm_features, axis=0), axis=1)
            velocities[1:] = palm_velocity
        features_seq[:, -1] = velocities

        # Pad to max_len
        max_len = 100
        T = len(features_seq)
        if T < max_len:
            pad_len = max_len - T
            features_seq = np.vstack([features_seq, np.zeros((pad_len, features_seq.shape[1]), dtype=np.float32)])
            mask = np.concatenate([np.ones(T, dtype=np.float32), np.zeros(pad_len, dtype=np.float32)])
        else:
            features_seq = features_seq[:max_len]
            mask = np.ones(max_len, dtype=np.float32)

        # Convert to tensor - explicitly use .float() to ensure float32
        features_tensor = torch.from_numpy(features_seq).float().unsqueeze(0).to(self.device)
        mask_tensor = torch.from_numpy(mask).float().unsqueeze(0).to(self.device)

        # Get top-3 predictions
        top_classes, top_probs, conf = predict_top_k(
            self.model, features_tensor, mask_tensor, k=3
        )

        top_classes = top_classes[0].cpu().numpy()
        top_probs = top_probs[0].cpu().numpy()
        conf = conf[0].item()

        self.top3_predictions = [int(c) for c in top_classes]
        self.top3_probabilities = [float(p) for p in top_probs]
        self.model_confidence = conf

        # Trigger cooldown if confident
        if top_probs[0] >= self.confidence_thresh and conf >= 0.5:
            self.cooldown_frames = 30

        # Save to file if specified
        if self.output_file is not None:
            self.save_result_to_file()

        return {
            'top3_classes': self.top3_predictions,
            'top3_probs': self.top3_probabilities,
            'model_confidence': self.model_confidence,
            'buffer_fill': 1.0
        }

    def save_result_to_file(self):
        """Save current detection result to text file."""
        import datetime

        with open(self.output_file, 'w') as f:
            # Write timestamp
            timestamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            f.write(f"timestamp: {timestamp}\n")

            # Write top-3 predictions
            if self.top3_predictions:
                for i, (cls, prob) in enumerate(zip(self.top3_predictions, self.top3_probabilities)):
                    gesture_name = self.label_map[cls]
                    f.write(f"prediction_{i+1}: {gesture_name}\n")
                    f.write(f"probability_{i+1}: {prob:.4f}\n")
            else:
                f.write("prediction_1: none\n")
                f.write("probability_1: 0.0000\n")

            # Write confidence
            f.write(f"confidence: {self.model_confidence:.4f}\n")

            # Write status
            status = "high" if self.model_confidence >= 0.6 else "low"
            f.write(f"status: {status}\n")
