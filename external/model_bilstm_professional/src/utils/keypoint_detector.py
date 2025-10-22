#!/usr/bin/env python3
"""
Key movement detection for amateur gesture recognition.

Instead of focusing on exact angles/timing, we detect:
- Critical finger movement events (extend, curl, spread, etc.)
- Distinctive movement sequences
- Order of movements (not exact timing)

This makes recognition robust to amateur execution variability.
"""
import numpy as np
from scipy.signal import find_peaks
from typing import List, Dict, Tuple


# Finger indices
THUMB = [1, 2, 3, 4]
INDEX = [5, 6, 7, 8]
MIDDLE = [9, 10, 11, 12]
RING = [13, 14, 15, 16]
PINKY = [17, 18, 19, 20]
WRIST = 0

ALL_FINGERS = {
    'thumb': THUMB,
    'index': INDEX,
    'middle': MIDDLE,
    'ring': RING,
    'pinky': PINKY
}


class FingerMovementDetector:
    """
    Detects key finger movement events from landmark sequences.

    Events include:
    - Extension/Flexion (straightening/bending)
    - Spreading/Closing (moving apart/together)
    - Forward/Backward motion (relative to palm)
    - Up/Down motion (relative to wrist)
    """

    def __init__(self, min_prominence=0.03, min_distance=5):
        """
        Args:
            min_prominence: minimum change to detect a movement
            min_distance: minimum frames between detected events
        """
        self.min_prominence = min_prominence
        self.min_distance = min_distance

    def compute_finger_extension(self, landmarks, finger_indices):
        """
        Compute finger extension over time (0=curled, 1=extended).

        Uses distance from fingertip to palm base.
        """
        T = len(landmarks)
        fingertip = landmarks[:, finger_indices[-1]]  # [T, 3]
        palm_base = landmarks[:, WRIST]  # [T, 3]

        # Distance from tip to wrist
        distances = np.linalg.norm(fingertip - palm_base, axis=1)  # [T]

        # Normalize to [0, 1]
        min_dist, max_dist = distances.min(), distances.max()
        if max_dist - min_dist < 1e-6:
            return np.zeros(T)

        extension = (distances - min_dist) / (max_dist - min_dist)
        return extension

    def compute_finger_curl(self, landmarks, finger_indices):
        """
        Compute finger curl angle over time.

        Measures angle between finger segments.
        """
        T = len(landmarks)
        curls = []

        for i in range(len(finger_indices) - 2):
            # Three consecutive joints
            p1 = landmarks[:, finger_indices[i]]      # [T, 3]
            p2 = landmarks[:, finger_indices[i+1]]    # [T, 3]
            p3 = landmarks[:, finger_indices[i+2]]    # [T, 3]

            # Vectors
            v1 = p1 - p2  # [T, 3]
            v2 = p3 - p2  # [T, 3]

            # Angle between vectors
            v1_norm = np.linalg.norm(v1, axis=1, keepdims=True) + 1e-8
            v2_norm = np.linalg.norm(v2, axis=1, keepdims=True) + 1e-8

            cos_angle = np.sum(v1 * v2, axis=1) / (v1_norm.squeeze() * v2_norm.squeeze())
            cos_angle = np.clip(cos_angle, -1.0, 1.0)
            angle = np.arccos(cos_angle)  # [T]

            curls.append(angle)

        # Average curl across joints
        avg_curl = np.mean(curls, axis=0) if curls else np.zeros(T)
        return avg_curl

    def compute_finger_spread(self, landmarks, finger1_indices, finger2_indices):
        """
        Compute spread between two adjacent fingers.
        """
        T = len(landmarks)
        tip1 = landmarks[:, finger1_indices[-1]]  # [T, 3]
        tip2 = landmarks[:, finger2_indices[-1]]  # [T, 3]

        # Distance between fingertips
        distances = np.linalg.norm(tip1 - tip2, axis=1)  # [T]

        # Normalize
        min_dist, max_dist = distances.min(), distances.max()
        if max_dist - min_dist < 1e-6:
            return np.zeros(T)

        spread = (distances - min_dist) / (max_dist - min_dist)
        return spread

    def compute_thumb_opposition(self, landmarks):
        """
        Compute thumb opposition (thumb tip approaching other fingertips).
        """
        T = len(landmarks)
        thumb_tip = landmarks[:, THUMB[-1]]  # [T, 3]

        # Distance to each other fingertip
        oppositions = []
        for finger_name, finger_indices in ALL_FINGERS.items():
            if finger_name == 'thumb':
                continue
            fingertip = landmarks[:, finger_indices[-1]]
            dist = np.linalg.norm(thumb_tip - fingertip, axis=1)
            oppositions.append(dist)

        # Minimum distance (closest finger)
        min_opposition = np.min(oppositions, axis=0)  # [T]

        # Normalize (invert so close = high value)
        min_dist, max_dist = min_opposition.min(), min_opposition.max()
        if max_dist - min_dist < 1e-6:
            return np.zeros(T)

        opposition = 1.0 - (min_opposition - min_dist) / (max_dist - min_dist)
        return opposition

    def detect_events(self, signal, event_type='peak'):
        """
        Detect movement events (peaks, valleys, transitions).

        Returns list of (frame_idx, value) tuples.
        """
        # Smooth signal
        from scipy.ndimage import gaussian_filter1d
        smoothed = gaussian_filter1d(signal, sigma=2)

        events = []

        if event_type == 'peak':
            # Find peaks (local maxima)
            peaks, properties = find_peaks(
                smoothed,
                prominence=self.min_prominence,
                distance=self.min_distance
            )
            events = [(int(idx), float(smoothed[idx])) for idx in peaks]

        elif event_type == 'valley':
            # Find valleys (local minima)
            valleys, properties = find_peaks(
                -smoothed,
                prominence=self.min_prominence,
                distance=self.min_distance
            )
            events = [(int(idx), float(smoothed[idx])) for idx in valleys]

        elif event_type == 'transition':
            # Find rising/falling edges
            diff = np.diff(smoothed)
            # Rising edges
            rising = np.where(diff > self.min_prominence)[0]
            # Falling edges
            falling = np.where(diff < -self.min_prominence)[0]

            events = []
            for idx in rising:
                events.append((int(idx), 'rise', float(smoothed[idx])))
            for idx in falling:
                events.append((int(idx), 'fall', float(smoothed[idx])))

            events = sorted(events, key=lambda x: x[0])

        return events

    def extract_key_movements(self, landmarks):
        """
        Extract all key movement features and events.

        Args:
            landmarks: [T, 21, 3] normalized landmarks

        Returns:
            dict of movement signals and detected events
        """
        T = len(landmarks)

        movements = {
            'signals': {},
            'events': {}
        }

        # 1. Finger extensions
        for finger_name, finger_indices in ALL_FINGERS.items():
            extension = self.compute_finger_extension(landmarks, finger_indices)
            movements['signals'][f'{finger_name}_extension'] = extension

            # Detect extend/retract events
            extend_events = self.detect_events(extension, 'peak')
            retract_events = self.detect_events(extension, 'valley')
            movements['events'][f'{finger_name}_extend'] = extend_events
            movements['events'][f'{finger_name}_retract'] = retract_events

        # 2. Finger curls
        for finger_name, finger_indices in ALL_FINGERS.items():
            curl = self.compute_finger_curl(landmarks, finger_indices)
            movements['signals'][f'{finger_name}_curl'] = curl

        # 3. Finger spreads (adjacent pairs)
        finger_pairs = [
            ('thumb', 'index'),
            ('index', 'middle'),
            ('middle', 'ring'),
            ('ring', 'pinky')
        ]

        for f1, f2 in finger_pairs:
            spread = self.compute_finger_spread(
                landmarks,
                ALL_FINGERS[f1],
                ALL_FINGERS[f2]
            )
            movements['signals'][f'{f1}_{f2}_spread'] = spread

            # Detect spread/close events
            spread_events = self.detect_events(spread, 'peak')
            close_events = self.detect_events(spread, 'valley')
            movements['events'][f'{f1}_{f2}_spread'] = spread_events
            movements['events'][f'{f1}_{f2}_close'] = close_events

        # 4. Thumb opposition
        opposition = self.compute_thumb_opposition(landmarks)
        movements['signals']['thumb_opposition'] = opposition

        oppose_events = self.detect_events(opposition, 'peak')
        movements['events']['thumb_oppose'] = oppose_events

        return movements

    def create_event_sequence(self, movements, T):
        """
        Create ordered sequence of movement events.

        Args:
            movements: dict from extract_key_movements
            T: total time steps

        Returns:
            List of (time, event_type, value) sorted by time
        """
        event_sequence = []

        for event_name, events in movements['events'].items():
            for event in events:
                if len(event) == 2:
                    time_idx, value = event
                    event_sequence.append((time_idx / T, event_name, value))
                else:
                    time_idx, direction, value = event
                    event_sequence.append((time_idx / T, f'{event_name}_{direction}', value))

        # Sort by time
        event_sequence = sorted(event_sequence, key=lambda x: x[0])

        return event_sequence


