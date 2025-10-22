#!/usr/bin/env python3
"""
Dataset loader for amateur/professional gesture recognition.

Supports:
- Relative geometry features
- Time-stretch augmentation (0.8-1.2x) for variability
- Motion quality metrics
- ST-GCN input format
"""
import torch
from torch.utils.data import Dataset, DataLoader
import numpy as np
from pathlib import Path
import random

import sys
sys.path.append(str(Path(__file__).parent))

from geometry_features import extract_relative_features, compute_motion_quality_metrics
from dtw_alignment import align_sequence_to_template


class LandmarkDatasetAmateur(Dataset):
    """
    Dataset for gesture recognition with amateur/professional handling.

    Args:
        data_root: path to landmarks directory
        augment: whether to apply time-stretch augmentation
        use_relative_features: whether to use relative geometry features
        templates: dict of gesture templates for DTW alignment (optional)
    """
    def __init__(self, data_root, augment=False, use_relative_features=True, templates=None):
        self.data_root = Path(data_root)
        self.augment = augment
        self.use_relative_features = use_relative_features
        self.templates = templates or {}

        # Collect all samples
        self.samples = []
        self.label_map = {}

        class_dirs = sorted([d for d in self.data_root.iterdir() if d.is_dir()])

        # Build label mapping: folder name (1-9) -> 0-indexed label
        for idx, class_dir in enumerate(class_dirs):
            self.label_map[int(class_dir.name)] = idx

        # Collect samples
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

        if len(landmarks) == 0:
            # Fallback for empty sequences
            landmarks = np.zeros((10, 21, 3), dtype=np.float32)
            mask = np.zeros(10, dtype=np.float32)
        else:
            mask = np.ones(len(landmarks), dtype=np.float32)

        # Apply time-stretch augmentation
        if self.augment:
            landmarks, mask = self.time_stretch_augment(landmarks, mask)

        # Extract relative features if requested
        if self.use_relative_features:
            # Extract geometry features: [T, 198]
            rel_features = extract_relative_features(landmarks, fps=30)

            # Compute motion quality metrics
            quality_metrics = compute_motion_quality_metrics(landmarks, fps=30)
            quality_score = np.array([
                quality_metrics['mean_jerk'],
                quality_metrics['speed_variance'],
                quality_metrics['pause_ratio'],
            ], dtype=np.float32)

            # For ST-GCN, we still use landmarks but also provide features
            features = landmarks  # [T, 21, 3] for ST-GCN
            extra_features = rel_features
        else:
            features = landmarks
            extra_features = None
            quality_score = np.zeros(3, dtype=np.float32)

        # Convert to tensors
        features = torch.from_numpy(features.astype(np.float32))
        mask = torch.from_numpy(mask.astype(np.float32))
        label = torch.tensor(sample['label'], dtype=torch.long)
        quality_score = torch.from_numpy(quality_score)

        output = {
            'features': features,
            'mask': mask,
            'label': label,
            'quality_score': quality_score,
        }

        if extra_features is not None:
            output['extra_features'] = torch.from_numpy(extra_features.astype(np.float32))

        return output

    def time_stretch_augment(self, landmarks, mask, stretch_range=(0.8, 1.2)):
        """
        Time-stretch augmentation to simulate different execution speeds.

        Args:
            landmarks: [T, 21, 3]
            mask: [T]
            stretch_range: (min, max) stretch factor

        Returns:
            stretched_landmarks: [T', 21, 3]
            stretched_mask: [T']
        """
        stretch_factor = random.uniform(*stretch_range)
        T_original = len(landmarks)
        T_new = int(T_original * stretch_factor)
        T_new = max(10, T_new)  # Minimum length

        # Resample using linear interpolation
        indices = np.linspace(0, T_original - 1, T_new)
        stretched_landmarks = np.zeros((T_new, 21, 3))
        stretched_mask = np.zeros(T_new)

        for i, idx in enumerate(indices):
            idx_low = int(np.floor(idx))
            idx_high = min(int(np.ceil(idx)), T_original - 1)
            alpha = idx - idx_low

            stretched_landmarks[i] = (1 - alpha) * landmarks[idx_low] + alpha * landmarks[idx_high]
            stretched_mask[i] = 1.0

        return stretched_landmarks, stretched_mask


