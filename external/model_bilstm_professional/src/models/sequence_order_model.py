#!/usr/bin/env python3
"""
Sequence-Order Model for Amateur Gesture Recognition.

Key ideas:
1. Process entire gesture temporal context
2. Focus on ORDER of finger movements, not exact timing
3. Use event sequences (thumb extend → index curl → etc.)
4. Output final confidence after seeing full gesture

Model architecture:
- Input: Event sequence (time, event_type, value)
- Temporal encoder: Bi-directional LSTM/Transformer
- Attention: Focus on key discriminative events
- Output: Final gesture class + confidence
"""
import torch
import torch.nn as nn
import torch.nn.functional as F
import numpy as np


class EventSequenceEncoder(nn.Module):
    """
    Encodes event sequences using Bi-LSTM.

    Processes events in both forward/backward to capture order.
    """
    def __init__(self, event_dim=3, hidden_dim=128, num_layers=2, dropout=0.3):
        super().__init__()

        self.event_embedding = nn.Linear(event_dim, hidden_dim)

        self.bilstm = nn.LSTM(
            hidden_dim,
            hidden_dim // 2,
            num_layers=num_layers,
            bidirectional=True,
            dropout=dropout if num_layers > 1 else 0,
            batch_first=True
        )

        self.layer_norm = nn.LayerNorm(hidden_dim)

    def forward(self, events, mask):
        """
        Args:
            events: [N, max_events, 3] (time, event_id, value)
            mask: [N, max_events] binary mask

        Returns:
            encoded: [N, max_events, hidden_dim]
            final_state: [N, hidden_dim]
        """
        # Embed events
        x = self.event_embedding(events)  # [N, max_events, hidden_dim]

        # Pack sequence
        lengths = mask.sum(dim=1).cpu().long()
        packed = nn.utils.rnn.pack_padded_sequence(
            x, lengths, batch_first=True, enforce_sorted=False
        )

        # Bi-LSTM
        packed_out, (h_n, c_n) = self.bilstm(packed)

        # Unpack
        output, _ = nn.utils.rnn.pad_packed_sequence(packed_out, batch_first=True)

        # Pad to original max_events length if needed
        if output.size(1) < x.size(1):
            pad_len = x.size(1) - output.size(1)
            padding = torch.zeros(output.size(0), pad_len, output.size(2), device=output.device)
            output = torch.cat([output, padding], dim=1)

        output = self.layer_norm(output)  # [N, max_events, hidden_dim]

        # Final state (concatenate forward/backward)
        h_n = h_n.view(self.bilstm.num_layers, 2, -1, self.bilstm.hidden_size)
        final_state = torch.cat([h_n[-1, 0], h_n[-1, 1]], dim=1)  # [N, hidden_dim]

        return output, final_state


