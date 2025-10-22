using UnityEngine;

namespace HandControl
{
  /// <summary>
  /// Minimal finger-to-bone rotation driver:
  ///   - Thumb raises the right upper arm, thumb curl bends the right forearm.
  ///   - Ring finger raises the left upper arm, ring curl bends the left forearm.
  /// No IK targets are needed; we only rotate the specified bones.
  /// </summary>
  [DefaultExecutionOrder(-5)]
  public class FingerRotationDriver : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource source;
    [SerializeField] private bool useRightHand = true;

    [Header("Right Arm (thumb)")]
    [SerializeField] private Transform rightUpperArm;
    [SerializeField] private Transform rightForearm;
    [SerializeField] private Vector3 rightUpperAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float rightUpperMaxAngle = 90f;
    [SerializeField] private Vector3 rightForeAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float rightForeMaxAngle = 110f;
    [SerializeField] private float rightUpperMinAngle = -90f;
    [SerializeField] private float rightForeMinAngle = 0f;
    [SerializeField] private float rightRaiseGain = 3.5f;
    [SerializeField] private float rightCurlGain = 1.2f;

    [Header("Left Arm (ring finger)")]
    [SerializeField] private Transform leftUpperArm;
    [SerializeField] private Transform leftForearm;
    [SerializeField] private Vector3 leftUpperAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float leftUpperMaxAngle = 90f;
    [SerializeField] private Vector3 leftForeAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float leftForeMaxAngle = 110f;
    [SerializeField] private float leftUpperMinAngle = -90f;
    [SerializeField] private float leftForeMinAngle = 0f;
    [SerializeField] private float leftRaiseGain = 3.5f;
    [SerializeField] private float leftCurlGain = 1.2f;

    [Header("Head (index finger)")]
    [SerializeField] private Transform headBone;
    [SerializeField] private Vector3 headYawAxis = new Vector3(0f, 1f, 0f);
    [SerializeField] private Vector3 headPitchAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float headYawRange = 45f;
    [SerializeField] private float headPitchRange = 30f;
    [SerializeField] private float headCurlAngle = 20f;
    [SerializeField] private float headMotionGain = 1.0f;

    [Header("Body Yaw (palm rotation)")]
    [SerializeField] private Transform bodyRoot;
    [SerializeField] private Vector3 bodyYawAxis = new Vector3(0f, 1f, 0f);
    [SerializeField] private float bodyYawRange = 70f;
    [SerializeField] private float bodyYawGain = 4f;
    [SerializeField, Tooltip("Treat bodyYawAxis as world-space direction (converted into local space at runtime).")]
    private bool bodyAxisInWorldSpace = false;

    [Header("Smoothing")]
    [SerializeField] private float smoothing = 12f;

    private Quaternion _rightUpperBase;
    private Quaternion _rightForeBase;
    private Quaternion _leftUpperBase;
    private Quaternion _leftForeBase;
    private Quaternion _headBase;
    private Quaternion _bodyBase;

    private bool _restCaptured;
    private Vector3 _thumbRestVector;
    private float _thumbCurlRest;
    private Vector3 _ringRestVector;
    private float _ringCurlRest;

    private float _rightRaise;
    private float _rightCurl;
    private float _leftRaise;
    private float _leftCurl;
    private float _headYaw;
    private float _headPitch;
    private float _headCurl;
    private float _bodyYaw;
    private Vector3 _indexRestDelta;
    private float _indexCurlRest;
    private Vector3 _palmNormalRest;
    private Vector3 _palmNormalRestNormalized;