def collate_fn_amateur(batch):
    """
    Collate function for variable-length sequences.

    Pads to maximum length in batch.
    """
    max_len = max(item['features'].shape[0] for item in batch)

    features_padded = []
    masks_padded = []
    labels = []
    quality_scores = []
    extra_features_padded = []

    for item in batch:
        T = item['features'].shape[0]
        pad_len = max_len - T

        # Pad features
        features = item['features']
        if pad_len > 0:
            features = torch.cat([features, torch.zeros(pad_len, *features.shape[1:])], dim=0)
        features_padded.append(features)

        # Pad mask
        mask = item['mask']
        if pad_len > 0:
            mask = torch.cat([mask, torch.zeros(pad_len)], dim=0)
        masks_padded.append(mask)

        labels.append(item['label'])
        quality_scores.append(item['quality_score'])

        # Pad extra features if present
        if 'extra_features' in item:
            extra = item['extra_features']
            if pad_len > 0:
                extra = torch.cat([extra, torch.zeros(pad_len, extra.shape[1])], dim=0)
            extra_features_padded.append(extra)

    output = {
        'features': torch.stack(features_padded),
        'mask': torch.stack(masks_padded),
        'label': torch.stack(labels),
        'quality_score': torch.stack(quality_scores),
    }

    if extra_features_padded:
        output['extra_features'] = torch.stack(extra_features_padded)

    return output


def create_dataloaders_amateur(train_root, val_root, test_root, batch_size=32,
                                num_workers=0, use_relative_features=True):
    """
    Create dataloaders for train/val/test.

    Args:
        train_root: path to training landmarks
        val_root: path to validation landmarks
        test_root: path to test landmarks
        batch_size: batch size
        num_workers: number of workers
        use_relative_features: whether to use relative geometry features

    Returns:
        train_loader, val_loader, test_loader, num_classes
    """
    train_dataset = LandmarkDatasetAmateur(
        train_root, augment=True, use_relative_features=use_relative_features
    )
    val_dataset = LandmarkDatasetAmateur(
        val_root, augment=False, use_relative_features=use_relative_features
    )
    test_dataset = LandmarkDatasetAmateur(
        test_root, augment=False, use_relative_features=use_relative_features
    )

    train_loader = DataLoader(
        train_dataset, batch_size=batch_size, shuffle=True,
        num_workers=num_workers, collate_fn=collate_fn_amateur
    )
    val_loader = DataLoader(
        val_dataset, batch_size=batch_size, shuffle=False,
        num_workers=num_workers, collate_fn=collate_fn_amateur
    )
    test_loader = DataLoader(
        test_dataset, batch_size=batch_size, shuffle=False,
        num_workers=num_workers, collate_fn=collate_fn_amateur
    )

    num_classes = len(train_dataset.label_map)

    return train_loader, val_loader, test_loader, num_classes


if __name__ == '__main__':
    print("Testing amateur/professional dataset loader...")

    # Test with existing data
    data_root = Path(__file__).parent.parent.parent / 'data_split' / 'landmarks_train'

    if data_root.exists():
        dataset = LandmarkDatasetAmateur(data_root, augment=True)
        print(f"Dataset size: {len(dataset)}")
        print(f"Label map: {dataset.label_map}")

        # Test sample
        sample = dataset[0]
        print(f"Features shape: {sample['features'].shape}")
        print(f"Mask shape: {sample['mask'].shape}")
        print(f"Label: {sample['label']}")
        print(f"Quality score: {sample['quality_score']}")

        # Test dataloader
        loader = DataLoader(dataset, batch_size=4, shuffle=True, collate_fn=collate_fn_amateur)
        batch = next(iter(loader))
        print(f"\nBatch features: {batch['features'].shape}")
        print(f"Batch mask: {batch['mask'].shape}")
        print(f"Batch labels: {batch['label'].shape}")

        print("\n✓ Dataset test passed!")
    else:
        print(f"Data root not found: {data_root}")
        print("Skipping dataset test")
