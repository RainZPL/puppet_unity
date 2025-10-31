using UnityEngine;

namespace HandControl
{
  // Finger rotation driver (newbie version).
  [DefaultExecutionOrder(-5)]
  public class FingerRotationDriver : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource handSource;
    [SerializeField] private bool useRightHand = true;

    [Header("Right Arm (thumb)")]
    [SerializeField] private Transform rightUpper;
    [SerializeField] private Transform rightFore;
    [SerializeField] private Vector3 rightUpperAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float rightUpperMaxAngle = 90f;
    [SerializeField] private Vector3 rightForeAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] private float rightForeMaxAngle = 110f;
    [SerializeField] private float rightUpperMinAngle = -90f;
    [SerializeField] private float rightForeMinAngle = 0f;
    [SerializeField] private float rightRaiseGain = 3.5f;
    [SerializeField] private float rightCurlGain = 1.2f;

    [Header("Left Arm (ring finger)")]
    [SerializeField] private Transform leftUpper;
    [SerializeField] private Transform leftFore;
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
    [SerializeField] private float headGain = 1.0f;

    [Header("Body (palm twist)")]
    [SerializeField] private Transform bodyRoot;
    [SerializeField] private Vector3 bodyYawAxis = new Vector3(0f, 1f, 0f);
    [SerializeField] private float bodyYawRange = 70f;
    [SerializeField] private float bodyYawGain = 4f;
    [SerializeField, Tooltip("Treat bodyYawAxis as world-space direction (converted into local space at runtime).")]
    private bool bodyAxisInWorldSpace = false;

    [Header("Smoothing")]
    [SerializeField] private float smoothing = 12f;

    private Quaternion rightUpperRest;
    private Quaternion rightForeRest;
    private Quaternion leftUpperRest;
    private Quaternion leftForeRest;
    private Quaternion headRest;
    private Quaternion bodyRest;

    private bool restReady;
    private Vector3 thumbRestVec;
    private float thumbCurlRest;
    private Vector3 ringRestVec;
    private float ringCurlRest;

    private float rightRaiseValue;
    private float rightCurlValue;
    private float leftRaiseValue;
    private float leftCurlValue;
    private float headYawValue;
    private float headPitchValue;
    private float headCurlValue;
    private float bodyYawValue;
    private Vector3 indexRestVec;
    private float indexCurlRest;
    private Vector3 palmRestVec;
    private Vector3 palmRestVecNormalized;

    private void OnEnable()
    {
      RememberBonePose();

      if (handSource != null)
      {
        handSource.OnHandFrame += HandleHandFrame;
      }
    }

    private void OnDisable()
    {
      if (handSource != null)
      {
        handSource.OnHandFrame -= HandleHandFrame;
      }

      restReady = false;
      rightRaiseValue = rightCurlValue = 0f;
      leftRaiseValue = leftCurlValue = 0f;
      headYawValue = headPitchValue = headCurlValue = 0f;
      bodyYawValue = 0f;
      RestoreBonePose();
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData frame)
    {
      if (frame == null || frame.landmarks == null || frame.landmarks.Length < 21 || !frame.tracked)
      {
        EaseBackToPose();
        return;
      }

      if (useRightHand && !frame.isRight || !useRightHand && frame.isRight)
      {
        EaseBackToPose();
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

      if (!restReady)
      {
        thumbRestVec = thumbVector;
        ringRestVec = ringVector;
        thumbCurlRest = thumbCurlCurrent;
        ringCurlRest = ringCurlCurrent;
        indexRestVec = indexDelta;
        indexCurlRest = indexCurlCurrent;
        palmRestVec = palmNormal;
        palmRestVecNormalized = palmNormal.normalized;
        restReady = true;
      }

      var thumbRaiseDelta = Vector3.Dot(thumbVector - thumbRestVec, Vector3.up);
      var ringRaiseDelta = Vector3.Dot(ringVector - ringRestVec, Vector3.up);

      var targetRightRaise = Mathf.Clamp(thumbRaiseDelta * rightRaiseGain, -1f, 1f);
      var targetLeftRaise = Mathf.Clamp(ringRaiseDelta * leftRaiseGain, -1f, 1f);

      var thumbCurlDelta = Mathf.Max(0f, thumbCurlRest - thumbCurlCurrent);
      var ringCurlDelta = Mathf.Max(0f, ringCurlRest - ringCurlCurrent);

      var targetRightCurl = Mathf.Clamp01(thumbCurlDelta * rightCurlGain);
      var targetLeftCurl = Mathf.Clamp01(ringCurlDelta * leftCurlGain);

      var indexOffset = indexDelta - indexRestVec;
      var targetHeadYaw = Mathf.Clamp(indexOffset.x * headGain, -1f, 1f) * headYawRange;
      var targetHeadPitch = Mathf.Clamp(-indexOffset.y * headGain, -1f, 1f) * headPitchRange;
      var indexCurlDelta = Mathf.Max(0f, indexCurlRest - indexCurlCurrent) * headCurlAngle;

      float targetBodyYaw = 0f;
      var restNorm = palmRestVecNormalized;
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
      rightRaiseValue = Mathf.Lerp(rightRaiseValue, targetRightRaise, lerp);
      rightCurlValue = Mathf.Lerp(rightCurlValue, targetRightCurl, lerp);
      leftRaiseValue = Mathf.Lerp(leftRaiseValue, targetLeftRaise, lerp);
      leftCurlValue = Mathf.Lerp(leftCurlValue, targetLeftCurl, lerp);
      headYawValue = Mathf.Lerp(headYawValue, targetHeadYaw, lerp);
      headPitchValue = Mathf.Lerp(headPitchValue, targetHeadPitch, lerp);
      headCurlValue = Mathf.Lerp(headCurlValue, indexCurlDelta, lerp);
      bodyYawValue = Mathf.Lerp(bodyYawValue, targetBodyYaw, lerp);

    }

    private void EaseBackToPose()
    {
      var lerp = Mathf.Clamp01(Time.deltaTime * Mathf.Max(1f, smoothing));
      rightRaiseValue = Mathf.Lerp(rightRaiseValue, 0f, lerp);
      rightCurlValue = Mathf.Lerp(rightCurlValue, 0f, lerp);
      leftRaiseValue = Mathf.Lerp(leftRaiseValue, 0f, lerp);
      leftCurlValue = Mathf.Lerp(leftCurlValue, 0f, lerp);
      headYawValue = Mathf.Lerp(headYawValue, 0f, lerp);
      headPitchValue = Mathf.Lerp(headPitchValue, 0f, lerp);
      headCurlValue = Mathf.Lerp(headCurlValue, 0f, lerp);
      bodyYawValue = Mathf.Lerp(bodyYawValue, 0f, lerp);
      restReady = false;
      palmRestVecNormalized = Vector3.zero;
    }

    private void LateUpdate()
    {
      ApplyRotations();
    }

    private void ApplyRotations()
    {
      if (rightUpper)
      {
        rightUpperRest = rightUpper.localRotation;
        var axis = rightUpperAxis.sqrMagnitude > 0f ? rightUpperAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(rightUpperMinAngle, rightUpperMaxAngle, Mathf.InverseLerp(-1f, 1f, rightRaiseValue));
        rightUpper.localRotation = rightUpperRest * Quaternion.AngleAxis(angle, axis);
      }

      if (rightFore)
      {
        rightForeRest = rightFore.localRotation;
        var axis = rightForeAxis.sqrMagnitude > 0f ? rightForeAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(rightForeMinAngle, rightForeMaxAngle, Mathf.Clamp01(rightCurlValue));
        rightFore.localRotation = rightForeRest * Quaternion.AngleAxis(angle, axis);
      }

      if (leftUpper)
      {
        leftUpperRest = leftUpper.localRotation;
        var axis = leftUpperAxis.sqrMagnitude > 0f ? leftUpperAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(leftUpperMinAngle, leftUpperMaxAngle, Mathf.InverseLerp(-1f, 1f, leftRaiseValue));
        leftUpper.localRotation = leftUpperRest * Quaternion.AngleAxis(angle, axis);
      }

      if (leftFore)
      {
        leftForeRest = leftFore.localRotation;
        var axis = leftForeAxis.sqrMagnitude > 0f ? leftForeAxis.normalized : Vector3.right;
        var angle = Mathf.Lerp(leftForeMinAngle, leftForeMaxAngle, Mathf.Clamp01(leftCurlValue));
        leftFore.localRotation = leftForeRest * Quaternion.AngleAxis(angle, axis);
      }

      if (headBone)
      {
        headRest = headBone.localRotation;
        var yawAxis = headYawAxis.sqrMagnitude > 0f ? headYawAxis.normalized : Vector3.up;
        var pitchAxis = headPitchAxis.sqrMagnitude > 0f ? headPitchAxis.normalized : Vector3.right;
        var yawRot = Quaternion.AngleAxis(headYawValue, yawAxis);
        var pitchRot = Quaternion.AngleAxis(headPitchValue + headCurlValue, pitchAxis);
        headBone.localRotation = headRest * yawRot * pitchRot;
      }

      if (bodyRoot)
      {
        bodyRest = bodyRoot.localRotation;
        var axis = bodyYawAxis.sqrMagnitude > 0f ? bodyYawAxis.normalized : Vector3.up;
        if (bodyAxisInWorldSpace)
        {
          axis = bodyRoot.InverseTransformDirection(axis).normalized;
        }
        bodyRoot.localRotation = bodyRest * Quaternion.AngleAxis(bodyYawValue, axis);
      }
    }

    private void RestoreBonePose()
    {
      if (rightUpper) rightUpper.localRotation = rightUpperRest;
      if (rightFore) rightFore.localRotation = rightForeRest;
      if (leftUpper) leftUpper.localRotation = leftUpperRest;
      if (leftFore) leftFore.localRotation = leftForeRest;
      if (headBone) headBone.localRotation = headRest;
      if (bodyRoot) bodyRoot.localRotation = bodyRest;
    }

    private void RememberBonePose()
    {
      if (rightUpper) rightUpperRest = rightUpper.localRotation;
      if (rightFore) rightForeRest = rightFore.localRotation;
      if (leftUpper) leftUpperRest = leftUpper.localRotation;
      if (leftFore) leftForeRest = leftFore.localRotation;
      if (headBone) headRest = headBone.localRotation;
      if (bodyRoot) bodyRest = bodyRoot.localRotation;
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