    private void OnEnable()
    {
      CaptureAnimatorPose();

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

      _restCaptured = false;
      _rightRaise = _rightCurl = 0f;
      _leftRaise = _leftCurl = 0f;
      _headYaw = _headPitch = _headCurl = 0f;
      _bodyYaw = 0f;
      RestoreAnimatorPose();
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData frame)
    {
      if (frame == null || frame.landmarks == null || frame.landmarks.Length < 21 || !frame.tracked)
      {
        ResetTargets();
        return;
      }

      if (useRightHand && !frame.isRight || !useRightHand && frame.isRight)
      {
        ResetTargets();
        return;
      }

      var pts = frame.landmarks;
      var wrist = pts[0];

      var thumbTip = pts[4];
      var ringTip = pts[16];
      var indexBase = pts[5];
      var indexTip = pts[8];
      var pinkyBase = pts[17];

      var thumbVector = thumbTip - wrist;
      var ringVector = ringTip - wrist;
      var indexDelta = indexTip - indexBase;
      var palmNormal = Vector3.Cross(indexBase - wrist, pinkyBase - wrist);
      var thumbCurlCurrent = ComputeFingerCurl(1, 2, 3, 4, pts);
      var ringCurlCurrent = ComputeFingerCurl(13, 14, 15, 16, pts);
      var indexCurlCurrent = ComputeFingerCurl(5, 6, 7, 8, pts);

      if (!_restCaptured)
      {
        _thumbRestVector = thumbVector;
        _ringRestVector = ringVector;
        _thumbCurlRest = thumbCurlCurrent;
        _ringCurlRest = ringCurlCurrent;
        _indexRestDelta = indexDelta;
        _indexCurlRest = indexCurlCurrent;
        _palmNormalRest = palmNormal;
        _palmNormalRestNormalized = palmNormal.normalized;
        _restCaptured = true;
      }

      var thumbRaiseDelta = Vector3.Dot(thumbVector - _thumbRestVector, Vector3.up);
      var ringRaiseDelta = Vector3.Dot(ringVector - _ringRestVector, Vector3.up);

      var targetRightRaise = Mathf.Clamp(thumbRaiseDelta * rightRaiseGain, -1f, 1f);
      var targetLeftRaise = Mathf.Clamp(ringRaiseDelta * leftRaiseGain, -1f, 1f);

      var thumbCurlDelta = Mathf.Max(0f, _thumbCurlRest - thumbCurlCurrent);
      var ringCurlDelta = Mathf.Max(0f, _ringCurlRest - ringCurlCurrent);

      var targetRightCurl = Mathf.Clamp01(thumbCurlDelta * rightCurlGain);
      var targetLeftCurl = Mathf.Clamp01(ringCurlDelta * leftCurlGain);

      var indexOffset = indexDelta - _indexRestDelta;
      var targetHeadYaw = Mathf.Clamp(indexOffset.x * headMotionGain, -1f, 1f) * headYawRange;
      var targetHeadPitch = Mathf.Clamp(-indexOffset.y * headMotionGain, -1f, 1f) * headPitchRange;
      var indexCurlDelta = Mathf.Max(0f, _indexCurlRest - indexCurlCurrent) * headCurlAngle;

      float targetBodyYaw = 0f;
      var restNorm = _palmNormalRestNormalized;
      var currNorm = palmNormal.normalized;
      if (restNorm.sqrMagnitude > 1e-6f && currNorm.sqrMagnitude > 1e-6f)
      {
        Vector3 axisWorld;
        if (bodyAxisInWorldSpace)
        {
          axisWorld = bodyYawAxis.sqrMagnitude > 0f ? bodyYawAxis.normalized : Vector3.up;
        }
        else
        {
          if (bodyRoot != null)
          {
            axisWorld = bodyRoot.TransformDirection(bodyYawAxis);
          }
          else
          {
            axisWorld = bodyYawAxis;
          }
          axisWorld = axisWorld.sqrMagnitude > 0f ? axisWorld.normalized : Vector3.up;
        }

        var signedAngle = Vector3.SignedAngle(restNorm, currNorm, axisWorld);
        targetBodyYaw = Mathf.Clamp(signedAngle * bodyYawGain, -bodyYawRange, bodyYawRange);
      }

      var lerp = Mathf.Clamp01(Time.deltaTime * Mathf.Max(1f, smoothing));
      _rightRaise = Mathf.Lerp(_rightRaise, targetRightRaise, lerp);
      _rightCurl = Mathf.Lerp(_rightCurl, targetRightCurl, lerp);
      _leftRaise = Mathf.Lerp(_leftRaise, targetLeftRaise, lerp);
      _leftCurl = Mathf.Lerp(_leftCurl, targetLeftCurl, lerp);
      _headYaw = Mathf.Lerp(_headYaw, targetHeadYaw, lerp);
      _headPitch = Mathf.Lerp(_headPitch, targetHeadPitch, lerp);
      _headCurl = Mathf.Lerp(_headCurl, indexCurlDelta, lerp);
      _bodyYaw = Mathf.Lerp(_bodyYaw, targetBodyYaw, lerp);

    }

