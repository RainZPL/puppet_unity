using System;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace HandControl
{
  /// <summary>
  /// Maps MediaPipe hand landmarks to rig constraints.
  /// Thumb drives the right arm, ring finger drives the left arm,
  /// index finger drives head motion, and wrist rotation drives body yaw.
  /// </summary>
  [DefaultExecutionOrder(-5)]
  public class HandRigConstraintDriver : MonoBehaviour
  {
    [Serializable]
    private class ArmIkConfig
    {
      public TwoBoneIKConstraint constraint;
      public Transform target;
      public Transform hint;
      [Tooltip("Direction the IK target moves when the arm raises. Local space of the target parent.")]
      public Vector3 shoulderDirection = new Vector3(0f, 0.35f, 0.2f);
      public float shoulderDistance = 0.35f;
      [Tooltip("Direction the hint moves when the elbow bends. Local space of the hint parent.")]
      public Vector3 elbowDirection = new Vector3(0.2f, -0.15f, 0.15f);
      public float elbowDistance = 0.25f;
      [Range(0f, 1f)] public float weightMultiplier = 1f;
      [Tooltip("Per-axis multiplier converting finger delta (normalized hand space) into IK target offset (meters).")]
      public Vector3 targetPositionMultiplier = new Vector3(0.45f, 0.45f, 0.45f);
      [Tooltip("Per-axis multiplier converting finger delta into IK hint offset (meters).")]
      public Vector3 hintPositionMultiplier = new Vector3(0.35f, 0.35f, 0.35f);

      [HideInInspector] public bool restCaptured;
      [HideInInspector] public Vector3 targetRest;
      [HideInInspector] public Vector3 hintRest;
      [HideInInspector] public Vector3 dynamicTargetOffset;
      [HideInInspector] public Vector3 dynamicHintOffset;
      [HideInInspector] public Vector3 fingerRestDelta;
      [HideInInspector] public Vector3 fingerTipRestRelative;
      [HideInInspector] public bool fingerRestCaptured;
      [HideInInspector] public float fingerCurlRest;
    }

    [SerializeField] private HandTrackingSource source;

    [Header("Animation Rigging Targets")]
    [SerializeField] private ArmIkConfig rightArm = new ArmIkConfig();
    [SerializeField] private ArmIkConfig leftArm = new ArmIkConfig();
    [SerializeField] private Transform headTarget;
    [SerializeField] private Vector3 headYawAxis = new Vector3(0.3f, 0f, 0.3f);
    [SerializeField] private Vector3 headPitchAxis = new Vector3(0f, 0.4f, 0.2f);
    [SerializeField] private float headYawRange = 0.2f;
    [SerializeField] private float headPitchRange = 0.2f;
    [SerializeField] private float headMotionGain = 0.8f;

    [Header("Bone Rotation Mapping")]
    [SerializeField] private Transform rightUpperArmBone;
    [SerializeField] private Transform rightForearmBone;
    [SerializeField] private Vector3 rightUpperArmAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float rightUpperArmAngle = 80f;
    [SerializeField] private Vector3 rightForearmAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float rightForearmAngle = 110f;

    [SerializeField] private Transform leftUpperArmBone;
    [SerializeField] private Transform leftForearmBone;
    [SerializeField] private Vector3 leftUpperArmAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float leftUpperArmAngle = 80f;
    [SerializeField] private Vector3 leftForearmAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float leftForearmAngle = 110f;

    [Header("Body Orientation")]
    [SerializeField] private Transform bodyRoot;
    [SerializeField] private float bodyYawGain = 260f;
    [SerializeField] private float bodyYawClamp = 70f;

    [Header("Hand Mapping")]
    [SerializeField, Tooltip("Use right-hand landmarks. Disable for left-hand control.")]
    private bool useRightHand = true;
    [SerializeField, Tooltip("Gain applied to thumb movement when driving the right shoulder weight.")]
    private float thumbRaiseGain = 4f;
    [SerializeField, Tooltip("Gain applied to ring-finger movement when driving the left shoulder weight.")]
    private float shoulderCurlGain = 1.2f;
    [SerializeField, Tooltip("Gain applied when mapping finger curl to elbow bend.")]
    private float elbowCurlGain = 1.2f;
    [SerializeField, Tooltip("Dead zone for finger movement before arms respond.")]
    private float trioActivationThreshold = 0.02f;
    [SerializeField, Tooltip("Blend between positional raise (0) and curl raise (1) for the ring finger.")]
    private float trioShoulderBlend = 0.3f;
    [SerializeField, Tooltip("Smoothing factor applied per frame.")]
    private float smoothing = 12f;

    private Vector3 _headRestPos;
    private Quaternion _headRestRot;
    private bool _headCaptured;
    private Quaternion _bodyRestRot;
    private bool _bodyCaptured;

    private Quaternion _rightUpperRestRot;
    private Quaternion _rightForearmRestRot;
    private Quaternion _leftUpperRestRot;
    private Quaternion _leftForearmRestRot;

    private float _targetShoulderRight;
    private float _targetElbowRight;
    private float _targetShoulderLeft;
    private float _targetElbowLeft;
    private float _targetHeadYaw;
    private float _targetHeadPitch;
    private float _targetBodyYaw;

    private float _shoulderRight;
    private float _elbowRight;
    private float _shoulderLeft;
    private float _elbowLeft;
    private float _headYaw;
    private float _headPitch;
    private float _bodyYaw;

    private Vector3 _indexRestDelta;
    private bool _indexRestCaptured;
    private float _palmYawRest;
    private bool _palmRestCaptured;

    private void Awake()
    {
      CaptureRestState();
    }

    private void OnEnable()
    {
      if (source != null)
      {
        source.OnHandFrame += HandleHandFrame;
      }
    }

    private void OnDisable()
    {
      if (source != null)
      {
        source.OnHandFrame -= HandleHandFrame;
      }

      ResetToRest();
    }

    public void CaptureRestState()
    {
      CaptureArmRest(rightArm);
      CaptureArmRest(leftArm);

      if (headTarget != null)
      {
        _headRestPos = headTarget.localPosition;
        _headRestRot = headTarget.localRotation;
        _headCaptured = true;
      }

      if (bodyRoot != null)
      {
        _bodyRestRot = bodyRoot.localRotation;
        _bodyCaptured = true;
      }

      if (rightUpperArmBone != null)
      {
        _rightUpperRestRot = rightUpperArmBone.localRotation;
      }

      if (rightForearmBone != null)
      {
        _rightForearmRestRot = rightForearmBone.localRotation;
      }

      if (leftUpperArmBone != null)
      {
        _leftUpperRestRot = leftUpperArmBone.localRotation;
      }

      if (leftForearmBone != null)
      {
        _leftForearmRestRot = leftForearmBone.localRotation;
      }
    }

    private void CaptureArmRest(ArmIkConfig arm)
    {
      if (arm == null)
      {
        return;
      }

      if (arm.target != null)
      {
        arm.targetRest = arm.target.localPosition;
      }

      if (arm.hint != null)
      {
        arm.hintRest = arm.hint.localPosition;
      }

      arm.dynamicTargetOffset = Vector3.zero;
      arm.dynamicHintOffset = Vector3.zero;
      arm.fingerRestDelta = Vector3.zero;
      arm.fingerTipRestRelative = Vector3.zero;
      arm.fingerRestCaptured = false;
      arm.fingerCurlRest = 0f;
      arm.restCaptured = arm.target != null;
    }

    private void ResetToRest()
    {
      ResetArm(rightArm);
      ResetArm(leftArm);

      if (_headCaptured && headTarget != null)
      {
        headTarget.localPosition = _headRestPos;
        headTarget.localRotation = _headRestRot;
      }

      if (_bodyCaptured && bodyRoot != null)
      {
        bodyRoot.localRotation = _bodyRestRot;
      }

      if (rightUpperArmBone != null)
      {
        rightUpperArmBone.localRotation = _rightUpperRestRot;
      }

      if (rightForearmBone != null)
      {
        rightForearmBone.localRotation = _rightForearmRestRot;
      }

      if (leftUpperArmBone != null)
      {
        leftUpperArmBone.localRotation = _leftUpperRestRot;
      }

      if (leftForearmBone != null)
      {
        leftForearmBone.localRotation = _leftForearmRestRot;
      }

      _targetShoulderRight = _targetElbowRight = 0f;
      _targetShoulderLeft = _targetElbowLeft = 0f;
      _targetHeadYaw = _targetHeadPitch = 0f;
      _targetBodyYaw = 0f;
      _indexRestCaptured = false;
      _palmRestCaptured = false;
    }

    private void ResetArm(ArmIkConfig arm)
    {
      if (arm == null || !arm.restCaptured)
      {
        return;
      }

      if (arm.target != null)
      {
        arm.target.localPosition = arm.targetRest;
      }

      if (arm.hint != null)
      {
        arm.hint.localPosition = arm.hintRest;
      }

      if (arm.constraint != null)
      {
        arm.constraint.weight = 0f;
      }

      arm.dynamicTargetOffset = Vector3.zero;
      arm.dynamicHintOffset = Vector3.zero;
      arm.fingerRestCaptured = false;
      arm.fingerRestDelta = Vector3.zero;
      arm.fingerTipRestRelative = Vector3.zero;
      arm.fingerCurlRest = 0f;
    }

    private void Update()
    {
      var lerp = Mathf.Clamp01(Time.deltaTime * Mathf.Max(0f, smoothing));
      _shoulderRight = Mathf.Lerp(_shoulderRight, _targetShoulderRight, lerp);
      _elbowRight = Mathf.Lerp(_elbowRight, _targetElbowRight, lerp);
      _shoulderLeft = Mathf.Lerp(_shoulderLeft, _targetShoulderLeft, lerp);
      _elbowLeft = Mathf.Lerp(_elbowLeft, _targetElbowLeft, lerp);
      _headYaw = Mathf.Lerp(_headYaw, _targetHeadYaw, lerp);
      _headPitch = Mathf.Lerp(_headPitch, _targetHeadPitch, lerp);
      _bodyYaw = Mathf.Lerp(_bodyYaw, _targetBodyYaw, Mathf.Clamp01(Time.deltaTime * (smoothing * 0.5f)));

      ApplyArm(rightArm, _shoulderRight, _elbowRight);
      ApplyArm(leftArm, _shoulderLeft, _elbowLeft);
      ApplyHead();
      ApplyBody();
      ApplyBoneRotations();
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData frame)
    {
      if (frame == null || frame.landmarks == null || frame.landmarks.Length < 21 || !frame.tracked)
      {
        SetTargetsForNoHand();
        return;
      }

      if (useRightHand && !frame.isRight || !useRightHand && frame.isRight)
      {
        SetTargetsForNoHand();
        return;
      }

      HandleTrackedHand(frame);
    }

    private void HandleTrackedHand(HandTrackingSource.HandFrameData frame)
    {
      var pts = frame.landmarks;
      var wrist = pts[0];

      UpdateRightArm(pts, wrist);
      UpdateLeftArm(pts, wrist);
      UpdateHead(pts);
      UpdateBody(frame, pts, wrist);
    }

    private void UpdateRightArm(Vector3[] pts, Vector3 wrist)
    {
      if (!rightArm.restCaptured)
      {
        return;
      }

      var thumbBase = pts[1];
      var thumbTip = pts[4];
      var thumbDelta = thumbTip - thumbBase;
      var thumbRelative = thumbTip - wrist;
      var thumbCurlRaw = ComputeFingerCurl(1, 2, 3, 4, pts);

      if (!rightArm.fingerRestCaptured)
      {
        rightArm.fingerRestDelta = thumbDelta;
        rightArm.fingerTipRestRelative = thumbRelative;
        rightArm.fingerRestCaptured = true;
        rightArm.fingerCurlRest = thumbCurlRaw;
      }

      var thumbDeltaFromRest = thumbDelta - rightArm.fingerRestDelta;
      var thumbRelativeFromRest = thumbRelative - rightArm.fingerTipRestRelative;

      rightArm.dynamicTargetOffset = Vector3.zero;
      rightArm.dynamicHintOffset = Vector3.zero;

      var magnitude = Mathf.Max(0f, thumbRelativeFromRest.magnitude - trioActivationThreshold);
      _targetShoulderRight = Mathf.Clamp01(magnitude * thumbRaiseGain);

      var thumbCurlDelta = rightArm.fingerCurlRest - thumbCurlRaw;
      thumbCurlDelta = Mathf.Max(0f, thumbCurlDelta);
      _targetElbowRight = Mathf.Clamp01(thumbCurlDelta * elbowCurlGain);
    }

    private void UpdateLeftArm(Vector3[] pts, Vector3 wrist)
    {
      if (!leftArm.restCaptured)
      {
        return;
      }

      var ringBase = pts[13];
      var ringTip = pts[16];
      var ringDelta = ringTip - ringBase;
      var ringRelative = ringTip - wrist;
      var ringCurlRaw = ComputeFingerCurl(13, 14, 15, 16, pts);

      if (!leftArm.fingerRestCaptured)
      {
        leftArm.fingerRestDelta = ringDelta;
        leftArm.fingerTipRestRelative = ringRelative;
        leftArm.fingerRestCaptured = true;
        leftArm.fingerCurlRest = ringCurlRaw;
      }

      var ringDeltaFromRest = ringDelta - leftArm.fingerRestDelta;
      var ringRelativeFromRest = ringRelative - leftArm.fingerTipRestRelative;

      leftArm.dynamicTargetOffset = Vector3.zero;
      leftArm.dynamicHintOffset = Vector3.zero;

      var positionalRaise = Mathf.Max(0f, ringRelativeFromRest.magnitude - trioActivationThreshold);
      positionalRaise = Mathf.Clamp01(positionalRaise * shoulderCurlGain);

      var ringCurlDelta = leftArm.fingerCurlRest - ringCurlRaw;
      ringCurlDelta = Mathf.Max(0f, ringCurlDelta);
      var ringCurl = Mathf.Clamp01(ringCurlDelta);
      var blendedRaise = Mathf.Lerp(positionalRaise, ringCurl, Mathf.Clamp01(trioShoulderBlend));
      _targetShoulderLeft = blendedRaise;

      _targetElbowLeft = Mathf.Clamp01(ringCurl * elbowCurlGain);
    }

    private void UpdateHead(Vector3[] pts)
    {
      if (!_headCaptured || headTarget == null)
      {
        return;
      }

      var indexMcp = pts[5];
      var indexTip = pts[8];
      var indexDelta = indexTip - indexMcp;

      if (!_indexRestCaptured)
      {
        _indexRestDelta = indexDelta;
        _indexRestCaptured = true;
      }

      var indexFromRest = indexDelta - _indexRestDelta;
      var yaw = Mathf.Clamp(indexFromRest.x * headMotionGain, -headYawRange, headYawRange);
      var pitchSignal = -indexFromRest.y;
      var pitch = Mathf.Clamp(pitchSignal * headMotionGain, -headPitchRange, headPitchRange);

      _targetHeadYaw = yaw;
      _targetHeadPitch = pitch;
    }

    private void UpdateBody(HandTrackingSource.HandFrameData frame, Vector3[] pts, Vector3 wrist)
    {
      if (!_bodyCaptured || bodyRoot == null)
      {
        return;
      }

      var indexMcp = pts[5];
      var pinkyBase = pts[17];
      var palmCross = Vector3.Cross(indexMcp - wrist, pinkyBase - wrist);

      if (!_palmRestCaptured)
      {
        _palmYawRest = palmCross.z;
        _palmRestCaptured = true;
      }

      var yawFactor = frame.isRight ? 1f : -1f;
      var yawSignal = (palmCross.z - _palmYawRest) * yawFactor;
      var yawValue = Mathf.Clamp(yawSignal * bodyYawGain, -bodyYawClamp, bodyYawClamp);
      _targetBodyYaw = yawValue;
    }

    private void SetTargetsForNoHand()
    {
      _targetShoulderRight = 0f;
      _targetElbowRight = 0f;
      _targetShoulderLeft = 0f;
      _targetElbowLeft = 0f;
      _targetHeadYaw = 0f;
      _targetHeadPitch = 0f;
      _targetBodyYaw = 0f;

      rightArm.dynamicTargetOffset = Vector3.zero;
      rightArm.dynamicHintOffset = Vector3.zero;
      rightArm.fingerRestCaptured = false;
      rightArm.fingerRestDelta = Vector3.zero;
      rightArm.fingerTipRestRelative = Vector3.zero;
      rightArm.fingerCurlRest = 0f;

      leftArm.dynamicTargetOffset = Vector3.zero;
      leftArm.dynamicHintOffset = Vector3.zero;
      leftArm.fingerRestCaptured = false;
      leftArm.fingerRestDelta = Vector3.zero;
      leftArm.fingerTipRestRelative = Vector3.zero;
      leftArm.fingerCurlRest = 0f;

      _indexRestCaptured = false;
      _palmRestCaptured = false;
    }

    private void ApplyArm(ArmIkConfig arm, float shoulderWeight, float elbowWeight)
    {
      if (arm == null || !arm.restCaptured)
      {
        return;
      }

      var activeWeight = Mathf.Clamp01(Mathf.Max(shoulderWeight, elbowWeight) * arm.weightMultiplier);
      if (arm.constraint != null)
      {
        arm.constraint.weight = activeWeight;
      }

      if (arm.target != null)
      {
        var dir = arm.shoulderDirection.sqrMagnitude > 0f ? arm.shoulderDirection.normalized : Vector3.up;
        var offset = arm.dynamicTargetOffset + dir * (arm.shoulderDistance * shoulderWeight);
        arm.target.localPosition = arm.targetRest + offset;
      }

      if (arm.hint != null)
      {
        var dirHint = arm.elbowDirection.sqrMagnitude > 0f ? arm.elbowDirection.normalized : Vector3.forward;
        var offset = arm.dynamicHintOffset + dirHint * (arm.elbowDistance * elbowWeight);
        arm.hint.localPosition = arm.hintRest + offset;
      }
    }

    private void ApplyHead()
    {
      if (!_headCaptured || headTarget == null)
      {
        return;
      }

      var yawOffset = headYawAxis.normalized * _headYaw;
      var pitchOffset = headPitchAxis.normalized * _headPitch;
      headTarget.localPosition = _headRestPos + yawOffset + pitchOffset;
      headTarget.localRotation = _headRestRot;
    }

    private void ApplyBody()
    {
      if (!_bodyCaptured || bodyRoot == null)
      {
        return;
      }

      bodyRoot.localRotation = _bodyRestRot * Quaternion.Euler(0f, _bodyYaw, 0f);
    }

    private void ApplyBoneRotations()
    {
      if (rightUpperArmBone != null)
      {
        var axis = rightUpperArmAxis.sqrMagnitude > 0f ? rightUpperArmAxis.normalized : Vector3.right;
        rightUpperArmBone.localRotation = _rightUpperRestRot * Quaternion.AngleAxis(_shoulderRight * rightUpperArmAngle, axis);
      }

      if (rightForearmBone != null)
      {
        var axis = rightForearmAxis.sqrMagnitude > 0f ? rightForearmAxis.normalized : Vector3.right;
        rightForearmBone.localRotation = _rightForearmRestRot * Quaternion.AngleAxis(_elbowRight * rightForearmAngle, axis);
      }

      if (leftUpperArmBone != null)
      {
        var axis = leftUpperArmAxis.sqrMagnitude > 0f ? leftUpperArmAxis.normalized : Vector3.right;
        leftUpperArmBone.localRotation = _leftUpperRestRot * Quaternion.AngleAxis(_shoulderLeft * leftUpperArmAngle, axis);
      }

      if (leftForearmBone != null)
      {
        var axis = leftForearmAxis.sqrMagnitude > 0f ? leftForearmAxis.normalized : Vector3.right;
        leftForearmBone.localRotation = _leftForearmRestRot * Quaternion.AngleAxis(_elbowLeft * leftForearmAngle, axis);
      }
    }

    private static Vector3 ClampPerAxis(Vector3 value, Vector3 multiplier, float maxMagnitude)
    {
      var scaled = new Vector3(
        value.x * multiplier.x,
        value.y * multiplier.y,
        value.z * multiplier.z
      );

      if (scaled.sqrMagnitude > maxMagnitude * maxMagnitude)
      {
        scaled = scaled.normalized * maxMagnitude;
      }

      return scaled;
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
