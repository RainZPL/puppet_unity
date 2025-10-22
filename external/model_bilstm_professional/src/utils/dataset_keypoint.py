#!/usr/bin/env python3
"""
Dataset loader for key-point based gesture recognition.

Extracts key finger movement sequences instead of raw landmarks.
"""
import torch
from torch.utils.data import Dataset, DataLoader
import numpy as np
from pathlib import Path
import sys

sys.path.append(str(Path(__file__).parent))

from keypoint_detector import FingerMovementDetector, encode_event_sequence


class KeyPointDataset(Dataset):
    """
    Dataset that extracts key movement sequences.

    Args:
        data_root: path to landmarks directory
        max_events: maximum number of events to extract
        augment: whether to apply augmentation
    """
    def __init__(self, data_root, max_events=20, augment=False):
        self.data_root = Path(data_root)
        self.max_events = max_events
        self.augment = augment

        # Initialize detector
        self.detector = FingerMovementDetector(
            min_prominence=0.03,
            min_distance=5
        )

        # Collect samples
        self.samples = []
        self.label_map = {}

        class_dirs = sorted([d for d in self.data_root.iterdir() if d.is_dir()])

        # Build label mapping
        for idx, class_dir in enumerate(class_dirs):
            self.label_map[int(class_dir.name)] = idx

        # Collect all samples
        for class_dir in class_dirs:
            class_id = int(class_dir.name)
            class_label = self.label_map[class_id]

            npz_files = sorted(class_dir.glob('*.npz'))
            for npz_file in npz_files:
                self.samples.append({
                    'path': npz_file,
                    'label': class_label,
                    'class_id': class_id
                })

    def __len__(self):
        return len(self.samples)

    def __getitem__(self, idx):
        sample = self.samples[idx]

        # Load landmarks
        data = np.load(sample['path'])
        landmarks = data['keypoints']  # [T, 21, 3]
        mask = data['mask']  # [T]

        # Get valid frames
        valid_frames = mask > 0.5
        landmarks = landmarks[valid_frames]

        if len(landmarks) < 10:
            # Too short, use zeros
            landmarks = np.zeros((10, 21, 3), dtype=np.float32)

        # Extract key movements
        movements = self.detector.extract_key_movements(landmarks)

        # Create event sequence
        event_seq = self.detector.create_event_sequence(movements, len(landmarks))

        # Apply augmentation if requested
        if self.augment:
            event_seq = self.augment_event_sequence(event_seq)

        # Encode event sequence
        encoded_events, event_mask = encode_event_sequence(
            event_seq,
            vocab_size=50,
            max_events=self.max_events
        )

        # Convert to tensors
        events = torch.from_numpy(encoded_events)  # [max_events, 3]
        event_mask = torch.from_numpy(event_mask)  # [max_events]
        label = torch.tensor(sample['label'], dtype=torch.long)

        return {
            'events': events,
            'mask': event_mask,
            'label': label,
            'num_events': int(event_mask.sum())
        }

    def augment_event_sequence(self, event_seq):
        """
        Augment event sequence by:
        - Randomly dropping some events (simulate missed detections)
        - Adding slight time jitter (but preserving order)
        """
        if len(event_seq) == 0:
            return event_seq

        # Random drop (10% chance per event)
        augmented = []
        for event in event_seq:
            if np.random.rand() > 0.1:  # Keep 90% of events
                time, event_type, value = event

                # Add slight time jitter (±5%)
                time_jitter = np.random.uniform(-0.05, 0.05)
                time = np.clip(time + time_jitter, 0.0, 1.0)

                augmented.append((time, event_type, value))

        # Re-sort by time (in case jitter changed order)
        augmented = sorted(augmented, key=lambda x: x[0])

        return augmented if len(augmented) > 0 else event_seq


def collate_fn_keypoint(batch):
    """
    Collate function for key-point dataset.

    All sequences already padded to max_events, just stack.
    """
    events = torch.stack([item['events'] for item in batch])  # [N, max_events, 3]
    masks = torch.stack([item['mask'] for item in batch])  # [N, max_events]
    labels = torch.stack([item['label'] for item in batch])  # [N]

    return {
        'events': events,
        'mask': masks,
        'label': labels
    }


def create_dataloaders_keypoint(train_root, val_root, test_root,
                                 batch_size=32, num_workers=0, max_events=20):
    """
    Create dataloaders for key-point based training.

    Args:
        train_root, val_root, test_root: paths to landmark directories
        batch_size: batch size
        num_workers: number of workers
        max_events: maximum events per sequence

    Returns:
        train_loader, val_loader, test_loader, num_classes
    """
    train_dataset = KeyPointDataset(train_root, max_events=max_events, augment=True)
    val_dataset = KeyPointDataset(val_root, max_events=max_events, augment=False)
    test_dataset = KeyPointDataset(test_root, max_events=max_events, augment=False)

    train_loader = DataLoader(
        train_dataset, batch_size=batch_size, shuffle=True,
        num_workers=num_workers, collate_fn=collate_fn_keypoint
    )
    val_loader = DataLoader(
        val_dataset, batch_size=batch_size, shuffle=False,
        num_workers=num_workers, collate_fn=collate_fn_keypoint
    )
    test_loader = DataLoader(
        test_dataset, batch_size=batch_size, shuffle=False,
        num_workers=num_workers, collate_fn=collate_fn_keypoint
    )

    num_classes = len(train_dataset.label_map)

    return train_loader, val_loader, test_loader, num_classes


if __name__ == '__main__':
    print("Testing Key-Point Dataset...")

    data_root = Path(__file__).parent.parent.parent / 'data_split' / 'landmarks_train'

    if data_root.exists():
        dataset = KeyPointDataset(data_root, max_events=20, augment=True)
        print(f"Dataset size: {len(dataset)}")
        print(f"Label map: {dataset.label_map}")

        # Test sample
        sample = dataset[0]
        print(f"\nEvents shape: {sample['events'].shape}")
        print(f"Mask shape: {sample['mask'].shape}")
        print(f"Label: {sample['label']}")
        print(f"Num events: {sample['num_events']}")

        # Show first few events
        print("\nFirst 5 events:")
        for i in range(min(5, sample['num_events'])):
            time, event_id, value = sample['events'][i]
            print(f"  Event {i}: time={time:.3f}, id={event_id:.3f}, value={value:.3f}")

        # Test dataloader
        loader = DataLoader(dataset, batch_size=4, shuffle=True, collate_fn=collate_fn_keypoint)
        batch = next(iter(loader))
        print(f"\nBatch events: {batch['events'].shape}")
        print(f"Batch mask: {batch['mask'].shape}")
        print(f"Batch labels: {batch['label'].shape}")

        print("\n✓ Key-Point Dataset test passed!")
    else:
        print(f"Data root not found: {data_root}")
        print("Skipping dataset test")
