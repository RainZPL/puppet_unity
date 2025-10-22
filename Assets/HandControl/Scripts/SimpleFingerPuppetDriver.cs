using UnityEngine;

namespace HandControl
{
  [DefaultExecutionOrder(-5)]
  public class SimpleFingerPuppetDriver : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource source;

    [Header("Right Arm (Thumb)")]
    [SerializeField] private Transform rightUpperArm;
    [SerializeField] private Transform rightForearm;
    [SerializeField] private Transform rightHand;
    [SerializeField] private float rightArmRaiseAngle = 90f;
    [SerializeField] private float rightElbowBendAngle = 90f;

    [Header("Left Arm (Ring)")]
    [SerializeField] private Transform leftUpperArm;
    [SerializeField] private Transform leftForearm;
    [SerializeField] private Transform leftHand;
    [SerializeField] private float leftArmRaiseAngle = 90f;
    [SerializeField] private float leftElbowBendAngle = 90f;

    [Header("Head (Index)")]
    [SerializeField] private Transform headBone;
    [SerializeField] private float headYawAngle = 40f;
    [SerializeField] private float headPitchAngle = 30f;

    [Header("Body Yaw (Wrist twist)")]
    [SerializeField] private Transform bodyRoot;
    [SerializeField] private float bodyYawAngle = 45f;

    [Header("Smoothing")]
    [SerializeField] private float lerpSpeed = 10f;

    private Vector3 _thumbBaseRest;
    private Vector3 _thumbTipRest;
    private Vector3 _ringBaseRest;
    private Vector3 _ringTipRest;
    private Vector3 _indexDeltaRest;
    private Vector3 _palmNormalRest;

    private bool _restCaptured;

    private Quaternion _rightUpperRest;
    private Quaternion _rightForearmRest;
    private Quaternion _rightHandRest;

    private Quaternion _leftUpperRest;
    private Quaternion _leftForearmRest;
    private Quaternion _leftHandRest;

    private Quaternion _headRest;
    private Quaternion _bodyRest;

    private float _currentRightRaise;
    private float _currentRightElbow;
    private float _currentLeftRaise;
    private float _currentLeftElbow;
    private float _currentHeadYaw;
    private float _currentHeadPitch;
    private float _currentBodyYaw;

    private void Awake()
    {
      CacheBoneRests();
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
    }