class TemporalAttention(nn.Module):
    """
    Attention mechanism to focus on key discriminative events.
    """
    def __init__(self, hidden_dim):
        super().__init__()

        self.attention = nn.Sequential(
            nn.Linear(hidden_dim, hidden_dim // 2),
            nn.Tanh(),
            nn.Linear(hidden_dim // 2, 1)
        )

    def forward(self, encoded, mask):
        """
        Args:
            encoded: [N, max_events, hidden_dim]
            mask: [N, max_events]

        Returns:
            attended: [N, hidden_dim]
            attention_weights: [N, max_events]
        """
        # Compute attention scores
        scores = self.attention(encoded).squeeze(-1)  # [N, max_events]

        # Mask invalid events
        scores = scores.masked_fill(mask == 0, -1e9)

        # Softmax
        weights = F.softmax(scores, dim=1)  # [N, max_events]

        # Weighted sum
        attended = torch.sum(encoded * weights.unsqueeze(-1), dim=1)  # [N, hidden_dim]

        return attended, weights


class OrderSensitivePooling(nn.Module):
    """
    Pooling that preserves order information.

    Uses position-aware aggregation.
    """
    def __init__(self, hidden_dim):
        super().__init__()

        self.position_weight = nn.Parameter(torch.randn(1, 1, hidden_dim))

    def forward(self, encoded, mask, positions):
        """
        Args:
            encoded: [N, max_events, hidden_dim]
            mask: [N, max_events]
            positions: [N, max_events] normalized time positions

        Returns:
            pooled: [N, hidden_dim]
        """
        # Weight by position (early vs late events)
        position_weights = positions.unsqueeze(-1) * self.position_weight  # [N, max_events, hidden_dim]

        # Combine with encoded features
        weighted = encoded * torch.sigmoid(position_weights)

        # Masked average pooling
        masked_sum = (weighted * mask.unsqueeze(-1)).sum(dim=1)  # [N, hidden_dim]
        masked_count = mask.sum(dim=1, keepdim=True) + 1e-8  # [N, 1]
        pooled = masked_sum / masked_count

        return pooled


class SequenceOrderModel(nn.Module):
    """
    Full model for order-based gesture recognition.

    Architecture:
    1. Event sequence encoder (Bi-LSTM)
    2. Temporal attention (focus on key events)
    3. Order-sensitive pooling
    4. Classification head
    """
    def __init__(self, num_classes=9, event_dim=3, hidden_dim=128,
                 num_layers=2, dropout=0.3):
        super().__init__()

        # Event encoder
        self.encoder = EventSequenceEncoder(
            event_dim=event_dim,
            hidden_dim=hidden_dim,
            num_layers=num_layers,
            dropout=dropout
        )

        # Attention mechanism
        self.attention = TemporalAttention(hidden_dim)

        # Order-sensitive pooling
        self.order_pooling = OrderSensitivePooling(hidden_dim)

        # Fusion layer
        self.fusion = nn.Sequential(
            nn.Linear(hidden_dim * 3, hidden_dim),  # 3 = final_state + attention + pooling
            nn.ReLU(),
            nn.Dropout(dropout),
        )

        # Classification head
        self.classifier = nn.Sequential(
            nn.Linear(hidden_dim, hidden_dim // 2),
            nn.ReLU(),
            nn.Dropout(dropout),
            nn.Linear(hidden_dim // 2, num_classes)
        )

        # Confidence head (estimates prediction confidence)
        self.confidence_head = nn.Sequential(
            nn.Linear(hidden_dim, hidden_dim // 2),
            nn.ReLU(),
            nn.Dropout(dropout),
            nn.Linear(hidden_dim // 2, 1),
            nn.Sigmoid()
        )

    def forward(self, events, mask):
        """
        Args:
            events: [N, max_events, 3] (time, event_id, value)
            mask: [N, max_events] binary mask

        Returns:
            logits: [N, num_classes]
            confidence: [N, 1] prediction confidence
            attention_weights: [N, max_events] for visualization
        """
        # Extract time positions
        positions = events[:, :, 0]  # [N, max_events]

        # Encode event sequence
        encoded, final_state = self.encoder(events, mask)  # [N, max_events, hidden_dim], [N, hidden_dim]

        # Attention over events
        attended, attention_weights = self.attention(encoded, mask)  # [N, hidden_dim], [N, max_events]

        # Order-sensitive pooling
        pooled = self.order_pooling(encoded, mask, positions)  # [N, hidden_dim]

        # Fuse all representations
        fused = torch.cat([final_state, attended, pooled], dim=1)  # [N, hidden_dim * 3]
        fused = self.fusion(fused)  # [N, hidden_dim]

        # Classification
        logits = self.classifier(fused)  # [N, num_classes]

        # Confidence estimation
        confidence = self.confidence_head(fused)  # [N, 1]

        return logits, confidence, attention_weights


def build_sequence_order_model(num_classes=9, event_dim=3, hidden_dim=128,
                                num_layers=2, dropout=0.3):
    """Build sequence-order model."""
    model = SequenceOrderModel(
        num_classes=num_classes,
        event_dim=event_dim,
        hidden_dim=hidden_dim,
        num_layers=num_layers,
        dropout=dropout
    )
    return model


if __name__ == '__main__':
    print("Testing Sequence-Order Model...")

    # Create model
    model = build_sequence_order_model(num_classes=9)
    print(f"Model parameters: {sum(p.numel() for p in model.parameters()):,}")

    # Test forward pass
    batch_size = 4
    max_events = 20
    event_dim = 3

    events = torch.randn(batch_size, max_events, event_dim)
    mask = torch.ones(batch_size, max_events)
    mask[:, 15:] = 0  # Variable length sequences

    logits, confidence, attention_weights = model(events, mask)

    print(f"Logits shape: {logits.shape}")
    print(f"Confidence shape: {confidence.shape}")
    print(f"Attention weights shape: {attention_weights.shape}")
    print(f"Sample attention weights: {attention_weights[0, :10]}")

    print("\n✓ Sequence-Order Model test passed!")