    private void ResetTargets()
    {
      var lerp = Mathf.Clamp01(Time.deltaTime * Mathf.Max(1f, smoothing));
      _rightRaise = Mathf.Lerp(_rightRaise, 0f, lerp);
      _rightCurl = Mathf.Lerp(_rightCurl, 0f, lerp);
      _leftRaise = Mathf.Lerp(_leftRaise, 0f, lerp);
      _leftCurl = Mathf.Lerp(_leftCurl, 0f, lerp);
      _headYaw = Mathf.Lerp(_headYaw, 0f, lerp);
      _headPitch = Mathf.Lerp(_headPitch, 0f, lerp);
      _headCurl = Mathf.Lerp(_headCurl, 0f, lerp);
      _bodyYaw = Mathf.Lerp(_bodyYaw, 0f, lerp);
      _restCaptured = false;
      _palmNormalRestNormalized = Vector3.zero;
    }

    private void LateUpdate()
    {
      ApplyRotations();
    }

    private void ApplyRotations()
    {
      if (rightUpperArm)
      {
        _rightUpperBase = rightUpperArm.localRotation;
        var axis = rightUpperAxis.sqrMagnitude > 0f ? rightUpperAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(rightUpperMinAngle, rightUpperMaxAngle, Mathf.InverseLerp(-1f, 1f, _rightRaise));
        rightUpperArm.localRotation = _rightUpperBase * Quaternion.AngleAxis(angle, axis);
      }

      if (rightForearm)
      {
        _rightForeBase = rightForearm.localRotation;
        var axis = rightForeAxis.sqrMagnitude > 0f ? rightForeAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(rightForeMinAngle, rightForeMaxAngle, Mathf.Clamp01(_rightCurl));
        rightForearm.localRotation = _rightForeBase * Quaternion.AngleAxis(angle, axis);
      }

      if (leftUpperArm)
      {
        _leftUpperBase = leftUpperArm.localRotation;
        var axis = leftUpperAxis.sqrMagnitude > 0f ? leftUpperAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(leftUpperMinAngle, leftUpperMaxAngle, Mathf.InverseLerp(-1f, 1f, _leftRaise));
        leftUpperArm.localRotation = _leftUpperBase * Quaternion.AngleAxis(angle, axis);
      }

      if (leftForearm)
      {
        _leftForeBase = leftForearm.localRotation;
        var axis = leftForeAxis.sqrMagnitude > 0f ? leftForeAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(leftForeMinAngle, leftForeMaxAngle, Mathf.Clamp01(_leftCurl));
        leftForearm.localRotation = _leftForeBase * Quaternion.AngleAxis(angle, axis);
      }

      if (headBone)
      {
        _headBase = headBone.localRotation;
        var yawAxis = headYawAxis.sqrMagnitude > 0f ? headYawAxis.normalized : Vector3.up;
        var pitchAxis = headPitchAxis.sqrMagnitude > 0f ? headPitchAxis.normalized : Vector3.right;
        var yawRot = Quaternion.AngleAxis(_headYaw, yawAxis);
        var pitchRot = Quaternion.AngleAxis(_headPitch + _headCurl, pitchAxis);
        headBone.localRotation = _headBase * yawRot * pitchRot;
      }

      if (bodyRoot)
      {
        _bodyBase = bodyRoot.localRotation;
        var axis = bodyYawAxis.sqrMagnitude > 0f ? bodyYawAxis.normalized : Vector3.up;
        if (bodyAxisInWorldSpace)
        {
          axis = bodyRoot.InverseTransformDirection(axis).normalized;
        }
        bodyRoot.localRotation = _bodyBase * Quaternion.AngleAxis(_bodyYaw, axis);
      }
    }

    private void RestoreAnimatorPose()
    {
      if (rightUpperArm) rightUpperArm.localRotation = _rightUpperBase;
      if (rightForearm) rightForearm.localRotation = _rightForeBase;
      if (leftUpperArm) leftUpperArm.localRotation = _leftUpperBase;
      if (leftForearm) leftForearm.localRotation = _leftForeBase;
      if (headBone) headBone.localRotation = _headBase;
      if (bodyRoot) bodyRoot.localRotation = _bodyBase;
    }

    private void CaptureAnimatorPose()
    {
      if (rightUpperArm) _rightUpperBase = rightUpperArm.localRotation;
      if (rightForearm) _rightForeBase = rightForearm.localRotation;
      if (leftUpperArm) _leftUpperBase = leftUpperArm.localRotation;
      if (leftForearm) _leftForeBase = leftForearm.localRotation;
      if (headBone) _headBase = headBone.localRotation;
      if (bodyRoot) _bodyBase = bodyRoot.localRotation;
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