    private void CacheBoneRests()
    {
      if (rightUpperArm) _rightUpperRest = rightUpperArm.localRotation;
      if (rightForearm) _rightForearmRest = rightForearm.localRotation;
      if (rightHand) _rightHandRest = rightHand.localRotation;

      if (leftUpperArm) _leftUpperRest = leftUpperArm.localRotation;
      if (leftForearm) _leftForearmRest = leftForearm.localRotation;
      if (leftHand) _leftHandRest = leftHand.localRotation;

      if (headBone) _headRest = headBone.localRotation;
      if (bodyRoot) _bodyRest = bodyRoot.localRotation;
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData frame)
    {
      if (frame == null || frame.landmarks == null || frame.landmarks.Length < 21 || !frame.tracked)
      {
        _restCaptured = false;
        return;
      }

      // 只接右手数据
      if (!frame.isRight)
      {
        _restCaptured = false;
        return;
      }

      var pts = frame.landmarks;
      var wrist = pts[0];
      var thumbBase = pts[1];
      var thumbTip = pts[4];

      var ringBase = pts[13];
      var ringTip = pts[16];

      var indexBase = pts[5];
      var indexTip = pts[8];

      var palmNormal = Vector3.Cross(indexBase - wrist, pts[17] - wrist);

      if (!_restCaptured)
      {
        _thumbBaseRest = thumbBase - wrist;
        _thumbTipRest = thumbTip - wrist;
        _ringBaseRest = ringBase - wrist;
        _ringTipRest = ringTip - wrist;
        _indexDeltaRest = indexTip - indexBase;
        _palmNormalRest = palmNormal;
        _restCaptured = true;
      }

      // Thumb → right arm
      var thumbRaise = Mathf.Clamp01(Vector3.Dot((thumbTip - wrist) - _thumbTipRest.normalized * (_thumbTipRest.magnitude),
                                                Vector3.up));
      var thumbCurl = ComputeFingerCurl(1, 2, 3, 4, pts);

      _currentRightRaise = Mathf.Lerp(_currentRightRaise, thumbRaise, Time.deltaTime * lerpSpeed);
      _currentRightElbow = Mathf.Lerp(_currentRightElbow, thumbCurl, Time.deltaTime * lerpSpeed);

      // Ring → left arm
      var ringRaise = Mathf.Clamp01(Vector3.Dot((ringTip - wrist) - _ringTipRest.normalized * (_ringTipRest.magnitude),
                                               Vector3.up));
      var ringCurl = ComputeFingerCurl(13, 14, 15, 16, pts);

      _currentLeftRaise = Mathf.Lerp(_currentLeftRaise, ringRaise, Time.deltaTime * lerpSpeed);
      _currentLeftElbow = Mathf.Lerp(_currentLeftElbow, ringCurl, Time.deltaTime * lerpSpeed);

      // Index → head
      var indexDelta = (indexTip - indexBase) - _indexDeltaRest;
      var headYaw = Mathf.Clamp(indexDelta.x, -1f, 1f);
      var headPitch = Mathf.Clamp(-indexDelta.y, -1f, 1f);

      _currentHeadYaw = Mathf.Lerp(_currentHeadYaw, headYaw, Time.deltaTime * lerpSpeed);
      _currentHeadPitch = Mathf.Lerp(_currentHeadPitch, headPitch, Time.deltaTime * lerpSpeed);

      // Wrist twist → body
      var palm = palmNormal - _palmNormalRest;
      var yaw = Mathf.Clamp(palm.z, -1f, 1f);
      _currentBodyYaw = Mathf.Lerp(_currentBodyYaw, yaw, Time.deltaTime * lerpSpeed);

      ApplyPose();
    }

    private void ApplyPose()
    {
      if (rightUpperArm)
      {
        rightUpperArm.localRotation = _rightUpperRest * Quaternion.Euler(-_currentRightRaise * rightArmRaiseAngle, 0f, 0f);
      }
      if (rightForearm)
      {
        rightForearm.localRotation = _rightForearmRest * Quaternion.Euler(_currentRightElbow * rightElbowBendAngle, 0f, 0f);
      }
      if (rightHand)
      {
        rightHand.localRotation = _rightHandRest;
      }

      if (leftUpperArm)
      {
        leftUpperArm.localRotation = _leftUpperRest * Quaternion.Euler(-_currentLeftRaise * leftArmRaiseAngle, 0f, 0f);
      }
      if (leftForearm)
      {
        leftForearm.localRotation = _leftForearmRest * Quaternion.Euler(_currentLeftElbow * leftElbowBendAngle, 0f, 0f);
      }
      if (leftHand)
      {
        leftHand.localRotation = _leftHandRest;
      }

      if (headBone)
      {
        headBone.localRotation = _headRest *
                                 Quaternion.Euler(_currentHeadPitch * headPitchAngle, _currentHeadYaw * headYawAngle, 0f);
      }

      if (bodyRoot)
      {
        bodyRoot.localRotation = _bodyRest * Quaternion.Euler(0f, _currentBodyYaw * bodyYawAngle, 0f);
      }
    }

    private static float ComputeFingerCurl(int mcp, int pip, int dip, int tip, Vector3[] pts)
    {
      var angle1 = Vector3.Angle(pts[pip] - pts[mcp], pts[dip] - pts[pip]);
      var angle2 = Vector3.Angle(pts[dip] - pts[pip], pts[tip] - pts[dip]);
      return Mathf.Clamp01((NormalizeCurl(angle1) + NormalizeCurl(angle2)) * 0.5f);
    }

    private static float NormalizeCurl(float angle)
    {
      // 30 度视作完全弯曲，165 度视作完全伸直
      return Mathf.InverseLerp(165f, 30f, angle);
    }
  }
}
