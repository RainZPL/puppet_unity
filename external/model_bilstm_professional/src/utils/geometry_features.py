#!/usr/bin/env python3
"""
Relative geometry feature extraction for amateur/professional variability handling.

Implements:
- Joint angles (finger flexion/extension)
- Bone direction vectors
- Velocity and acceleration features
- Motion quality metrics (jerk, smoothness)
"""
import numpy as np
from scipy.spatial.distance import euclidean


# MediaPipe hand landmark indices
WRIST = 0
THUMB_CMC, THUMB_MCP, THUMB_IP, THUMB_TIP = 1, 2, 3, 4
INDEX_MCP, INDEX_PIP, INDEX_DIP, INDEX_TIP = 5, 6, 7, 8
MIDDLE_MCP, MIDDLE_PIP, MIDDLE_DIP, MIDDLE_TIP = 9, 10, 11, 12
RING_MCP, RING_PIP, RING_DIP, RING_TIP = 13, 14, 15, 16
PINKY_MCP, PINKY_PIP, PINKY_DIP, PINKY_TIP = 17, 18, 19, 20


# Bone connections (parent -> child)
HAND_BONES = [
    (WRIST, THUMB_CMC), (THUMB_CMC, THUMB_MCP), (THUMB_MCP, THUMB_IP), (THUMB_IP, THUMB_TIP),
    (WRIST, INDEX_MCP), (INDEX_MCP, INDEX_PIP), (INDEX_PIP, INDEX_DIP), (INDEX_DIP, INDEX_TIP),
    (WRIST, MIDDLE_MCP), (MIDDLE_MCP, MIDDLE_PIP), (MIDDLE_PIP, MIDDLE_DIP), (MIDDLE_DIP, MIDDLE_TIP),
    (WRIST, RING_MCP), (RING_MCP, RING_PIP), (RING_PIP, RING_DIP), (RING_DIP, RING_TIP),
    (WRIST, PINKY_MCP), (PINKY_MCP, PINKY_PIP), (PINKY_PIP, PINKY_DIP), (PINKY_DIP, PINKY_TIP),
]


def compute_bone_vectors(landmarks):
    """
    Compute bone direction vectors.

    Args:
        landmarks: [T, 21, 3] numpy array

    Returns:
        bones: [T, 20, 3] bone vectors
    """
    T = landmarks.shape[0]
    bones = np.zeros((T, len(HAND_BONES), 3))

    for i, (parent, child) in enumerate(HAND_BONES):
        bones[:, i] = landmarks[:, child] - landmarks[:, parent]

    return bones


def compute_joint_angles(landmarks):
    """
    Compute joint angles (flexion/extension).

    For each finger joint, compute angle between two consecutive bones.

    Args:
        landmarks: [T, 21, 3] numpy array

    Returns:
        angles: [T, N_angles] numpy array
    """
    T = landmarks.shape[0]

    # Finger chains (excluding thumb for simplicity)
    finger_chains = [
        [INDEX_MCP, INDEX_PIP, INDEX_DIP, INDEX_TIP],
        [MIDDLE_MCP, MIDDLE_PIP, MIDDLE_DIP, MIDDLE_TIP],
        [RING_MCP, RING_PIP, RING_DIP, RING_TIP],
        [PINKY_MCP, PINKY_PIP, PINKY_DIP, PINKY_TIP],
    ]

    angles = []

    for chain in finger_chains:
        for i in range(len(chain) - 2):
            # Compute angle at joint chain[i+1]
            v1 = landmarks[:, chain[i]] - landmarks[:, chain[i+1]]  # [T, 3]
            v2 = landmarks[:, chain[i+2]] - landmarks[:, chain[i+1]]  # [T, 3]

            # Normalize
            v1_norm = np.linalg.norm(v1, axis=1, keepdims=True) + 1e-8
            v2_norm = np.linalg.norm(v2, axis=1, keepdims=True) + 1e-8
            v1 = v1 / v1_norm
            v2 = v2 / v2_norm

            # Compute angle via dot product
            cos_angle = np.sum(v1 * v2, axis=1)
            cos_angle = np.clip(cos_angle, -1.0, 1.0)
            angle = np.arccos(cos_angle)  # [T]

            angles.append(angle)

    angles = np.stack(angles, axis=1)  # [T, N_angles]
    return angles


def compute_velocities(landmarks, fps=30):
    """
    Compute velocities (first-order differences).

    Args:
        landmarks: [T, 21, 3]
        fps: frame rate

    Returns:
        velocities: [T, 21, 3]
    """
    velocities = np.zeros_like(landmarks)
    velocities[1:] = (landmarks[1:] - landmarks[:-1]) * fps
    return velocities