def encode_event_sequence(event_sequence, vocab_size=50, max_events=20):
    """
    Encode event sequence into fixed-length representation.

    Args:
        event_sequence: list of (time, event_type, value)
        vocab_size: size of event vocabulary
        max_events: maximum number of events to keep

    Returns:
        encoded: [max_events, 3] array of (time, event_id, value)
        mask: [max_events] binary mask
    """
    # Build vocabulary of event types
    event_types = list(set([e[1] for e in event_sequence]))
    event_to_id = {event: i for i, event in enumerate(event_types)}

    # Take first max_events
    sequence = event_sequence[:max_events]

    # Encode
    encoded = np.zeros((max_events, 3), dtype=np.float32)
    mask = np.zeros(max_events, dtype=np.float32)

    for i, (time, event_type, value) in enumerate(sequence):
        encoded[i, 0] = time
        encoded[i, 1] = event_to_id[event_type] / vocab_size  # Normalize
        encoded[i, 2] = value
        mask[i] = 1.0

    return encoded, mask


if __name__ == '__main__':
    print("Testing key movement detector...")

    # Create dummy landmark sequence
    T = 100
    landmarks = np.random.randn(T, 21, 3) * 0.1

    # Add some pattern (finger extending)
    for t in range(20, 40):
        landmarks[t, THUMB[-1]] += (t - 20) * 0.02  # Thumb extends

    # Detect movements
    detector = FingerMovementDetector()
    movements = detector.extract_key_movements(landmarks)

    print(f"\nDetected signals: {list(movements['signals'].keys())}")
    print(f"Detected events: {list(movements['events'].keys())}")

    # Create event sequence
    event_seq = detector.create_event_sequence(movements, T)
    print(f"\nEvent sequence length: {len(event_seq)}")
    if event_seq:
        print("First 5 events:")
        for event in event_seq[:5]:
            print(f"  Time: {event[0]:.2f}, Type: {event[1]}, Value: {event[2]:.3f}")

    # Encode
    encoded, mask = encode_event_sequence(event_seq)
    print(f"\nEncoded shape: {encoded.shape}, Mask sum: {mask.sum()}")

    print("\n✓ Key movement detector test passed!")
