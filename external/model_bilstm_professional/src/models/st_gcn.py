#!/usr/bin/env python3
"""
Spatial-Temporal Graph Convolutional Network (ST-GCN) for hand gesture recognition.

Handles skeleton graph structure with temporal dependencies.
Robust to amateur/professional variability through graph structure.
"""
import torch
import torch.nn as nn
import torch.nn.functional as F
import numpy as np


class GraphConvolution(nn.Module):
    """
    Spatial graph convolution layer.

    Args:
        in_channels: input feature channels
        out_channels: output feature channels
        A: adjacency matrix [K, V, V] where K is number of subsets, V is number of vertices
    """
    def __init__(self, in_channels, out_channels, A, residual=True):
        super().__init__()

        self.A = nn.Parameter(torch.from_numpy(A.astype(np.float32)), requires_grad=False)
        self.num_subsets = A.shape[0]

        # Separate conv for each adjacency subset
        self.convs = nn.ModuleList([
            nn.Conv2d(in_channels, out_channels, 1)
            for _ in range(self.num_subsets)
        ])

        # Learnable edge importance weighting
        self.edge_importance = nn.Parameter(torch.ones(self.num_subsets, A.shape[1], A.shape[2]))

        self.residual = residual
        if residual:
            if in_channels != out_channels:
                self.residual_conv = nn.Conv2d(in_channels, out_channels, 1)
            else:
                self.residual_conv = lambda x: x

        self.bn = nn.BatchNorm2d(out_channels)
        self.relu = nn.ReLU(inplace=True)

    def forward(self, x):
        """
        Args:
            x: [N, C, T, V] where N=batch, C=channels, T=time, V=vertices

        Returns:
            out: [N, C_out, T, V]
        """
        res = self.residual_conv(x) if self.residual else 0

        # Apply graph convolution for each subset
        out = None
        for i in range(self.num_subsets):
            # A_i: [V, V], edge_importance: [V, V]
            A_i = self.A[i] * self.edge_importance[i]

            # Graph convolution: x @ A
            # x: [N, C, T, V], A_i: [V, V] -> [N, C, T, V]
            x_i = torch.einsum('nctv,vw->nctw', x, A_i)
            x_i = self.convs[i](x_i)

            out = x_i if out is None else out + x_i

        out = self.bn(out)
        out = self.relu(out + res)

        return out


class ST_GCN_Block(nn.Module):
    """
    Spatial-Temporal GCN block.

    Spatial graph conv -> Temporal conv
    """
    def __init__(self, in_channels, out_channels, A, stride=1, residual=True):
        super().__init__()

        self.gcn = GraphConvolution(in_channels, out_channels, A, residual=residual)

        # Temporal convolution
        self.tcn = nn.Sequential(
            nn.Conv2d(out_channels, out_channels, (9, 1), (stride, 1), (4, 0)),
            nn.BatchNorm2d(out_channels),
            nn.Dropout(0.5, inplace=True),
        )

        if stride != 1 or in_channels != out_channels:
            self.residual = nn.Sequential(
                nn.Conv2d(in_channels, out_channels, 1, (stride, 1)),
                nn.BatchNorm2d(out_channels),
            )
        else:
            self.residual = lambda x: x

        self.relu = nn.ReLU(inplace=True)

    def forward(self, x):
        res = self.residual(x)
        x = self.gcn(x)
        x = self.tcn(x)
        return self.relu(x + res)


