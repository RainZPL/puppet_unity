#!/usr/bin/env python3
"""
Bi-directional LSTM for whole movement trend recognition.

Following PDF guidance:
- Processes entire gesture sequence (forward + backward)
- Learns temporal signature of movement pattern
- Returns top-3 predictions with confidence intervals
- Robust to speed variations via bidirectional context
"""
import torch
import torch.nn as nn
import torch.nn.functional as F


class BiLSTMTrendModel(nn.Module):
    """
    Bidirectional LSTM for gesture movement pattern recognition.

    Architecture:
    - Input: [N, T, D] sequence of relative features
    - Bi-LSTM: Processes forward and backward
    - Attention: Focuses on discriminative parts
    - Output: Class probabilities + confidence score
    """

    def __init__(self, input_dim, hidden_dim=256, num_layers=3, num_classes=9, dropout=0.3):
        super().__init__()

        self.input_dim = input_dim
        self.hidden_dim = hidden_dim
        self.num_layers = num_layers
        self.num_classes = num_classes

        # Input projection
        self.input_proj = nn.Sequential(
            nn.Linear(input_dim, hidden_dim),
            nn.LayerNorm(hidden_dim),
            nn.ReLU(),
            nn.Dropout(dropout)
        )

        # Bidirectional LSTM layers
        self.bilstm = nn.LSTM(
            hidden_dim,
            hidden_dim // 2,  # Each direction has hidden_dim//2
            num_layers=num_layers,
            batch_first=True,
            bidirectional=True,
            dropout=dropout if num_layers > 1 else 0
        )

        # Attention mechanism
        self.attention = nn.Sequential(
            nn.Linear(hidden_dim, hidden_dim // 2),
            nn.Tanh(),
            nn.Linear(hidden_dim // 2, 1)
        )

        # Classification head
        self.classifier = nn.Sequential(
            nn.Linear(hidden_dim, hidden_dim // 2),
            nn.ReLU(),
            nn.Dropout(dropout),
            nn.Linear(hidden_dim // 2, num_classes)
        )

        # Confidence estimation head
        self.confidence = nn.Sequential(
            nn.Linear(hidden_dim, hidden_dim // 2),
            nn.ReLU(),
            nn.Dropout(dropout),
            nn.Linear(hidden_dim // 2, 1),
            nn.Sigmoid()
        )

    def forward(self, x, mask=None):
        """
        Forward pass.

        Args:
            x: [N, T, D] input features
            mask: [N, T] binary mask (1=valid, 0=padding)

        Returns:
            logits: [N, num_classes]
            confidence: [N] confidence scores
            attention_weights: [N, T] for visualization
        """
        N, T, D = x.shape

        # Project input
        x = self.input_proj(x)  # [N, T, hidden_dim]

        # Bidirectional LSTM
        if mask is not None:
            # Pack sequence for efficiency
            lengths = mask.sum(dim=1).cpu().long()
            packed = nn.utils.rnn.pack_padded_sequence(
                x, lengths, batch_first=True, enforce_sorted=False
            )
            packed_out, (h_n, c_n) = self.bilstm(packed)
            lstm_out, _ = nn.utils.rnn.pad_packed_sequence(packed_out, batch_first=True)

            # Pad to original length if needed
            if lstm_out.size(1) < T:
                pad_len = T - lstm_out.size(1)
                padding = torch.zeros(N, pad_len, self.hidden_dim, device=x.device)
                lstm_out = torch.cat([lstm_out, padding], dim=1)
        else:
            lstm_out, (h_n, c_n) = self.bilstm(x)

        # lstm_out: [N, T, hidden_dim]

        # Compute attention weights
        attn_scores = self.attention(lstm_out).squeeze(-1)  # [N, T]

        if mask is not None:
            attn_scores = attn_scores.masked_fill(mask == 0, -1e9)

        attn_weights = F.softmax(attn_scores, dim=1)  # [N, T]

        # Weighted sum over time
        context = torch.sum(lstm_out * attn_weights.unsqueeze(-1), dim=1)  # [N, hidden_dim]

        # Classification
        logits = self.classifier(context)  # [N, num_classes]

        # Confidence score
        conf = self.confidence(context).squeeze(-1)  # [N]

        return logits, conf, attn_weights


def build_bilstm_trend_model(input_dim, num_classes=9, hidden_dim=256, num_layers=3, dropout=0.3):
    """Build Bi-LSTM trend model."""
    model = BiLSTMTrendModel(
        input_dim=input_dim,
        hidden_dim=hidden_dim,
        num_layers=num_layers,
        num_classes=num_classes,
        dropout=dropout
    )
    return model


def predict_top_k(model, x, mask=None, k=3, temperature=1.0):
    """
    Get top-k predictions with confidence intervals.

    Args:
        model: trained model
        x: input features [N, T, D]
        mask: optional mask [N, T]
        k: number of top predictions
        temperature: softmax temperature for calibration

    Returns:
        top_classes: [N, k] top class indices
        top_probs: [N, k] probabilities
        confidence: [N] model confidence scores
    """
    model.eval()

    with torch.no_grad():
        logits, conf, _ = model(x, mask)

        # Apply temperature scaling
        logits = logits / temperature

        # Softmax to get probabilities
        probs = F.softmax(logits, dim=1)  # [N, num_classes]

        # Get top-k
        top_probs, top_classes = torch.topk(probs, k=k, dim=1)

    return top_classes, top_probs, conf


if __name__ == '__main__':
    print("Testing Bi-LSTM Trend Model...")

    # Test model
    input_dim = 30  # Number of relative features
    model = build_bilstm_trend_model(input_dim=input_dim, num_classes=9)

    print(f"Model parameters: {sum(p.numel() for p in model.parameters()):,}")

    # Test forward pass
    batch_size = 4
    seq_len = 50
    x = torch.randn(batch_size, seq_len, input_dim)
    mask = torch.ones(batch_size, seq_len)
    mask[:, 40:] = 0  # Variable length

    logits, conf, attn = model(x, mask)

    print(f"Logits shape: {logits.shape}")
    print(f"Confidence shape: {conf.shape}")
    print(f"Attention shape: {attn.shape}")

    # Test top-k prediction
    top_classes, top_probs, confidence = predict_top_k(model, x, mask, k=3)
    print(f"\nTop-3 predictions shape: {top_classes.shape}")
    print(f"Top-3 probabilities shape: {top_probs.shape}")
    print(f"Sample top-3: classes={top_classes[0]}, probs={top_probs[0]}")

    print("\n✓ Bi-LSTM Trend Model test passed!")
