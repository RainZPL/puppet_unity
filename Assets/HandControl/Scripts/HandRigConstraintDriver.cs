using System;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace HandControl
{
  [DefaultExecutionOrder(-5)]
  public class HandRigConstraintDriver : MonoBehaviour
  {
    [Serializable]
    private class ArmStuff
    {
      public TwoBoneIKConstraint rig;
      public Transform target;
      public Transform hint;
      public Vector3 shoulderDir = new(0f, 0.35f, 0.2f);
      public float shoulderDistance = 0.35f;
      public Vector3 elbowDir = new(0.2f, -0.15f, 0.15f);
      public float elbowDistance = 0.25f;
      [Range(0f, 1f)] public float weightScale = 1f;
      public Vector3 targetScale = new(0.45f, 0.45f, 0.45f);
      public Vector3 hintScale = new(0.35f, 0.35f, 0.35f);

      [HideInInspector] public bool hasRest;
      [HideInInspector] public Vector3 targetRest;
      [HideInInspector] public Vector3 hintRest;
      [HideInInspector] public Vector3 targetOffset;
      [HideInInspector] public Vector3 hintOffset;
      [HideInInspector] public Vector3 fingerRestDelta;
      [HideInInspector] public Vector3 fingerTipRest;
      [HideInInspector] public bool fingerRestCaptured;
      [HideInInspector] public float fingerCurlRest;
    }

    [SerializeField] private HandTrackingSource source;

    [Header("Rig targets")]
    [SerializeField] private ArmStuff rightArm = new();
    [SerializeField] private ArmStuff leftArm = new();
    [SerializeField] private Transform headTarget;
    [SerializeField] private Vector3 headYawAxis = new(0.3f, 0f, 0.3f);
    [SerializeField] private Vector3 headPitchAxis = new(0f, 0.4f, 0.2f);
    [SerializeField] private float headYawRange = 0.2f;
    [SerializeField] private float headPitchRange = 0.2f;
    [SerializeField] private float headGain = 0.8f;

    [Header("Extra bone rotation")]
    [SerializeField] private Transform rightUpperBone;
    [SerializeField] private Transform rightForeBone;
    [SerializeField] private Vector3 rightUpperAxis = new(1f, 0f, 0f);
    [SerializeField] private float rightUpperAngle = 80f;
    [SerializeField] private Vector3 rightForeAxis = new(1f, 0f, 0f);
    [SerializeField] private float rightForeAngle = 110f;
    [SerializeField] private Transform leftUpperBone;
    [SerializeField] private Transform leftForeBone;
    [SerializeField] private Vector3 leftUpperAxis = new(1f, 0f, 0f);
    [SerializeField] private float leftUpperAngle = 80f;
    [SerializeField] private Vector3 leftForeAxis = new(1f, 0f, 0f);
    [SerializeField] private float leftForeAngle = 110f;

    [Header("Body turning")]
    [SerializeField] private Transform bodyRoot;
    [SerializeField] private float bodyYawGain = 260f;
    [SerializeField] private float bodyYawClamp = 70f;

    [Header("Finger mapping knobs")]
    [SerializeField] private bool useRightHand = true;
    [SerializeField] private float thumbRaiseGain = 4f;
    [SerializeField] private float trioRaiseGain = 1.2f;
    [SerializeField] private float elbowCurlGain = 1.2f;
    [SerializeField] private float trioDeadZone = 0.02f;
    [SerializeField] private float trioBlend = 0.3f;
    [SerializeField] private float smoothing = 12f;

    private Vector3 headRestPos;
    private Quaternion headRestRot;
    private bool headHasRest;
    private Quaternion bodyRestRot;
    private bool bodyHasRest;
    private Quaternion rightUpperRest;
    private Quaternion rightForeRest;
    private Quaternion leftUpperRest;
    private Quaternion leftForeRest;

    private float wantShoulderRight;
    private float wantElbowRight;
    private float wantShoulderLeft;
    private float wantElbowLeft;
    private float wantHeadYaw;
    private float wantHeadPitch;
    private float wantBodyYaw;

    private float currentShoulderRight;
    private float currentElbowRight;
    private float currentShoulderLeft;
    private float currentElbowLeft;
    private float currentHeadYaw;
    private float currentHeadPitch;
    private float currentBodyYaw;

    private Vector3 indexRestDelta;
    private bool indexRestCaptured;
    private Vector3 palmRestNormal;
    private bool palmRestCaptured;

    private void Awake()
    {
      CaptureRest();
    }

    private void OnEnable()
    {
      if (source != null)
      {
        source.OnHandFrame += OnHandFrame;
      }
    }

    private void OnDisable()
    {
      if (source != null)
      {
        source.OnHandFrame -= OnHandFrame;
      }
      ResetEverything();
    }

    private void Update()
    {
      var t = Mathf.Clamp01(Time.deltaTime * Mathf.Max(0f, smoothing));
      currentShoulderRight = Mathf.Lerp(currentShoulderRight, wantShoulderRight, t);
      currentElbowRight = Mathf.Lerp(currentElbowRight, wantElbowRight, t);
      currentShoulderLeft = Mathf.Lerp(currentShoulderLeft, wantShoulderLeft, t);
      currentElbowLeft = Mathf.Lerp(currentElbowLeft, wantElbowLeft, t);
      currentHeadYaw = Mathf.Lerp(currentHeadYaw, wantHeadYaw, t);
      currentHeadPitch = Mathf.Lerp(currentHeadPitch, wantHeadPitch, t);
      currentBodyYaw = Mathf.Lerp(currentBodyYaw, wantBodyYaw, Mathf.Clamp01(Time.deltaTime * (smoothing * 0.5f)));

      ApplyArm(rightArm, currentShoulderRight, currentElbowRight);
      ApplyArm(leftArm, currentShoulderLeft, currentElbowLeft);
      ApplyHead();
      ApplyBody();
      ApplyExtraBones();
    }

    private void OnHandFrame(HandTrackingSource.HandFrameData frame)
    {
      if (frame == null || frame.landmarks == null || frame.landmarks.Length < 21 || !frame.tracked)
      {
        SetTargetsWhenMissing();
        return;
      }

      if (useRightHand && !frame.isRight || !useRightHand && frame.isRight)
      {
        SetTargetsWhenMissing();
        return;
      }

      var points = frame.landmarks;
      var wrist = points[0];

      UpdateArm(rightArm, points, wrist, 1, 4, ref wantShoulderRight, ref wantElbowRight, thumbRaiseGain);
      UpdateArm(leftArm, points, wrist, 13, 16, ref wantShoulderLeft, ref wantElbowLeft, trioRaiseGain, trioBlend);
      UpdateHead(points);
      UpdateBody(points, wrist);
    }

    private void UpdateArm(ArmStuff arm, Vector3[] pts, Vector3 wrist, int baseIdx, int tipIdx, ref float shoulder, ref float elbow, float raiseGain, float blend = 0.25f)
    {
      if (arm == null || !arm.hasRest)
      {
        return;
      }

      var basePos = pts[baseIdx];
      var tipPos = pts[tipIdx];
      var fingerDelta = tipPos - basePos;
      var fingerTipOffset = tipPos - wrist;
      var curlNow = ComputeFingerCurl(baseIdx, baseIdx + 1, baseIdx + 2, tipIdx, pts);

      if (!arm.fingerRestCaptured)
      {
        arm.fingerRestDelta = fingerDelta;
        arm.fingerTipRest = fingerTipOffset;
        arm.fingerCurlRest = curlNow;
        arm.fingerRestCaptured = true;
      }

      var deltaFromRest = fingerDelta - arm.fingerRestDelta;
      var tipFromRest = fingerTipOffset - arm.fingerTipRest;
      shoulder = Mathf.Clamp01(Mathf.Max(0f, tipFromRest.magnitude - trioDeadZone) * raiseGain);

      var curlDelta = Mathf.Max(0f, arm.fingerCurlRest - curlNow);
      elbow = Mathf.Clamp01(curlDelta * elbowCurlGain);

      if (arm.target != null)
      {
        arm.targetOffset = Vector3.Scale(tipFromRest, arm.targetScale);
      }

      if (arm.hint != null)
      {
        arm.hintOffset = Vector3.Scale(deltaFromRest, arm.hintScale);
      }

      if (blend > 0f)
      {
        var extraRaise = Mathf.Clamp01((curlDelta - trioDeadZone) / Mathf.Max(0.0001f, 1f - trioDeadZone));
        shoulder = Mathf.Clamp01(Mathf.Lerp(shoulder, extraRaise, Mathf.Clamp01(blend)));
      }
    }

    private void UpdateHead(Vector3[] pts)
    {
      if (!headHasRest || headTarget == null)
      {
        return;
      }

      var indexBase = pts[5];
      var indexTip = pts[8];
      var offset = indexTip - indexBase;

      if (!indexRestCaptured)
      {
        indexRestDelta = offset;
        indexRestCaptured = true;
      }

      var fromRest = offset - indexRestDelta;
      wantHeadYaw = Mathf.Clamp(fromRest.x * headGain, -headYawRange, headYawRange);
      var pitchSignal = -fromRest.y;
      wantHeadPitch = Mathf.Clamp(pitchSignal * headGain, -headPitchRange, headPitchRange);
    }

    private void UpdateBody(Vector3[] pts, Vector3 wrist)
    {
      if (!bodyHasRest || bodyRoot == null)
      {
        return;
      }

      var indexBase = pts[5];
      var pinkyBase = pts[17];
      var palmNormal = Vector3.Cross(indexBase - wrist, pinkyBase - wrist);

      if (!palmRestCaptured)
      {
        palmRestNormal = palmNormal;
        palmRestCaptured = true;
      }

      var rest = palmRestNormal.normalized;
      var curr = palmNormal.normalized;
      var angle = Vector3.SignedAngle(rest, curr, Vector3.forward);
      wantBodyYaw = Mathf.Clamp(angle * bodyYawGain, -bodyYawClamp, bodyYawClamp);
    }

    private void CaptureRest()
    {
      CaptureArmRest(rightArm);
      CaptureArmRest(leftArm);

      if (headTarget != null)
      {
        headRestPos = headTarget.localPosition;
        headRestRot = headTarget.localRotation;
        headHasRest = true;
      }

      if (bodyRoot != null)
      {
        bodyRestRot = bodyRoot.localRotation;
        bodyHasRest = true;
      }

      if (rightUpperBone != null) rightUpperRest = rightUpperBone.localRotation;
      if (rightForeBone != null) rightForeRest = rightForeBone.localRotation;
      if (leftUpperBone != null) leftUpperRest = leftUpperBone.localRotation;
      if (leftForeBone != null) leftForeRest = leftForeBone.localRotation;
    }

    private void CaptureArmRest(ArmStuff arm)
    {
      if (arm == null)
      {
        return;
      }

      if (arm.target != null) arm.targetRest = arm.target.localPosition;
      if (arm.hint != null) arm.hintRest = arm.hint.localPosition;

      arm.targetOffset = Vector3.zero;
      arm.hintOffset = Vector3.zero;
      arm.fingerRestDelta = Vector3.zero;
      arm.fingerTipRest = Vector3.zero;
      arm.fingerCurlRest = 0f;
      arm.fingerRestCaptured = false;
      arm.hasRest = arm.target != null;
    }

    private void ResetEverything()
    {
      ResetArm(rightArm);
      ResetArm(leftArm);

      if (headHasRest && headTarget != null)
      {
        headTarget.localPosition = headRestPos;
        headTarget.localRotation = headRestRot;
      }

      if (bodyHasRest && bodyRoot != null)
      {
        bodyRoot.localRotation = bodyRestRot;
      }

      if (rightUpperBone) rightUpperBone.localRotation = rightUpperRest;
      if (rightForeBone) rightForeBone.localRotation = rightForeRest;
      if (leftUpperBone) leftUpperBone.localRotation = leftUpperRest;
      if (leftForeBone) leftForeBone.localRotation = leftForeRest;

      wantShoulderRight = wantElbowRight = 0f;
      wantShoulderLeft = wantElbowLeft = 0f;
      wantHeadYaw = wantHeadPitch = 0f;
      wantBodyYaw = 0f;
      indexRestCaptured = false;
      palmRestCaptured = false;
    }

    private void ResetArm(ArmStuff arm)
    {
      if (arm == null || !arm.hasRest)
      {
        return;
      }

      if (arm.target != null) arm.target.localPosition = arm.targetRest;
      if (arm.hint != null) arm.hint.localPosition = arm.hintRest;
      if (arm.rig != null) arm.rig.weight = 0f;

      arm.targetOffset = Vector3.zero;
      arm.hintOffset = Vector3.zero;
      arm.fingerRestCaptured = false;
      arm.fingerRestDelta = Vector3.zero;
      arm.fingerTipRest = Vector3.zero;
      arm.fingerCurlRest = 0f;
    }

    private void SetTargetsWhenMissing()
    {
      wantShoulderRight = 0f;
      wantElbowRight = 0f;
      wantShoulderLeft = 0f;
      wantElbowLeft = 0f;
      wantHeadYaw = 0f;
      wantHeadPitch = 0f;
      wantBodyYaw = 0f;
    }

    private void ApplyArm(ArmStuff arm, float shoulderWeight, float elbowWeight)
    {
      if (arm == null || !arm.hasRest)
      {
        return;
      }

      var weight = Mathf.Clamp01(Mathf.Max(shoulderWeight, elbowWeight) * arm.weightScale);
      if (arm.rig != null)
      {
        arm.rig.weight = weight;
      }

      if (arm.target != null)
      {
        var dir = arm.shoulderDir.sqrMagnitude > 0f ? arm.shoulderDir.normalized : Vector3.up;
        var offset = arm.targetOffset + dir * (arm.shoulderDistance * shoulderWeight);
        arm.target.localPosition = arm.targetRest + offset;
      }

      if (arm.hint != null)
      {
        var dir = arm.elbowDir.sqrMagnitude > 0f ? arm.elbowDir.normalized : Vector3.forward;
        var offset = arm.hintOffset + dir * (arm.elbowDistance * elbowWeight);
        arm.hint.localPosition = arm.hintRest + offset;
      }
    }

    private void ApplyHead()
    {
      if (!headHasRest || headTarget == null)
      {
        return;
      }

      var yawOffset = headYawAxis.normalized * currentHeadYaw;
      var pitchOffset = headPitchAxis.normalized * currentHeadPitch;
      headTarget.localPosition = headRestPos + yawOffset + pitchOffset;
      headTarget.localRotation = headRestRot;
    }

    private void ApplyBody()
    {
      if (!bodyHasRest || bodyRoot == null)
      {
        return;
      }

      bodyRoot.localRotation = bodyRestRot * Quaternion.Euler(0f, currentBodyYaw, 0f);
    }

    private void ApplyExtraBones()
    {
      if (rightUpperBone) rightUpperBone.localRotation = rightUpperRest * Quaternion.AngleAxis(currentShoulderRight * rightUpperAngle, rightUpperAxis.normalized);
      if (rightForeBone) rightForeBone.localRotation = rightForeRest * Quaternion.AngleAxis(currentElbowRight * rightForeAngle, rightForeAxis.normalized);
      if (leftUpperBone) leftUpperBone.localRotation = leftUpperRest * Quaternion.AngleAxis(currentShoulderLeft * leftUpperAngle, leftUpperAxis.normalized);
      if (leftForeBone) leftForeBone.localRotation = leftForeRest * Quaternion.AngleAxis(currentElbowLeft * leftForeAngle, leftForeAxis.normalized);
    }

    private static float ComputeFingerCurl(int mcp, int pip, int dip, int tip, Vector3[] pts)
    {
      var angle1 = Vector3.Angle(pts[pip] - pts[mcp], pts[dip] - pts[pip]);
      var angle2 = Vector3.Angle(pts[dip] - pts[pip], pts[tip] - pts[dip]);
      return Mathf.Clamp01((NormalizeCurl(angle1) + NormalizeCurl(angle2)) * 0.5f);
    }

    private static float NormalizeCurl(float angle)
    {
      return Mathf.InverseLerp(165f, 30f, angle);
    }
  }
}