class ST_GCN(nn.Module):
    """
    ST-GCN model for gesture classification.

    Args:
        in_channels: input feature channels (default 3 for x,y,z)
        num_classes: number of gesture classes
        graph_args: dict with 'num_nodes' and 'edges'
        edge_importance_weighting: whether to use learnable edge weights
    """
    def __init__(self, in_channels=3, num_classes=9, graph_args=None,
                 edge_importance_weighting=True, dropout=0.5):
        super().__init__()

        if graph_args is None:
            # Default: MediaPipe hand skeleton (21 nodes)
            graph_args = {'num_nodes': 21, 'edges': self.get_hand_edges()}

        # Build adjacency matrix
        A = self.build_adjacency_matrix(
            graph_args['num_nodes'],
            graph_args['edges']
        )

        # Input embedding
        self.data_bn = nn.BatchNorm1d(in_channels * graph_args['num_nodes'])

        # ST-GCN layers
        self.st_gcn_layers = nn.ModuleList([
            ST_GCN_Block(in_channels, 64, A, residual=False),
            ST_GCN_Block(64, 64, A),
            ST_GCN_Block(64, 64, A),
            ST_GCN_Block(64, 128, A, stride=2),
            ST_GCN_Block(128, 128, A),
            ST_GCN_Block(128, 128, A),
            ST_GCN_Block(128, 256, A, stride=2),
            ST_GCN_Block(256, 256, A),
            ST_GCN_Block(256, 256, A),
        ])

        # Classification head
        self.fc = nn.Conv2d(256, num_classes, kernel_size=1)

        # Quality/phase regression head (optional)
        self.quality_head = nn.Sequential(
            nn.AdaptiveAvgPool2d(1),
            nn.Flatten(),
            nn.Linear(256, 128),
            nn.ReLU(),
            nn.Dropout(dropout),
            nn.Linear(128, 2)  # [quality_score, phase]
        )

    @staticmethod
    def get_hand_edges():
        """MediaPipe hand skeleton edges."""
        # Wrist to palm
        edges = [
            (0, 1), (0, 5), (0, 9), (0, 13), (0, 17),  # Wrist to finger bases
        ]
        # Finger chains
        fingers = [
            [1, 2, 3, 4],    # Thumb
            [5, 6, 7, 8],    # Index
            [9, 10, 11, 12], # Middle
            [13, 14, 15, 16],# Ring
            [17, 18, 19, 20],# Pinky
        ]
        for finger in fingers:
            for i in range(len(finger) - 1):
                edges.append((finger[i], finger[i+1]))

        return edges

    @staticmethod
    def build_adjacency_matrix(num_nodes, edges):
        """
        Build adjacency matrix with 3 partitions:
        - Self connections
        - Centripetal (toward root)
        - Centrifugal (away from root)

        Returns:
            A: [3, num_nodes, num_nodes] numpy array
        """
        A = np.zeros((3, num_nodes, num_nodes))

        # Partition 0: Self connections
        for i in range(num_nodes):
            A[0, i, i] = 1

        # Partition 1: Centripetal (child -> parent)
        # Partition 2: Centrifugal (parent -> child)
        for i, j in edges:
            A[1, j, i] = 1  # j receives from i (centripetal)
            A[2, i, j] = 1  # i sends to j (centrifugal)

        # Normalize
        for k in range(3):
            for i in range(num_nodes):
                degree = A[k, i].sum()
                if degree > 0:
                    A[k, i] /= degree

        return A

    def forward(self, x, mask=None):
        """
        Args:
            x: [N, T, V, C] where N=batch, T=time, V=vertices, C=channels
            mask: [N, T] optional mask for variable-length sequences

        Returns:
            logits: [N, num_classes]
            quality: [N, 2] quality and phase scores
        """
        N, T, V, C = x.shape

        # Reshape to [N, C, T, V]
        x = x.permute(0, 3, 1, 2).contiguous()

        # Batch normalization on input
        x_flat = x.permute(0, 2, 3, 1).contiguous().view(N, T, -1)
        x_flat = x_flat.permute(0, 2, 1).contiguous()  # [N, V*C, T]
        x_flat = self.data_bn(x_flat)
        x = x_flat.view(N, V, C, T).permute(0, 2, 3, 1).contiguous()  # [N, C, T, V]

        # ST-GCN layers
        for layer in self.st_gcn_layers:
            x = layer(x)

        # Global pooling
        x_pooled = F.adaptive_avg_pool2d(x, 1)  # [N, 256, 1, 1]

        # Classification
        logits = self.fc(x_pooled).squeeze(-1).squeeze(-1)  # [N, num_classes]

        # Quality/phase prediction
        quality = self.quality_head(x)  # [N, 2]

        return logits, quality


def build_st_gcn_model(num_classes=9, in_channels=3, dropout=0.5):
    """Build ST-GCN model."""
    model = ST_GCN(
        in_channels=in_channels,
        num_classes=num_classes,
        dropout=dropout
    )
    return model


if __name__ == '__main__':
    print("Testing ST-GCN model...")

    model = build_st_gcn_model(num_classes=9, in_channels=3)
    print(f"Model parameters: {sum(p.numel() for p in model.parameters()):,}")

    # Test forward pass
    batch_size = 4
    seq_len = 64
    num_nodes = 21
    channels = 3

    x = torch.randn(batch_size, seq_len, num_nodes, channels)
    mask = torch.ones(batch_size, seq_len)

    logits, quality = model(x, mask)
    print(f"Logits shape: {logits.shape}")
    print(f"Quality shape: {quality.shape}")

    print("\n✓ ST-GCN model test passed!")