def compute_accelerations(velocities, fps=30):
    """
    Compute accelerations (second-order differences).

    Args:
        velocities: [T, 21, 3]
        fps: frame rate

    Returns:
        accelerations: [T, 21, 3]
    """
    accelerations = np.zeros_like(velocities)
    accelerations[1:] = (velocities[1:] - velocities[:-1]) * fps
    return accelerations


def compute_jerk(accelerations, fps=30):
    """
    Compute jerk (third-order derivative).

    Args:
        accelerations: [T, 21, 3]
        fps: frame rate

    Returns:
        jerk: [T, 21, 3]
    """
    jerk = np.zeros_like(accelerations)
    jerk[1:] = (accelerations[1:] - accelerations[:-1]) * fps
    return jerk


def compute_motion_quality_metrics(landmarks, fps=30):
    """
    Compute motion quality metrics for amateur/professional distinction.

    Metrics:
    - Mean jerk (smoothness)
    - Path length (efficiency)
    - Speed variance (consistency)
    - Pause ratio (hesitation)

    Args:
        landmarks: [T, 21, 3]
        fps: frame rate

    Returns:
        metrics: dict of scalar values
    """
    velocities = compute_velocities(landmarks, fps)
    accelerations = compute_accelerations(velocities, fps)
    jerk = compute_jerk(accelerations, fps)

    # Compute per-joint speed
    speeds = np.linalg.norm(velocities, axis=2)  # [T, 21]

    # Metric 1: Mean jerk (lower = smoother, more professional)
    jerk_magnitude = np.linalg.norm(jerk, axis=2)  # [T, 21]
    mean_jerk = np.mean(jerk_magnitude)

    # Metric 2: Path length (distance traveled by wrist)
    wrist_path_length = np.sum(np.linalg.norm(landmarks[1:, WRIST] - landmarks[:-1, WRIST], axis=1))

    # Metric 3: Speed variance (lower = more consistent)
    wrist_speed = speeds[:, WRIST]
    speed_variance = np.var(wrist_speed)

    # Metric 4: Pause ratio (frames with very low speed)
    pause_threshold = 0.01  # Adjust based on normalization
    pause_frames = np.sum(wrist_speed < pause_threshold)
    pause_ratio = pause_frames / len(wrist_speed)

    # Metric 5: Speed peaks (count of local maxima)
    speed_peaks = 0
    for i in range(1, len(wrist_speed) - 1):
        if wrist_speed[i] > wrist_speed[i-1] and wrist_speed[i] > wrist_speed[i+1]:
            speed_peaks += 1

    return {
        'mean_jerk': mean_jerk,
        'path_length': wrist_path_length,
        'speed_variance': speed_variance,
        'pause_ratio': pause_ratio,
        'speed_peaks': speed_peaks,
    }


def extract_relative_features(landmarks, fps=30):
    """
    Extract all relative geometry features.

    Args:
        landmarks: [T, 21, 3] normalized landmarks
        fps: frame rate

    Returns:
        features: [T, D] combined feature vector
    """
    # Bone vectors: [T, 20, 3] -> flatten to [T, 60]
    bones = compute_bone_vectors(landmarks)
    bones_flat = bones.reshape(bones.shape[0], -1)

    # Joint angles: [T, N_angles]
    angles = compute_joint_angles(landmarks)

    # Velocities: [T, 21, 3] -> flatten to [T, 63]
    velocities = compute_velocities(landmarks, fps)
    velocities_flat = velocities.reshape(velocities.shape[0], -1)

    # Concatenate all features
    features = np.concatenate([
        landmarks.reshape(landmarks.shape[0], -1),  # [T, 63] raw landmarks
        bones_flat,  # [T, 60] bone vectors
        angles,  # [T, 12] joint angles
        velocities_flat,  # [T, 63] velocities
    ], axis=1)

    return features  # [T, 198]


if __name__ == '__main__':
    # Test with dummy data
    T = 100
    landmarks = np.random.randn(T, 21, 3) * 0.1

    print("Testing geometry feature extraction...")

    bones = compute_bone_vectors(landmarks)
    print(f"Bones: {bones.shape}")

    angles = compute_joint_angles(landmarks)
    print(f"Angles: {angles.shape}")

    velocities = compute_velocities(landmarks)
    print(f"Velocities: {velocities.shape}")

    metrics = compute_motion_quality_metrics(landmarks)
    print(f"Quality metrics: {metrics}")

    features = extract_relative_features(landmarks)
    print(f"Combined features: {features.shape}")

    print("\n✓ All tests passed!")
