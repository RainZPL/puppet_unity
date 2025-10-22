#!/usr/bin/env python3
"""
Test realtime script with video file instead of camera.
"""
import sys
from pathlib import Path
sys.path.append(str(Path(__file__).parent / 'src'))

import argparse
import cv2
import numpy as np
from realtime_enhanced import RealtimeEnhancedDetector
from models.bilstm_trend import build_bilstm_trend_model
import torch
import json

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--video', type=str, required=True, help='Path to test video')
    parser.add_argument('--ckpt', type=str, default='artifacts/checkpoints/best.pt')
    parser.add_argument('--label_map', type=str, default=None)
    parser.add_argument('--buffer_size', type=int, default=100)
    parser.add_argument('--confidence_thresh', type=float, default=0.6)
    args = parser.parse_args()

    # Load model
    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"Loading model from {args.ckpt}...")

    checkpoint = torch.load(args.ckpt, map_location=device)
    num_classes = checkpoint.get('num_classes', None)
    feature_dim = checkpoint.get('feature_dim', 20)

    # Determine label_map path
    if args.label_map is None:
        ckpt_path = Path(args.ckpt)
        label_map_path = ckpt_path.parent.parent / 'label_map.json'
        if not label_map_path.exists():
            print(f"Warning: label_map.json not found at {label_map_path}")
            # Try absolute path
            label_map_path = Path('/Users/zhengxinyi/hand_movement/shared/label_map_corrected.json')
    else:
        label_map_path = Path(args.label_map)

    # Load label map
    print(f"Loading label map from {label_map_path}...")
    with open(label_map_path, 'r') as f:
        label_map = json.load(f)
    label_map = {int(k): v for k, v in label_map.items()}

    # Use num_classes from checkpoint if available
    if num_classes is None:
        num_classes = len(label_map)

    print(f"Number of classes: {num_classes}")
    print(f"Label map: {label_map}")

    model = build_bilstm_trend_model(input_dim=feature_dim, num_classes=num_classes)
    model.load_state_dict(checkpoint['model_state_dict'])
    model = model.to(device)
    model.eval()

    print("Model loaded successfully!")
    print(f"\nTesting with video: {args.video}\n")

    # Initialize detector
    detector = RealtimeEnhancedDetector(
        model, label_map, device,
        buffer_size=args.buffer_size,
        confidence_thresh=args.confidence_thresh
    )

    # Initialize MediaPipe
    import mediapipe as mp
    mp_hands = mp.solutions.hands
    hands = mp_hands.Hands(
        static_image_mode=False,
        max_num_hands=1,
        min_detection_confidence=0.5,
        min_tracking_confidence=0.5
    )
    mp_draw = mp.solutions.drawing_utils

    # Open video
    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        print(f"ERROR: Could not open video: {args.video}")
        return

    print("Controls:")
    print("  'q' - Quit")
    print("  'r' - Reset buffer")
    print("")

    frame_count = 0
    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            print("\nEnd of video reached.")
            break

        frame_count += 1

        # Process with MediaPipe
        frame_rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        results = hands.process(frame_rgb)

        # Extract landmarks
        landmarks = None
        if results.multi_hand_landmarks:
            hand_landmarks = results.multi_hand_landmarks[0]
            landmarks = np.array([[lm.x, lm.y, lm.z] for lm in hand_landmarks.landmark])
            mp_draw.draw_landmarks(frame, hand_landmarks, mp_hands.HAND_CONNECTIONS)

        # Process frame
        info = detector.process_frame(landmarks)

        # Display predictions on frame
        h, w = frame.shape[:2]
        if info['top3_classes']:
            y_offset = 30
            cv2.putText(frame, "TOP-3:", (10, y_offset),
                       cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)

            for i, (cls, prob) in enumerate(zip(info['top3_classes'], info['top3_probs'])):
                y_offset += 35
                gesture_name = label_map[cls]
                color = (0, 255, 0) if i == 0 else (0, 255, 255)
                text = f"#{i+1}: {gesture_name} ({prob:.2f})"
                cv2.putText(frame, text, (10, y_offset),
                           cv2.FONT_HERSHEY_SIMPLEX, 0.6, color, 2)

            # Print info every 30 frames
            if frame_count % 30 == 0:
                top_gesture = label_map[info['top3_classes'][0]]
                print(f"Frame {frame_count}: Prediction={top_gesture}, Confidence={info['model_confidence']:.3f}")

        # Display
        cv2.imshow('Gesture Recognition (Video Test)', frame)

        # Controls
        key = cv2.waitKey(30) & 0xFF
        if key == ord('q'):
            break
        elif key == ord('r'):
            detector.feature_buffer.clear()
            print("Buffer reset!")

    cap.release()
    cv2.destroyAllWindows()
    print(f"\nProcessed {frame_count} frames")

if __name__ == '__main__':
    main()
