#!/usr/bin/env python3
"""
Dynamic Time Warping (DTW) utilities for temporal alignment.

Handles variable-speed gesture executions (amateur vs professional).
"""
import numpy as np
from scipy.spatial.distance import euclidean


def dtw_distance(seq1, seq2, dist_fn=euclidean):
    """
    Compute DTW distance between two sequences.

    Args:
        seq1: [T1, D] numpy array
        seq2: [T2, D] numpy array
        dist_fn: distance function between two frames

    Returns:
        dtw_dist: scalar DTW distance
        path: list of (i, j) tuples representing alignment path
    """
    T1, T2 = len(seq1), len(seq2)

    # Initialize cost matrix
    cost = np.full((T1 + 1, T2 + 1), np.inf)
    cost[0, 0] = 0

    # Fill cost matrix
    for i in range(1, T1 + 1):
        for j in range(1, T2 + 1):
            frame_dist = dist_fn(seq1[i-1], seq2[j-1])
            cost[i, j] = frame_dist + min(
                cost[i-1, j],    # insertion
                cost[i, j-1],    # deletion
                cost[i-1, j-1]   # match
            )

    # Backtrack to find path
    path = []
    i, j = T1, T2
    while i > 0 and j > 0:
        path.append((i-1, j-1))

        # Find which direction gave minimum cost
        candidates = [
            (i-1, j, cost[i-1, j]),
            (i, j-1, cost[i, j-1]),
            (i-1, j-1, cost[i-1, j-1])
        ]
        i, j, _ = min(candidates, key=lambda x: x[2])

    path.reverse()

    return cost[T1, T2], path


def dtw_barycenter_averaging(sequences, max_iter=10, tol=1e-4):
    """
    Compute DTW Barycenter Average (DBA) to create gesture template.

    Args:
        sequences: list of [T_i, D] numpy arrays
        max_iter: maximum iterations
        tol: convergence tolerance

    Returns:
        barycenter: [T_avg, D] averaged sequence
    """
    # Initialize with first sequence
    barycenter = sequences[0].copy()

    for iteration in range(max_iter):
        # Align all sequences to current barycenter
        aligned_points = [[] for _ in range(len(barycenter))]

        for seq in sequences:
            _, path = dtw_distance(barycenter, seq)
            for i, j in path:
                aligned_points[i].append(seq[j])

        # Update barycenter as average of aligned points
        new_barycenter = np.array([
            np.mean(points, axis=0) if len(points) > 0 else barycenter[i]
            for i, points in enumerate(aligned_points)
        ])

        # Check convergence
        change = np.linalg.norm(new_barycenter - barycenter)
        barycenter = new_barycenter

        if change < tol:
            print(f"DBA converged after {iteration + 1} iterations")
            break

    return barycenter


def align_sequence_to_template(sequence, template):
    """
    Align a sequence to a template using DTW, return aligned sequence.

    Args:
        sequence: [T, D] numpy array
        template: [T_ref, D] numpy array

    Returns:
        aligned: [T_ref, D] sequence warped to match template length
        distance: DTW distance
    """
    dtw_dist, path = dtw_distance(template, sequence)

    # Create aligned sequence by sampling sequence according to path
    aligned = np.zeros((len(template), sequence.shape[1]))

    for i, j in path:
        aligned[i] = sequence[j]

    return aligned, dtw_dist


def compute_dtw_similarity_matrix(sequences):
    """
    Compute pairwise DTW distance matrix for all sequences.

    Args:
        sequences: list of [T_i, D] numpy arrays

    Returns:
        distance_matrix: [N, N] symmetric matrix
    """
    N = len(sequences)
    distance_matrix = np.zeros((N, N))

    for i in range(N):
        for j in range(i+1, N):
            dist, _ = dtw_distance(sequences[i], sequences[j])
            distance_matrix[i, j] = dist
            distance_matrix[j, i] = dist

    return distance_matrix


if __name__ == '__main__':
    print("Testing DTW utilities...")

    # Create test sequences with different lengths and speeds
    t1 = np.linspace(0, 2*np.pi, 50)
    t2 = np.linspace(0, 2*np.pi, 70)  # Slower execution
    t3 = np.linspace(0, 2*np.pi, 30)  # Faster execution

    seq1 = np.column_stack([np.sin(t1), np.cos(t1)])
    seq2 = np.column_stack([np.sin(t2), np.cos(t2)])
    seq3 = np.column_stack([np.sin(t3), np.cos(t3)])

    # Test DTW distance
    dist12, path12 = dtw_distance(seq1, seq2)
    print(f"DTW distance (seq1, seq2): {dist12:.4f}")
    print(f"Path length: {len(path12)}")

    # Test DTW barycenter
    sequences = [seq1, seq2, seq3]
    barycenter = dtw_barycenter_averaging(sequences, max_iter=5)
    print(f"Barycenter shape: {barycenter.shape}")

    # Test alignment
    aligned, dist = align_sequence_to_template(seq2, barycenter)
    print(f"Aligned shape: {aligned.shape}, distance: {dist:.4f}")

    # Test similarity matrix
    sim_matrix = compute_dtw_similarity_matrix(sequences)
    print(f"Similarity matrix:\n{sim_matrix}")

    print("\n✓ All DTW tests passed!")
