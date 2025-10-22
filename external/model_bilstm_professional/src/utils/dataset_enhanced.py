#!/usr/bin/env python3
"""
Enhanced dataset loader with temporal augmentation.

Following PDF guidance for temporal invariance.
"""
import torch
from torch.utils.data import Dataset, DataLoader
import numpy as np
from pathlib import Path
import random


class EnhancedGestureDataset(Dataset):
    """
    Dataset with relative features and temporal augmentation.

    Features:
    - Loads pre-extracted relative features
    - Applies time warping augmentation
    - Variable-length sequences with masking
    """

    def __init__(self, data_root, augment=False, max_len=100):
        self.data_root = Path(data_root)
        self.augment = augment
        self.max_len = max_len

        # Collect samples
        self.samples = []
        self.label_map = {}

        class_dirs = sorted([d for d in self.data_root.iterdir() if d.is_dir()])

        # Build label mapping (model_output_idx -> class_id)
        # This allows us to skip gesture 5 and 6 while keeping original names
        self.model_idx_to_class_id = {}
        for idx, class_dir in enumerate(class_dirs):
            class_id = int(class_dir.name)
            self.model_idx_to_class_id[idx] = class_id
            self.label_map[class_id] = idx

        # Collect all npz files
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

        # Load data
        data = np.load(sample['path'])
        features = data['features']  # [T, D]
        mask = data['mask']  # [T]

        # Get valid frames
        valid_idx = mask > 0.5
        features = features[valid_idx]

        if len(features) == 0:
            features = np.zeros((10, features.shape[1] if len(features.shape) > 1 else 30))
            mask = np.zeros(10)
        else:
            mask = np.ones(len(features))

        # Apply temporal augmentation
        if self.augment:
            features, mask = self.temporal_augment(features, mask)

        # Pad or truncate to max_len
        T = len(features)
        if T > self.max_len:
            # Truncate
            features = features[:self.max_len]
            mask = mask[:self.max_len]
        elif T < self.max_len:
            # Pad
            pad_len = self.max_len - T
            features = np.vstack([features, np.zeros((pad_len, features.shape[1]))])
            mask = np.concatenate([mask, np.zeros(pad_len)])

        # Convert to tensors
        features = torch.from_numpy(features.astype(np.float32))
        mask = torch.from_numpy(mask.astype(np.float32))
        label = torch.tensor(sample['label'], dtype=torch.long)

        return {
            'features': features,
            'mask': mask,
            'label': label
        }

    def temporal_augment(self, features, mask, stretch_range=(0.7, 1.3)):
        """
        Time warping augmentation following PDF.

        Randomly stretches or compresses the sequence in time.
        """
        stretch_factor = random.uniform(*stretch_range)
        T_original = len(features)
        T_new = int(T_original * stretch_factor)
        T_new = max(10, min(T_new, self.max_len))  # Clamp

        # Resample using linear interpolation
        indices = np.linspace(0, T_original - 1, T_new)
        stretched_features = np.zeros((T_new, features.shape[1]))

        for i, idx in enumerate(indices):
            idx_low = int(np.floor(idx))
            idx_high = min(int(np.ceil(idx)), T_original - 1)
            alpha = idx - idx_low

            stretched_features[i] = (1 - alpha) * features[idx_low] + alpha * features[idx_high]

        stretched_mask = np.ones(T_new)

        return stretched_features, stretched_mask


def collate_fn_enhanced(batch):
    """Collate function for batching."""
    features = torch.stack([item['features'] for item in batch])
    masks = torch.stack([item['mask'] for item in batch])
    labels = torch.stack([item['label'] for item in batch])

    return {
        'features': features,
        'mask': masks,
        'label': labels
    }


def create_dataloaders_enhanced(train_root, val_root, test_root,
                                 batch_size=32, num_workers=0, max_len=100):
    """
    Create enhanced dataloaders.

    Returns:
        train_loader, val_loader, test_loader, num_classes, feature_dim, model_idx_to_class_id
    """
    train_dataset = EnhancedGestureDataset(train_root, augment=True, max_len=max_len)
    val_dataset = EnhancedGestureDataset(val_root, augment=False, max_len=max_len)
    test_dataset = EnhancedGestureDataset(test_root, augment=False, max_len=max_len)

    # Get feature dimension from first sample
    sample = train_dataset[0]
    feature_dim = sample['features'].shape[1]

    train_loader = DataLoader(
        train_dataset, batch_size=batch_size, shuffle=True,
        num_workers=num_workers, collate_fn=collate_fn_enhanced
    )
    val_loader = DataLoader(
        val_dataset, batch_size=batch_size, shuffle=False,
        num_workers=num_workers, collate_fn=collate_fn_enhanced
    )
    test_loader = DataLoader(
        test_dataset, batch_size=batch_size, shuffle=False,
        num_workers=num_workers, collate_fn=collate_fn_enhanced
    )

    num_classes = len(train_dataset.label_map)
    model_idx_to_class_id = train_dataset.model_idx_to_class_id

    return train_loader, val_loader, test_loader, num_classes, feature_dim, model_idx_to_class_id


if __name__ == '__main__':
    print("Testing Enhanced Dataset...")

    # This will be tested after data preparation
    print("Dataset loader ready for use after data preparation")
